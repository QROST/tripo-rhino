using Xunit;

namespace Tripo.HostUi.Tests;

public sealed class TripoPanelPresentationTests
{
    [Fact]
    public void ApiKeyPromptPolicyMakesRecoverySessionOnlyAndUsesLatestUuid()
    {
        Tripo.HostUi.PreparedTextGeneration generation = new(
            "a chair",
            10_000,
            false,
            DocumentSessionId,
            "11111111-1111-4111-8111-111111111111");
        Tripo.HostUi.PreparedObjConversion conversion = new(
            "task_source123",
            10_000,
            false,
            DocumentSessionId,
            "22222222-2222-4222-8222-222222222222");
        Tripo.HostUi.PreparedObjImport import = new(
            "task_conversion123",
            "Chair",
            DocumentSessionId,
            "33333333-3333-4333-8333-333333333333",
            "native",
            true);
        Tripo.HostUi.TripoApiKeyPromptPolicy policy =
            Tripo.HostUi.TripoApiKeyPromptPolicy.Create(
                ReadyState() with
                {
                    PreparedGeneration = generation,
                    GenerationDispatchAttempted = true,
                    PreparedConversion = conversion,
                    ConversionDispatchAttempted = true,
                    PreparedImport = import,
                    ImportDispatchAttempted = true,
                });

        Assert.True(policy.Replacing);
        Assert.True(policy.RecoveryMode);
        Assert.True(policy.ExactOriginalKeyRequired);
        Assert.False(policy.PersistAllowed);
        Assert.False(policy.RequiresReplacementConfirmation);
        Assert.Equal(import.OperationId, policy.WorkflowOperationId);
    }

    [Fact]
    public void ApiKeyPromptPolicyKeepsNormalReplacementAttestation()
    {
        Tripo.HostUi.TripoApiKeyPromptPolicy policy =
            Tripo.HostUi.TripoApiKeyPromptPolicy.Create(
                ReadyState());

        Assert.True(policy.Replacing);
        Assert.False(policy.RecoveryMode);
        Assert.False(policy.ExactOriginalKeyRequired);
        Assert.True(policy.PersistAllowed);
        Assert.True(policy.RequiresReplacementConfirmation);
        Assert.Null(policy.WorkflowOperationId);
    }

    [Fact]
    public void ApiKeyPromptPolicyIgnoresUnsentDownstreamUuid()
    {
        Tripo.HostUi.PreparedTextGeneration generation = new(
            "a chair",
            10_000,
            false,
            DocumentSessionId,
            "11111111-1111-4111-8111-111111111111");
        Tripo.HostUi.PreparedObjConversion conversion = new(
            "task_source123",
            10_000,
            false,
            DocumentSessionId,
            "22222222-2222-4222-8222-222222222222");
        Tripo.HostUi.TripoApiKeyPromptPolicy policy =
            Tripo.HostUi.TripoApiKeyPromptPolicy.Create(
                ReadyState() with
                {
                    PreparedGeneration = generation,
                    GenerationDispatchAttempted = true,
                    GenerationReceipt =
                        new Tripo.Bridge.HostControlTextTaskCreationReceipt(
                            generation.OperationId,
                            "task_source123",
                            "v3"),
                    PreparedConversion = conversion,
                });

        Assert.True(policy.RecoveryMode);
        Assert.False(policy.ExactOriginalKeyRequired);
        Assert.Equal(
            generation.OperationId,
            policy.WorkflowOperationId);
    }

    [Fact]
    public void InitialStateIsCompactAndDisconnected()
    {
        Tripo.HostUi.TripoPanelPresentation presentation =
            Tripo.HostUi.TripoPanelPresentation.Create(
                Tripo.HostUi.TripoPanelState.Initial,
                Tripo.HostUi.TripoPanelRecoveryLoadResult.Empty,
                recoveryInspection: null,
                prompt: "a pavilion",
                objectName: "Tripo Model");

        Assert.Equal("Document: not connected", presentation.DocumentStatus);
        Assert.Equal("Not connected", presentation.DocumentSessionId);
        Assert.Equal("API key: unknown", presentation.CredentialStatus);
        Assert.Equal("API key…", presentation.ApiKeyText);
        Assert.Equal("Recovery · Clear", presentation.RecoveryHeader);
        Assert.Equal(
            "Review recovery…",
            presentation.RecoveryActionText);
        Assert.Equal("Not prepared", presentation.GenerationOperationId);
        Assert.Equal("Not created", presentation.GenerationTaskId);
        Assert.Equal("Not started", presentation.GenerationStatus);
        Assert.True(presentation.ConnectEnabled);
        Assert.False(presentation.GenerateEnabled);
        Assert.False(presentation.GenerationProgress.HasValue);
        Assert.False(presentation.GenerationDiagnosticVisible);
        Assert.False(presentation.ConversionDiagnosticVisible);
        Assert.False(presentation.ImportReceiptDetailsVisible);
        Assert.False(presentation.ResultVisible);
    }

