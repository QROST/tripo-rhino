using Xunit;

namespace Tripo.HostUi.Tests;

public sealed class TripoPanelImageGenerationTests
{
    private static Tripo.HostUi.TripoPanelSession CreateSession(
        TripoPanelSessionTests.FakeHostControlClient client) =>
        TripoPanelSessionTests.CreateSession(client);

    private static Tripo.Bridge.StagedImageTransfer ValidImage() =>
        new(
            "11111111-1111-1111-8111-111111111111",
            new string('a', 64),
            1024,
            "image/png");

    [Fact]
    public async Task UnconfirmedImageGenerationMakesNoPaidCallButPreparesOperation()
    {
        TripoPanelSessionTests.FakeHostControlClient client = new();
        await using Tripo.HostUi.TripoPanelSession session =
            CreateSession(client);
        await session.ConnectAsync();

        Tripo.HostUi.PreparedImageGeneration prepared =
            session.PrepareImageGeneration(
                ValidImage(),
                20_000,
                withMaterials: true);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.DispatchPreparedImageGenerationAsync(
                userConfirmedExternalCost: false));

        Assert.Equal(
            prepared.OperationId,
            session.State.PreparedImageGeneration?.OperationId);
        Assert.Equal(0, client.CreateImageCalls);
    }

    [Fact]
    public async Task ImageGenerationDispatchesCreateImageTaskWithStagedDescriptor()
    {
        TripoPanelSessionTests.FakeHostControlClient client = new();
        await using Tripo.HostUi.TripoPanelSession session =
            CreateSession(client);
        await session.ConnectAsync();
        Tripo.Bridge.StagedImageTransfer image = ValidImage();
        Tripo.HostUi.PreparedImageGeneration prepared =
            session.PrepareImageGeneration(image, 15_000, withMaterials: false);

        await session.DispatchPreparedImageGenerationAsync(
            userConfirmedExternalCost: true);

        Assert.Equal(1, client.CreateImageCalls);
        Tripo.Bridge.HostControlCreateImageTaskRequest request =
            Assert.Single(client.ImageRequests);
        Assert.Equal(prepared.OperationId, request.OperationId);
        Assert.Equal(image.Sha256, request.Image.Sha256);
        Assert.Equal(15_000, request.FaceLimit);
        Assert.False(request.WithMaterials);
        Assert.Equal(
            "task_image456",
            session.State.ImageGenerationReceipt?.TaskId);
        // A successful image dispatch must surface as a durable generation task
        // exactly like a text dispatch, so conversion/import/recovery work.
        Assert.True(session.State.HasDurableGenerationTask);
    }

    [Fact]
    public async Task RecoveryBackedImageDispatchPersistsExactUuidAndReceiptOnce()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "tripo-image-recovery-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            TripoPanelSessionTests.FakeHostControlClient client = new();
            await using Tripo.HostUi.TripoPanelSession session =
                new(
                    new TestConnector(client),
                    new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root));
            await session.ConnectAsync();
            Tripo.HostUi.PreparedImageGeneration prepared =
                session.PrepareImageGeneration(
                    ValidImage(),
                    15_000,
                    withMaterials: false);

            await session.DispatchPreparedImageGenerationAsync(
                userConfirmedExternalCost: true);

            Assert.Equal(1, client.CreateImageCalls);
            Assert.Equal(
                prepared.OperationId,
                Assert.Single(client.ImageRequests).OperationId);
            Assert.Equal(
                prepared.OperationId,
                session.State.ImageGenerationReceipt?.OperationId);
            string hintPath = Assert.Single(
                Directory.GetFiles(
                    Path.Combine(root, "ui-recovery", "rhino"),
                    "*.json"));
            Tripo.HostUi.TripoPanelRecoveryHint hint =
                System.Text.Json.JsonSerializer.Deserialize<
                    Tripo.HostUi.TripoPanelRecoveryHint>(
                    File.ReadAllText(hintPath),
                    Tripo.Bridge.BridgeJson.Options)!;
            Assert.Equal(prepared.OperationId, hint.Generation?.OperationId);
            Assert.Equal("task_image456", hint.Generation?.TaskId);
            Assert.True(hint.Generation?.TaskIdDurable);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LostImageResponseRestartsBlockedWithoutReplacementUuid()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "tripo-image-recovery-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string operationId;
        try
        {
            TripoPanelSessionTests.FakeHostControlClient firstClient = new()
            {
                FailFirstTextResponse = true,
            };
            await using (Tripo.HostUi.TripoPanelSession first =
                         new(
                             new TestConnector(firstClient),
                             new Tripo.HostUi.TripoPanelRecoveryStore(
                                 "rhino",
                                 root)))
            {
                await first.ConnectAsync();
                operationId = first.PrepareImageGeneration(
                        ValidImage(),
                        15_000,
                        withMaterials: false)
                    .OperationId;

                await Assert.ThrowsAsync<
                    Tripo.Bridge.HostControlCallException>(
                    () => first.DispatchPreparedImageGenerationAsync(
                        userConfirmedExternalCost: true));

                Assert.Equal(1, firstClient.CreateImageCalls);
                Assert.Equal(
                    operationId,
                    Assert.Single(firstClient.ImageRequests).OperationId);
            }

            TripoPanelSessionTests.FakeHostControlClient restartClient = new();
            await using Tripo.HostUi.TripoPanelSession restarted =
                new(
                    new TestConnector(restartClient),
                    new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root));
            Tripo.HostUi.LoadedTripoPanelRecoveryHint loaded =
                Assert.Single(restarted.Recovery.Hints);
            Assert.Equal(operationId, loaded.Hint.Generation?.OperationId);
            Assert.Null(loaded.Hint.Generation?.TaskId);
            await restarted.ConnectAsync();

            InvalidOperationException blocked = Assert.Throws<
                InvalidOperationException>(
                () => restarted.PrepareImageGeneration(
                    ValidImage(),
                    15_000,
                    withMaterials: false));
            Assert.Contains("reconciliation", blocked.Message);
            Assert.Equal(0, restartClient.CreateImageCalls);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PreparingImageWhileWorkflowIsActiveIsRejected()
    {
        TripoPanelSessionTests.FakeHostControlClient client = new();
        await using Tripo.HostUi.TripoPanelSession session =
            CreateSession(client);
        await session.ConnectAsync();
        session.PrepareGeneration("a chair", 10_000, withMaterials: false);

        InvalidOperationException exception = Assert.Throws<
            InvalidOperationException>(
            () => session.PrepareImageGeneration(
                ValidImage(),
                10_000,
                withMaterials: false));
        Assert.Contains("new workflow", exception.Message);
    }

    [Fact]
    public async Task ResetWorkflowClearsPreparedImageGeneration()
    {
        TripoPanelSessionTests.FakeHostControlClient client = new();
        await using Tripo.HostUi.TripoPanelSession session =
            CreateSession(client);
        await session.ConnectAsync();
        session.PrepareImageGeneration(ValidImage(), 10_000, withMaterials: false);
        Assert.NotNull(session.State.PreparedImageGeneration);

        session.ResetWorkflow();

        Assert.Null(session.State.PreparedImageGeneration);
        Assert.False(session.State.HasWorkflowState);
    }

    [Fact]
    public async Task ImageGenerationTaskIdResolvesForStatusRefresh()
    {
        TripoPanelSessionTests.FakeHostControlClient client = new();
        await using Tripo.HostUi.TripoPanelSession session =
            CreateSession(client);
        await session.ConnectAsync();
        session.PrepareImageGeneration(ValidImage(), 10_000, withMaterials: false);
        await session.DispatchPreparedImageGenerationAsync(
            userConfirmedExternalCost: true);

        await session.RefreshGenerationStatusAsync();

        Assert.Equal(1, client.TaskStatusCalls);
        Assert.Equal(
            "task_image456",
            session.State.GenerationStatus?.TaskId);
        Assert.Equal(
            "image_to_model",
            session.State.GenerationStatus?.Type);
    }

    [Fact]
    public async Task SuccessfulImageGenerationImportsDirectGlbWithOriginalDocumentIdentity()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "tripo-image-recovery-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            TripoPanelSessionTests.FakeHostControlClient client = new();
            await using Tripo.HostUi.TripoPanelSession session =
                new(
                    new TestConnector(client),
                    new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root));
            await session.ConnectAsync();
            Tripo.HostUi.PreparedImageGeneration generation =
                session.PrepareImageGeneration(
                    ValidImage(),
                    10_000,
                    withMaterials: true);
            Assert.Null(session.State.PreparedGeneration);
            Assert.Equal(
                generation,
                session.State.PreparedImageGeneration);
            Tripo.HostUi.DirectGlbAutoImportIntent intent = new(
                sessionGeneration: 1,
                generation.OperationId,
                generation.DocumentSessionId,
                "Image Chair",
                imageGeneration: true);
            await session.DispatchPreparedImageGenerationRequiringCapabilityAsync(
                userConfirmedExternalCost: true,
                requiredHostCapability:
                    Tripo.Bridge.BridgeConstants.ImportGlbMethod,
                requiredSidecarCapability:
                    Tripo.Bridge.HostControlConstants
                        .ImportGenerationGlbMethod);
            await session.RefreshGenerationStatusAsync();
            Assert.Equal(
                Tripo.HostUi.DirectGlbAutoImportDecision.BeginImport,
                intent.ObserveState(
                    sessionGeneration: 1,
                    session.State));

            Tripo.HostUi.PreparedObjImport prepared =
                session.PrepareGlbImport(intent.ObjectName);
            await session.ImportPreparedAsync();
            Assert.True(intent.TryFinishImport(1, session.State));

            Assert.True(prepared.IsDirectGlb);
            Assert.Equal("task_image456", prepared.ConversionTaskId);
            Assert.Equal(
                generation.DocumentSessionId,
                prepared.DocumentSessionId);
            Assert.Equal(
                generation.DocumentSessionId,
                client.LastGlbImportRequest?.DocumentSessionId);
            Assert.Equal(
                prepared.OperationId,
                client.LastGlbImportRequest?.OperationId);
            Assert.Equal(
                "task_image456",
                client.LastGlbImportRequest?.GenerationTaskId);
            Assert.Equal(
                generation.DocumentSessionId,
                session.State.ImportReceipt?.HostReceipt.DocumentSessionId);
            Assert.Equal(1, client.CreateImageCalls);
            Assert.Equal(0, client.CreateConversionCalls);
            Assert.Equal(1, client.GlbImportCalls);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SuccessfulImageGenerationConvertsAndImportsObjWithOriginalDocumentIdentity()
    {
        TripoPanelSessionTests.FakeHostControlClient client = new();
        await using Tripo.HostUi.TripoPanelSession session =
            CreateSession(client);
        await session.ConnectAsync();
        Tripo.HostUi.PreparedImageGeneration generation =
            session.PrepareImageGeneration(
                ValidImage(),
                12_000,
                withMaterials: true);
        await session.DispatchPreparedImageGenerationAsync(
            userConfirmedExternalCost: true);
        await session.RefreshGenerationStatusAsync();

        Tripo.HostUi.PreparedObjConversion conversion =
            session.PrepareConversion(12_000, withMaterials: true);
        Assert.Equal("task_image456", conversion.SourceTaskId);
        Assert.Equal(
            generation.DocumentSessionId,
            conversion.DocumentSessionId);

        await session.DispatchPreparedConversionAsync(
            userConfirmedExternalCost: true);
        Assert.Equal(
            generation.DocumentSessionId,
            client.LastConversionRequest?.DocumentSessionId);
        Assert.Equal(
            conversion.OperationId,
            client.LastConversionRequest?.OperationId);
        Assert.Equal(
            "task_image456",
            client.LastConversionRequest?.SourceTaskId);

        await session.RefreshConversionStatusAsync();
        Tripo.HostUi.PreparedObjImport preparedImport =
            session.PrepareImport(
                "Image Chair",
                "native",
                applyMaterials: true);
        await session.ImportPreparedAsync();

        Assert.Equal(
            generation.DocumentSessionId,
            preparedImport.DocumentSessionId);
        Assert.Equal(
            generation.DocumentSessionId,
            client.LastObjImportRequest?.DocumentSessionId);
        Assert.Equal(
            preparedImport.OperationId,
            client.LastObjImportRequest?.OperationId);
        Assert.Equal(1, client.CreateImageCalls);
        Assert.Equal(1, client.CreateConversionCalls);
        Assert.Equal(1, client.ImportCalls);
    }

    [Fact]
    public async Task NullImageIsRejectedBeforeAnyStateMutation()
    {
        TripoPanelSessionTests.FakeHostControlClient client = new();
        await using Tripo.HostUi.TripoPanelSession session =
            CreateSession(client);
        await session.ConnectAsync();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => Task.FromResult(
                session.PrepareImageGeneration(null!, 10_000, false)));
    }

    [Fact]
    public void SnapshotCleanupPreservesOnlyAdmittedOrAmbiguousImage()
    {
        Tripo.Bridge.StagedImageTransfer image = ValidImage();
        Tripo.HostUi.PreparedImageGeneration prepared = new(
            image,
            10_000,
            false,
            "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
            "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
        Tripo.HostUi.TripoPanelState preparedOnly =
            Tripo.HostUi.TripoPanelState.Initial with
            {
                PreparedImageGeneration = prepared,
            };

        Assert.True(
            Tripo.HostUi.TripoPanelImageSnapshotPolicy
                .CanDeleteUnadmittedSnapshot(
                    image,
                    Tripo.HostUi.TripoPanelState.Initial));
        Assert.True(
            Tripo.HostUi.TripoPanelImageSnapshotPolicy
                .CanDeleteUnadmittedSnapshot(image, preparedOnly));
        Assert.False(
            Tripo.HostUi.TripoPanelImageSnapshotPolicy
                .CanDeleteUnadmittedSnapshot(
                    image,
                    preparedOnly with { GenerationDispatchAttempted = true }));
        Assert.False(
            Tripo.HostUi.TripoPanelImageSnapshotPolicy
                .CanDeleteUnadmittedSnapshot(
                    image,
                    preparedOnly with
                    {
                        ImageGenerationReceipt =
                            new Tripo.Bridge
                                .HostControlImageTaskCreationReceipt(
                                    prepared.OperationId,
                                    "task_image456",
                                    "v3",
                                    image.Sha256),
                    }));
        Assert.True(
            Tripo.HostUi.TripoPanelImageSnapshotPolicy
                .CanDeleteUnadmittedSnapshot(
                    image with
                    {
                        TransferId =
                            "cccccccc-cccc-4ccc-8ccc-cccccccccccc",
                    },
                    preparedOnly with { GenerationDispatchAttempted = true }));
    }

    private sealed class TestConnector :
        Tripo.Bridge.IHostSidecarConnector
    {
        private readonly Tripo.Bridge.IHostControlClient _client;

        public TestConnector(Tripo.Bridge.IHostControlClient client)
        {
            _client = client;
        }

        public Task<Tripo.Bridge.IHostControlClient> EnsureConnectedAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(_client);
    }
}
