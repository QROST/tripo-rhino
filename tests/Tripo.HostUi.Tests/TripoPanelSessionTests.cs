using Xunit;

namespace Tripo.HostUi.Tests;

public sealed class TripoPanelSessionTests
{
    private const string RhinoObjectId =
        "44444444-4444-4444-8444-444444444444";

    [Fact]
    public async Task UnconfirmedGenerationMakesNoPaidCallButDisplaysOperationId()
    {
        FakeHostControlClient client = new();
        await using Tripo.HostUi.TripoPanelSession session = CreateSession(client);
        await session.ConnectAsync();

        Tripo.HostUi.PreparedTextGeneration prepared =
            session.PrepareGeneration(
                "a mass timber transit pavilion",
                20_000,
                withMaterials: true);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.DispatchPreparedGenerationAsync(
                userConfirmedExternalCost: false));

        Assert.Equal(prepared.OperationId, session.State.PreparedGeneration?.OperationId);
        Assert.Equal(0, client.CreateTextCalls);
    }

    [Fact]
    public async Task LostGenerationResponseRetriesTheSameOperationIdentity()
    {
        FakeHostControlClient client = new()
        {
            FailFirstTextResponse = true,
        };
        await using Tripo.HostUi.TripoPanelSession session = CreateSession(client);
        await session.ConnectAsync();
        Tripo.HostUi.PreparedTextGeneration prepared =
            session.PrepareGeneration("a chair", 10_000, withMaterials: false);
        client.OperationStatus =
            ResumableOperationStatus(prepared.OperationId);

        await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
            () => session.DispatchPreparedGenerationAsync(
                userConfirmedExternalCost: true));
        InvalidOperationException earlyRetry =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.DispatchPreparedGenerationAsync(
                    userConfirmedExternalCost: true));
        Assert.Contains("Refresh", earlyRetry.Message);
        Assert.Equal(1, client.CreateTextCalls);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.RefreshGenerationStatusAsync());
        Assert.True(session.State.GenerationRetryAllowed);
        await session.DispatchPreparedGenerationAsync(
            userConfirmedExternalCost: true);

        Assert.Equal(2, client.CreateTextCalls);
        Assert.All(
            client.TextRequests,
            request => Assert.Equal(prepared.OperationId, request.OperationId));
        Assert.Equal(
            prepared,
            session.State.PreparedGeneration);
        Assert.Equal(
            "task_source123",
            session.State.GenerationReceipt?.TaskId);
    }

    [Fact]
    public async Task DocumentSwitchStopsPaidDispatchBeforeTheSidecarCreateCall()
    {
        FakeHostControlClient client = new();
        await using Tripo.HostUi.TripoPanelSession session = CreateSession(client);
        await session.ConnectAsync();
        session.PrepareGeneration("a chair", 10_000, withMaterials: false);
        client.CurrentSessionId = Guid.NewGuid().ToString("D");

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.DispatchPreparedGenerationAsync(
                    userConfirmedExternalCost: true));

        Assert.Contains("active host document changed", exception.Message);
        Assert.Equal(0, client.CreateTextCalls);
    }

    [Fact]
    public async Task ReconnectCannotMoveExistingWorkflowToAnotherDocument()
    {
        FakeHostControlClient client = new();
        await using Tripo.HostUi.TripoPanelSession session = CreateSession(client);
        await session.ConnectAsync();
        string originalSessionId = session.State.Context!.DocumentSessionId;
        session.PrepareGeneration("a chair", 10_000, withMaterials: false);
        client.CurrentSessionId = Guid.NewGuid().ToString("D");

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.ConnectAsync());

        Assert.Contains("document changed", exception.Message);
        Assert.Equal(
            originalSessionId,
            session.State.Context?.DocumentSessionId);
    }

    [Fact]
    public async Task UnresolvedPaidDispatchCannotBeReset()
    {
        FakeHostControlClient client = new()
        {
            FailFirstTextResponse = true,
        };
        await using Tripo.HostUi.TripoPanelSession session = CreateSession(client);
        await session.ConnectAsync();
        Tripo.HostUi.PreparedTextGeneration prepared =
            session.PrepareGeneration("a chair", 10_000, withMaterials: false);

        await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
            () => session.DispatchPreparedGenerationAsync(
                userConfirmedExternalCost: true));
        InvalidOperationException resetError =
            Assert.Throws<InvalidOperationException>(session.ResetWorkflow);

        Assert.True(session.State.HasUnresolvedPaidDispatch);
        Assert.Contains("unresolved", resetError.Message);
        Assert.Equal(
            prepared.OperationId,
            session.State.PreparedGeneration?.OperationId);
    }

    [Fact]
    public async Task RunningDurableTaskCannotBeResetOrLoseRecoveryIdentity()
    {
        string root = CreateTemporaryRoot();
        try
        {
            FakeHostControlClient client = new()
            {
                TaskStatusValue = "running",
            };
            await using Tripo.HostUi.TripoPanelSession session =
                new(
                    new FakeConnector(client),
                    new Tripo.HostUi.TripoPanelRecoveryStore(
                        "rhino",
                        root));
            await session.ConnectAsync();
            Tripo.HostUi.PreparedTextGeneration prepared =
                session.PrepareGeneration(
                    "a chair",
                    10_000,
                    withMaterials: false);
            await session.DispatchPreparedGenerationAsync(
                userConfirmedExternalCost: true);
            await session.RefreshGenerationStatusAsync();
            string recoveryFile = Assert.Single(
                Directory.GetFiles(
                    Path.Combine(root, "ui-recovery", "rhino"),
                    "*.json"));

            InvalidOperationException resetError =
                Assert.Throws<InvalidOperationException>(
                    session.ResetWorkflow);

            Assert.True(session.State.HasUnconfirmedTerminalPaidTask);
            Assert.False(session.State.CanResetWorkflow);
            Assert.Contains("terminal status", resetError.Message);
            Assert.Equal(
                prepared.OperationId,
                session.State.PreparedGeneration?.OperationId);
            Assert.True(File.Exists(recoveryFile));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UnresolvedPaidDispatchAllowsOnlySessionRecoveryKey()
    {
        FakeHostControlClient client = new()
        {
            FailFirstTextResponse = true,
        };
        await using Tripo.HostUi.TripoPanelSession session = CreateSession(client);
        await session.ConnectAsync();
        session.PrepareGeneration("a chair", 10_000, withMaterials: false);
        await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
            () => session.DispatchPreparedGenerationAsync(
                userConfirmedExternalCost: true));

        InvalidOperationException persistError =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.SetApiKeyAsync(
                    "same-account-key",
                    persist: true));
        InvalidOperationException clearError =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.ClearApiKeyAsync());
        await session.SetApiKeyAsync(
            "same-account-key",
            persist: false);

        Assert.Contains("session-only", persistError.Message);
        Assert.Contains("account-bound", clearError.Message);
        Assert.Equal("same-account-key", client.LastApiKey);
        Assert.Equal(0, client.ClearApiKeyCalls);
    }

    [Fact]
    public async Task ClearApiKeyAdoptsReceiptAndReleasesBusyState()
    {
        FakeHostControlClient client = new();
        await using Tripo.HostUi.TripoPanelSession session =
            CreateSession(client);
        await session.ConnectAsync();
        await session.SetApiKeyAsync("stored-key", persist: true);

        await session.ClearApiKeyAsync();

        Assert.Equal(1, client.ClearApiKeyCalls);
        Assert.NotNull(session.State.CredentialStatus);
        Assert.False(session.State.CredentialStatus.HasApiKey);
        Assert.False(session.State.CredentialStatus.StoredKeyPresent);
        Assert.False(session.State.CredentialStatus.CanClearStoredKey);
        Assert.False(session.State.Busy);
        Assert.Null(session.State.LastError);
    }

    [Fact]
    public async Task LocalCredentialFailureClearsFalsePaidDispatchRecovery()
    {
        string root = CreateTemporaryRoot();
        try
        {
            FakeHostControlClient client = new()
            {
                FailFirstTextResponse = true,
                FirstTextFailureCode =
                    Tripo.Bridge.HostControlConstants.CredentialInvalidError,
            };
            await using Tripo.HostUi.TripoPanelSession session =
                new(
                    new FakeConnector(client),
                    new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root));
            await session.ConnectAsync();
            Tripo.HostUi.PreparedTextGeneration prepared =
                session.PrepareGeneration(
                    "a chair",
                    10_000,
                    withMaterials: false);

            await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
                () => session.DispatchPreparedGenerationAsync(
                    userConfirmedExternalCost: true));

            Assert.False(session.State.GenerationDispatchAttempted);
            Assert.False(session.State.HasUnresolvedPaidDispatch);
            Assert.Equal(
                prepared.OperationId,
                session.State.PreparedGeneration?.OperationId);
            Assert.Empty(
                Directory.GetFiles(
                    Path.Combine(root, "ui-recovery", "rhino"),
                    "*.json"));

            await session.SetApiKeyAsync("replacement-key", persist: true);
            await session.DispatchPreparedGenerationAsync(
                userConfirmedExternalCost: true);

            Assert.Equal("replacement-key", client.LastApiKey);
            Assert.Equal(2, client.CreateTextCalls);
            Assert.Equal(
                "task_source123",
                session.State.GenerationReceipt?.TaskId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CredentialFailureDuringRetryPreservesExistingRecovery()
    {
        string root = CreateTemporaryRoot();
        try
        {
            FakeHostControlClient client = new()
            {
                FailFirstTextResponse = true,
            };
            Tripo.HostUi.TripoPanelRecoveryStore store =
                new("rhino", root);
            await using Tripo.HostUi.TripoPanelSession session =
                new(new FakeConnector(client), store);
            await session.ConnectAsync();
            Tripo.HostUi.PreparedTextGeneration prepared =
                session.PrepareGeneration(
                    "a chair",
                    10_000,
                    withMaterials: false);
            client.OperationStatus =
                ResumableOperationStatus(prepared.OperationId);

            await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
                () => session.DispatchPreparedGenerationAsync(
                    userConfirmedExternalCost: true));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.RefreshGenerationStatusAsync());
            client.TextFailureCode =
                Tripo.Bridge.HostControlConstants.CredentialInvalidError;

            await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
                () => session.DispatchPreparedGenerationAsync(
                    userConfirmedExternalCost: true));
            InvalidOperationException persistError =
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => session.SetApiKeyAsync(
                        "replacement-key",
                        persist: true));
            await session.SetApiKeyAsync(
                "replacement-key",
                persist: false);

            Assert.True(session.State.GenerationDispatchAttempted);
            Assert.True(session.State.HasUnresolvedPaidDispatch);
            Assert.True(session.State.GenerationRetryAllowed);
            Assert.Equal(
                prepared.OperationId,
                session.State.PreparedGeneration?.OperationId);
            Assert.Contains("session-only", persistError.Message);
            Assert.Equal("replacement-key", client.LastApiKey);
            Assert.True(store.LoadCredentialMutationBlocks().HasBlock);
            Assert.Single(
                Directory.GetFiles(
                    Path.Combine(root, "ui-recovery", "rhino"),
                    "*.json"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ProviderCredentialRejectionStartsANewGenerationIdentity()
    {
        string root = CreateTemporaryRoot();
        try
        {
            FakeHostControlClient client = new()
            {
                FailFirstTextResponse = true,
                FirstTextFailureCode =
                    Tripo.Bridge.HostControlConstants.CredentialRejectedError,
            };
            await using Tripo.HostUi.TripoPanelSession session =
                new(
                    new FakeConnector(client),
                    new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root));
            await session.ConnectAsync();
            Tripo.HostUi.PreparedTextGeneration rejected =
                session.PrepareGeneration(
                    "a chair",
                    10_000,
                    withMaterials: false);
            client.OperationStatus =
                RejectedOperationStatus(
                    rejected.OperationId,
                    "text_task_creation");

            await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
                () => session.DispatchPreparedGenerationAsync(
                    userConfirmedExternalCost: true));

            Assert.Null(session.State.PreparedGeneration);
            Assert.False(session.State.HasUnresolvedPaidDispatch);
            Assert.Empty(
                Directory.GetFiles(
                    Path.Combine(root, "ui-recovery", "rhino"),
                    "*.json"));
            await session.SetApiKeyAsync("replacement-key", persist: false);
            Tripo.HostUi.PreparedTextGeneration replacement =
                session.PrepareGeneration(
                    "a chair",
                    10_000,
                    withMaterials: false);
            await session.DispatchPreparedGenerationAsync(
                userConfirmedExternalCost: true);

            Assert.NotEqual(rejected.OperationId, replacement.OperationId);
            Assert.Equal(2, client.CreateTextCalls);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LostResponseRefreshClearsDurableGenerationRejection()
    {
        string root = CreateTemporaryRoot();
        try
        {
            FakeHostControlClient client = new()
            {
                FailFirstTextResponse = true,
            };
            Tripo.HostUi.TripoPanelRecoveryStore store =
                new("rhino", root);
            await using Tripo.HostUi.TripoPanelSession session =
                new(new FakeConnector(client), store);
            await session.ConnectAsync();
            Tripo.HostUi.PreparedTextGeneration rejected =
                session.PrepareGeneration(
                    "a chair",
                    10_000,
                    withMaterials: false);
            client.OperationStatus =
                RejectedOperationStatus(
                    rejected.OperationId,
                    "text_task_creation");

            await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
                () => session.DispatchPreparedGenerationAsync(
                    userConfirmedExternalCost: true));
            Assert.True(store.LoadCredentialMutationBlocks().HasBlock);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.RefreshGenerationStatusAsync());

            Assert.Null(session.State.PreparedGeneration);
            Assert.False(session.State.HasUnresolvedPaidDispatch);
            Assert.False(store.LoadCredentialMutationBlocks().HasBlock);
            Assert.Empty(
                Directory.GetFiles(
                    Path.Combine(root, "ui-recovery", "rhino"),
                    "*.json"));
            await session.SetApiKeyAsync("replacement-key", persist: false);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MismatchedRejectedStatusCannotUnlockGeneration()
    {
        FakeHostControlClient client = new()
        {
            FailFirstTextResponse = true,
        };
        await using Tripo.HostUi.TripoPanelSession session = CreateSession(client);
        await session.ConnectAsync();
        Tripo.HostUi.PreparedTextGeneration prepared =
            session.PrepareGeneration("a chair", 10_000, withMaterials: false);
        client.OperationStatus =
            RejectedOperationStatus(
                prepared.OperationId,
                "obj_conversion_creation");

        await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
            () => session.DispatchPreparedGenerationAsync(
                userConfirmedExternalCost: true));
        await Assert.ThrowsAsync<InvalidDataException>(
            () => session.RefreshGenerationStatusAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.SetApiKeyAsync("replacement-key", persist: true));
        await session.SetApiKeyAsync(
            "replacement-key",
            persist: false);

        Assert.Equal(
            prepared.OperationId,
            session.State.PreparedGeneration?.OperationId);
        Assert.True(session.State.HasUnresolvedPaidDispatch);
        Assert.Equal("replacement-key", client.LastApiKey);
    }

    [Fact]
    public async Task LostConversionResponseRefreshPreservesGeneration()
    {
        string root = CreateTemporaryRoot();
        try
        {
            FakeHostControlClient client = new();
            Tripo.HostUi.TripoPanelRecoveryStore store =
                new("rhino", root);
            await using Tripo.HostUi.TripoPanelSession session =
                new(new FakeConnector(client), store);
            await session.ConnectAsync();
            session.PrepareGeneration("a chair", 10_000, withMaterials: false);
            await session.DispatchPreparedGenerationAsync(
                userConfirmedExternalCost: true);
            await session.RefreshGenerationStatusAsync();
            Tripo.HostUi.PreparedObjConversion rejected =
                session.PrepareConversion(10_000, withMaterials: false);
            client.FailFirstConversionResponse = true;
            client.OperationStatus =
                RejectedOperationStatus(
                    rejected.OperationId,
                    "obj_conversion_creation");

            await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
                () => session.DispatchPreparedConversionAsync(
                    userConfirmedExternalCost: true));
            Assert.True(store.LoadCredentialMutationBlocks().HasBlock);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.RefreshConversionStatusAsync());

            Assert.NotNull(session.State.GenerationReceipt);
            Assert.NotNull(session.State.GenerationStatus);
            Assert.Null(session.State.PreparedConversion);
            Assert.False(session.State.HasUnresolvedPaidDispatch);
            Assert.True(store.LoadCredentialMutationBlocks().HasBlock);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.SetApiKeyAsync(
                    "replacement-key",
                    persist: true));
            await session.SetApiKeyAsync("replacement-key", persist: false);
            Tripo.HostUi.PreparedObjConversion replacement =
                session.PrepareConversion(10_000, withMaterials: false);
            await session.DispatchPreparedConversionAsync(
                userConfirmedExternalCost: true);

            Assert.NotEqual(rejected.OperationId, replacement.OperationId);
            Assert.Equal(2, client.CreateConversionCalls);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ProviderCredentialRejectionClearsOnlyConversionStage()
    {
        string root = CreateTemporaryRoot();
        try
        {
            FakeHostControlClient client = new();
            Tripo.HostUi.TripoPanelRecoveryStore store =
                new("rhino", root);
            await using Tripo.HostUi.TripoPanelSession session =
                new(new FakeConnector(client), store);
            await session.ConnectAsync();
            session.PrepareGeneration("a chair", 10_000, withMaterials: false);
            await session.DispatchPreparedGenerationAsync(
                userConfirmedExternalCost: true);
            await session.RefreshGenerationStatusAsync();
            Tripo.HostUi.PreparedObjConversion rejected =
                session.PrepareConversion(10_000, withMaterials: false);
            client.FailFirstConversionResponse = true;
            client.FirstConversionFailureCode =
                Tripo.Bridge.HostControlConstants.CredentialRejectedError;
            client.OperationStatus =
                RejectedOperationStatus(
                    rejected.OperationId,
                    "obj_conversion_creation");

            await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
                () => session.DispatchPreparedConversionAsync(
                    userConfirmedExternalCost: true));

            Assert.NotNull(session.State.GenerationReceipt);
            Assert.NotNull(session.State.GenerationStatus);
            Assert.Null(session.State.PreparedConversion);
            Assert.False(session.State.HasUnresolvedPaidDispatch);
            Assert.True(store.LoadCredentialMutationBlocks().HasBlock);
            string recoveryFile = Assert.Single(
                Directory.GetFiles(
                    Path.Combine(root, "ui-recovery", "rhino"),
                    "*.json"));
            Assert.DoesNotContain(
                rejected.OperationId,
                File.ReadAllText(recoveryFile),
                StringComparison.Ordinal);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.SetApiKeyAsync(
                    "replacement-key",
                    persist: true));
            await session.SetApiKeyAsync("replacement-key", persist: false);
            Tripo.HostUi.PreparedObjConversion replacement =
                session.PrepareConversion(10_000, withMaterials: false);
            await session.DispatchPreparedConversionAsync(
                userConfirmedExternalCost: true);

            Assert.NotEqual(rejected.OperationId, replacement.OperationId);
            Assert.Equal(2, client.CreateConversionCalls);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CancellationWhileVerifyingCredentialRejectionKeepsRecovery()
    {
        string root = CreateTemporaryRoot();
        try
        {
            FakeHostControlClient client = new()
            {
                FailFirstTextResponse = true,
                FirstTextFailureCode =
                    Tripo.Bridge.HostControlConstants.CredentialRejectedError,
                OperationStatusEntered =
                    new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously),
                ContinueOperationStatus =
                    new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously),
            };
            Tripo.HostUi.TripoPanelRecoveryStore store =
                new("rhino", root);
            await using Tripo.HostUi.TripoPanelSession session =
                new(new FakeConnector(client), store);
            await session.ConnectAsync();
            Tripo.HostUi.PreparedTextGeneration prepared =
                session.PrepareGeneration(
                    "a chair",
                    10_000,
                    withMaterials: false);
            client.OperationStatus =
                RejectedOperationStatus(
                    prepared.OperationId,
                    "text_task_creation");
            using CancellationTokenSource cancellation = new();

            Task dispatch = session.DispatchPreparedGenerationAsync(
                userConfirmedExternalCost: true,
                cancellation.Token);
            await client.OperationStatusEntered.Task.WaitAsync(
                TimeSpan.FromSeconds(5));
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => dispatch);
            Assert.True(session.State.HasUnresolvedPaidDispatch);
            Assert.True(store.LoadCredentialMutationBlocks().HasBlock);
            Assert.Single(
                Directory.GetFiles(
                    Path.Combine(root, "ui-recovery", "rhino"),
                    "*.json"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RefreshReconcilesDurableTaskIdAfterLostResponse()
    {
        FakeHostControlClient client = new()
        {
            FailFirstTextResponse = true,
        };
        await using Tripo.HostUi.TripoPanelSession session = CreateSession(client);
        await session.ConnectAsync();
        Tripo.HostUi.PreparedTextGeneration prepared =
            session.PrepareGeneration("a chair", 10_000, withMaterials: false);
        client.OperationStatus =
            DurableOperationStatus(prepared.OperationId, "task_source123");

        await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
            () => session.DispatchPreparedGenerationAsync(
                userConfirmedExternalCost: true));
        await session.RefreshGenerationStatusAsync();

        Assert.True(session.State.GenerationOperationStatus?.TaskIdDurable);
        Assert.Equal(
            "task_source123",
            session.State.GenerationStatus?.TaskId);
        Assert.False(session.State.HasUnresolvedPaidDispatch);
    }

    [Fact]
    public async Task GenerationAndConversionUseSeparateConfirmationsAndUuids()
    {
        FakeHostControlClient client = new();
        await using Tripo.HostUi.TripoPanelSession session = CreateSession(client);
        await session.ConnectAsync();
        Tripo.HostUi.PreparedTextGeneration generation =
            session.PrepareGeneration("a chair", 10_000, withMaterials: true);
        await session.DispatchPreparedGenerationAsync(
            userConfirmedExternalCost: true);
        await session.RefreshGenerationStatusAsync();
        Tripo.HostUi.PreparedObjConversion conversion =
            session.PrepareConversion(10_000, withMaterials: true);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.DispatchPreparedConversionAsync(
                userConfirmedExternalCost: false));
        Assert.Equal(0, client.CreateConversionCalls);

        await session.DispatchPreparedConversionAsync(
            userConfirmedExternalCost: true);

        Assert.NotEqual(generation.OperationId, conversion.OperationId);
        Assert.True(client.LastTextRequest?.ConfirmExternalCost);
        Assert.True(client.LastConversionRequest?.ConfirmExternalCost);
        Assert.Equal(1, client.CreateTextCalls);
        Assert.Equal(1, client.CreateConversionCalls);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.DispatchPreparedConversionAsync(
                userConfirmedExternalCost: true));
        Assert.Equal(1, client.CreateConversionCalls);
    }

    [Fact]
    public async Task CompletedImportCannotEnterTheConnectorAgain()
    {
        FakeHostControlClient client = new();
        await using Tripo.HostUi.TripoPanelSession session = CreateSession(client);
        await session.ConnectAsync();
        session.PrepareGeneration("a chair", 10_000, withMaterials: true);
        await session.DispatchPreparedGenerationAsync(
            userConfirmedExternalCost: true);
        await session.RefreshGenerationStatusAsync();
        session.PrepareConversion(10_000, withMaterials: true);
        await session.DispatchPreparedConversionAsync(
            userConfirmedExternalCost: true);
        await session.RefreshConversionStatusAsync();
        session.PrepareImport("Chair", "native", applyMaterials: true);

        await session.ImportPreparedAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.ImportPreparedAsync());

        Assert.Equal(1, client.ImportCalls);
    }

    [Fact]
    public async Task CredentialValueNeverEntersPanelState()
    {
        const string secret = "ui-secret-value";
        FakeHostControlClient client = new();
        await using Tripo.HostUi.TripoPanelSession session = CreateSession(client);
        await session.ConnectAsync();

        await session.SetApiKeyAsync(secret, persist: true);
        string stateJson = System.Text.Json.JsonSerializer.Serialize(
            session.State,
            Tripo.Bridge.BridgeJson.Options);

        Assert.Equal(secret, client.LastApiKey);
        Assert.DoesNotContain(secret, stateJson, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, session.State.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void StageDispatchStateDistinguishesFirstSendRetryAndCompletion()
    {
        const string documentSessionId =
            "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
        Tripo.HostUi.PreparedTextGeneration generation = new(
            "a chair",
            10_000,
            false,
            documentSessionId,
            "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
        Tripo.HostUi.TripoPanelState firstSend =
            Tripo.HostUi.TripoPanelState.Initial with
            {
                PreparedGeneration = generation,
            };

        Assert.True(firstSend.CanDispatchPreparedGeneration);
        Assert.False(firstSend.GenerationRetryRequired);
        Assert.False(firstSend.HasDurableGenerationTask);

        Tripo.HostUi.TripoPanelState retry = firstSend with
        {
            GenerationDispatchAttempted = true,
        };
        Assert.True(retry.CanDispatchPreparedGeneration);
        Assert.True(retry.GenerationRetryRequired);
        Assert.False(retry.GenerationRetryAllowed);

        retry = retry with
        {
            GenerationOperationStatus =
                ResumableOperationStatus(generation.OperationId),
        };
        Assert.True(retry.GenerationRetryAllowed);

        Tripo.HostUi.TripoPanelState complete = retry with
        {
            GenerationReceipt =
                new Tripo.Bridge.HostControlTextTaskCreationReceipt(
                    generation.OperationId,
                    "task_source123",
                    "v3.1-20260211"),
        };
        Assert.False(complete.CanDispatchPreparedGeneration);
        Assert.False(complete.GenerationRetryRequired);
        Assert.True(complete.HasDurableGenerationTask);

        Tripo.HostUi.PreparedObjImport import = new(
            "task_conversion123",
            "Chair",
            documentSessionId,
            "cccccccc-cccc-4ccc-8ccc-cccccccccccc",
            "native",
            true);
        Tripo.HostUi.TripoPanelState importRetry = complete with
        {
            PreparedImport = import,
            ImportDispatchAttempted = true,
        };
        Assert.True(importRetry.CanDispatchPreparedImport);
        Assert.True(importRetry.ImportRetryRequired);

        Tripo.HostUi.TripoPanelState importUncertain = importRetry with
        {
            ImportFailureCode =
                Tripo.Bridge.BridgeConstants.MutationStateUncertainError,
        };
        Assert.False(importUncertain.CanDispatchPreparedImport);
        Assert.False(importUncertain.ImportRetryRequired);
        Assert.True(importUncertain.ImportRequiresManualReview);

        Tripo.HostUi.TripoPanelState importComplete = importRetry with
        {
            ImportReceipt = new Tripo.Bridge.HostControlObjTaskImportReceipt(
                import.OperationId,
                import.ConversionTaskId,
                null,
                new Tripo.Bridge.HostImportReceipt(
                    "revit",
                    documentSessionId,
                    import.OperationId,
                    RhinoObjectId,
                    1,
                    1,
                    0,
                    "committed",
                    import.ImportMode,
                    1,
                    1,
                    null)),
        };
        Assert.False(importComplete.CanDispatchPreparedImport);
        Assert.False(importComplete.ImportRetryRequired);
    }

    [Fact]
    public async Task CompletedGenerationCannotEnterTheConnectorAgain()
    {
        FakeHostControlClient client = new();
        await using Tripo.HostUi.TripoPanelSession session = CreateSession(client);
        await session.ConnectAsync();
        session.PrepareGeneration("a chair", 10_000, withMaterials: false);
        await session.DispatchPreparedGenerationAsync(
            userConfirmedExternalCost: true);

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.DispatchPreparedGenerationAsync(
                    userConfirmedExternalCost: true));

        Assert.Contains("durable task ID", exception.Message);
        Assert.Equal(1, client.CreateTextCalls);
    }

    [Fact]
    public async Task ResetIsRejectedWhilePaidDispatchIsInFlight()
    {
        string root = CreateTemporaryRoot();
        try
        {
            FakeHostControlClient client = new();
            await using Tripo.HostUi.TripoPanelSession session =
                new(
                    new FakeConnector(client),
                    new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root));
            await session.ConnectAsync();
            Tripo.HostUi.PreparedTextGeneration prepared =
                session.PrepareGeneration(
                    "a chair",
                    10_000,
                    withMaterials: false);
            TaskCompletionSource<bool> hostContextEntered =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> continueHostContext =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            client.HostContextEntered = hostContextEntered;
            client.ContinueHostContext = continueHostContext;

            Task dispatch = session.DispatchPreparedGenerationAsync(
                userConfirmedExternalCost: true);
            await hostContextEntered.Task.WaitAsync(
                TimeSpan.FromSeconds(5));

            try
            {
                InvalidOperationException blocked =
                    Assert.Throws<InvalidOperationException>(
                        session.ResetWorkflow);
                Assert.Contains("current panel operation", blocked.Message);
            }
            finally
            {
                continueHostContext.TrySetResult(true);
            }

            await dispatch;
            Assert.Equal(
                prepared.OperationId,
                session.State.GenerationReceipt?.OperationId);
            string hint = File.ReadAllText(
                Assert.Single(
                    Directory.GetFiles(
                        Path.Combine(root, "ui-recovery", "rhino"),
                        "*.json")));
            Assert.Contains(
                prepared.OperationId,
                hint,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ResetIsRejectedWhileStatusRefreshIsInFlight()
    {
        FakeHostControlClient client = new();
        await using Tripo.HostUi.TripoPanelSession session = CreateSession(client);
        await session.ConnectAsync();
        session.PrepareGeneration("a chair", 10_000, withMaterials: false);
        await session.DispatchPreparedGenerationAsync(
            userConfirmedExternalCost: true);
        TaskCompletionSource<bool> taskStatusEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> continueTaskStatus =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        client.TaskStatusEntered = taskStatusEntered;
        client.ContinueTaskStatus = continueTaskStatus;

        Task refresh = session.RefreshGenerationStatusAsync();
        await taskStatusEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            InvalidOperationException blocked =
                Assert.Throws<InvalidOperationException>(session.ResetWorkflow);
            Assert.Contains("current panel operation", blocked.Message);
        }
        finally
        {
            continueTaskStatus.TrySetResult(true);
        }

        await refresh;
        Assert.NotNull(session.State.PreparedGeneration);
        Assert.Equal("success", session.State.GenerationStatus?.Status);
    }

    [Fact]
    public async Task RecoveryLockContentionFailsBeforeConnectorAndReleasesGate()
    {
        string root = CreateTemporaryRoot();
        try
        {
            FakeHostControlClient client = new();
            await using Tripo.HostUi.TripoPanelSession session =
                new(
                    new FakeConnector(client),
                    new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root));
            await session.ConnectAsync();
            session.PrepareGeneration(
                "a chair",
                10_000,
                withMaterials: false);
            string lockPath = Path.Combine(
                root,
                "ui-recovery",
                "rhino",
                ".recovery.lock");

            using (FileStream heldLock = new(
                       lockPath,
                       FileMode.OpenOrCreate,
                       FileAccess.ReadWrite,
                       FileShare.None))
            {
                Tripo.Bridge.BridgePaths.SetPrivateFileMode(lockPath);
                await Assert.ThrowsAsync<IOException>(
                    () => session.DispatchPreparedGenerationAsync(
                        userConfirmedExternalCost: true));
            }

            Assert.Equal(0, client.CreateTextCalls);
            Assert.False(session.State.Busy);
            await session.DispatchPreparedGenerationAsync(
                userConfirmedExternalCost: true);
            Assert.Equal(1, client.CreateTextCalls);
            Assert.NotNull(session.State.GenerationReceipt);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CredentialMutationLeaseBlocksPaidDispatchUntilMutationCompletes()
    {
        string root = CreateTemporaryRoot();
        try
        {
            TaskCompletionSource<bool> credentialMutationEntered =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> continueCredentialMutation =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            FakeHostControlClient credentialClient = new()
            {
                Host = "rhino",
                CredentialMutationEntered = credentialMutationEntered,
                ContinueCredentialMutation = continueCredentialMutation,
            };
            FakeHostControlClient dispatchClient = new()
            {
                Host = "revit",
            };
            await using Tripo.HostUi.TripoPanelSession credentialSession =
                new(
                    new FakeConnector(credentialClient),
                    new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root));
            await using Tripo.HostUi.TripoPanelSession dispatchSession =
                new(
                    new FakeConnector(dispatchClient),
                    new Tripo.HostUi.TripoPanelRecoveryStore("revit", root));
            await credentialSession.ConnectAsync();
            await dispatchSession.ConnectAsync();
            dispatchSession.PrepareGeneration(
                "a chair",
                10_000,
                withMaterials: false);

            Task mutation = credentialSession.SetApiKeyAsync(
                "replacement-key",
                persist: false);
            await credentialMutationEntered.Task.WaitAsync(
                TimeSpan.FromSeconds(5));

            try
            {
                await Assert.ThrowsAsync<IOException>(
                    () => dispatchSession.DispatchPreparedGenerationAsync(
                        userConfirmedExternalCost: true));
                Assert.Equal(0, dispatchClient.CreateTextCalls);
                Assert.False(dispatchSession.State.Busy);
            }
            finally
            {
                continueCredentialMutation.TrySetResult(true);
            }

            await mutation;
            await dispatchSession.DispatchPreparedGenerationAsync(
                userConfirmedExternalCost: true);
            Assert.Equal(1, dispatchClient.CreateTextCalls);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PaidWorkflowBlocksSiblingCredentialMutationUntilCleared()
    {
        string root = CreateTemporaryRoot();
        try
        {
            FakeHostControlClient dispatchClient = new()
            {
                Host = "rhino",
            };
            FakeHostControlClient credentialClient = new()
            {
                Host = "revit",
            };
            await using Tripo.HostUi.TripoPanelSession dispatchSession =
                new(
                    new FakeConnector(dispatchClient),
                    new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root));
            await using Tripo.HostUi.TripoPanelSession credentialSession =
                new(
                    new FakeConnector(credentialClient),
                    new Tripo.HostUi.TripoPanelRecoveryStore("revit", root));
            await dispatchSession.ConnectAsync();
            await credentialSession.ConnectAsync();
            TaskCompletionSource<bool> hostContextEntered =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> continueHostContext =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            dispatchClient.HostContextEntered = hostContextEntered;
            dispatchClient.ContinueHostContext = continueHostContext;
            dispatchSession.PrepareGeneration(
                "a chair",
                10_000,
                withMaterials: false);

            Task dispatch = dispatchSession.DispatchPreparedGenerationAsync(
                userConfirmedExternalCost: true);
            await hostContextEntered.Task.WaitAsync(
                TimeSpan.FromSeconds(5));

            try
            {
                await Assert.ThrowsAsync<IOException>(
                    () => credentialSession.SetApiKeyAsync(
                        "replacement-key",
                        persist: false));
                Assert.Null(credentialClient.LastApiKey);
                Assert.False(credentialSession.State.Busy);
            }
            finally
            {
                continueHostContext.TrySetResult(true);
            }

            await dispatch;
            InvalidOperationException acceptedTaskBlock =
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => credentialSession.SetApiKeyAsync(
                        "replacement-key",
                        persist: false));
            Assert.Contains(
                "another Rhino/Revit panel",
                acceptedTaskBlock.Message);
            await dispatchSession.RefreshGenerationStatusAsync();
            await dispatchSession.SetApiKeyAsync(
                "owner-terminal-key",
                persist: false);
            Assert.Equal(
                "owner-terminal-key",
                dispatchClient.LastApiKey);
            dispatchSession.ResetWorkflow();
            await credentialSession.SetApiKeyAsync(
                "replacement-key",
                persist: false);
            Assert.Equal(
                "replacement-key",
                credentialClient.LastApiKey);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DanglingRecoveryLockSymlinkFailsClosed()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string root = CreateTemporaryRoot();
        try
        {
            FakeHostControlClient client = new();
            await using Tripo.HostUi.TripoPanelSession session =
                new(
                    new FakeConnector(client),
                    new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root));
            await session.ConnectAsync();
            session.PrepareGeneration(
                "a chair",
                10_000,
                withMaterials: false);
            string lockPath = Path.Combine(
                root,
                "ui-recovery",
                "rhino",
                ".recovery.lock");
            File.CreateSymbolicLink(
                lockPath,
                Path.Combine(root, "missing", "lock"));

            Assert.Throws<InvalidOperationException>(
                () => session.PrepareGeneration(
                    "a chair",
                    10_000,
                    withMaterials: false));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.SetApiKeyAsync(
                    "session-key",
                    persist: false));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.ClearApiKeyAsync());
            Assert.Null(client.LastApiKey);
            Assert.Equal(0, client.ClearApiKeyCalls);

            File.Delete(lockPath);
            File.CreateSymbolicLink(
                lockPath,
                Path.Combine(root, "missing", "lock"));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.DispatchPreparedGenerationAsync(
                    userConfirmedExternalCost: true));

            Assert.Equal(0, client.CreateTextCalls);
            Assert.False(session.State.Busy);
            File.Delete(lockPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DanglingCredentialWorkflowLockSymlinkFailsClosed()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string root = CreateTemporaryRoot();
        try
        {
            FakeHostControlClient client = new();
            await using Tripo.HostUi.TripoPanelSession session =
                new(
                    new FakeConnector(client),
                    new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root));
            await session.ConnectAsync();
            session.PrepareGeneration(
                "a chair",
                10_000,
                withMaterials: false);
            string lockPath = Path.Combine(
                root,
                "ui-recovery",
                ".credential-workflow.lock");
            File.CreateSymbolicLink(
                lockPath,
                Path.Combine(root, "missing", "credential-workflow-lock"));

            await Assert.ThrowsAsync<InvalidDataException>(
                () => session.SetApiKeyAsync(
                    "session-key",
                    persist: false));
            await Assert.ThrowsAsync<InvalidDataException>(
                () => session.DispatchPreparedGenerationAsync(
                    userConfirmedExternalCost: true));

            Assert.Null(client.LastApiKey);
            Assert.Equal(0, client.CreateTextCalls);
            Assert.False(session.State.Busy);
            File.Delete(lockPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExistingHintPersistenceFailureClearsBusyAndPreservesIdentity()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string root = CreateTemporaryRoot();
        try
        {
            FakeHostControlClient client = new()
            {
                FailFirstTextResponse = true,
            };
            await using Tripo.HostUi.TripoPanelSession session =
                new(
                    new FakeConnector(client),
                    new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root));
            await session.ConnectAsync();
            Tripo.HostUi.PreparedTextGeneration prepared =
                session.PrepareGeneration(
                    "a chair",
                    10_000,
                    withMaterials: false);
            await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
                () => session.DispatchPreparedGenerationAsync(
                    userConfirmedExternalCost: true));
            string recoveryDirectory =
                Path.Combine(root, "ui-recovery", "rhino");
            string hintPath = Assert.Single(
                Directory.GetFiles(recoveryDirectory, "*.json"));
            string originalHint = File.ReadAllText(hintPath);
            string lockPath =
                Path.Combine(recoveryDirectory, ".recovery.lock");
            File.Delete(lockPath);
            File.CreateSymbolicLink(
                lockPath,
                Path.Combine(root, "missing", "lock"));
            client.OperationStatus = DurableOperationStatus(
                prepared.OperationId,
                "task_source123");

            await Assert.ThrowsAsync<InvalidDataException>(
                () => session.RefreshGenerationStatusAsync());

            Assert.False(session.State.Busy);
            Assert.Equal(
                prepared.OperationId,
                session.State.PreparedGeneration?.OperationId);
            Assert.True(session.State.GenerationDispatchAttempted);
            Assert.Equal(originalHint, File.ReadAllText(hintPath));

            File.Delete(lockPath);
            await session.RefreshGenerationStatusAsync();
            Assert.Equal("success", session.State.GenerationStatus?.Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DanglingRecoveryDestinationSymlinkFailsClosed()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string root = CreateTemporaryRoot();
        try
        {
            FakeHostControlClient client = new()
            {
                FailFirstTextResponse = true,
            };
            await using Tripo.HostUi.TripoPanelSession session =
                new(
                    new FakeConnector(client),
                    new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root));
            await session.ConnectAsync();
            Tripo.HostUi.PreparedTextGeneration prepared =
                session.PrepareGeneration(
                    "a chair",
                    10_000,
                    withMaterials: false);
            await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
                () => session.DispatchPreparedGenerationAsync(
                    userConfirmedExternalCost: true));
            string hintPath = Assert.Single(
                Directory.GetFiles(
                    Path.Combine(root, "ui-recovery", "rhino"),
                    "*.json"));
            File.Delete(hintPath);
            File.CreateSymbolicLink(
                hintPath,
                Path.Combine(root, "missing", "hint.json"));
            client.OperationStatus = DurableOperationStatus(
                prepared.OperationId,
                "task_source123");

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.RefreshGenerationStatusAsync());

            Assert.False(session.State.Busy);
            Assert.Equal(
                prepared.OperationId,
                session.State.PreparedGeneration?.OperationId);
            File.Delete(hintPath);
            await session.RefreshGenerationStatusAsync();
            Assert.Equal("success", session.State.GenerationStatus?.Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RecoveryHintExcludesPromptCredentialAndUrlLikeText()
    {
        string root = CreateTemporaryRoot();
        try
        {
            const string secret = "recovery-secret-sentinel";
            const string prompt =
                "Authorization: Bearer prompt-sentinel https://example.invalid";
            FakeHostControlClient client = new()
            {
                FailFirstTextResponse = true,
            };
            string? jsonObservedAtConnectorEntry = null;
            client.BeforeCreateTextCall = _ =>
            {
                string observedFile = Assert.Single(
                    Directory.GetFiles(
                        Path.Combine(root, "ui-recovery", "rhino"),
                        "*.json"));
                jsonObservedAtConnectorEntry =
                    File.ReadAllText(observedFile);
            };
            Tripo.HostUi.TripoPanelRecoveryStore store =
                new("rhino", root);
            await using Tripo.HostUi.TripoPanelSession session =
                new(new FakeConnector(client), store);
            await session.ConnectAsync();
            await session.SetApiKeyAsync(secret, persist: false);
            Tripo.HostUi.PreparedTextGeneration prepared =
                session.PrepareGeneration(
                    prompt,
                    10_000,
                    withMaterials: false);

            await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
                () => session.DispatchPreparedGenerationAsync(
                    userConfirmedExternalCost: true));

            string file = Assert.Single(
                Directory.GetFiles(
                    Path.Combine(root, "ui-recovery", "rhino"),
                    "*.json"));
            string json = File.ReadAllText(file);
            string observedAtConnectorEntry =
                Assert.IsType<string>(jsonObservedAtConnectorEntry);
            Assert.Contains(
                prepared.OperationId,
                observedAtConnectorEntry,
                StringComparison.Ordinal);
            Assert.Contains(prepared.OperationId, json, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
            Assert.DoesNotContain(prompt, json, StringComparison.Ordinal);
            Assert.DoesNotContain("Authorization", json, StringComparison.Ordinal);
            Assert.DoesNotContain("http", json, StringComparison.OrdinalIgnoreCase);
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(file));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RestartRecoveryBlocksNewPaidWorkUntilExplicitReconciliation()
    {
        string root = CreateTemporaryRoot();
        try
        {
            FakeHostControlClient originalClient = new()
            {
                FailFirstTextResponse = true,
            };
            string operationId;
            string documentSessionId;
            await using (Tripo.HostUi.TripoPanelSession original =
                         new(
                             new FakeConnector(originalClient),
                             new Tripo.HostUi.TripoPanelRecoveryStore(
                                 "rhino",
                                 root)))
            {
                await original.ConnectAsync();
                documentSessionId =
                    original.State.Context!.DocumentSessionId;
                operationId = original.PrepareGeneration(
                        "a chair",
                        10_000,
                        withMaterials: false)
                    .OperationId;
                await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
                    () => original.DispatchPreparedGenerationAsync(
                        userConfirmedExternalCost: true));
            }

            MakeRecoveryOwnerStale(root, "rhino");
            FakeHostControlClient restartedClient = new()
            {
                CurrentSessionId = documentSessionId,
            };
            await using Tripo.HostUi.TripoPanelSession restarted =
                new(
                    new FakeConnector(restartedClient),
                    new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root));
            Assert.True(restarted.Recovery.HasBlock);
            Assert.Equal(
                operationId,
                Assert.Single(restarted.Recovery.Hints)
                    .Hint.Generation?.OperationId);

            await restarted.ConnectAsync();
            Assert.True(restarted.Recovery.HasBlock);
            Assert.Single(
                Directory.GetFiles(
                    Path.Combine(root, "ui-recovery", "rhino"),
                    "*.json"));
            InvalidOperationException blocked =
                Assert.Throws<InvalidOperationException>(
                    () => restarted.PrepareGeneration(
                        "a replacement chair",
                        10_000,
                        withMaterials: false));
            Assert.Contains("reconciliation", blocked.Message);
            Assert.Equal(0, restartedClient.CreateTextCalls);
            Tripo.HostUi.TripoPanelRecoveryReviewSnapshot review =
                await restarted.CreateRecoveryReviewSnapshotAsync();
            string recoveryFile = Assert.Single(
                Directory.GetFiles(
                    Path.Combine(root, "ui-recovery", "rhino"),
                    "*.json"));
            byte[] recoveryBytes = File.ReadAllBytes(recoveryFile);
            int operationStatusCalls =
                restartedClient.OperationStatusCalls;
            int recoveryChangedCount = 0;
            restarted.RecoveryChanged +=
                (_, _) => recoveryChangedCount++;
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => restarted.UnlockRecoveredOperationsAsync(
                    userConfirmed: false,
                    review));
            Assert.True(restarted.Recovery.HasBlock);
            Assert.Equal(
                operationStatusCalls,
                restartedClient.OperationStatusCalls);
            Assert.Equal(0, recoveryChangedCount);
            Assert.Equal(recoveryBytes, File.ReadAllBytes(recoveryFile));
            Assert.False(
                Directory.Exists(
                    Path.Combine(
                        root,
                        "ui-recovery",
                        "rhino",
                        "archive")));
            Assert.Single(
                Directory.GetFiles(
                    Path.Combine(root, "ui-recovery", "rhino"),
                    "*.json"));

            await restarted.UnlockRecoveredOperationsAsync(
                userConfirmed: true,
                review);
            Assert.False(restarted.Recovery.HasBlock);
            string archivedFile = Assert.Single(
                Directory.GetFiles(
                    Path.Combine(
                        root,
                        "ui-recovery",
                        "rhino",
                        "archive"),
                    "*.json"));
            Assert.Contains(
                operationId,
                File.ReadAllText(archivedFile),
                StringComparison.Ordinal);
            _ = restarted.PrepareGeneration(
                "a replacement chair",
                10_000,
                withMaterials: false);
            Assert.Empty(
                Directory.GetFiles(
                    Path.Combine(root, "ui-recovery", "rhino"),
                    "*.json"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ChangedRecoverySetMustBeReviewedBeforeUnlock()
    {
        string root = CreateTemporaryRoot();
        try
        {
            await using (Tripo.HostUi.TripoPanelSession owner =
                         new(
                             new FakeConnector(
                                 new FakeHostControlClient
                                 {
                                     FailFirstTextResponse = true,
                                 }),
                             new Tripo.HostUi.TripoPanelRecoveryStore(
                                 "rhino",
                                 root)))
            {
                await owner.ConnectAsync();
                owner.PrepareGeneration(
                    "first chair",
                    10_000,
                    withMaterials: false);
                await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
                    () => owner.DispatchPreparedGenerationAsync(
                        userConfirmedExternalCost: true));
            }

            FakeHostControlClient observerClient = new();
            await using Tripo.HostUi.TripoPanelSession observer =
                new(
                    new FakeConnector(observerClient),
                    new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root));
            Assert.Single(observer.Recovery.Hints);
            await observer.ConnectAsync();
            Tripo.HostUi.TripoPanelRecoveryReviewSnapshot initiallyReviewed =
                await observer.CreateRecoveryReviewSnapshotAsync();
            string firstFile = Assert.Single(
                Directory.GetFiles(
                    Path.Combine(root, "ui-recovery", "rhino"),
                    "*.json"));
            Tripo.HostUi.TripoPanelRecoveryHint firstHint =
                System.Text.Json.JsonSerializer.Deserialize<
                    Tripo.HostUi.TripoPanelRecoveryHint>(
                    File.ReadAllText(firstFile),
                    Tripo.Bridge.BridgeJson.Options)
                ?? throw new InvalidOperationException(
                    "The first recovery hint could not be read.");
            Tripo.HostUi.TripoPanelRecoveryHint secondHint =
                firstHint with
                {
                    RecoveryId = Guid.NewGuid().ToString("D"),
                    DocumentSessionId = Guid.NewGuid().ToString("D"),
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                    Generation = firstHint.Generation! with
                    {
                        OperationId = Guid.NewGuid().ToString("D"),
                    },
                };
            string secondFile = Path.Combine(
                Path.GetDirectoryName(firstFile)!,
                secondHint.RecoveryId + ".json");
            File.WriteAllText(
                secondFile,
                System.Text.Json.JsonSerializer.Serialize(
                    secondHint,
                    CreateStrictRecoveryJsonOptions()));
            Tripo.Bridge.BridgePaths.SetPrivateFileMode(secondFile);
            Tripo.HostUi.TripoPanelRecoveryLoadResult? rendered = null;
            observer.RecoveryChanged += (_, recovery) => rendered = recovery;

            InvalidOperationException changed =
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => observer.UnlockRecoveredOperationsAsync(
                        userConfirmed: true,
                        initiallyReviewed));

            Assert.Contains("list changed", changed.Message);
            Assert.Contains(
                "Review the refreshed list before unlocking again.",
                changed.Message);
            Tripo.HostUi.TripoPanelRecoveryLoadResult renderedSnapshot =
                Assert.IsType<
                    Tripo.HostUi.TripoPanelRecoveryLoadResult>(rendered);
            Assert.Equal(2, renderedSnapshot.Hints.Count);
            Assert.Equal(2, observer.Recovery.Hints.Count);
            Assert.Equal(
                2,
                Directory.GetFiles(
                    Path.Combine(root, "ui-recovery", "rhino"),
                    "*.json").Length);

            Tripo.HostUi.TripoPanelRecoveryReviewSnapshot refreshedReview =
                await observer.CreateRecoveryReviewSnapshotAsync();
            await observer.UnlockRecoveredOperationsAsync(
                userConfirmed: true,
                refreshedReview);
            Assert.Empty(
                Directory.GetFiles(
                    Path.Combine(root, "ui-recovery", "rhino"),
                    "*.json"));
            Assert.Equal(
                2,
                Directory.GetFiles(
                    Path.Combine(
                        root,
                        "ui-recovery",
                        "rhino",
                        "archive"),
                    "*.json").Length);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PaidJournalChangeAfterReviewBlocksUnlock()
    {
        string root = CreateTemporaryRoot();
        try
        {
            (string operationId, string documentSessionId) =
                await CreateLostResponseRecoveryAsync(root);
            FakeHostControlClient client = new()
            {
                CurrentSessionId = documentSessionId,
                OperationStatus = ResumableOperationStatus(operationId),
            };
            await using Tripo.HostUi.TripoPanelSession session =
                new(
                    new FakeConnector(client),
                    new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root));
            await session.ConnectAsync();
            Tripo.HostUi.TripoPanelRecoveryReviewSnapshot reviewed =
                await session.CreateRecoveryReviewSnapshotAsync();
            Assert.Contains(
                "Local state: prepared",
                Tripo.HostUi.TripoPanelRecoveryReviewFormatter.Format(
                    reviewed));

            client.OperationStatus =
                DurableOperationStatus(operationId, "task_changed123");
            InvalidOperationException changed =
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => session.UnlockRecoveredOperationsAsync(
                        userConfirmed: true,
                        reviewed));

            Assert.Contains("status changed", changed.Message);
            Assert.True(session.Recovery.HasBlock);
            Assert.Single(
                Directory.GetFiles(
                    Path.Combine(root, "ui-recovery", "rhino"),
                    "*.json"));

            Tripo.HostUi.TripoPanelRecoveryReviewSnapshot refreshed =
                await session.CreateRecoveryReviewSnapshotAsync();
            int blockedCompetingGateCalls = 0;
            Tripo.Bridge.CredentialWorkflowExecutionGate competingGate =
                new(root);
            client.BeforeOperationStatusCall = () =>
            {
                Tripo.Bridge.BridgeCallException gateBlocked =
                    Assert.Throws<Tripo.Bridge.BridgeCallException>(
                        () =>
                        {
                            using IDisposable unexpected =
                                competingGate.Acquire();
                        });
                Assert.Equal(
                    "credential_workflow_unavailable",
                    gateBlocked.Code);
                blockedCompetingGateCalls++;
            };
            await session.UnlockRecoveredOperationsAsync(
                userConfirmed: true,
                refreshed);
            Assert.False(session.Recovery.HasBlock);
            Assert.Equal(2, blockedCompetingGateCalls);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OperationInProgressCannotBeArchived()
    {
        string root = CreateTemporaryRoot();
        try
        {
            (string operationId, string documentSessionId) =
                await CreateLostResponseRecoveryAsync(root);
            FakeHostControlClient client = new()
            {
                CurrentSessionId = documentSessionId,
                OperationStatus = new(
                    operationId,
                    "text_task_creation",
                    "dispatching",
                    null,
                    null,
                    null,
                    null,
                    TaskIdDurable: false,
                    MayHaveCreatedRemoteTask: true,
                    CanResumeCreation: false,
                    NextAction: "Wait and query again.",
                    UpdatedAtUtc: DateTimeOffset.UtcNow,
                    OperationInProgress: true),
            };
            await using Tripo.HostUi.TripoPanelSession session =
                new(
                    new FakeConnector(client),
                    new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root));
            await session.ConnectAsync();
            Tripo.HostUi.TripoPanelRecoveryReviewSnapshot reviewed =
                await session.CreateRecoveryReviewSnapshotAsync();

            Assert.True(reviewed.HasOperationInProgress);
            InvalidOperationException blocked =
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => session.UnlockRecoveredOperationsAsync(
                        userConfirmed: true,
                        reviewed));
            Assert.Contains("still active", blocked.Message);
            Assert.True(session.Recovery.HasBlock);
            Assert.Single(
                Directory.GetFiles(
                    Path.Combine(root, "ui-recovery", "rhino"),
                    "*.json"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ActiveSidecarExecutionGateBlocksRecoveryArchival()
    {
        string root = CreateTemporaryRoot();
        try
        {
            (string operationId, string documentSessionId) =
                await CreateLostResponseRecoveryAsync(root);
            FakeHostControlClient client = new()
            {
                CurrentSessionId = documentSessionId,
                OperationStatus = ResumableOperationStatus(operationId),
            };
            await using Tripo.HostUi.TripoPanelSession session =
                new(
                    new FakeConnector(client),
                    new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root));
            await session.ConnectAsync();
            Tripo.HostUi.TripoPanelRecoveryReviewSnapshot reviewed =
                await session.CreateRecoveryReviewSnapshotAsync();
            Tripo.Bridge.CredentialWorkflowExecutionGate activeGate =
                new(root);

            using (activeGate.Acquire())
            {
                Tripo.Bridge.BridgeCallException blocked =
                    await Assert.ThrowsAsync<
                        Tripo.Bridge.BridgeCallException>(
                        () => session.UnlockRecoveredOperationsAsync(
                            userConfirmed: true,
                            reviewed));
                Assert.Equal(
                    "credential_workflow_unavailable",
                    blocked.Code);
            }

            Assert.True(session.Recovery.HasBlock);
            Assert.Single(
                Directory.GetFiles(
                    Path.Combine(root, "ui-recovery", "rhino"),
                    "*.json"));
            await session.UnlockRecoveredOperationsAsync(
                userConfirmed: true,
                reviewed);
            Assert.False(session.Recovery.HasBlock);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CancellationDuringUnlockPreservesRecovery()
    {
        string root = CreateTemporaryRoot();
        try
        {
            (string operationId, string documentSessionId) =
                await CreateLostResponseRecoveryAsync(root);
            FakeHostControlClient client = new()
            {
                CurrentSessionId = documentSessionId,
                OperationStatus = ResumableOperationStatus(operationId),
            };
            await using Tripo.HostUi.TripoPanelSession session =
                new(
                    new FakeConnector(client),
                    new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root));
            await session.ConnectAsync();
            Tripo.HostUi.TripoPanelRecoveryReviewSnapshot reviewed =
                await session.CreateRecoveryReviewSnapshotAsync();
            client.OperationStatusEntered =
                new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            client.ContinueOperationStatus =
                new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            using CancellationTokenSource cancellation = new();

            Task unlock = session.UnlockRecoveredOperationsAsync(
                userConfirmed: true,
                reviewed,
                cancellation.Token);
            await client.OperationStatusEntered.Task.WaitAsync(
                TimeSpan.FromSeconds(5));
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => unlock);
            Assert.True(session.Recovery.HasBlock);
            Assert.Single(
                Directory.GetFiles(
                    Path.Combine(root, "ui-recovery", "rhino"),
                    "*.json"));
            Assert.False(
                Directory.Exists(
                    Path.Combine(
                        root,
                        "ui-recovery",
                        "rhino",
                        "archive")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CancellationAtArchiveBoundaryPreservesRecovery()
    {
        string root = CreateTemporaryRoot();
        try
        {
            (string operationId, string documentSessionId) =
                await CreateLostResponseRecoveryAsync(root);
            FakeHostControlClient client = new()
            {
                CurrentSessionId = documentSessionId,
                OperationStatus = ResumableOperationStatus(operationId),
            };
            await using Tripo.HostUi.TripoPanelSession session =
                new(
                    new FakeConnector(client),
                    new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root));
            await session.ConnectAsync();
            Tripo.HostUi.TripoPanelRecoveryReviewSnapshot reviewed =
                await session.CreateRecoveryReviewSnapshotAsync();
            using CancellationTokenSource cancellation = new();
            client.AfterOperationStatusCall = callCount =>
            {
                if (callCount == 3)
                {
                    cancellation.Cancel();
                }
            };

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => session.UnlockRecoveredOperationsAsync(
                    userConfirmed: true,
                    reviewed,
                    cancellation.Token));

            Assert.True(session.Recovery.HasBlock);
            Assert.Single(
                Directory.GetFiles(
                    Path.Combine(root, "ui-recovery", "rhino"),
                    "*.json"));
            Assert.False(
                Directory.Exists(
                    Path.Combine(
                        root,
                        "ui-recovery",
                        "rhino",
                        "archive")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReloadedSessionCanReviewCurrentAndStaleWorkTogether()
    {
        string root = CreateTemporaryRoot();
        try
        {
            FakeHostControlClient currentClient = new();
            Tripo.HostUi.TripoPanelSession current =
                new(
                    new FakeConnector(currentClient),
                    new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root));
            await current.ConnectAsync();
            current.PrepareGeneration(
                "current chair",
                10_000,
                withMaterials: false);
            await current.DispatchPreparedGenerationAsync(
                userConfirmedExternalCost: true);
            string currentFile = Assert.Single(
                Directory.GetFiles(
                    Path.Combine(root, "ui-recovery", "rhino"),
                    "*.json"));
            Tripo.HostUi.TripoPanelRecoveryHint currentHint =
                System.Text.Json.JsonSerializer.Deserialize<
                    Tripo.HostUi.TripoPanelRecoveryHint>(
                    File.ReadAllText(currentFile),
                    Tripo.Bridge.BridgeJson.Options)
                ?? throw new InvalidOperationException(
                    "The current recovery hint could not be read.");
            Tripo.HostUi.TripoPanelRecoveryHint staleHint =
                currentHint with
                {
                    RecoveryId = Guid.NewGuid().ToString("D"),
                    OwnerProcessId = int.MaxValue,
                    OwnerProcessStartedAtUtc = DateTimeOffset.UnixEpoch,
                    DocumentSessionId = Guid.NewGuid().ToString("D"),
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                    Generation = currentHint.Generation! with
                    {
                        OperationId = Guid.NewGuid().ToString("D"),
                        TaskId = null,
                        JournalState = null,
                        TaskIdDurable = false,
                        CanResumeCreation = false,
                    },
                };
            string staleFile = Path.Combine(
                Path.GetDirectoryName(currentFile)!,
                staleHint.RecoveryId + ".json");
            File.WriteAllText(
                staleFile,
                System.Text.Json.JsonSerializer.Serialize(
                    staleHint,
                    CreateStrictRecoveryJsonOptions()));
            Tripo.Bridge.BridgePaths.SetPrivateFileMode(staleFile);

            Assert.True(current.State.HasWorkflowState);
            Assert.Single(current.RefreshRecovery().Hints);
            Tripo.HostUi.TripoPanelRecoveryReviewSnapshot blockedReview =
                await current.CreateRecoveryReviewSnapshotAsync();
            InvalidOperationException blocked =
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => current.UnlockRecoveredOperationsAsync(
                        userConfirmed: true,
                        blockedReview));
            Assert.Contains("Reload the panel session", blocked.Message);

            await current.DisposeAsync();
            FakeHostControlClient reloadedClient = new()
            {
                CurrentSessionId =
                    currentHint.DocumentSessionId,
            };
            await using Tripo.HostUi.TripoPanelSession reloaded =
                new(
                    new FakeConnector(reloadedClient),
                    new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root));
            Assert.Equal(2, reloaded.Recovery.Hints.Count);
            await reloaded.ConnectAsync();
            Tripo.HostUi.TripoPanelRecoveryReviewSnapshot combined =
                await reloaded.CreateRecoveryReviewSnapshotAsync();
            await reloaded.UnlockRecoveredOperationsAsync(
                userConfirmed: true,
                combined);

            Assert.False(reloaded.Recovery.HasBlock);
            Assert.Equal(
                2,
                Directory.GetFiles(
                    Path.Combine(
                        root,
                        "ui-recovery",
                        "rhino",
                        "archive"),
                    "*.json").Length);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ActiveSessionOwnsHintUntilItsSessionIsDisposed()
    {
        string root = CreateTemporaryRoot();
        Tripo.HostUi.TripoPanelSession? owner = null;
        try
        {
            FakeHostControlClient ownerClient = new()
            {
                FailFirstTextResponse = true,
            };
            owner = new Tripo.HostUi.TripoPanelSession(
                new FakeConnector(ownerClient),
                new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root));
            await owner.ConnectAsync();
            owner.PrepareGeneration(
                "a chair",
                10_000,
                withMaterials: false);
            await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
                () => owner.DispatchPreparedGenerationAsync(
                    userConfirmedExternalCost: true));

            await using Tripo.HostUi.TripoPanelSession observer =
                new(
                    new FakeConnector(new FakeHostControlClient()),
                    new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root));
            Assert.False(observer.Recovery.HasBlock);

            await owner.DisposeAsync();
            owner = null;
            Assert.True(observer.RefreshRecovery().HasBlock);
        }
        finally
        {
            if (owner is not null)
            {
                await owner.DisposeAsync();
            }

            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CachedEmptyPanelRescansBeforeCreatingANewOperationId()
    {
        string root = CreateTemporaryRoot();
        Tripo.HostUi.TripoPanelSession? owner = null;
        try
        {
            FakeHostControlClient observerClient = new();
            await using Tripo.HostUi.TripoPanelSession observer =
                new(
                    new FakeConnector(observerClient),
                    new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root));
            await observer.ConnectAsync();
            Assert.False(observer.Recovery.HasBlock);

            FakeHostControlClient ownerClient = new()
            {
                FailFirstTextResponse = true,
            };
            owner = new Tripo.HostUi.TripoPanelSession(
                new FakeConnector(ownerClient),
                new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root));
            await owner.ConnectAsync();
            owner.PrepareGeneration(
                "a chair",
                10_000,
                withMaterials: false);
            await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
                () => owner.DispatchPreparedGenerationAsync(
                    userConfirmedExternalCost: true));

            await owner.DisposeAsync();
            owner = null;
            InvalidOperationException blocked =
                Assert.Throws<InvalidOperationException>(
                    () => observer.PrepareGeneration(
                        "a new chair",
                        10_000,
                        withMaterials: false));

            Assert.Contains("reconciliation", blocked.Message);
            Assert.True(observer.Recovery.HasBlock);
            Assert.Equal(0, observerClient.CreateTextCalls);
        }
        finally
        {
            if (owner is not null)
            {
                await owner.DisposeAsync();
            }

            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UnresolvedHintGloballyBlocksCredentialMutation()
    {
        string root = CreateTemporaryRoot();
        Tripo.HostUi.TripoPanelSession? owner = null;
        try
        {
            owner = new Tripo.HostUi.TripoPanelSession(
                new FakeConnector(
                    new FakeHostControlClient
                    {
                        FailFirstTextResponse = true,
                    }),
                new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root));
            await owner.ConnectAsync();
            owner.PrepareGeneration(
                "a chair",
                10_000,
                withMaterials: false);
            await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
                () => owner.DispatchPreparedGenerationAsync(
                    userConfirmedExternalCost: true));

            FakeHostControlClient sameHostClient = new();
            await using (Tripo.HostUi.TripoPanelSession sameHost =
                         new(
                             new FakeConnector(sameHostClient),
                             new Tripo.HostUi.TripoPanelRecoveryStore(
                                 "rhino",
                                 root)))
            {
                await sameHost.ConnectAsync();
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => sameHost.SetApiKeyAsync(
                        "replacement-key",
                        persist: true));
                Assert.Null(sameHostClient.LastApiKey);
            }

            await owner.DisposeAsync();
            owner = null;
            FakeHostControlClient otherHostClient = new()
            {
                Host = "revit",
            };
            await using Tripo.HostUi.TripoPanelSession otherHost =
                new(
                    new FakeConnector(otherHostClient),
                    new Tripo.HostUi.TripoPanelRecoveryStore("revit", root));
            await otherHost.ConnectAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => otherHost.ClearApiKeyAsync());
            Assert.Equal(0, otherHostClient.ClearApiKeyCalls);
        }
        finally
        {
            if (owner is not null)
            {
                await owner.DisposeAsync();
            }

            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UnconfirmedImportHintGloballyBlocksCredentialMutation()
    {
        string root = CreateTemporaryRoot();
        try
        {
            string directory =
                Path.Combine(root, "ui-recovery", "rhino");
            Directory.CreateDirectory(directory);
            Tripo.HostUi.TripoPanelRecoveryHint hint = new(
                Tripo.HostUi.TripoPanelRecoveryStore.CurrentSchemaVersion,
                "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
                "rhino",
                int.MaxValue,
                DateTimeOffset.UnixEpoch,
                "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
                DateTimeOffset.UtcNow,
                null,
                null,
                new Tripo.HostUi.TripoPanelImportRecoveryHint(
                    "cccccccc-cccc-4ccc-8ccc-cccccccccccc",
                    "task_conversion123",
                    "Chair",
                    "native",
                    ApplyMaterials: true,
                    DispatchAttempted: true,
                    ReceiptKnown: false));
            string path = Path.Combine(
                directory,
                hint.RecoveryId + ".json");
            File.WriteAllText(
                path,
                System.Text.Json.JsonSerializer.Serialize(
                    hint,
                    CreateStrictRecoveryJsonOptions()));
            Tripo.Bridge.BridgePaths.SetPrivateFileMode(path);

            FakeHostControlClient observerClient = new()
            {
                Host = "revit",
            };
            await using Tripo.HostUi.TripoPanelSession observer =
                new(
                    new FakeConnector(observerClient),
                    new Tripo.HostUi.TripoPanelRecoveryStore("revit", root));
            await observer.ConnectAsync();
            InvalidOperationException blocked =
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => observer.SetApiKeyAsync(
                        "replacement-key",
                        persist: false));

            Assert.Contains("another Rhino/Revit panel", blocked.Message);
            Assert.Null(observerClient.LastApiKey);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RecoveryStorePersistsCanonicalTaskUuidWithoutRewritingIt()
    {
        const string documentSessionId =
            "11111111-1111-4111-8111-111111111111";
        const string operationId =
            "22222222-2222-4222-8222-222222222222";
        const string taskId =
            "ef731ad6-aeb0-4950-9a2e-2298359dfaf8";
        string root = CreateTemporaryRoot();
        try
        {
            using Tripo.HostUi.TripoPanelRecoveryStore store =
                new("rhino", root);
            Tripo.HostUi.TripoPanelState state =
                Tripo.HostUi.TripoPanelState.Initial with
                {
                    Connected = true,
                    Context = new Tripo.Bridge.HostContextReceipt(
                        "rhino",
                        "8-test",
                        123,
                        documentSessionId,
                        "Test.3dm",
                        "Meters",
                        []),
                    PreparedGeneration =
                        new Tripo.HostUi.PreparedTextGeneration(
                            "a chair",
                            10_000,
                            false,
                            documentSessionId,
                            operationId),
                    GenerationDispatchAttempted = true,
                    GenerationReceipt =
                        new Tripo.Bridge.HostControlTextTaskCreationReceipt(
                            operationId,
                            taskId,
                            "v3.1-20260211"),
                };

            store.Save(state);

            string file = Assert.Single(
                Directory.GetFiles(
                    Path.Combine(root, "ui-recovery", "rhino"),
                    "*.json"));
            Tripo.HostUi.TripoPanelRecoveryHint hint =
                System.Text.Json.JsonSerializer.Deserialize<
                    Tripo.HostUi.TripoPanelRecoveryHint>(
                    File.ReadAllText(file),
                    Tripo.Bridge.BridgeJson.Options)
                ?? throw new InvalidOperationException(
                    "The recovery hint could not be read.");
            Assert.Equal(taskId, hint.Generation?.TaskId);
            Assert.True(hint.Generation?.TaskIdDurable);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CurrentHintExclusionRequiresExactOwnerIdentity()
    {
        string root = CreateTemporaryRoot();
        try
        {
            Tripo.HostUi.TripoPanelRecoveryStore store =
                new("rhino", root);
            await using Tripo.HostUi.TripoPanelSession session =
                new(
                    new FakeConnector(new FakeHostControlClient()),
                    store);
            await session.ConnectAsync();
            session.PrepareGeneration(
                "a chair",
                10_000,
                withMaterials: false);
            await session.DispatchPreparedGenerationAsync(
                userConfirmedExternalCost: true);

            string file = Assert.Single(
                Directory.GetFiles(
                    Path.Combine(root, "ui-recovery", "rhino"),
                    "*.json"));
            Tripo.HostUi.TripoPanelRecoveryHint hint =
                System.Text.Json.JsonSerializer.Deserialize<
                    Tripo.HostUi.TripoPanelRecoveryHint>(
                    File.ReadAllText(file),
                    Tripo.Bridge.BridgeJson.Options)
                ?? throw new InvalidOperationException(
                    "The recovery hint could not be read.");
            hint = hint with
            {
                OwnerProcessId = int.MaxValue,
                OwnerProcessStartedAtUtc = DateTimeOffset.UnixEpoch,
            };
            File.WriteAllText(
                file,
                System.Text.Json.JsonSerializer.Serialize(
                    hint,
                    CreateStrictRecoveryJsonOptions()));
            Tripo.Bridge.BridgePaths.SetPrivateFileMode(file);

            Assert.True(
                store.LoadCredentialMutationBlocks(
                    excludeCurrentStoreHint: true).HasBlock);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void HintFromAnotherLiveProcessIsConservativelyBlocking()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string root = CreateTemporaryRoot();
        using System.Diagnostics.Process ownerProcess =
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("/bin/sh")
                {
                    ArgumentList = { "-c", "sleep 30" },
                    CreateNoWindow = true,
                    UseShellExecute = false,
                })
            ?? throw new InvalidOperationException(
                "The live-owner canary process did not start.");
        try
        {
            string directory =
                Path.Combine(root, "ui-recovery", "rhino");
            Directory.CreateDirectory(directory);
            Tripo.HostUi.TripoPanelRecoveryHint hint = new(
                Tripo.HostUi.TripoPanelRecoveryStore.CurrentSchemaVersion,
                "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
                "rhino",
                ownerProcess.Id,
                new DateTimeOffset(
                    ownerProcess.StartTime.ToUniversalTime(),
                    TimeSpan.Zero),
                "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
                DateTimeOffset.UtcNow,
                new Tripo.HostUi.TripoPanelPaidRecoveryHint(
                    "cccccccc-cccc-4ccc-8ccc-cccccccccccc",
                    DispatchAttempted: true,
                    TaskId: null,
                    JournalState: null,
                    TaskIdDurable: false,
                    CanResumeCreation: false),
                null,
                null);
            string path =
                Path.Combine(directory, hint.RecoveryId + ".json");
            File.WriteAllText(
                path,
                System.Text.Json.JsonSerializer.Serialize(
                    hint,
                    CreateStrictRecoveryJsonOptions()));
            Tripo.Bridge.BridgePaths.SetPrivateFileMode(path);

            Tripo.HostUi.TripoPanelRecoveryLoadResult result =
                new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root)
                    .LoadStale();

            Assert.Empty(result.Hints);
            Assert.Equal(
                "recovery_owner_process_alive",
                Assert.Single(result.Issues).Code);
        }
        finally
        {
            if (!ownerProcess.HasExited)
            {
                ownerProcess.Kill(entireProcessTree: true);
                ownerProcess.WaitForExit();
            }

            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SameDocumentPanelsUseIndependentRecoveryFiles()
    {
        string root = CreateTemporaryRoot();
        try
        {
            string documentSessionId = Guid.NewGuid().ToString("D");
            FakeHostControlClient firstClient = new()
            {
                CurrentSessionId = documentSessionId,
                FailFirstTextResponse = true,
            };
            FakeHostControlClient secondClient = new()
            {
                CurrentSessionId = documentSessionId,
                FailFirstTextResponse = true,
            };
            string firstOperationId;
            string secondOperationId;
            await using (Tripo.HostUi.TripoPanelSession first =
                         new(
                             new FakeConnector(firstClient),
                             new Tripo.HostUi.TripoPanelRecoveryStore(
                                 "rhino",
                                 root)))
            await using (Tripo.HostUi.TripoPanelSession second =
                         new(
                             new FakeConnector(secondClient),
                             new Tripo.HostUi.TripoPanelRecoveryStore(
                                 "rhino",
                                 root)))
            {
                await first.ConnectAsync();
                await second.ConnectAsync();
                firstOperationId = first.PrepareGeneration(
                        "first chair",
                        10_000,
                        withMaterials: false)
                    .OperationId;
                secondOperationId = second.PrepareGeneration(
                        "second chair",
                        10_000,
                        withMaterials: false)
                    .OperationId;
                await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
                    () => first.DispatchPreparedGenerationAsync(
                        userConfirmedExternalCost: true));
                await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
                    () => second.DispatchPreparedGenerationAsync(
                        userConfirmedExternalCost: true));

                Assert.Equal(
                    2,
                    Directory.GetFiles(
                        Path.Combine(root, "ui-recovery", "rhino"),
                        "*.json").Length);
            }

            Tripo.HostUi.TripoPanelRecoveryLoadResult recovered =
                new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root)
                    .LoadStale();
            Assert.Equal(2, recovered.Hints.Count);
            string[] operationIds = recovered.Hints
                .Select(item => item.Hint.Generation!.OperationId)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(
                new[] { firstOperationId, secondOperationId }
                    .OrderBy(value => value, StringComparer.Ordinal),
                operationIds);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DirectGlbImportUsesGenerationTaskWithoutObjConversion()
    {
        FakeHostControlClient client = new();
        await using Tripo.HostUi.TripoPanelSession session =
            new(new FakeConnector(client));
        await session.ConnectAsync();
        Tripo.HostUi.PreparedTextGeneration generation =
            session.PrepareGeneration(
                "a PBR chair",
                10_000,
                withMaterials: true);
        Tripo.HostUi.DirectGlbAutoImportIntent intent = new(
            sessionGeneration: 1,
            generation.OperationId,
            generation.DocumentSessionId,
            "PBR Chair");
        await session.DispatchPreparedGenerationAsync(
            userConfirmedExternalCost: true);
        await session.RefreshGenerationStatusAsync();

        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportDecision.BeginImport,
            intent.ObserveState(
                sessionGeneration: 1,
                session.State));
        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportDecision.NoAction,
            intent.ObserveState(
                sessionGeneration: 1,
                session.State));
        Tripo.HostUi.PreparedObjImport prepared =
            session.PrepareGlbImport(intent.ObjectName);
        await session.ImportPreparedAsync();
        Assert.True(intent.TryFinishImport(1, session.State));

        Assert.True(prepared.IsDirectGlb);
        Assert.Equal("glb", prepared.ArtifactFormat);
        Assert.Equal("task_source123", prepared.ConversionTaskId);
        Assert.Equal("glb_instance", prepared.ImportMode);
        Assert.True(prepared.ApplyMaterials);
        Assert.Equal(1, client.CreateTextCalls);
        Assert.Equal(0, client.CreateConversionCalls);
        Assert.Equal(1, client.GlbImportCalls);
        Assert.Equal(
            prepared.OperationId,
            client.LastGlbImportRequest?.OperationId);
        Assert.Equal("glb", session.State.ImportReceipt?.ArtifactFormat);
        Assert.Equal(
            "task_source123",
            session.State.ImportReceipt?.SourceTaskId);
    }

    [Fact]
    public async Task MismatchedImportReceiptRequiresManualReview()
    {
        FakeHostControlClient client = new()
        {
            ImportReceiptHostOverride = "revit",
        };
        await using Tripo.HostUi.TripoPanelSession session =
            new(new FakeConnector(client));
        await session.ConnectAsync();
        session.PrepareGeneration(
            "a PBR chair",
            10_000,
            withMaterials: true);
        await session.DispatchPreparedGenerationAsync(
            userConfirmedExternalCost: true);
        await session.RefreshGenerationStatusAsync();
        session.PrepareGlbImport("PBR Chair");

        Tripo.Bridge.HostControlCallException error =
            await Assert.ThrowsAsync<
                Tripo.Bridge.HostControlCallException>(
                () => session.ImportPreparedAsync());

        Assert.Equal(
            Tripo.Bridge.BridgeConstants.MutationStateUncertainError,
            error.Code);
        Assert.Null(session.State.ImportReceipt);
        Assert.True(session.State.ImportRequiresManualReview);
        Assert.False(session.State.CanResetWorkflow);
        Assert.Contains(
            "did not match",
            session.State.LastError,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PausedDirectGlbManualRefreshImportsOnlyAfterResume()
    {
        FakeHostControlClient client = new()
        {
            TaskStatusValue = "running",
        };
        await using Tripo.HostUi.TripoPanelSession session =
            new(new FakeConnector(client));
        await session.ConnectAsync();
        Tripo.HostUi.PreparedTextGeneration generation =
            session.PrepareGeneration(
                "a PBR chair",
                10_000,
                withMaterials: true);
        Tripo.HostUi.DirectGlbAutoImportIntent intent = new(
            sessionGeneration: 1,
            generation.OperationId,
            generation.DocumentSessionId,
            "PBR Chair");
        await session.DispatchPreparedGenerationAsync(
            userConfirmedExternalCost: true);
        await session.RefreshGenerationStatusAsync();
        string operationId = generation.OperationId;
        string taskId = session.State.GenerationReceipt!.TaskId;

        Assert.True(intent.TryStopWaiting(1, session.State));
        Assert.Null(
            Tripo.HostUi.DirectGlbGenerationPollingPolicy.GetPendingTaskId(
                session.State,
                session.Recovery,
                intent));

        client.TaskStatusValue = "success";
        await session.RefreshGenerationStatusAsync();
        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportDecision.Stopped,
            intent.ObserveState(1, session.State));
        Assert.Equal(0, client.GlbImportCalls);
        Assert.Equal(1, client.CreateTextCalls);
        Assert.Equal(0, client.CreateConversionCalls);
        Assert.Equal(
            operationId,
            session.State.PreparedGeneration?.OperationId);
        Assert.Equal(taskId, session.State.GenerationReceipt?.TaskId);

        Assert.True(intent.TryResumeWaiting(1, session.State));
        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportDecision.BeginImport,
            intent.ObserveState(1, session.State));
        _ = session.PrepareGlbImport(intent.ObjectName);
        await session.ImportPreparedAsync();
        Assert.True(intent.TryFinishImport(1, session.State));
        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportDecision.NoAction,
            intent.ObserveState(1, session.State));
        Assert.Equal(1, client.GlbImportCalls);
        Assert.Equal(1, client.CreateTextCalls);
        Assert.Equal(0, client.CreateConversionCalls);
        Assert.Equal(taskId, client.LastGlbImportRequest?.GenerationTaskId);
        Assert.Equal(2, client.TaskStatusCalls);
    }

    [Fact]
    public async Task DirectGlbCredentialRecoveryKeepsTaskAndImportsOnce()
    {
        FakeHostControlClient client = new()
        {
            TaskStatusFailureCode =
                Tripo.Bridge.HostControlConstants.CredentialInvalidError,
        };
        await using Tripo.HostUi.TripoPanelSession session =
            new(new FakeConnector(client));
        await session.ConnectAsync();
        Tripo.HostUi.PreparedTextGeneration generation =
            session.PrepareGeneration(
                "a PBR chair",
                10_000,
                withMaterials: true);
        Tripo.HostUi.DirectGlbAutoImportIntent intent = new(
            sessionGeneration: 1,
            generation.OperationId,
            generation.DocumentSessionId,
            "PBR Chair");
        await session.DispatchPreparedGenerationAsync(
            userConfirmedExternalCost: true);

        await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
            () => session.RefreshGenerationStatusAsync());
        Tripo.HostUi.TripoApiKeyPromptPolicy recoveryPolicy =
            Tripo.HostUi.TripoApiKeyPromptPolicy.Create(session.State);
        Assert.True(session.State.HasDurableGenerationTask);
        Assert.Contains("credential", session.State.LastError);
        Assert.Equal(
            Tripo.Bridge.HostControlConstants.CredentialInvalidError,
            session.State.LastErrorCode);
        Assert.True(session.State.HasCredentialRefreshFailure);
        Assert.True(recoveryPolicy.RecoveryMode);
        Assert.False(recoveryPolicy.PersistAllowed);

        await session.SetApiKeyAsync(
            "same-account-recovery-key",
            persist: false);
        client.TaskStatusFailureCode = null;
        await session.RefreshGenerationStatusAsync();
        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportDecision.BeginImport,
            intent.ObserveState(1, session.State));
        _ = session.PrepareGlbImport(intent.ObjectName);
        await session.ImportPreparedAsync();
        Assert.True(intent.TryFinishImport(1, session.State));
        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportDecision.NoAction,
            intent.ObserveState(1, session.State));

        Assert.Equal("same-account-recovery-key", client.LastApiKey);
        Assert.Equal("task_source123", intent.TaskId);
        Assert.Equal(1, client.CreateTextCalls);
        Assert.Equal(0, client.CreateConversionCalls);
        Assert.Equal(1, client.GlbImportCalls);
        Assert.Equal(
            "task_source123",
            client.LastGlbImportRequest?.GenerationTaskId);
    }

    [Fact]
    public async Task DirectGlbContinuesAfterBusyRefreshFailsWithSuccessEvidence()
    {
        FakeHostControlClient client = new();
        await using Tripo.HostUi.TripoPanelSession session =
            new(new FakeConnector(client));
        await session.ConnectAsync();
        Tripo.HostUi.PreparedTextGeneration generation =
            session.PrepareGeneration(
                "a PBR chair",
                10_000,
                withMaterials: true);
        Tripo.HostUi.DirectGlbAutoImportIntent intent = new(
            sessionGeneration: 1,
            generation.OperationId,
            generation.DocumentSessionId,
            "PBR Chair");
        await session.DispatchPreparedGenerationRequiringCapabilityAsync(
            userConfirmedExternalCost: true,
            requiredHostCapability:
                Tripo.Bridge.BridgeConstants.ImportGlbMethod,
            requiredSidecarCapability:
                Tripo.Bridge.HostControlConstants.ImportGenerationGlbMethod);
        await session.RefreshGenerationStatusAsync();

        TaskCompletionSource<bool> statusEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> releaseStatus =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        client.TaskStatusEntered = statusEntered;
        client.ContinueTaskStatus = releaseStatus;
        Task failingRefresh = session.RefreshGenerationStatusAsync();
        await statusEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(session.State.Busy);
        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportDecision.NoAction,
            intent.ObserveState(1, session.State));

        client.TaskStatusFailureCode = "sidecar_unavailable";
        releaseStatus.TrySetResult(true);
        await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
            () => failingRefresh);
        Assert.False(session.State.Busy);
        Assert.Equal("success", session.State.GenerationStatus?.Status);
        Assert.Equal(
            "sidecar_unavailable",
            session.State.LastErrorCode);
        Assert.True(intent.TryBindDurableTask(1, session.State));
        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportDecision.BeginImport,
            intent.ObserveState(1, session.State));

        _ = session.PrepareGlbImport(intent.ObjectName);
        await session.ImportPreparedAsync();
        Assert.True(intent.TryFinishImport(1, session.State));
        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportDecision.NoAction,
            intent.ObserveState(1, session.State));
        Assert.Equal(1, client.CreateTextCalls);
        Assert.Equal(0, client.CreateConversionCalls);
        Assert.Equal(1, client.GlbImportCalls);
    }

    [Fact]
    public async Task DirectGlbCredentialRefreshFailureWaitsForKeyBeforeImport()
    {
        FakeHostControlClient client = new();
        await using Tripo.HostUi.TripoPanelSession session =
            new(new FakeConnector(client));
        await session.ConnectAsync();
        Tripo.HostUi.PreparedTextGeneration generation =
            session.PrepareGeneration(
                "a PBR chair",
                10_000,
                withMaterials: true);
        Tripo.HostUi.DirectGlbAutoImportIntent intent = new(
            sessionGeneration: 1,
            generation.OperationId,
            generation.DocumentSessionId,
            "PBR Chair");
        await session.DispatchPreparedGenerationRequiringCapabilityAsync(
            userConfirmedExternalCost: true,
            requiredHostCapability:
                Tripo.Bridge.BridgeConstants.ImportGlbMethod,
            requiredSidecarCapability:
                Tripo.Bridge.HostControlConstants.ImportGenerationGlbMethod);
        await session.RefreshGenerationStatusAsync();

        TaskCompletionSource<bool> statusEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> releaseStatus =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        client.TaskStatusEntered = statusEntered;
        client.ContinueTaskStatus = releaseStatus;
        Task failingRefresh = session.RefreshGenerationStatusAsync();
        await statusEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        client.TaskStatusFailureCode =
            Tripo.Bridge.HostControlConstants.CredentialInvalidError;
        releaseStatus.TrySetResult(true);
        await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
            () => failingRefresh);

        Assert.True(session.State.HasCredentialRefreshFailure);
        Assert.True(intent.TryBindDurableTask(1, session.State));
        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportDecision.Waiting,
            intent.ObserveState(1, session.State));
        Assert.Equal(0, client.GlbImportCalls);

        await session.SetApiKeyAsync(
            "same-account-recovery-key",
            persist: false);
        Assert.False(session.State.HasCredentialRefreshFailure);
        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportDecision.BeginImport,
            intent.ObserveState(1, session.State));
        _ = session.PrepareGlbImport(intent.ObjectName);
        await session.ImportPreparedAsync();
        Assert.True(intent.TryFinishImport(1, session.State));
        Assert.Equal(1, client.CreateTextCalls);
        Assert.Equal(0, client.CreateConversionCalls);
        Assert.Equal(1, client.GlbImportCalls);
    }

    [Fact]
    public async Task DirectGlbDocumentDriftStopsBeforeHostImportDispatch()
    {
        FakeHostControlClient client = new();
        await using Tripo.HostUi.TripoPanelSession session =
            new(new FakeConnector(client));
        await session.ConnectAsync();
        Tripo.HostUi.PreparedTextGeneration generation =
            session.PrepareGeneration(
                "a PBR chair",
                10_000,
                withMaterials: true);
        Tripo.HostUi.DirectGlbAutoImportIntent intent = new(
            sessionGeneration: 1,
            generation.OperationId,
            generation.DocumentSessionId,
            "PBR Chair");
        await session.DispatchPreparedGenerationAsync(
            userConfirmedExternalCost: true);
        await session.RefreshGenerationStatusAsync();
        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportDecision.BeginImport,
            intent.ObserveState(1, session.State));
        _ = session.PrepareGlbImport(intent.ObjectName);
        client.CurrentSessionId = Guid.NewGuid().ToString("D");

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.ImportPreparedAsync());

        Assert.Contains("active host document changed", exception.Message);
        Assert.Equal(1, client.CreateTextCalls);
        Assert.Equal(0, client.CreateConversionCalls);
        Assert.Equal(0, client.GlbImportCalls);
        Assert.False(session.State.ImportDispatchAttempted);
    }

    [Fact]
    public async Task DirectGlbHostCapabilityIsRecheckedAtGenerationDispatch()
    {
        FakeHostControlClient client = new();
        await using Tripo.HostUi.TripoPanelSession session =
            new(new FakeConnector(client));
        await session.ConnectAsync();
        Tripo.HostUi.PreparedTextGeneration prepared =
            session.PrepareGeneration(
                "a PBR chair",
                10_000,
                withMaterials: true);
        client.AdvertiseGlbHostCapability = false;

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session
                    .DispatchPreparedGenerationRequiringCapabilityAsync(
                    userConfirmedExternalCost: true,
                    requiredHostCapability:
                        Tripo.Bridge.BridgeConstants.ImportGlbMethod,
                    requiredSidecarCapability:
                        Tripo.Bridge.HostControlConstants
                            .ImportGenerationGlbMethod));

        Assert.Contains("no longer advertises", exception.Message);
        Assert.Equal(0, client.CreateTextCalls);
        Assert.False(session.State.GenerationDispatchAttempted);
        Assert.Equal(prepared, session.State.PreparedGeneration);
    }

    [Fact]
    public async Task DirectGlbSidecarCapabilityIsRecheckedAtGenerationDispatch()
    {
        FakeHostControlClient client = new();
        await using Tripo.HostUi.TripoPanelSession session =
            new(new FakeConnector(client));
        await session.ConnectAsync();
        Tripo.HostUi.PreparedTextGeneration prepared =
            session.PrepareGeneration(
                "a PBR chair",
                10_000,
                withMaterials: true);
        client.AdvertiseGlbSidecarCapability = false;

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session
                    .DispatchPreparedGenerationRequiringCapabilityAsync(
                    userConfirmedExternalCost: true,
                    requiredHostCapability:
                        Tripo.Bridge.BridgeConstants.ImportGlbMethod,
                    requiredSidecarCapability:
                        Tripo.Bridge.HostControlConstants
                            .ImportGenerationGlbMethod));

        Assert.Contains("sidecar no longer advertises", exception.Message);
        Assert.Equal(0, client.CreateTextCalls);
        Assert.False(session.State.GenerationDispatchAttempted);
        Assert.Equal(prepared, session.State.PreparedGeneration);
    }

    [Fact]
    public async Task DirectGlbIsDisabledForPluginSidecarCapabilitySkew()
    {
        FakeHostControlClient client = new()
        {
            AdvertiseGlbWorkflowCapability = false,
        };
        await using Tripo.HostUi.TripoPanelSession session =
            new(new FakeConnector(client));
        await session.ConnectAsync();
        session.PrepareGeneration(
            "a PBR chair",
            10_000,
            withMaterials: true);
        await session.DispatchPreparedGenerationAsync(
            userConfirmedExternalCost: true);
        await session.RefreshGenerationStatusAsync();

        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(
                () => session.PrepareGlbImport("PBR Chair"));

        Assert.DoesNotContain(
            Tripo.Bridge.BridgeConstants.ImportGlbMethod,
            session.State.Context!.Capabilities);
        Assert.Contains("does not advertise", exception.Message);
        Assert.Equal(0, client.GlbImportCalls);
    }

    [Fact]
    public async Task DirectGlbCredentialFailureClearsFalseImportOccupancy()
    {
        FakeHostControlClient client = new()
        {
            GlbImportFailureCode =
                Tripo.Bridge.HostControlConstants.CredentialInvalidError,
        };
        await using Tripo.HostUi.TripoPanelSession session =
            new(new FakeConnector(client));
        await session.ConnectAsync();
        session.PrepareGeneration(
            "a PBR chair",
            10_000,
            withMaterials: true);
        await session.DispatchPreparedGenerationAsync(
            userConfirmedExternalCost: true);
        await session.RefreshGenerationStatusAsync();
        Tripo.HostUi.PreparedObjImport prepared =
            session.PrepareGlbImport("PBR Chair");

        await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
            () => session.ImportPreparedAsync());

        Assert.False(session.State.ImportDispatchAttempted);
        Assert.False(session.State.HasUnresolvedDispatch);
        Assert.Equal(prepared, session.State.PreparedImport);
        Assert.Null(session.State.ImportReceipt);
        await session.SetApiKeyAsync("replacement-key", persist: false);
        client.GlbImportFailureCode = null;
        await session.ImportPreparedAsync();

        Assert.Equal("replacement-key", client.LastApiKey);
        Assert.Equal(2, client.GlbImportCalls);
        Assert.Equal(
            prepared.OperationId,
            session.State.ImportReceipt?.OperationId);
    }

    [Fact]
    public async Task UncertainDirectGlbImportCannotBeRetriedOrReset()
    {
        FakeHostControlClient client = new()
        {
            GlbImportFailureCode =
                Tripo.Bridge.BridgeConstants.MutationStateUncertainError,
        };
        await using Tripo.HostUi.TripoPanelSession session =
            new(new FakeConnector(client));
        await session.ConnectAsync();
        session.PrepareGeneration(
            "a PBR chair",
            10_000,
            withMaterials: true);
        await session.DispatchPreparedGenerationAsync(
            userConfirmedExternalCost: true);
        await session.RefreshGenerationStatusAsync();
        Tripo.HostUi.PreparedObjImport prepared =
            session.PrepareGlbImport("PBR Chair");

        await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
            () => session.ImportPreparedAsync());
        InvalidOperationException retry =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.ImportPreparedAsync());
        InvalidOperationException reset =
            Assert.Throws<InvalidOperationException>(session.ResetWorkflow);

        Assert.True(session.State.ImportRequiresManualReview);
        Assert.False(session.State.CanDispatchPreparedImport);
        Assert.False(session.State.ImportRetryRequired);
        Assert.Equal(prepared.OperationId, session.State.PreparedImport?.OperationId);
        Assert.Equal(1, client.GlbImportCalls);
        Assert.Contains("manual document review", retry.Message);
        Assert.Contains("unresolved", reset.Message);
    }

    [Fact]
    public async Task ImportReceiptHintPersistsUntilExplicitWorkflowReset()
    {
        string root = CreateTemporaryRoot();
        try
        {
            await using Tripo.HostUi.TripoPanelSession session =
                new(
                    new FakeConnector(new FakeHostControlClient()),
                    new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root));
            await session.ConnectAsync();
            session.PrepareGeneration(
                "a chair",
                10_000,
                withMaterials: true);
            await session.DispatchPreparedGenerationAsync(
                userConfirmedExternalCost: true);
            await session.RefreshGenerationStatusAsync();
            session.PrepareConversion(10_000, withMaterials: true);
            await session.DispatchPreparedConversionAsync(
                userConfirmedExternalCost: true);
            await session.RefreshConversionStatusAsync();
            Tripo.HostUi.PreparedObjImport import =
                session.PrepareImport(
                    "Chair",
                    "native",
                    applyMaterials: true);
            await session.ImportPreparedAsync();

            string file = Assert.Single(
                Directory.GetFiles(
                    Path.Combine(root, "ui-recovery", "rhino"),
                    "*.json"));
            Tripo.HostUi.TripoPanelRecoveryHint hint =
                System.Text.Json.JsonSerializer.Deserialize<
                    Tripo.HostUi.TripoPanelRecoveryHint>(
                    File.ReadAllText(file),
                    Tripo.Bridge.BridgeJson.Options)
                ?? throw new InvalidOperationException(
                    "The import recovery hint could not be read.");
            Assert.Equal(import.OperationId, hint.Import?.OperationId);
            Assert.True(hint.Import!.ReceiptKnown);

            session.ResetWorkflow();
            Assert.Empty(
                Directory.GetFiles(
                    Path.Combine(root, "ui-recovery", "rhino"),
                    "*.json"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void InvalidOversizedAndUnknownSchemaRecoveryFilesFailClosed()
    {
        string root = CreateTemporaryRoot();
        try
        {
            string directory =
                Path.Combine(root, "ui-recovery", "rhino");
            Directory.CreateDirectory(directory);
            string invalidJsonPath =
                Path.Combine(
                    directory,
                    "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa.json");
            File.WriteAllText(
                invalidJsonPath,
                "{not-json");
            Tripo.Bridge.BridgePaths.SetPrivateFileMode(invalidJsonPath);
            string oversizedPath =
                Path.Combine(
                    directory,
                    "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb.json");
            File.WriteAllBytes(
                oversizedPath,
                new byte[
                    Tripo.HostUi.TripoPanelRecoveryStore
                        .MaximumRecoveryFileBytes + 1]);
            Tripo.Bridge.BridgePaths.SetPrivateFileMode(oversizedPath);
            Tripo.HostUi.TripoPanelRecoveryHint wrongSchema = new(
                99,
                "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee",
                "rhino",
                int.MaxValue,
                DateTimeOffset.UnixEpoch,
                "cccccccc-cccc-4ccc-8ccc-cccccccccccc",
                DateTimeOffset.UtcNow,
                new Tripo.HostUi.TripoPanelPaidRecoveryHint(
                    "dddddddd-dddd-4ddd-8ddd-dddddddddddd",
                    true,
                    null,
                    null,
                    false,
                    false),
                null,
                null);
            string wrongSchemaPath =
                Path.Combine(
                    directory,
                    wrongSchema.RecoveryId + ".json");
            File.WriteAllText(
                wrongSchemaPath,
                System.Text.Json.JsonSerializer.Serialize(
                    wrongSchema,
                    CreateStrictRecoveryJsonOptions()));
            Tripo.Bridge.BridgePaths.SetPrivateFileMode(wrongSchemaPath);

            Tripo.HostUi.TripoPanelRecoveryLoadResult result =
                new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root)
                    .LoadStale();

            Assert.True(result.HasBlock);
            Assert.Empty(result.Hints);
            Assert.Equal(3, result.Issues.Count);
            Assert.Equal(
                "recovery_file_invalid_json",
                Assert.Single(
                    result.Issues,
                    issue => issue.FileName ==
                        Path.GetFileName(invalidJsonPath)).Code);
            Assert.Equal(
                "recovery_file_invalid",
                Assert.Single(
                    result.Issues,
                    issue => issue.FileName ==
                        Path.GetFileName(oversizedPath)).Code);
            Tripo.HostUi.TripoPanelRecoveryIssue schemaIssue =
                Assert.Single(
                    result.Issues,
                    issue => issue.FileName ==
                        Path.GetFileName(wrongSchemaPath));
            Assert.Equal("recovery_file_invalid", schemaIssue.Code);
            Assert.Contains(
                "unsupported schema",
                schemaIssue.Message,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MissingRequiredRecoveryPropertyFailsClosed()
    {
        string root = CreateTemporaryRoot();
        try
        {
            string directory =
                Path.Combine(root, "ui-recovery", "rhino");
            Directory.CreateDirectory(directory);
            Tripo.HostUi.TripoPanelRecoveryHint hint = new(
                Tripo.HostUi.TripoPanelRecoveryStore.CurrentSchemaVersion,
                "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
                "rhino",
                int.MaxValue,
                DateTimeOffset.UnixEpoch,
                "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
                DateTimeOffset.UtcNow,
                null,
                null,
                new Tripo.HostUi.TripoPanelImportRecoveryHint(
                    "cccccccc-cccc-4ccc-8ccc-cccccccccccc",
                    "task_conversion123",
                    "Chair",
                    "native",
                    ApplyMaterials: true,
                    DispatchAttempted: true,
                    ReceiptKnown: false));
            string json = System.Text.Json.JsonSerializer.Serialize(
                hint,
                CreateStrictRecoveryJsonOptions());
            System.Text.Json.Nodes.JsonObject rootObject =
                System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
            rootObject["import"]!.AsObject().Remove("applyMaterials");
            string path = Path.Combine(
                directory,
                hint.RecoveryId + ".json");
            File.WriteAllText(path, rootObject.ToJsonString());
            Tripo.Bridge.BridgePaths.SetPrivateFileMode(path);

            Tripo.HostUi.TripoPanelRecoveryLoadResult result =
                new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root)
                    .LoadStale();

            Tripo.HostUi.TripoPanelRecoveryIssue issue =
                Assert.Single(result.Issues);
            Assert.Equal("recovery_file_invalid_json", issue.Code);
            Assert.Empty(result.Hints);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SymbolicLinkRecoveryDirectoryFailsClosed()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string root = CreateTemporaryRoot();
        try
        {
            string target = Path.Combine(root, "redirected");
            Directory.CreateDirectory(target);
            Directory.CreateSymbolicLink(
                Path.Combine(root, "ui-recovery"),
                target);

            Assert.Throws<InvalidDataException>(
                () => new Tripo.HostUi.TripoPanelRecoveryStore(
                    "rhino",
                    root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SymbolicLinkRecoveryFileFailsClosed()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string root = CreateTemporaryRoot();
        try
        {
            string directory =
                Path.Combine(root, "ui-recovery", "rhino");
            Directory.CreateDirectory(directory);
            string target = Path.Combine(root, "target.json");
            File.WriteAllText(target, "{}");
            File.CreateSymbolicLink(
                Path.Combine(
                    directory,
                    "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa.json"),
                target);

            Tripo.HostUi.TripoPanelRecoveryLoadResult result =
                new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root)
                    .LoadStale();

            Tripo.HostUi.TripoPanelRecoveryIssue issue =
                Assert.Single(result.Issues);
            Assert.Equal("recovery_file_invalid", issue.Code);
            Assert.Empty(result.Hints);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    internal static Tripo.HostUi.TripoPanelSession CreateSession(
        FakeHostControlClient client) =>
        new(new FakeConnector(client));

    private static Tripo.Bridge.HostControlOperationStatusReceipt
        DurableOperationStatus(string operationId, string taskId) =>
        new(
            operationId,
            "text_task_creation",
            "task_id_persisted",
            null,
            taskId,
            null,
            null,
            TaskIdDurable: true,
            MayHaveCreatedRemoteTask: true,
            CanResumeCreation: true,
            NextAction: "Query the durable task ID.",
            UpdatedAtUtc: DateTimeOffset.UtcNow);

    private static Tripo.Bridge.HostControlOperationStatusReceipt
        ResumableOperationStatus(string operationId) =>
        new(
            operationId,
            "text_task_creation",
            "prepared",
            null,
            null,
            null,
            null,
            TaskIdDurable: false,
            MayHaveCreatedRemoteTask: false,
            CanResumeCreation: true,
            NextAction: "Retry the same operation ID.",
            UpdatedAtUtc: DateTimeOffset.UtcNow);

    private static Tripo.Bridge.HostControlOperationStatusReceipt
        RejectedOperationStatus(string operationId, string kind) =>
        new(
            operationId,
            kind,
            Tripo.Bridge.HostControlConstants.RequestRejectedState,
            null,
            null,
            "credential_rejected",
            "The provider rejected the credential.",
            TaskIdDurable: false,
            MayHaveCreatedRemoteTask: false,
            CanResumeCreation: false,
            NextAction: "Correct the credential and use a new operation ID.",
            UpdatedAtUtc: DateTimeOffset.UtcNow);

    private static async Task<(string OperationId, string DocumentSessionId)>
        CreateLostResponseRecoveryAsync(string root)
    {
        FakeHostControlClient client = new()
        {
            FailFirstTextResponse = true,
        };
        string operationId;
        string documentSessionId;
        await using (Tripo.HostUi.TripoPanelSession owner =
                     new(
                         new FakeConnector(client),
                         new Tripo.HostUi.TripoPanelRecoveryStore(
                             "rhino",
                             root)))
        {
            await owner.ConnectAsync();
            documentSessionId =
                owner.State.Context!.DocumentSessionId;
            operationId = owner.PrepareGeneration(
                    "a chair",
                    10_000,
                    withMaterials: false)
                .OperationId;
            await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
                () => owner.DispatchPreparedGenerationAsync(
                    userConfirmedExternalCost: true));
        }

        MakeRecoveryOwnerStale(root, "rhino");
        return (operationId, documentSessionId);
    }

    private static string CreateTemporaryRoot()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "tripo-host-ui-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void MakeRecoveryOwnerStale(
        string root,
        string host)
    {
        string file = Assert.Single(
            Directory.GetFiles(
                Path.Combine(root, "ui-recovery", host),
                "*.json"));
        Tripo.HostUi.TripoPanelRecoveryHint hint =
            System.Text.Json.JsonSerializer.Deserialize<
                Tripo.HostUi.TripoPanelRecoveryHint>(
                File.ReadAllText(file),
                Tripo.Bridge.BridgeJson.Options)
            ?? throw new InvalidOperationException(
                "The test recovery hint could not be read.");
        hint = hint with
        {
            OwnerProcessId = int.MaxValue,
            OwnerProcessStartedAtUtc = DateTimeOffset.UnixEpoch,
        };
        File.WriteAllText(
            file,
            System.Text.Json.JsonSerializer.Serialize(
                hint,
                CreateStrictRecoveryJsonOptions()));
        Tripo.Bridge.BridgePaths.SetPrivateFileMode(file);
    }

    private static System.Text.Json.JsonSerializerOptions
        CreateStrictRecoveryJsonOptions() =>
        new(Tripo.Bridge.BridgeJson.Options)
        {
            DefaultIgnoreCondition =
                System.Text.Json.Serialization.JsonIgnoreCondition.Never,
        };

    private sealed class FakeConnector :
        Tripo.Bridge.IHostSidecarConnector
    {
        private readonly Tripo.Bridge.IHostControlClient _client;

        public FakeConnector(Tripo.Bridge.IHostControlClient client)
        {
            _client = client;
        }

        public Task<Tripo.Bridge.IHostControlClient> EnsureConnectedAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(_client);
    }

    internal sealed class FakeHostControlClient :
        Tripo.Bridge.IHostControlClient
    {
        private readonly int _hostProcessId = Environment.ProcessId;

        public FakeHostControlClient()
        {
            CurrentSessionId = Guid.NewGuid().ToString("D");
        }

        public string CurrentSessionId { get; set; }

        public string Host { get; set; } = "rhino";

        public string? ImportReceiptHostOverride { get; set; }

        public bool AdvertiseGlbHostCapability { get; set; } = true;

        public bool AdvertiseGlbSidecarCapability { get; set; } = true;

        public bool AdvertiseGlbWorkflowCapability
        {
            get =>
                AdvertiseGlbHostCapability &&
                AdvertiseGlbSidecarCapability;
            set
            {
                AdvertiseGlbHostCapability = value;
                AdvertiseGlbSidecarCapability = value;
            }
        }

        public bool FailFirstTextResponse { get; set; }

        public string FirstTextFailureCode { get; set; } =
            "sidecar_unavailable";

        public string? TextFailureCode { get; set; }

        public string? TaskStatusFailureCode { get; set; }

        public string TaskStatusValue { get; set; } = "success";

        public bool FailFirstConversionResponse { get; set; }

        public string FirstConversionFailureCode { get; set; } =
            "sidecar_unavailable";

        public string? GlbImportFailureCode { get; set; }

        public Action<Tripo.Bridge.HostControlCreateTextTaskRequest>?
            BeforeCreateTextCall
        {
            get;
            set;
        }

        public TaskCompletionSource<bool>? HostContextEntered { get; set; }

        public TaskCompletionSource<bool>? ContinueHostContext { get; set; }

        public TaskCompletionSource<bool>? TaskStatusEntered { get; set; }

        public TaskCompletionSource<bool>? ContinueTaskStatus { get; set; }

        public TaskCompletionSource<bool>? OperationStatusEntered
        {
            get;
            set;
        }

        public TaskCompletionSource<bool>? ContinueOperationStatus
        {
            get;
            set;
        }

        public TaskCompletionSource<bool>? CredentialMutationEntered
        {
            get;
            set;
        }

        public TaskCompletionSource<bool>? ContinueCredentialMutation
        {
            get;
            set;
        }

        public int CreateTextCalls { get; private set; }

        public int CreateImageCalls { get; private set; }

        public int CreateConversionCalls { get; private set; }

        public int TaskStatusCalls { get; private set; }

        public int ClearApiKeyCalls { get; private set; }

        public int ImportCalls { get; private set; }

        public int GlbImportCalls { get; private set; }

        public string? LastApiKey { get; private set; }

        public Tripo.Bridge.HostControlOperationStatusReceipt? OperationStatus
        {
            get;
            set;
        }

        public int OperationStatusCalls { get; private set; }

        public Action? BeforeOperationStatusCall { get; set; }

        public Action<int>? AfterOperationStatusCall { get; set; }

        public List<Tripo.Bridge.HostControlCreateTextTaskRequest> TextRequests
        {
            get;
        } = [];

        public Tripo.Bridge.HostControlCreateTextTaskRequest? LastTextRequest =>
            TextRequests.LastOrDefault();

        public List<Tripo.Bridge.HostControlCreateImageTaskRequest>
            ImageRequests { get; } = [];

        public Tripo.Bridge.HostControlCreateImageTaskRequest? LastImageRequest =>
            ImageRequests.LastOrDefault();

        public Tripo.Bridge.HostControlCreateObjConversionRequest?
            LastConversionRequest
        {
            get;
            private set;
        }

        public Tripo.Bridge.HostControlImportGenerationGlbRequest?
            LastGlbImportRequest
        {
            get;
            private set;
        }

        public Tripo.Bridge.HostControlImportObjTaskRequest?
            LastObjImportRequest
        {
            get;
            private set;
        }

        public Task<Tripo.Bridge.HostControlHealthReceipt> GetHealthAsync(
            CancellationToken cancellationToken)
        {
            IReadOnlyList<string> capabilities =
                Tripo.Bridge.HostControlConstants
                    .GetWorkflowCapabilities(Host);
            if (!AdvertiseGlbSidecarCapability)
            {
                capabilities = capabilities
                    .Where(capability => !string.Equals(
                        capability,
                        Tripo.Bridge.HostControlConstants
                            .ImportGenerationGlbMethod,
                        StringComparison.Ordinal))
                    .ToArray();
            }

            return Task.FromResult(
                new Tripo.Bridge.HostControlHealthReceipt(
                    Host,
                    _hostProcessId,
                    Environment.ProcessId,
                    capabilities));
        }

        public Task<Tripo.Bridge.HostControlCredentialStatusReceipt>
            GetCredentialStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(CredentialStatus(hasKey: false));

        public async Task<Tripo.Bridge.HostControlCredentialMutationReceipt>
            SetApiKeyAsync(
                string apiKey,
                bool persist,
                CancellationToken cancellationToken)
        {
            CredentialMutationEntered?.TrySetResult(true);
            if (ContinueCredentialMutation is not null)
            {
                await ContinueCredentialMutation.Task
                    .WaitAsync(cancellationToken);
            }

            LastApiKey = apiKey;
            return new Tripo.Bridge.HostControlCredentialMutationReceipt(
                CredentialStatus(hasKey: true));
        }

        public Task<Tripo.Bridge.HostControlCredentialMutationReceipt>
            ClearApiKeyAsync(CancellationToken cancellationToken)
        {
            ClearApiKeyCalls++;
            return Task.FromResult(
                new Tripo.Bridge.HostControlCredentialMutationReceipt(
                    CredentialStatus(hasKey: false)));
        }

        public async Task<Tripo.Bridge.HostContextReceipt> GetHostContextAsync(
            CancellationToken cancellationToken)
        {
            HostContextEntered?.TrySetResult(true);
            if (ContinueHostContext is not null)
            {
                await ContinueHostContext.Task
                    .WaitAsync(cancellationToken);
            }

            return new Tripo.Bridge.HostContextReceipt(
                Host,
                "8-test",
                _hostProcessId,
                CurrentSessionId,
                "Test.3dm",
                "Meters",
                string.Equals(
                    Host,
                    "rhino",
                    StringComparison.OrdinalIgnoreCase)
                    ? AdvertiseGlbHostCapability
                        ?
                        [
                            Tripo.Bridge.BridgeConstants.ContextMethod,
                            Tripo.Bridge.BridgeConstants.ImportMeshMethod,
                            Tripo.Bridge.BridgeConstants.ImportGlbMethod,
                        ]
                        :
                        [
                            Tripo.Bridge.BridgeConstants.ContextMethod,
                            Tripo.Bridge.BridgeConstants.ImportMeshMethod,
                        ]
                    :
                    [
                        Tripo.Bridge.BridgeConstants.ContextMethod,
                        Tripo.Bridge.BridgeConstants.ImportMeshMethod,
                    ]);
        }

        public Task<Tripo.Bridge.HostControlTextTaskCreationReceipt>
            CreateTextTaskAsync(
                Tripo.Bridge.HostControlCreateTextTaskRequest request,
                CancellationToken cancellationToken)
        {
            BeforeCreateTextCall?.Invoke(request);
            CreateTextCalls++;
            TextRequests.Add(request);
            if (TextFailureCode is not null ||
                FailFirstTextResponse && CreateTextCalls == 1)
            {
                throw new Tripo.Bridge.HostControlCallException(
                    TextFailureCode ?? FirstTextFailureCode,
                    "The response was lost.");
            }

            return Task.FromResult(
                new Tripo.Bridge.HostControlTextTaskCreationReceipt(
                    request.OperationId,
                    "task_source123",
                    "v3.1-20260211"));
        }

        public Task<Tripo.Bridge.HostControlImageTaskCreationReceipt>
            CreateImageTaskAsync(
                Tripo.Bridge.HostControlCreateImageTaskRequest request,
                CancellationToken cancellationToken)
        {
            CreateImageCalls++;
            ImageRequests.Add(request);
            if (TextFailureCode is not null ||
                FailFirstTextResponse && CreateImageCalls == 1)
            {
                throw new Tripo.Bridge.HostControlCallException(
                    TextFailureCode ?? FirstTextFailureCode,
                    "The response was lost.");
            }

            return Task.FromResult(
                new Tripo.Bridge.HostControlImageTaskCreationReceipt(
                    request.OperationId,
                    "task_image456",
                    "v3.1-20260211",
                    request.Image.Sha256));
        }

        public async Task<Tripo.Bridge.HostControlTaskStatusReceipt>
            GetTaskStatusAsync(
                string taskId,
                CancellationToken cancellationToken)
        {
            TaskStatusCalls++;
            TaskStatusEntered?.TrySetResult(true);
            if (ContinueTaskStatus is not null)
            {
                await ContinueTaskStatus.Task
                    .WaitAsync(cancellationToken);
            }

            if (TaskStatusFailureCode is not null)
            {
                throw new Tripo.Bridge.HostControlCallException(
                    TaskStatusFailureCode,
                    "The provider rejected the credential.");
            }

            return new Tripo.Bridge.HostControlTaskStatusReceipt(
                taskId,
                taskId == "task_source123"
                    ? "text_to_model"
                    : taskId == "task_image456"
                        ? "image_to_model"
                        : "convert_model",
                TaskStatusValue,
                string.Equals(
                    TaskStatusValue,
                    "success",
                    StringComparison.OrdinalIgnoreCase)
                    ? 100
                    : 50,
                null,
                null,
                null,
                null,
                null);
        }

        public async Task<Tripo.Bridge.HostControlOperationStatusReceipt>
            GetOperationStatusAsync(
                string operationId,
                CancellationToken cancellationToken)
        {
            OperationStatusCalls++;
            BeforeOperationStatusCall?.Invoke();
            OperationStatusEntered?.TrySetResult(true);
            if (ContinueOperationStatus is not null)
            {
                await ContinueOperationStatus.Task
                    .WaitAsync(cancellationToken);
            }

            if (OperationStatus is null ||
                !string.Equals(
                    OperationStatus.OperationId,
                    operationId,
                    StringComparison.Ordinal))
            {
                throw new Tripo.Bridge.HostControlCallException(
                    "workflow_error",
                    "No local paid operation was found.");
            }

            AfterOperationStatusCall?.Invoke(OperationStatusCalls);
            return OperationStatus;
        }

        public Task<Tripo.Bridge.HostControlObjConversionCreationReceipt>
            CreateObjConversionAsync(
                Tripo.Bridge.HostControlCreateObjConversionRequest request,
                CancellationToken cancellationToken)
        {
            CreateConversionCalls++;
            LastConversionRequest = request;
            if (FailFirstConversionResponse &&
                CreateConversionCalls == 1)
            {
                throw new Tripo.Bridge.HostControlCallException(
                    FirstConversionFailureCode,
                    "The conversion request was rejected.");
            }

            return Task.FromResult(
                new Tripo.Bridge.HostControlObjConversionCreationReceipt(
                    request.OperationId,
                    request.SourceTaskId,
                    "task_conversion123",
                    "OBJ"));
        }

        public Task<Tripo.Bridge.HostControlObjTaskImportReceipt>
            ImportObjTaskAsync(
                Tripo.Bridge.HostControlImportObjTaskRequest request,
                CancellationToken cancellationToken)
        {
            ImportCalls++;
            LastObjImportRequest = request;
            return Task.FromResult(
                new Tripo.Bridge.HostControlObjTaskImportReceipt(
                    request.OperationId,
                    request.ConversionTaskId,
                    null,
                    new Tripo.Bridge.HostImportReceipt(
                        ImportReceiptHostOverride ?? Host,
                        request.DocumentSessionId,
                        request.OperationId,
                        RhinoObjectId,
                        1,
                        1,
                        0,
                        "committed",
                        request.ImportMode == "native"
                            ? "instance"
                            : request.ImportMode,
                        request.ApplyMaterials ? 1 : 0,
                        request.ApplyMaterials ? 1 : 0,
                        null)));
        }

        public Task<Tripo.Bridge.HostControlGenerationGlbImportReceipt>
            ImportGenerationGlbAsync(
                Tripo.Bridge.HostControlImportGenerationGlbRequest request,
                CancellationToken cancellationToken)
        {
            GlbImportCalls++;
            LastGlbImportRequest = request;
            if (GlbImportFailureCode is not null)
            {
                throw new Tripo.Bridge.HostControlCallException(
                    GlbImportFailureCode,
                    "The provider rejected the GLB task read.");
            }

            return Task.FromResult(
                new Tripo.Bridge.HostControlGenerationGlbImportReceipt(
                    request.OperationId,
                    request.GenerationTaskId,
                    2.5m,
                    new Tripo.Bridge.HostImportReceipt(
                        ImportReceiptHostOverride ?? Host,
                        request.DocumentSessionId,
                        request.OperationId,
                        RhinoObjectId,
                        3,
                        1,
                        0,
                        "committed",
                        "glb_instance",
                        1,
                        1,
                        null)));
        }

        public Task<Tripo.Bridge.HostControlHealthReceipt> ShutdownAsync(
            CancellationToken cancellationToken) =>
            GetHealthAsync(cancellationToken);

        private static Tripo.Bridge.HostControlCredentialStatusReceipt
            CredentialStatus(bool hasKey) =>
            new(
                hasKey,
                hasKey ? "store" : "none",
                StoredKeyPresent: hasKey,
                CanClearStoredKey: hasKey,
                PersistenceBackend: "fake",
                UsesWeakerFileFallback: false);
    }
}