    [Fact]
    public void RunningGenerationSeparatesSummaryFromCopyableIds()
    {
        const string operationId =
            "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
        const string taskId = "task_source123";
        Tripo.HostUi.TripoPanelState state =
            ReadyState() with
            {
                PreparedGeneration = new Tripo.HostUi.PreparedTextGeneration(
                    "a chair",
                    10_000,
                    false,
                    DocumentSessionId,
                    operationId),
                GenerationReceipt =
                    new Tripo.Bridge.HostControlTextTaskCreationReceipt(
                        operationId,
                        taskId,
                        "v3.0-20250812"),
                GenerationStatus =
                    new Tripo.Bridge.HostControlTaskStatusReceipt(
                        taskId,
                        "text_to_model",
                        "running",
                        42,
                        null,
                        null,
                        null,
                        null,
                        null),
            };

        Tripo.HostUi.TripoPanelPresentation presentation =
            Tripo.HostUi.TripoPanelPresentation.Create(
                state,
                Tripo.HostUi.TripoPanelRecoveryLoadResult.Empty,
                recoveryInspection: null,
                prompt: "a chair",
                objectName: "Tripo Model");

        Assert.Equal(operationId, presentation.GenerationOperationId);
        Assert.Equal(taskId, presentation.GenerationTaskId);
        Assert.Equal("Running · 42%", presentation.GenerationStatus);
        Assert.Contains(
            "Task status: running",
            presentation.GenerationDiagnostic);
        Assert.Equal(42, presentation.GenerationProgress);
        Assert.True(presentation.GenerationDiagnosticVisible);
        Assert.Equal(
            operationId,
            presentation.LatestPreparedOperationId);
        Assert.False(presentation.GenerateEnabled);
        Assert.True(presentation.RefreshGenerationEnabled);
    }

    [Fact]
    public void RecoveryBlockOffersGuidedPathWithoutOpeningPaidWorkGates()
    {
        Tripo.HostUi.TripoPanelRecoveryLoadResult recovery =
            BlockingGenerationRecovery();
        Tripo.HostUi.TripoPanelState state =
            ReadyState() with
            {
                CredentialStatus =
                    new Tripo.Bridge
                        .HostControlCredentialStatusReceipt(
                            false,
                            "none",
                            false,
                            false,
                            "keychain",
                            false),
            };

        Tripo.HostUi.TripoPanelPresentation presentation =
            Tripo.HostUi.TripoPanelPresentation.Create(
                state,
                recovery,
                "Generation inspection pending.",
                prompt: "a chair",
                objectName: "Tripo Model");

        Assert.True(presentation.RecoveryHasBlock);
        Assert.Equal(
            "Recovery · Review before continuing",
            presentation.RecoveryHeader);
        Assert.Contains(
            "Tripo paused new paid work and API-key changes",
            presentation.RecoveryDetails);
        Assert.Contains(
            "Choose “Review recovery…”",
            presentation.RecoveryDetails);
        Assert.Contains(
            "Generation UUID: cccccccc-cccc-4ccc-8ccc-cccccccccccc",
            presentation.RecoveryDetails);
        Assert.Contains(
            "Generation inspection pending.",
            presentation.RecoveryDetails);
        Assert.True(presentation.ApiKeyEnabled);
        Assert.Equal(
            "Review recovery to set API key…",
            presentation.ApiKeyText);
        Assert.Equal(
            "Review recovery…",
            presentation.RecoveryActionText);
        Assert.False(presentation.GenerateEnabled);
        Assert.False(presentation.RefreshConversionEnabled);
        Assert.True(presentation.CheckRecoveryEnabled);
        Assert.True(presentation.ReviewRecoveryEnabled);
        Assert.True(presentation.PromptEnabled);
        Assert.Equal(
            recovery.PresentationToken,
            presentation.RecoveryToken);
    }

