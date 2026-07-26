using Xunit;

namespace Tripo.HostUi.Tests;

public sealed class TripoPanelSessionTests
{
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
    public async Task UnresolvedPaidDispatchCannotChangeTheApiKey()
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

        InvalidOperationException setError =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.SetApiKeyAsync("replacement-key", persist: false));
        InvalidOperationException clearError =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.ClearApiKeyAsync());

        Assert.Contains("cannot change", setError.Message);
        Assert.Contains("cannot change", clearError.Message);
        Assert.Null(client.LastApiKey);
        Assert.Equal(0, client.ClearApiKeyCalls);
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
                    "created-1",
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
    public async Task PaidDispatchLeaseBlocksCredentialMutationUntilTaskIdIsDurable()
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
            Assert.Throws<InvalidOperationException>(
                () => restarted.AcknowledgeRecoveredOperations(
                    "yes",
                    restarted.Recovery.PresentationToken));

            restarted.AcknowledgeRecoveredOperations(
                "RECONCILED",
                restarted.Recovery.PresentationToken);
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
    public async Task ChangedRecoverySetMustBeRenderedBeforeAcknowledgement()
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

            await using Tripo.HostUi.TripoPanelSession observer =
                new(
                    new FakeConnector(new FakeHostControlClient()),
                    new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root));
            Assert.Single(observer.Recovery.Hints);
            string initiallyDisplayedToken =
                observer.Recovery.PresentationToken;
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
                Assert.Throws<InvalidOperationException>(
                    () => observer.AcknowledgeRecoveredOperations(
                        "RECONCILED",
                        initiallyDisplayedToken));

            Assert.Contains("list changed", changed.Message);
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

            observer.AcknowledgeRecoveredOperations(
                "RECONCILED",
                renderedSnapshot.PresentationToken);
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

    private static Tripo.HostUi.TripoPanelSession CreateSession(
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

    private sealed class FakeHostControlClient :
        Tripo.Bridge.IHostControlClient
    {
        private readonly int _hostProcessId = Environment.ProcessId;

        public FakeHostControlClient()
        {
            CurrentSessionId = Guid.NewGuid().ToString("D");
        }

        public string CurrentSessionId { get; set; }

        public string Host { get; set; } = "rhino";

        public bool FailFirstTextResponse { get; set; }

        public Action<Tripo.Bridge.HostControlCreateTextTaskRequest>?
            BeforeCreateTextCall { get; set; }

        public TaskCompletionSource<bool>? HostContextEntered { get; set; }

        public TaskCompletionSource<bool>? ContinueHostContext { get; set; }

        public TaskCompletionSource<bool>? TaskStatusEntered { get; set; }

        public TaskCompletionSource<bool>? ContinueTaskStatus { get; set; }

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

        public int CreateConversionCalls { get; private set; }

        public int ClearApiKeyCalls { get; private set; }

        public int ImportCalls { get; private set; }

        public string? LastApiKey { get; private set; }

        public Tripo.Bridge.HostControlOperationStatusReceipt? OperationStatus
        {
            get;
            set;
        }

        public List<Tripo.Bridge.HostControlCreateTextTaskRequest> TextRequests
        {
            get;
        } = [];

        public Tripo.Bridge.HostControlCreateTextTaskRequest? LastTextRequest =>
            TextRequests.LastOrDefault();

        public Tripo.Bridge.HostControlCreateObjConversionRequest?
            LastConversionRequest { get; private set; }

        public Task<Tripo.Bridge.HostControlHealthReceipt> GetHealthAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(
                new Tripo.Bridge.HostControlHealthReceipt(
                    Host,
                    _hostProcessId,
                    Environment.ProcessId,
                    Tripo.Bridge.HostControlConstants.WorkflowCapabilities));

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
                []);
        }

        public Task<Tripo.Bridge.HostControlTextTaskCreationReceipt>
            CreateTextTaskAsync(
                Tripo.Bridge.HostControlCreateTextTaskRequest request,
                CancellationToken cancellationToken)
        {
            BeforeCreateTextCall?.Invoke(request);
            CreateTextCalls++;
            TextRequests.Add(request);
            if (FailFirstTextResponse && CreateTextCalls == 1)
            {
                throw new Tripo.Bridge.HostControlCallException(
                    "sidecar_unavailable",
                    "The response was lost.");
            }

            return Task.FromResult(
                new Tripo.Bridge.HostControlTextTaskCreationReceipt(
                    request.OperationId,
                    "task_source123",
                    "v3.1-20260211"));
        }

        public async Task<Tripo.Bridge.HostControlTaskStatusReceipt>
            GetTaskStatusAsync(
                string taskId,
                CancellationToken cancellationToken)
        {
            TaskStatusEntered?.TrySetResult(true);
            if (ContinueTaskStatus is not null)
            {
                await ContinueTaskStatus.Task
                    .WaitAsync(cancellationToken);
            }

            return new Tripo.Bridge.HostControlTaskStatusReceipt(
                taskId,
                taskId == "task_source123"
                    ? "text_to_model"
                    : "convert_model",
                "success",
                100,
                null,
                null,
                null,
                null,
                null);
        }

        public Task<Tripo.Bridge.HostControlOperationStatusReceipt>
            GetOperationStatusAsync(
                string operationId,
                CancellationToken cancellationToken) =>
            OperationStatus is not null &&
            string.Equals(
                OperationStatus.OperationId,
                operationId,
                StringComparison.Ordinal)
                ? Task.FromResult(OperationStatus)
                : throw new Tripo.Bridge.HostControlCallException(
                    "workflow_error",
                    "No local paid operation was found.");

        public Task<Tripo.Bridge.HostControlObjConversionCreationReceipt>
            CreateObjConversionAsync(
                Tripo.Bridge.HostControlCreateObjConversionRequest request,
                CancellationToken cancellationToken)
        {
            CreateConversionCalls++;
            LastConversionRequest = request;
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
            return Task.FromResult(
                new Tripo.Bridge.HostControlObjTaskImportReceipt(
                    request.OperationId,
                    request.ConversionTaskId,
                    null,
                    new Tripo.Bridge.HostImportReceipt(
                        Host,
                        request.DocumentSessionId,
                        request.OperationId,
                        "created-1",
                        1,
                        1,
                        0,
                        "committed",
                        request.ImportMode,
                        request.ApplyMaterials ? 1 : 0,
                        request.ApplyMaterials ? 1 : 0,
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