    [Fact]
    public void CurrentUnresolvedPaidDispatchKeepsRecoveryKeyEntryAvailable()
    {
        Tripo.HostUi.TripoPanelPresentation presentation =
            Present(
                ReadyState() with
                {
                    GenerationDispatchAttempted = true,
                },
                BlockingGenerationRecovery());

        Assert.True(presentation.ReviewRecoveryEnabled);
        Assert.True(presentation.ApiKeyEnabled);
        Assert.False(presentation.GenerateEnabled);
        Assert.Equal(
            "Reload and review all work…",
            presentation.RecoveryActionText);
        Assert.Contains(
            "recovery",
            presentation.ApiKeyText,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "preserve dispatched operation IDs",
            presentation.RecoveryDetails);
    }

    [Fact]
    public void MissingKeyDuringPaidRecoveryKeepsSessionOnlyEntryAvailable()
    {
        Tripo.HostUi.TripoPanelPresentation presentation =
            Present(
                ReadyState() with
                {
                    CredentialStatus =
                        new Tripo.Bridge.HostControlCredentialStatusReceipt(
                            false,
                            "none",
                            false,
                            true,
                            "keychain",
                            false),
                    GenerationDispatchAttempted = true,
                });

        Assert.True(presentation.ApiKeyEnabled);
        Assert.Contains(
            "Restore",
            presentation.ApiKeyText,
            StringComparison.Ordinal);
        Assert.False(presentation.GenerateEnabled);
    }

    [Fact]
    public void RecoveryBlockDisablesBothWorkflowRefreshButtons()
    {
        Tripo.HostUi.TripoPanelPresentation presentation =
            Present(
                ReadyState() with
                {
                    GenerationDispatchAttempted = true,
                    ConversionDispatchAttempted = true,
                    ConversionReceipt = new(
                        "dddddddd-dddd-4ddd-8ddd-dddddddddddd",
                        "task_source123",
                        "task_conversion123",
                        "OBJ"),
                },
                BlockingGenerationRecovery());

        Assert.False(presentation.RefreshGenerationEnabled);
        Assert.False(presentation.RefreshConversionEnabled);
    }

    [Fact]
    public void ResumableGenerationUsesSameUuidRetryCopy()
    {
        const string operationId =
            "dddddddd-dddd-4ddd-8ddd-dddddddddddd";
        Tripo.HostUi.TripoPanelState state =
            ReadyState() with
            {
                PreparedGeneration = new Tripo.HostUi.PreparedTextGeneration(
                    "a chair",
                    10_000,
                    false,
                    DocumentSessionId,
                    operationId),
                GenerationDispatchAttempted = true,
                GenerationOperationStatus =
                    new Tripo.Bridge.HostControlOperationStatusReceipt(
                        operationId,
                        "text_generation",
                        "prepared",
                        null,
                        null,
                        null,
                        null,
                        false,
                        false,
                        true,
                        "Retry the same operation.",
                        DateTimeOffset.UnixEpoch),
            };

        Tripo.HostUi.TripoPanelPresentation presentation =
            Tripo.HostUi.TripoPanelPresentation.Create(
                state,
                Tripo.HostUi.TripoPanelRecoveryLoadResult.Empty,
                recoveryInspection: null,
                prompt: "a chair",
                objectName: "Tripo Model");

        Assert.True(presentation.GenerateEnabled);
        Assert.Equal("Retry same UUID", presentation.GenerateText);
        Assert.Equal(
            "Prepared · Ready to send",
            presentation.GenerationStatus);
        Assert.Contains(
            "Operation state: prepared",
            presentation.GenerationDiagnostic);
    }

    [Fact]
    public void BlankRequiredInputsDisableOnlyTheirFirstActions()
    {
        Tripo.HostUi.TripoPanelPresentation blankPrompt =
            Tripo.HostUi.TripoPanelPresentation.Create(
                ReadyState(),
                Tripo.HostUi.TripoPanelRecoveryLoadResult.Empty,
                recoveryInspection: null,
                prompt: " \t",
                objectName: "Tripo Model");
        Tripo.HostUi.TripoPanelState conversionReady =
            ReadyState() with
            {
                ConversionStatus =
                    new Tripo.Bridge.HostControlTaskStatusReceipt(
                        "task_convert123",
                        "model_to_model",
                        "success",
                        100,
                        null,
                        null,
                        null,
                        null,
                        null),
            };
        Tripo.HostUi.TripoPanelPresentation blankName =
            Tripo.HostUi.TripoPanelPresentation.Create(
                conversionReady,
                Tripo.HostUi.TripoPanelRecoveryLoadResult.Empty,
                recoveryInspection: null,
                prompt: "a chair",
                objectName: " ");
        Tripo.HostUi.TripoPanelPresentation validName =
            Tripo.HostUi.TripoPanelPresentation.Create(
                conversionReady,
                Tripo.HostUi.TripoPanelRecoveryLoadResult.Empty,
                recoveryInspection: null,
                prompt: "a chair",
                objectName: "Chair");

        Assert.False(blankPrompt.GenerateEnabled);
        Assert.False(blankName.ImportEnabled);
        Assert.True(validName.ImportEnabled);
    }

    [Fact]
    public void MissingKeyStillDisclosesFallbackBackend()
    {
        Tripo.HostUi.TripoPanelState state =
            ReadyState() with
            {
                CredentialStatus =
                    new Tripo.Bridge.HostControlCredentialStatusReceipt(
                        false,
                        "none",
                        false,
                        false,
                        "private-file",
                        true),
            };

        Tripo.HostUi.TripoPanelPresentation presentation =
            Tripo.HostUi.TripoPanelPresentation.Create(
                state,
                Tripo.HostUi.TripoPanelRecoveryLoadResult.Empty,
                recoveryInspection: null,
                prompt: "a chair",
                objectName: "Chair");

        Assert.Equal(
            "API key: not configured · Source: none " +
            "(private-file fallback)",
            presentation.CredentialStatus);
        Assert.False(presentation.GenerateEnabled);
    }

    [Fact]
    public void OutcomeUnknownIsFriendlyButRawEvidenceRemainsAvailable()
    {
        const string operationId =
            "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee";
        Tripo.HostUi.TripoPanelState state =
            ReadyState() with
            {
                PreparedGeneration = new Tripo.HostUi.PreparedTextGeneration(
                    "a chair",
                    10_000,
                    false,
                    DocumentSessionId,
                    operationId),
                GenerationDispatchAttempted = true,
                GenerationOperationStatus =
                    new Tripo.Bridge.HostControlOperationStatusReceipt(
                        operationId,
                        "text_generation",
                        "outcome_unknown",
                        null,
                        null,
                        "lost_response",
                        "The response was lost.",
                        false,
                        true,
                        false,
                        "Do not resend the paid request.",
                        DateTimeOffset.UnixEpoch),
            };

        Tripo.HostUi.TripoPanelPresentation presentation =
            Tripo.HostUi.TripoPanelPresentation.Create(
                state,
                Tripo.HostUi.TripoPanelRecoveryLoadResult.Empty,
                recoveryInspection: null,
                prompt: "a chair",
                objectName: "Chair");

        Assert.Equal(
            "Outcome unknown · Do not resend",
            presentation.GenerationStatus);
        Assert.Contains(
            "Operation state: outcome_unknown",
            presentation.GenerationDiagnostic);
        Assert.True(presentation.GenerationDiagnosticVisible);
        Assert.Contains(
            "Next action: Do not resend the paid request.",
            presentation.GenerationDiagnostic);
    }

    [Fact]
    public void PresentationPreservesOutOfRangeProgressEvidence()
    {
        Tripo.HostUi.TripoPanelState state =
            ReadyState() with
            {
                GenerationStatus =
                    new Tripo.Bridge.HostControlTaskStatusReceipt(
                        "task_source123",
                        "text_to_model",
                        "running",
                        137,
                        null,
                        null,
                        null,
                        null,
                        null),
            };

        Tripo.HostUi.TripoPanelPresentation presentation =
            Tripo.HostUi.TripoPanelPresentation.Create(
                state,
                Tripo.HostUi.TripoPanelRecoveryLoadResult.Empty,
                recoveryInspection: null,
                prompt: "a chair",
                objectName: "Chair");

        Assert.Equal("Running · 137%", presentation.GenerationStatus);
        Assert.Equal(137, presentation.GenerationProgress);
        Assert.Contains(
            "Task progress: 137%",
            presentation.GenerationDiagnostic);
    }

    [Fact]
    public void BusyAndStrictSuccessStatesRemainFailClosed()
    {
        Tripo.HostUi.TripoPanelPresentation busy =
            Present(ReadyState() with { Busy = true });
        Tripo.HostUi.TripoPanelPresentation wrongGenerationCase =
            Present(
                ReadyState() with
                {
                    GenerationStatus = TaskStatus(
                        "task_source123",
                        "text_to_model",
                        "Success"),
                });
        Tripo.HostUi.TripoPanelPresentation exactGenerationSuccess =
            Present(
                ReadyState() with
                {
                    GenerationStatus = TaskStatus(
                        "task_source123",
                        "text_to_model",
                        "success"),
                });
        Tripo.HostUi.TripoPanelPresentation wrongConversionCase =
            Present(
                ReadyState() with
                {
                    ConversionStatus = TaskStatus(
                        "task_convert123",
                        "model_to_model",
                        "Success"),
                });
        Tripo.HostUi.TripoPanelPresentation exactConversionSuccess =
            Present(
                ReadyState() with
                {
                    ConversionStatus = TaskStatus(
                        "task_convert123",
                        "model_to_model",
                        "success"),
                });

        Assert.False(busy.ConnectEnabled);
        Assert.False(busy.ApiKeyEnabled);
        Assert.False(busy.GenerateEnabled);
        Assert.False(busy.RefreshGenerationEnabled);
        Assert.False(busy.ConvertEnabled);
        Assert.False(busy.RefreshConversionEnabled);
        Assert.False(busy.ImportEnabled);
        Assert.False(busy.ResetEnabled);
        Assert.False(busy.PromptEnabled);
        Assert.False(busy.NameEnabled);
        Assert.False(wrongGenerationCase.ConvertEnabled);
        Assert.True(exactGenerationSuccess.ConvertEnabled);
        Assert.False(wrongConversionCase.ImportEnabled);
        Assert.True(exactConversionSuccess.ImportEnabled);
    }

    [Fact]
    public void PreparedConversionAndImportKeepSameUuidRetryContract()
    {
        const string conversionOperationId =
            "ffffffff-ffff-4fff-8fff-ffffffffffff";
        const string importOperationId =
            "11111111-1111-4111-8111-111111111111";
        Tripo.HostUi.TripoPanelState conversionBase =
            ReadyState() with
            {
                GenerationStatus = TaskStatus(
                    "task_source123",
                    "text_to_model",
                    "success"),
                PreparedConversion =
                    new Tripo.HostUi.PreparedObjConversion(
                        "task_source123",
                        10_000,
                        false,
                        DocumentSessionId,
                        conversionOperationId),
            };

        Tripo.HostUi.TripoPanelPresentation firstSend =
            Present(conversionBase);
        Tripo.HostUi.TripoPanelPresentation refreshBeforeRetry =
            Present(
                conversionBase with
                {
                    ConversionDispatchAttempted = true,
                });
        Tripo.HostUi.TripoPanelPresentation retrySameUuid =
            Present(
                conversionBase with
                {
                    ConversionDispatchAttempted = true,
                    ConversionOperationStatus =
                        ResumableOperation(
                            conversionOperationId,
                            "obj_conversion"),
                });
        Tripo.HostUi.TripoPanelPresentation importRetry =
            Present(
                ReadyState() with
                {
                    ConversionStatus = TaskStatus(
                        "task_convert123",
                        "model_to_model",
                        "success"),
                    PreparedImport = new Tripo.HostUi.PreparedObjImport(
                        "task_convert123",
                        "Chair",
                        DocumentSessionId,
                        importOperationId,
                        "native",
                        false),
                    ImportDispatchAttempted = true,
                },
                objectName: " ");

        Assert.True(firstSend.ConvertEnabled);
        Assert.Equal("Send prepared", firstSend.ConvertText);
        Assert.False(refreshBeforeRetry.ConvertEnabled);
        Assert.Equal(
            "Refresh before retry",
            refreshBeforeRetry.ConvertText);
        Assert.True(refreshBeforeRetry.RefreshConversionEnabled);
        Assert.True(retrySameUuid.ConvertEnabled);
        Assert.Equal("Retry same UUID", retrySameUuid.ConvertText);
        Assert.Equal(
            conversionOperationId,
            retrySameUuid.LatestPreparedOperationId);
        Assert.True(importRetry.ImportEnabled);
        Assert.Equal("Retry same UUID", importRetry.ImportText);
        Assert.Equal(
            importOperationId,
            importRetry.LatestPreparedOperationId);
    }

    [Fact]
    public void PreparedGenerationRetryIgnoresEditedLivePrompt()
    {
        const string operationId =
            "22222222-2222-4222-8222-222222222222";
        Tripo.HostUi.TripoPanelState state =
            ReadyState() with
            {
                PreparedGeneration =
                    new Tripo.HostUi.PreparedTextGeneration(
                        "the frozen prompt",
                        10_000,
                        false,
                        DocumentSessionId,
                        operationId),
                GenerationDispatchAttempted = true,
                GenerationOperationStatus =
                    ResumableOperation(
                        operationId,
                        "text_generation"),
            };

        Tripo.HostUi.TripoPanelPresentation presentation =
            Present(state, prompt: " ");

        Assert.True(presentation.GenerateEnabled);
        Assert.Equal("Retry same UUID", presentation.GenerateText);
        Assert.Equal(
            operationId,
            presentation.LatestPreparedOperationId);
    }

    [Fact]
    public void ResultVisibilityUsesStateAndCurrentEvidenceWins()
    {
        Tripo.Bridge.HostControlObjTaskImportReceipt importReceipt =
            ImportReceipt();
        Tripo.HostUi.TripoPanelPresentation busy =
            Present(ReadyState() with { Busy = true });
        Tripo.HostUi.TripoPanelPresentation literalReadyError =
            Present(ReadyState() with { LastError = "Ready." });
        Tripo.HostUi.TripoPanelPresentation imported =
            Present(ReadyState() with { ImportReceipt = importReceipt });
        Tripo.HostUi.TripoPanelPresentation alreadyImported =
            Present(
                ReadyState() with
                {
                    ImportReceipt = ImportReceipt("already_exists"),
                });
        Tripo.HostUi.TripoPanelPresentation unknownReceipt =
            Present(
                ReadyState() with
                {
                    ImportReceipt = ImportReceipt("future_status"),
                });
        Tripo.HostUi.TripoPanelPresentation errorAfterImport =
            Present(
                ReadyState() with
                {
                    ImportReceipt = importReceipt,
                    LastError = "Refresh failed.",
                });

        Assert.True(busy.ResultVisible);
        Assert.Equal("Working…", busy.ResultStatus);
        Assert.True(literalReadyError.ResultVisible);
        Assert.Equal("Ready.", literalReadyError.ResultStatus);
        Assert.True(imported.ResultVisible);
        Assert.Equal("Imported into Rhino", imported.ResultStatus);
        Assert.Equal(
            "rhino-object-1",
            imported.ImportCreatedObjectId);
        Assert.Equal(
            "committed",
            imported.ImportTransactionStatus);
        Assert.True(imported.ImportReceiptDetailsVisible);
        Assert.Equal(
            "Already imported in Rhino",
            alreadyImported.ResultStatus);
        Assert.Equal(
            "already_exists",
            alreadyImported.ImportTransactionStatus);
        Assert.Equal(
            "Import receipt available · See details",
            unknownReceipt.ResultStatus);
        Assert.Equal(
            "future_status",
            unknownReceipt.ImportTransactionStatus);
        Assert.True(errorAfterImport.ResultVisible);
        Assert.Equal(
            "Refresh failed.",
            errorAfterImport.ResultStatus);
    }

    [Fact]
    public void RecoveryIssuesRequireManualRepairAndCannotBeUnlocked()
    {
        Tripo.HostUi.TripoPanelRecoveryLoadResult recovery =
            new(
                [],
                [
                    new Tripo.HostUi.TripoPanelRecoveryIssue(
                        "blocked.json",
                        "invalid_json",
                        "The recovery record could not be parsed."),
                ]);

        Tripo.HostUi.TripoPanelPresentation presentation =
            Present(ReadyState(), recovery);

        Assert.True(presentation.RecoveryHasBlock);
        Assert.Contains("invalid_json", presentation.RecoveryDetails);
        Assert.Contains(
            "cannot safely read one or more local recovery records",
            presentation.RecoveryDetails);
        Assert.True(presentation.CheckRecoveryEnabled);
        Assert.False(presentation.ReviewRecoveryEnabled);
        Assert.False(presentation.GenerateEnabled);
        Assert.False(presentation.ApiKeyEnabled);
        Assert.Equal(
            "Recovery needs attention…",
            presentation.ApiKeyText);
    }

    [Fact]
    public void MixedRecoveryIssuesKeepInspectionButDisableUnsafeUnlock()
    {
        Tripo.HostUi.TripoPanelRecoveryLoadResult hints =
            BlockingGenerationRecovery();
        Tripo.HostUi.TripoPanelRecoveryLoadResult mixed =
            new(
                hints.Hints,
                [
                    new Tripo.HostUi.TripoPanelRecoveryIssue(
                        "blocked.json",
                        "invalid_json",
                        "The recovery record could not be parsed."),
                ]);

        Tripo.HostUi.TripoPanelPresentation presentation =
            Present(ReadyState(), mixed);

        Assert.True(presentation.CheckRecoveryEnabled);
        Assert.False(presentation.ReviewRecoveryEnabled);
        Assert.False(presentation.ApiKeyEnabled);
        Assert.False(presentation.GenerateEnabled);
    }

    private const string DocumentSessionId =
        "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";

    private static Tripo.HostUi.TripoPanelPresentation Present(
        Tripo.HostUi.TripoPanelState state,
        Tripo.HostUi.TripoPanelRecoveryLoadResult? recovery = null,
        string prompt = "a chair",
        string objectName = "Chair") =>
        Tripo.HostUi.TripoPanelPresentation.Create(
            state,
            recovery ??
                Tripo.HostUi.TripoPanelRecoveryLoadResult.Empty,
            recoveryInspection: null,
            prompt,
            objectName);

    private static Tripo.HostUi.TripoPanelRecoveryLoadResult
        BlockingGenerationRecovery() =>
        new(
            [
                new Tripo.HostUi.LoadedTripoPanelRecoveryHint(
                    "recovery.json",
                    new Tripo.HostUi.TripoPanelRecoveryHint(
                        Tripo.HostUi.TripoPanelRecoveryStore
                            .CurrentSchemaVersion,
                        "recovery-id",
                        "rhino",
                        123,
                        DateTimeOffset.UnixEpoch,
                        DocumentSessionId,
                        DateTimeOffset.UnixEpoch,
                        new Tripo.HostUi.TripoPanelPaidRecoveryHint(
                            "cccccccc-cccc-4ccc-8ccc-cccccccccccc",
                            true,
                            null,
                            "outcome_unknown",
                            false,
                            false),
                        null,
                        null)),
            ],
            []);

    private static Tripo.Bridge.HostControlTaskStatusReceipt TaskStatus(
        string taskId,
        string type,
        string status) =>
        new(
            taskId,
            type,
            status,
            status == "success" ? 100 : 42,
            null,
            null,
            null,
            null,
            null);

    private static Tripo.Bridge.HostControlOperationStatusReceipt
        ResumableOperation(
            string operationId,
            string kind) =>
        new(
            operationId,
            kind,
            "prepared",
            null,
            null,
            null,
            null,
            false,
            false,
            true,
            "Retry the same operation.",
            DateTimeOffset.UnixEpoch);

    private static Tripo.Bridge.HostControlObjTaskImportReceipt
        ImportReceipt(string transactionStatus = "committed") =>
        new(
            "33333333-3333-4333-8333-333333333333",
            "task_convert123",
            null,
            new Tripo.Bridge.HostImportReceipt(
                "rhino",
                DocumentSessionId,
                "33333333-3333-4333-8333-333333333333",
                "rhino-object-1",
                12,
                10,
                0,
                transactionStatus,
                "native",
                1,
                1,
                null));

    private static Tripo.HostUi.TripoPanelState ReadyState() =>
        Tripo.HostUi.TripoPanelState.Initial with
        {
            Connected = true,
            Context = new Tripo.Bridge.HostContextReceipt(
                "rhino",
                "8-test",
                123,
                DocumentSessionId,
                "Test.3dm",
                "Meters",
                []),
            CredentialStatus =
                new Tripo.Bridge.HostControlCredentialStatusReceipt(
                    true,
                    "macOS Keychain",
                    true,
                    true,
                    "keychain",
                    false),
        };
}
