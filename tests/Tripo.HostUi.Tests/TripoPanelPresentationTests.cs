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
    public void ApiKeyPromptPolicyUsesImageGenerationUuid()
    {
        Tripo.HostUi.PreparedImageGeneration generation = new(
            new Tripo.Bridge.StagedImageTransfer(
                "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
                new string('a', 64),
                128,
                "image/png"),
            10_000,
            false,
            DocumentSessionId,
            "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
        Tripo.HostUi.TripoApiKeyPromptPolicy policy =
            Tripo.HostUi.TripoApiKeyPromptPolicy.Create(
                ReadyState() with
                {
                    PreparedImageGeneration = generation,
                    GenerationDispatchAttempted = true,
                });

        Assert.True(policy.RecoveryMode);
        Assert.True(policy.ExactOriginalKeyRequired);
        Assert.Equal(generation.OperationId, policy.WorkflowOperationId);
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
        Assert.False(presentation.ApiKeyEnabled);
        Assert.Contains(
            "Connect",
            presentation.ApiKeyHelp,
            StringComparison.OrdinalIgnoreCase);
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
        Assert.False(presentation.WorkflowStatusVisible);
        Assert.False(presentation.GenerationDiagnosticVisible);
        Assert.False(presentation.ConversionDiagnosticVisible);
        Assert.False(presentation.ImportReceiptDetailsVisible);
        Assert.False(presentation.ResultVisible);
        Assert.False(presentation.ClearApiKeyEnabled);
        Assert.False(presentation.ResetVisible);
        Assert.Contains(
            "Connect",
            presentation.ClearApiKeyHelp,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AutomaticOutcomeWithoutWorkflowStillOffersReset()
    {
        Tripo.HostUi.TripoPanelPresentation presentation =
            Present(
                ReadyState(),
                importSource: "glb",
                directGlbCreateStage:
                    Tripo.HostUi.DirectGlbCreateUiStage.Refused);

        Assert.True(presentation.ResetVisible);
        Assert.True(presentation.ResetEnabled);
        Assert.False(presentation.WorkflowStatusVisible);
    }

    [Fact]
    public void SavedApiKeyRemovalRequiresKnownClearableStoredKey()
    {
        Tripo.HostUi.TripoPanelPresentation available =
            Present(ReadyState());
        Tripo.HostUi.TripoPanelPresentation unknownPresence =
            Present(
                ReadyState() with
                {
                    CredentialStatus =
                        new Tripo.Bridge.HostControlCredentialStatusReceipt(
                            true,
                            "store",
                            true,
                            true,
                            "keychain",
                            false,
                            StoredKeyPresenceKnown: false),
                });
        Tripo.HostUi.TripoPanelPresentation noStoredKey =
            Present(
                ReadyState() with
                {
                    CredentialStatus =
                        new Tripo.Bridge.HostControlCredentialStatusReceipt(
                            false,
                            "none",
                            false,
                            false,
                            "keychain",
                            false),
                });
        Tripo.HostUi.TripoPanelPresentation cannotClear =
            Present(
                ReadyState() with
                {
                    CredentialStatus =
                        new Tripo.Bridge.HostControlCredentialStatusReceipt(
                            true,
                            "store",
                            true,
                            false,
                            "unsupported",
                            false),
                });
        Tripo.HostUi.TripoPanelPresentation environmentOverride =
            Present(
                ReadyState() with
                {
                    CredentialStatus =
                        new Tripo.Bridge.HostControlCredentialStatusReceipt(
                            true,
                            "environment",
                            false,
                            false,
                            "keychain",
                            false,
                            StoredKeyPresenceKnown: false),
                });

        Assert.True(available.ClearApiKeyEnabled);
        Assert.True(available.ApiKeyEnabled);
        Assert.Contains(
            "Remove",
            available.ClearApiKeyHelp,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(unknownPresence.ClearApiKeyEnabled);
        Assert.Contains(
            "unknown",
            unknownPresence.ClearApiKeyHelp,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(noStoredKey.ClearApiKeyEnabled);
        Assert.Contains(
            "No OS-stored",
            noStoredKey.ClearApiKeyHelp,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(cannotClear.ClearApiKeyEnabled);
        Assert.Contains(
            "cannot clear",
            cannotClear.ClearApiKeyHelp,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(environmentOverride.ApiKeyEnabled);
        Assert.Contains(
            "TRIPO_API_KEY",
            environmentOverride.ApiKeyHelp,
            StringComparison.Ordinal);
        Assert.Contains(
            "restart Rhino",
            environmentOverride.ApiKeyHelp,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(environmentOverride.ClearApiKeyEnabled);
        Assert.Contains(
            "TRIPO_API_KEY",
            environmentOverride.ClearApiKeyHelp,
            StringComparison.Ordinal);
        Assert.Contains(
            "restart Rhino",
            environmentOverride.ClearApiKeyHelp,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SavedApiKeyRemovalStaysBlockedDuringUnsafeWorkflowStates()
    {
        Tripo.HostUi.TripoPanelPresentation busy =
            Present(ReadyState() with { Busy = true });
        Tripo.HostUi.TripoPanelPresentation accountBound =
            Present(
                ReadyState() with
                {
                    GenerationDispatchAttempted = true,
                });
        Tripo.HostUi.TripoPanelPresentation recoveryBlocked =
            Present(
                ReadyState(),
                BlockingGenerationRecovery());

        Assert.False(busy.ClearApiKeyEnabled);
        Assert.Contains(
            "Wait",
            busy.ClearApiKeyHelp,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(accountBound.ClearApiKeyEnabled);
        Assert.Contains(
            "account-bound workflow",
            accountBound.ClearApiKeyHelp,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(recoveryBlocked.ClearApiKeyEnabled);
        Assert.Contains(
            "Reconcile",
            recoveryBlocked.ClearApiKeyHelp,
            StringComparison.OrdinalIgnoreCase);
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
        Assert.True(presentation.WorkflowStatusVisible);
        Assert.False(presentation.ResetEnabled);
        Assert.False(presentation.ResetVisible);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("queued", false)]
    [InlineData("running", false)]
    [InlineData("success", true)]
    [InlineData("failed", true)]
    [InlineData("cancelled", true)]
    [InlineData("banned", true)]
    [InlineData("expired", true)]
    [InlineData("future-state", false)]
    public void ResetRequiresConfirmedTerminalPaidTask(
        string? status,
        bool expectedEnabled)
    {
        const string operationId =
            "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
        const string taskId = "task_source123";
        Tripo.HostUi.TripoPanelState state =
            ReadyState() with
            {
                PreparedGeneration =
                    new Tripo.HostUi.PreparedTextGeneration(
                        "a chair",
                        10_000,
                        false,
                        DocumentSessionId,
                        operationId),
                GenerationDispatchAttempted = true,
                GenerationReceipt =
                    new Tripo.Bridge.HostControlTextTaskCreationReceipt(
                        operationId,
                        taskId,
                        "v3"),
                GenerationStatus = status is null
                    ? null
                    : TaskStatus(
                        taskId,
                        "text_to_model",
                        status),
            };

        Tripo.HostUi.TripoPanelPresentation presentation =
            Present(state);

        Assert.Equal(expectedEnabled, state.CanResetWorkflow);
        Assert.Equal(expectedEnabled, presentation.ResetEnabled);
        Assert.Equal(expectedEnabled, presentation.ResetVisible);
    }

    [Fact]
    public void ResetRejectsConflictingTaskIdentityEvidence()
    {
        const string generationOperationId =
            "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
        const string conversionOperationId =
            "cccccccc-cccc-4ccc-8ccc-cccccccccccc";
        const string importOperationId =
            "dddddddd-dddd-4ddd-8ddd-dddddddddddd";
        Tripo.HostUi.TripoPanelState terminalGeneration =
            ReadyState() with
            {
                PreparedGeneration =
                    new Tripo.HostUi.PreparedTextGeneration(
                        "a chair",
                        10_000,
                        false,
                        DocumentSessionId,
                        generationOperationId),
                GenerationDispatchAttempted = true,
                GenerationReceipt =
                    new Tripo.Bridge.HostControlTextTaskCreationReceipt(
                        generationOperationId,
                        "task_source123",
                        "v3"),
                GenerationStatus = TaskStatus(
                    "task_source123",
                    "text_to_model",
                    "success"),
            };
        Tripo.HostUi.TripoPanelState terminalConversion =
            terminalGeneration with
            {
                PreparedConversion =
                    new Tripo.HostUi.PreparedObjConversion(
                        "task_source123",
                        10_000,
                        false,
                        DocumentSessionId,
                        conversionOperationId),
                ConversionDispatchAttempted = true,
                ConversionReceipt =
                    new Tripo.Bridge
                        .HostControlObjConversionCreationReceipt(
                            conversionOperationId,
                            "task_source123",
                            "task_conversion123",
                            "OBJ"),
                ConversionStatus = TaskStatus(
                    "task_conversion123",
                    "convert_model",
                    "success"),
            };
        Tripo.HostUi.PreparedObjImport preparedImport = new(
            "task_conversion123",
            "Chair",
            DocumentSessionId,
            importOperationId,
            "native",
            true,
            "obj");
        Tripo.HostUi.TripoPanelImportReceipt validImportReceipt = new(
            importOperationId,
            "task_conversion123",
            "obj",
            null,
            new Tripo.Bridge.HostImportReceipt(
                "rhino",
                DocumentSessionId,
                importOperationId,
                RhinoObjectId,
                12,
                10,
                0,
                "committed",
                "instance",
                1,
                1,
                null));
        Tripo.HostUi.TripoPanelState terminalImport =
            terminalConversion with
            {
                PreparedImport = preparedImport,
                ImportDispatchAttempted = true,
                ImportReceipt = validImportReceipt,
            };
        Tripo.HostUi.PreparedObjImport preparedDirectImport = new(
            "task_source123",
            "Chair",
            DocumentSessionId,
            importOperationId,
            "glb_instance",
            true,
            "glb");
        Tripo.HostUi.TripoPanelImportReceipt validDirectImportReceipt =
            DirectGlbImportReceipt(
                importOperationId,
                "task_source123",
                "committed");
        Tripo.HostUi.TripoPanelState terminalDirectImport =
            terminalGeneration with
            {
                PreparedImport = preparedDirectImport,
                ImportDispatchAttempted = true,
                ImportReceipt = validDirectImportReceipt,
            };

        Tripo.HostUi.TripoPanelState wrongStatusId =
            terminalGeneration with
            {
                GenerationStatus = TaskStatus(
                    "task_other",
                    "text_to_model",
                    "success"),
            };
        Tripo.HostUi.TripoPanelState paddedStatusId =
            terminalGeneration with
            {
                GenerationStatus = TaskStatus(
                    " task_source123 ",
                    "text_to_model",
                    "success"),
            };
        Tripo.HostUi.TripoPanelState generationEvidenceConflict =
            terminalGeneration with
            {
                GenerationOperationStatus = DurableOperation(
                    generationOperationId,
                    "text_task_creation",
                    "task_other"),
            };
        Tripo.HostUi.TripoPanelState conversionEvidenceConflict =
            terminalConversion with
            {
                ConversionOperationStatus = DurableOperation(
                    conversionOperationId,
                    "obj_conversion_creation",
                    "task_other",
                    "task_source123"),
            };
        Tripo.HostUi.TripoPanelState conversionReceiptOperationConflict =
            terminalConversion with
            {
                ConversionReceipt =
                    terminalConversion.ConversionReceipt! with
                    {
                        OperationId = generationOperationId,
                    },
            };
        Tripo.HostUi.TripoPanelState conversionReceiptSourceConflict =
            terminalConversion with
            {
                ConversionReceipt =
                    terminalConversion.ConversionReceipt! with
                    {
                        SourceTaskId = "task_other",
                    },
            };
        Tripo.HostUi.TripoPanelState conversionReceiptFormatConflict =
            terminalConversion with
            {
                ConversionReceipt =
                    terminalConversion.ConversionReceipt! with
                    {
                        Format = "obj",
                    },
            };
        Tripo.HostUi.TripoPanelState importReceiptOperationConflict =
            terminalImport with
            {
                ImportReceipt = validImportReceipt with
                {
                    OperationId = generationOperationId,
                },
            };
        Tripo.HostUi.TripoPanelState importReceiptSourceConflict =
            terminalImport with
            {
                ImportReceipt = validImportReceipt with
                {
                    SourceTaskId = "task_other",
                },
            };
        Tripo.HostUi.TripoPanelState importReceiptFormatConflict =
            terminalImport with
            {
                ImportReceipt = validImportReceipt with
                {
                    ArtifactFormat = "glb",
                },
            };
        Tripo.HostUi.TripoPanelState importHostOperationConflict =
            terminalImport with
            {
                ImportReceipt = validImportReceipt with
                {
                    HostReceipt = validImportReceipt.HostReceipt with
                    {
                        IdempotencyKey = generationOperationId,
                    },
                },
            };
        Tripo.HostUi.TripoPanelState importHostDocumentConflict =
            terminalImport with
            {
                ImportReceipt = validImportReceipt with
                {
                    HostReceipt = validImportReceipt.HostReceipt with
                    {
                        DocumentSessionId = "rhino:other",
                    },
                },
            };
        Tripo.HostUi.TripoPanelState importHostConflict =
            terminalImport with
            {
                ImportReceipt = validImportReceipt with
                {
                    HostReceipt = validImportReceipt.HostReceipt with
                    {
                        Host = "revit",
                    },
                },
            };
        Tripo.HostUi.TripoPanelState importCreatedIdConflict =
            terminalImport with
            {
                ImportReceipt = validImportReceipt with
                {
                    HostReceipt = validImportReceipt.HostReceipt with
                    {
                        CreatedId = "not-a-rhino-guid",
                    },
                },
            };
        Tripo.HostUi.TripoPanelState importModeConflict =
            terminalImport with
            {
                ImportReceipt = validImportReceipt with
                {
                    HostReceipt = validImportReceipt.HostReceipt with
                    {
                        ImportMode = "mesh",
                    },
                },
            };
        Tripo.HostUi.TripoPanelState conversionUpstreamConflict =
            terminalConversion with
            {
                PreparedConversion =
                    terminalConversion.PreparedConversion! with
                    {
                        SourceTaskId = "task_other",
                    },
                ConversionReceipt =
                    terminalConversion.ConversionReceipt! with
                    {
                        SourceTaskId = "task_other",
                    },
            };
        Tripo.HostUi.TripoPanelState objImportUpstreamConflict =
            terminalImport with
            {
                PreparedImport = preparedImport with
                {
                    ConversionTaskId = "task_other",
                },
                ImportReceipt = validImportReceipt with
                {
                    SourceTaskId = "task_other",
                },
            };
        Tripo.HostUi.TripoPanelState directImportUpstreamConflict =
            terminalDirectImport with
            {
                PreparedImport = preparedDirectImport with
                {
                    ConversionTaskId = "task_other",
                },
                ImportReceipt = validDirectImportReceipt with
                {
                    SourceTaskId = "task_other",
                },
            };
        Tripo.HostUi.TripoPanelState importFutureTransaction =
            terminalImport with
            {
                ImportReceipt = validImportReceipt with
                {
                    HostReceipt = validImportReceipt.HostReceipt with
                    {
                        TransactionStatus = "future_status",
                    },
                },
            };
        Tripo.HostUi.TripoPanelState importAlreadyExists =
            terminalImport with
            {
                ImportReceipt = validImportReceipt with
                {
                    HostReceipt = validImportReceipt.HostReceipt with
                    {
                        TransactionStatus = "already_exists",
                    },
                },
            };

        Assert.True(terminalImport.CanResetWorkflow);
        Assert.True(terminalDirectImport.CanResetWorkflow);
        Assert.True(importAlreadyExists.CanResetWorkflow);
        Assert.False(wrongStatusId.CanResetWorkflow);
        Assert.False(paddedStatusId.CanResetWorkflow);
        Assert.False(generationEvidenceConflict.CanResetWorkflow);
        Assert.False(conversionEvidenceConflict.CanResetWorkflow);
        Assert.False(conversionReceiptOperationConflict.CanResetWorkflow);
        Assert.False(conversionReceiptSourceConflict.CanResetWorkflow);
        Assert.False(conversionReceiptFormatConflict.CanResetWorkflow);
        Assert.False(importReceiptOperationConflict.CanResetWorkflow);
        Assert.False(importReceiptSourceConflict.CanResetWorkflow);
        Assert.False(importReceiptFormatConflict.CanResetWorkflow);
        Assert.False(importHostOperationConflict.CanResetWorkflow);
        Assert.False(importHostDocumentConflict.CanResetWorkflow);
        Assert.False(importHostConflict.CanResetWorkflow);
        Assert.False(importCreatedIdConflict.CanResetWorkflow);
        Assert.False(importModeConflict.CanResetWorkflow);
        Assert.False(conversionUpstreamConflict.CanResetWorkflow);
        Assert.False(objImportUpstreamConflict.CanResetWorkflow);
        Assert.False(directImportUpstreamConflict.CanResetWorkflow);
        Assert.False(importFutureTransaction.CanResetWorkflow);
        Assert.True(importFutureTransaction.HasUnresolvedImport);
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
            "Review, then set key…",
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
            "restore key",
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
                objectName: " ",
                importSource: "obj");
        Tripo.HostUi.TripoPanelPresentation validName =
            Tripo.HostUi.TripoPanelPresentation.Create(
                conversionReady,
                Tripo.HostUi.TripoPanelRecoveryLoadResult.Empty,
                recoveryInspection: null,
                prompt: "a chair",
                objectName: "Chair",
                importSource: "obj");

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
            "API key: not configured · private-file fallback",
            presentation.CredentialStatus);
        Assert.False(presentation.GenerateEnabled);
    }

    [Theory]
    [InlineData("store", false, "API key: saved")]
    [InlineData("session", false, "API key: session only")]
    [InlineData(
        "environment",
        false,
        "API key: environment override")]
    [InlineData(
        "macOS Keychain",
        false,
        "API key: saved in macOS Keychain")]
    [InlineData(
        "store",
        true,
        "API key: saved · private-file fallback")]
    [InlineData("managed vault", false, "API key: managed vault")]
    public void CredentialStatusUsesHumanReadableSources(
        string source,
        bool usesWeakerFileFallback,
        string expected)
    {
        Tripo.HostUi.TripoPanelPresentation presentation =
            Present(
                ReadyState() with
                {
                    CredentialStatus =
                        new Tripo.Bridge.HostControlCredentialStatusReceipt(
                            true,
                            source,
                            true,
                            true,
                            "keychain",
                            usesWeakerFileFallback),
                });

        Assert.Equal(expected, presentation.CredentialStatus);
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
        Tripo.HostUi.TripoPanelState importedState =
            CompletedDirectGlbImportState();
        Tripo.HostUi.TripoPanelPresentation busy =
            Present(ReadyState() with { Busy = true });
        Tripo.HostUi.TripoPanelPresentation literalReadyError =
            Present(ReadyState() with { LastError = "Ready." });
        Tripo.HostUi.TripoPanelPresentation imported =
            Present(importedState);
        Tripo.HostUi.TripoPanelPresentation alreadyImported =
            Present(CompletedDirectGlbImportState("already_exists"));
        Tripo.HostUi.TripoPanelPresentation unknownReceipt =
            Present(CompletedDirectGlbImportState("future_status"));
        Tripo.HostUi.TripoPanelPresentation errorAfterImport =
            Present(
                importedState with
                {
                    LastError = "Refresh failed.",
                });

        Assert.True(busy.ResultVisible);
        Assert.Equal("Working…", busy.ResultStatus);
        Assert.True(literalReadyError.ResultVisible);
        Assert.Equal("Ready.", literalReadyError.ResultStatus);
        Assert.True(imported.ResultVisible);
        Assert.Equal("Imported into Rhino", imported.ResultStatus);
        Assert.Equal(
            RhinoObjectId,
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
            "Import receipt conflicts with this Rhino workflow · " +
            "Review details",
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
    public void CreateInRhinoRequiresDirectGlbCapabilityAndKeepsObjManual()
    {
        Tripo.HostUi.TripoPanelState directReady =
            ReadyState() with
            {
                Context = ReadyState().Context! with
                {
                    Capabilities =
                    [
                        Tripo.Bridge.BridgeConstants.ContextMethod,
                        Tripo.Bridge.BridgeConstants.ImportGlbMethod,
                    ],
                },
            };

        Tripo.HostUi.TripoPanelPresentation direct =
            Present(directReady, importSource: "glb");
        Tripo.HostUi.TripoPanelPresentation preflighting =
            Present(
                directReady,
                importSource: "glb",
                directGlbCreateStage:
                    Tripo.HostUi.DirectGlbCreateUiStage.Preflighting);
        Tripo.HostUi.TripoPanelPresentation capabilitySkew =
            Present(ReadyState(), importSource: "glb");
        Tripo.HostUi.TripoPanelPresentation obj =
            Present(directReady, importSource: "obj");

        Assert.True(direct.CreateInRhinoEnabled);
        Assert.True(direct.CanStartDirectGlbCreate);
        Assert.Equal("Create in Rhino", direct.CreateInRhinoText);
        Assert.Contains("can consume credits", direct.CreateInRhinoHelp);
        Assert.Contains(
            "Clicking Create in Rhino authorizes",
            direct.CreateInRhinoHelp);
        Assert.Contains(
            "No separate OBJ conversion request",
            direct.CreateInRhinoHelp);
        Assert.True(direct.CreateInRhinoGuidanceVisible);
        Assert.True(preflighting.CanStartDirectGlbCreate);
        Assert.False(preflighting.CreateInRhinoEnabled);
        Assert.Equal("Checking…", preflighting.CreateInRhinoText);
        Assert.False(preflighting.ConnectEnabled);
        Assert.False(preflighting.GenerateEnabled);
        Assert.False(capabilitySkew.CreateInRhinoEnabled);
        Assert.True(capabilitySkew.CreateInRhinoGuidanceVisible);
        Assert.Contains("matching build", capabilitySkew.CreateInRhinoHelp);
        Assert.False(obj.CreateInRhinoEnabled);
        Assert.True(obj.CreateInRhinoGuidanceVisible);
        Assert.Contains("selected in Advanced", obj.CreateInRhinoHelp);
        Assert.Contains("separate manual path", obj.CreateInRhinoHelp);
        Assert.True(obj.GenerateEnabled);
    }

    [Fact]
    public void CreateInRhinoValidatesPromptAndNameBeforePaidGeneration()
    {
        Tripo.HostUi.TripoPanelState state =
            ReadyState() with
            {
                Context = ReadyState().Context! with
                {
                    Capabilities =
                    [
                        Tripo.Bridge.BridgeConstants.ContextMethod,
                        Tripo.Bridge.BridgeConstants.ImportGlbMethod,
                    ],
                },
            };

        Tripo.HostUi.TripoPanelPresentation blankPrompt =
            Present(state, prompt: " ", importSource: "glb");
        Tripo.HostUi.TripoPanelPresentation longPrompt =
            Present(
                state,
                prompt: new string('p', 1025),
                importSource: "glb");
        Tripo.HostUi.TripoPanelPresentation blankName =
            Present(state, objectName: " ", importSource: "glb");
        Tripo.HostUi.TripoPanelPresentation longName =
            Present(
                state,
                objectName: new string('n', 129),
                importSource: "glb");

        Assert.False(blankPrompt.CreateInRhinoEnabled);
        Assert.True(blankPrompt.CreateInRhinoGuidanceVisible);
        Assert.Contains("1 to 1024", blankPrompt.CreateInRhinoHelp);
        Assert.False(longPrompt.CreateInRhinoEnabled);
        Assert.Contains("1 to 1024", longPrompt.CreateInRhinoHelp);
        Assert.False(blankName.CreateInRhinoEnabled);
        Assert.True(blankName.CreateInRhinoGuidanceVisible);
        Assert.Contains("in Settings", blankName.CreateInRhinoHelp);
        Assert.Contains("1 to 128", blankName.CreateInRhinoHelp);
        Assert.False(longName.CreateInRhinoEnabled);
        Assert.Contains("1 to 128", longName.CreateInRhinoHelp);
    }

    [Fact]
    public void ImageOneClickGatingNeverFallsBackToHiddenPrompt()
    {
        Tripo.HostUi.TripoPanelState state = ReadyState() with
        {
            Context = ReadyState().Context! with
            {
                Capabilities =
                [
                    Tripo.Bridge.BridgeConstants.ContextMethod,
                    Tripo.Bridge.BridgeConstants.ImportGlbMethod,
                ],
            },
        };

        Tripo.HostUi.TripoPanelPresentation missingImage =
            Tripo.HostUi.TripoPanelPresentation.Create(
                state,
                Tripo.HostUi.TripoPanelRecoveryLoadResult.Empty,
                recoveryInspection: null,
                prompt: "a hidden fallback prompt",
                objectName: "Image Model",
                importSource: "glb",
                directGlbCreateStage:
                    Tripo.HostUi.DirectGlbCreateUiStage.Inactive,
                imageMode: true,
                hasImage: false);
        Tripo.HostUi.TripoPanelPresentation readyImage =
            Tripo.HostUi.TripoPanelPresentation.Create(
                state,
                Tripo.HostUi.TripoPanelRecoveryLoadResult.Empty,
                recoveryInspection: null,
                prompt: string.Empty,
                objectName: "Image Model",
                importSource: "glb",
                directGlbCreateStage:
                    Tripo.HostUi.DirectGlbCreateUiStage.Inactive,
                imageMode: true,
                hasImage: true,
                imageName: "input.webp");

        Assert.False(missingImage.CreateInRhinoEnabled);
        Assert.Contains("Choose an image", missingImage.CreateInRhinoHelp);
        Assert.True(readyImage.CreateInRhinoEnabled);
        Assert.True(readyImage.InputModeEnabled);
    }

    [Fact]
    public void PreparedImageLocksInputModeAndSurfacesUnifiedIdentity()
    {
        Tripo.HostUi.PreparedImageGeneration prepared = new(
            new Tripo.Bridge.StagedImageTransfer(
                "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
                new string('a', 64),
                128,
                "image/webp"),
            10_000,
            true,
            DocumentSessionId,
            "11111111-1111-4111-8111-111111111111");
        Tripo.HostUi.TripoPanelPresentation presentation =
            Tripo.HostUi.TripoPanelPresentation.Create(
                ReadyState() with { PreparedImageGeneration = prepared },
                Tripo.HostUi.TripoPanelRecoveryLoadResult.Empty,
                recoveryInspection: null,
                prompt: "a hidden fallback prompt",
                objectName: "Image Model",
                importSource: "glb",
                directGlbCreateStage:
                    Tripo.HostUi.DirectGlbCreateUiStage.Inactive,
                imageMode: true,
                hasImage: true,
                imageName: "input.webp");

        Assert.False(presentation.InputModeEnabled);
        Assert.False(presentation.PickImageEnabled);
        Assert.False(presentation.ClearImageVisible);
        Assert.Equal(prepared.OperationId, presentation.GenerationOperationId);
        Assert.Equal(prepared.OperationId, presentation.LatestPreparedOperationId);
    }

    [Fact]
    public void ImageDirectGlbGuardRejectsTextOrChangedImageIdentity()
    {
        Tripo.HostUi.PreparedImageGeneration prepared = new(
            new Tripo.Bridge.StagedImageTransfer(
                "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
                new string('a', 64),
                128,
                "image/png"),
            10_000,
            true,
            DocumentSessionId,
            "11111111-1111-4111-8111-111111111111");
        Tripo.HostUi.TripoPanelState ready = ReadyState() with
        {
            Context = ReadyState().Context! with
            {
                Capabilities =
                [
                    Tripo.Bridge.BridgeConstants.ContextMethod,
                    Tripo.Bridge.BridgeConstants.ImportGlbMethod,
                ],
            },
            PreparedImageGeneration = prepared,
        };

        Assert.Null(
            Tripo.HostUi.DirectGlbFirstDispatchGuard.GetBlockingReason(
                ready,
                Tripo.HostUi.TripoPanelRecoveryLoadResult.Empty,
                prepared,
                directGlbSelected: true));
        Assert.Contains(
            "no longer matches",
            Tripo.HostUi.DirectGlbFirstDispatchGuard.GetBlockingReason(
                ready with
                {
                    PreparedImageGeneration = prepared with
                    {
                        Image = prepared.Image with { ByteLength = 129 },
                    },
                },
                Tripo.HostUi.TripoPanelRecoveryLoadResult.Empty,
                prepared,
                directGlbSelected: true));
        Assert.Contains(
            "no longer matches",
            Tripo.HostUi.DirectGlbFirstDispatchGuard.GetBlockingReason(
                ready with
                {
                    PreparedImageGeneration = null,
                    PreparedGeneration = new Tripo.HostUi.PreparedTextGeneration(
                        "hidden prompt",
                        prepared.FaceLimit,
                        prepared.WithMaterials,
                        prepared.DocumentSessionId,
                        prepared.OperationId),
                },
                Tripo.HostUi.TripoPanelRecoveryLoadResult.Empty,
                prepared,
                directGlbSelected: true));
    }

    [Fact]
    public void DirectGlbFirstDispatchGuardRechecksRefreshedSafetyState()
    {
        Tripo.HostUi.PreparedTextGeneration prepared = new(
            "a chair",
            10_000,
            true,
            DocumentSessionId,
            "11111111-1111-4111-8111-111111111111");
        Tripo.HostUi.TripoPanelState ready = ReadyState() with
        {
            Context = ReadyState().Context! with
            {
                Capabilities =
                [
                    Tripo.Bridge.BridgeConstants.ContextMethod,
                    Tripo.Bridge.BridgeConstants.ImportGlbMethod,
                ],
            },
            PreparedGeneration = prepared,
        };

        Assert.Null(
            Tripo.HostUi.DirectGlbFirstDispatchGuard.GetBlockingReason(
                ready,
                Tripo.HostUi.TripoPanelRecoveryLoadResult.Empty,
                prepared,
                directGlbSelected: true));
        Assert.Contains(
            "no longer selected",
            Tripo.HostUi.DirectGlbFirstDispatchGuard.GetBlockingReason(
                ready,
                Tripo.HostUi.TripoPanelRecoveryLoadResult.Empty,
                prepared,
                directGlbSelected: false));
        Assert.Contains(
            "recovered operation IDs",
            Tripo.HostUi.DirectGlbFirstDispatchGuard.GetBlockingReason(
                ready,
                BlockingGenerationRecovery(),
                prepared,
                directGlbSelected: true));
        Assert.Contains(
            "not ready",
            Tripo.HostUi.DirectGlbFirstDispatchGuard.GetBlockingReason(
                ready with { Busy = true },
                Tripo.HostUi.TripoPanelRecoveryLoadResult.Empty,
                prepared,
                directGlbSelected: true));
        Assert.Contains(
            "not ready",
            Tripo.HostUi.DirectGlbFirstDispatchGuard.GetBlockingReason(
                ready with
                {
                    CredentialStatus = ready.CredentialStatus! with
                    {
                        HasApiKey = false,
                    },
                },
                Tripo.HostUi.TripoPanelRecoveryLoadResult.Empty,
                prepared,
                directGlbSelected: true));
        Assert.Contains(
            "unavailable",
            Tripo.HostUi.DirectGlbFirstDispatchGuard.GetBlockingReason(
                ready with
                {
                    Context = ready.Context! with { Capabilities = [] },
                },
                Tripo.HostUi.TripoPanelRecoveryLoadResult.Empty,
                prepared,
                directGlbSelected: true));
        Assert.Contains(
            "no longer matches",
            Tripo.HostUi.DirectGlbFirstDispatchGuard.GetBlockingReason(
                ready with
                {
                    Context = ready.Context! with
                    {
                        DocumentSessionId =
                            "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
                    },
                },
                Tripo.HostUi.TripoPanelRecoveryLoadResult.Empty,
                prepared,
                directGlbSelected: true));
        Assert.Contains(
            "no longer matches",
            Tripo.HostUi.DirectGlbFirstDispatchGuard.GetBlockingReason(
                ready with
                {
                    PreparedGeneration = prepared with
                    {
                        Prompt = "a different prompt",
                    },
                },
                Tripo.HostUi.TripoPanelRecoveryLoadResult.Empty,
                prepared,
                directGlbSelected: true));
        Assert.Contains(
            "no longer matches",
            Tripo.HostUi.DirectGlbFirstDispatchGuard.GetBlockingReason(
                ready with { GenerationDispatchAttempted = true },
                Tripo.HostUi.TripoPanelRecoveryLoadResult.Empty,
                prepared,
                directGlbSelected: true));
    }

    [Fact]
    public void ActiveDirectGlbCreateShowsProgressAndLocksMutationControls()
    {
        Tripo.HostUi.PreparedTextGeneration prepared = new(
            "a chair",
            10_000,
            true,
            DocumentSessionId,
            "11111111-1111-4111-8111-111111111111");
        Tripo.HostUi.TripoPanelState waiting =
            ReadyState() with
            {
                Context = ReadyState().Context! with
                {
                    Capabilities =
                    [
                        Tripo.Bridge.BridgeConstants.ContextMethod,
                        Tripo.Bridge.BridgeConstants.ImportGlbMethod,
                    ],
                },
                PreparedGeneration = prepared,
                GenerationDispatchAttempted = true,
                GenerationReceipt =
                    new Tripo.Bridge.HostControlTextTaskCreationReceipt(
                        prepared.OperationId,
                        "task_source123",
                        "v3"),
                GenerationStatus = TaskStatus(
                    "task_source123",
                    "text_to_model",
                    "running"),
            };

        Tripo.HostUi.TripoPanelPresentation presentation =
            Present(
                waiting,
                importSource: "glb",
                directGlbCreateStage:
                    Tripo.HostUi.DirectGlbCreateUiStage
                        .WaitingForGeneration);
        Tripo.HostUi.TripoPanelPresentation noObservedStatus =
            Present(
                waiting with { GenerationStatus = null },
                importSource: "glb",
                directGlbCreateStage:
                    Tripo.HostUi.DirectGlbCreateUiStage
                        .WaitingForGeneration);
        Tripo.HostUi.TripoPanelPresentation inactive =
            Present(waiting, importSource: "glb");
        Tripo.HostUi.TripoPanelPresentation mismatchedStatus =
            Present(
                waiting with
                {
                    GenerationStatus = TaskStatus(
                        "task_other",
                        "text_to_model",
                        "running"),
                },
                importSource: "glb",
                directGlbCreateStage:
                    Tripo.HostUi.DirectGlbCreateUiStage
                        .WaitingForGeneration);
        Tripo.HostUi.TripoPanelPresentation recoveryBlocked =
            Present(
                waiting,
                BlockingGenerationRecovery(),
                importSource: "glb",
                directGlbCreateStage:
                    Tripo.HostUi.DirectGlbCreateUiStage
                        .WaitingForGeneration);

        Assert.False(presentation.CreateInRhinoEnabled);
        Assert.Equal("Generating…", presentation.CreateInRhinoText);
        Assert.Contains(
            "Waiting for Tripo generation",
            presentation.ResultStatus);
        Assert.True(presentation.ResultVisible);
        Assert.False(presentation.ConnectEnabled);
        Assert.False(presentation.ApiKeyEnabled);
        Assert.False(presentation.GenerateEnabled);
        Assert.False(presentation.ConvertEnabled);
        Assert.False(presentation.ImportEnabled);
        Assert.False(presentation.ResetEnabled);
        Assert.False(presentation.ResetVisible);
        Assert.True(presentation.WorkflowStatusVisible);
        Assert.False(presentation.PromptEnabled);
        Assert.False(presentation.FaceLimitEnabled);
        Assert.False(presentation.WithMaterialsEnabled);
        Assert.False(presentation.NameEnabled);
        Assert.False(presentation.ImportSourceEnabled);
        Assert.True(presentation.RefreshGenerationEnabled);
        Assert.True(presentation.DirectGlbWaitActionVisible);
        Assert.True(presentation.DirectGlbWaitActionEnabled);
        Assert.Equal(
            "Stop waiting",
            presentation.DirectGlbWaitActionText);
        Assert.Contains(
            "does not cancel",
            presentation.DirectGlbWaitActionHelp);
        Assert.Contains(
            "refund credits",
            presentation.DirectGlbWaitActionHelp);
        Assert.True(noObservedStatus.DirectGlbWaitActionVisible);
        Assert.False(noObservedStatus.DirectGlbWaitActionEnabled);
        Assert.False(mismatchedStatus.DirectGlbWaitActionEnabled);
        Assert.False(recoveryBlocked.DirectGlbWaitActionEnabled);
        Assert.False(inactive.DirectGlbWaitActionVisible);
    }

    [Fact]
    public void PausedDirectGlbWaitPreservesTaskAndRequiresExplicitResume()
    {
        Tripo.HostUi.PreparedTextGeneration prepared = new(
            "a chair",
            10_000,
            true,
            DocumentSessionId,
            "11111111-1111-4111-8111-111111111111");
        Tripo.HostUi.TripoPanelState waiting =
            ReadyState() with
            {
                Context = ReadyState().Context! with
                {
                    Capabilities =
                    [
                        Tripo.Bridge.BridgeConstants.ContextMethod,
                        Tripo.Bridge.BridgeConstants.ImportGlbMethod,
                    ],
                },
                PreparedGeneration = prepared,
                GenerationDispatchAttempted = true,
                GenerationReceipt =
                    new Tripo.Bridge.HostControlTextTaskCreationReceipt(
                        prepared.OperationId,
                        "task_source123",
                        "v3"),
                GenerationStatus = TaskStatus(
                    "task_source123",
                    "text_to_model",
                    "running"),
            };

        Tripo.HostUi.TripoPanelPresentation paused =
            Present(
                waiting,
                importSource: "glb",
                directGlbCreateStage:
                    Tripo.HostUi.DirectGlbCreateUiStage.WaitingPaused);
        Tripo.HostUi.TripoPanelPresentation succeeded =
            Present(
                waiting with
                {
                    GenerationStatus = TaskStatus(
                        "task_source123",
                        "text_to_model",
                        "success"),
                },
                importSource: "glb",
                directGlbCreateStage:
                    Tripo.HostUi.DirectGlbCreateUiStage.WaitingPaused);
        Tripo.HostUi.TripoPanelPresentation credentialFailure =
            Present(
                waiting with
                {
                    LastError = "The provider rejected the credential.",
                    LastErrorCode =
                        Tripo.Bridge.HostControlConstants
                            .CredentialInvalidError,
                },
                importSource: "glb",
                directGlbCreateStage:
                    Tripo.HostUi.DirectGlbCreateUiStage.WaitingPaused);
        Tripo.HostUi.TripoPanelPresentation busy =
            Present(
                waiting with { Busy = true },
                importSource: "glb",
                directGlbCreateStage:
                    Tripo.HostUi.DirectGlbCreateUiStage.WaitingPaused);

        Assert.False(paused.CreateInRhinoEnabled);
        Assert.Equal("Waiting paused", paused.CreateInRhinoText);
        Assert.Contains(
            "remote Tripo task was not canceled",
            paused.ResultStatus);
        Assert.Contains(
            "nothing will import while paused",
            paused.ResultStatus);
        Assert.True(paused.DirectGlbWaitActionVisible);
        Assert.True(paused.DirectGlbWaitActionEnabled);
        Assert.Equal(
            "Resume automatic waiting",
            paused.DirectGlbWaitActionText);
        Assert.Contains(
            "same generation task",
            paused.DirectGlbWaitActionHelp);
        Assert.True(paused.RefreshGenerationEnabled);
        Assert.False(paused.GenerateEnabled);
        Assert.False(paused.ConvertEnabled);
        Assert.False(paused.ImportEnabled);
        Assert.False(paused.ResetEnabled);
        Assert.False(paused.ResetVisible);
        Assert.True(paused.WorkflowStatusVisible);

        Assert.Contains(
            "Generation is ready",
            succeeded.ResultStatus);
        Assert.Contains(
            "automatic import is paused",
            succeeded.ResultStatus);
        Assert.True(succeeded.DirectGlbWaitActionEnabled);
        Assert.False(succeeded.RefreshGenerationEnabled);

        Assert.False(credentialFailure.DirectGlbWaitActionEnabled);
        Assert.True(credentialFailure.ApiKeyEnabled);
        Assert.Contains(
            "same-account session-only API key",
            credentialFailure.DirectGlbWaitActionHelp);
        Assert.Contains(
            "nothing will import",
            credentialFailure.ResultStatus);
        Assert.False(busy.DirectGlbWaitActionEnabled);
    }

    [Fact]
    public void WaitingDirectGlbRefreshFailureOffersSessionOnlyKeyRecovery()
    {
        Tripo.HostUi.PreparedTextGeneration prepared = new(
            "a chair",
            10_000,
            true,
            DocumentSessionId,
            "11111111-1111-4111-8111-111111111111");
        Tripo.HostUi.TripoPanelState waiting = ReadyState() with
        {
            Context = ReadyState().Context! with
            {
                Capabilities =
                [
                    Tripo.Bridge.BridgeConstants.ContextMethod,
                    Tripo.Bridge.BridgeConstants.ImportGlbMethod,
                ],
            },
            PreparedGeneration = prepared,
            GenerationDispatchAttempted = true,
            GenerationReceipt =
                new Tripo.Bridge.HostControlTextTaskCreationReceipt(
                    prepared.OperationId,
                    "task_source123",
                    "v3"),
            GenerationStatus = TaskStatus(
                "task_source123",
                "text_to_model",
                "running"),
            LastError = "The provider rejected the credential.",
            LastErrorCode =
                Tripo.Bridge.HostControlConstants.CredentialInvalidError,
        };

        Tripo.HostUi.TripoPanelPresentation presentation =
            Present(
                waiting,
                importSource: "glb",
                directGlbCreateStage:
                    Tripo.HostUi.DirectGlbCreateUiStage
                        .WaitingForGeneration);
        Tripo.HostUi.TripoPanelPresentation environmentOverride =
            Present(
                waiting with
                {
                    CredentialStatus =
                        new Tripo.Bridge.HostControlCredentialStatusReceipt(
                            true,
                            "environment",
                            false,
                            false,
                            "keychain",
                            false,
                            StoredKeyPresenceKnown: false),
                },
                importSource: "glb",
                directGlbCreateStage:
                    Tripo.HostUi.DirectGlbCreateUiStage
                        .WaitingForGeneration);
        Tripo.HostUi.TripoPanelPresentation busy =
            Present(
                waiting with { Busy = true },
                importSource: "glb",
                directGlbCreateStage:
                    Tripo.HostUi.DirectGlbCreateUiStage
                        .WaitingForGeneration);
        Tripo.HostUi.TripoPanelPresentation recoveryIssue =
            Present(
                waiting,
                new Tripo.HostUi.TripoPanelRecoveryLoadResult(
                    [],
                    [
                        new Tripo.HostUi.TripoPanelRecoveryIssue(
                            "blocked.json",
                            "invalid_json",
                            "The recovery record could not be parsed."),
                    ]),
                importSource: "glb",
                directGlbCreateStage:
                    Tripo.HostUi.DirectGlbCreateUiStage
                        .WaitingForGeneration);
        Tripo.HostUi.TripoPanelPresentation networkFailure =
            Present(
                waiting with
                {
                    LastError = "The sidecar timed out.",
                    LastErrorCode = "sidecar_unavailable",
                },
                importSource: "glb",
                directGlbCreateStage:
                    Tripo.HostUi.DirectGlbCreateUiStage
                        .WaitingForGeneration);

        Assert.True(presentation.ApiKeyEnabled);
        Assert.False(presentation.ClearApiKeyEnabled);
        Assert.True(presentation.RefreshGenerationEnabled);
        Assert.False(presentation.ResetEnabled);
        Assert.Contains("refresh paused", presentation.ResultStatus);
        Assert.Contains("No import has run", presentation.ResultStatus);
        Assert.Contains("same-account", presentation.CreateInRhinoHelp);
        Assert.Contains("session-only", presentation.CreateInRhinoHelp);
        Assert.False(environmentOverride.ApiKeyEnabled);
        Assert.False(busy.ApiKeyEnabled);
        Assert.False(recoveryIssue.ApiKeyEnabled);
        Assert.False(networkFailure.ApiKeyEnabled);
        Assert.Contains(
            "API-key changes remain locked",
            networkFailure.CreateInRhinoHelp);
    }

    [Fact]
    public void DirectGlbRecoveryCanHandOffToExistingSameUuidRetry()
    {
        Tripo.HostUi.PreparedTextGeneration prepared = new(
            "a chair",
            10_000,
            true,
            DocumentSessionId,
            "11111111-1111-4111-8111-111111111111");
        Tripo.HostUi.TripoPanelState retryable =
            ReadyState() with
            {
                Context = ReadyState().Context! with
                {
                    Capabilities =
                    [
                        Tripo.Bridge.BridgeConstants.ContextMethod,
                        Tripo.Bridge.BridgeConstants.ImportGlbMethod,
                    ],
                },
                PreparedGeneration = prepared,
                GenerationDispatchAttempted = true,
                GenerationOperationStatus =
                    ResumableOperation(
                        prepared.OperationId,
                        "text_task_creation"),
                LastError =
                    "The generation task ID is not durable; retry the same UUID.",
            };

        Tripo.HostUi.TripoPanelPresentation automatic =
            Present(
                retryable,
                importSource: "glb",
                directGlbCreateStage:
                    Tripo.HostUi.DirectGlbCreateUiStage
                        .WaitingForGeneration);
        Tripo.HostUi.TripoPanelPresentation handedOff =
            Present(retryable, importSource: "glb");

        Assert.False(automatic.GenerateEnabled);
        Assert.True(automatic.RefreshGenerationEnabled);
        Assert.True(handedOff.GenerateEnabled);
        Assert.Equal("Retry same UUID", handedOff.GenerateText);
        Assert.Contains("not durable", handedOff.ResultStatus);
    }

    [Fact]
    public void DirectGlbCreateImportAndRefusalHaveExplicitOutcomes()
    {
        const string generationOperationId =
            "11111111-1111-4111-8111-111111111111";
        const string importOperationId =
            "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaab";
        Tripo.HostUi.TripoPanelState succeeded =
            ReadyState() with
            {
                Context = ReadyState().Context! with
                {
                    Capabilities =
                    [
                        Tripo.Bridge.BridgeConstants.ContextMethod,
                        Tripo.Bridge.BridgeConstants.ImportGlbMethod,
                    ],
                },
                PreparedGeneration =
                    new Tripo.HostUi.PreparedTextGeneration(
                        "a chair",
                        10_000,
                        false,
                        DocumentSessionId,
                        generationOperationId),
                GenerationDispatchAttempted = true,
                GenerationReceipt =
                    new Tripo.Bridge.HostControlTextTaskCreationReceipt(
                        generationOperationId,
                        "task_source123",
                        "v3"),
                GenerationStatus = TaskStatus(
                    "task_source123",
                    "text_to_model",
                    "success"),
            };
        Tripo.HostUi.TripoPanelState failed = succeeded with
        {
            GenerationStatus = TaskStatus(
                "task_source123",
                "text_to_model",
                "failed"),
        };

        Tripo.HostUi.TripoPanelPresentation importing =
            Present(
                succeeded,
                importSource: "glb",
                directGlbCreateStage:
                    Tripo.HostUi.DirectGlbCreateUiStage.Importing);
        Tripo.HostUi.TripoPanelPresentation terminal =
            Present(
                failed,
                importSource: "glb",
                directGlbCreateStage:
                    Tripo.HostUi.DirectGlbCreateUiStage
                        .TerminalWithoutImport);
        Tripo.HostUi.TripoPanelPresentation recoveryBlocked =
            Present(
                succeeded,
                BlockingGenerationRecovery(),
                importSource: "glb",
                directGlbCreateStage:
                    Tripo.HostUi.DirectGlbCreateUiStage.RecoveryBlocked);
        Tripo.HostUi.TripoPanelPresentation refused =
            Present(
                succeeded,
                importSource: "glb",
                directGlbCreateStage:
                    Tripo.HostUi.DirectGlbCreateUiStage.Refused);
        Tripo.HostUi.TripoPanelPresentation importFailed =
            Present(
                succeeded,
                importSource: "glb",
                directGlbCreateStage:
                    Tripo.HostUi.DirectGlbCreateUiStage.ImportFailed);
        Tripo.HostUi.TripoPanelState dispatchedWithoutReceipt =
            succeeded with
            {
                PreparedImport =
                    new Tripo.HostUi.PreparedObjImport(
                        "task_source123",
                        "Chair",
                        DocumentSessionId,
                        importOperationId,
                        "glb_instance",
                        ApplyMaterials: true,
                        ArtifactFormat: "glb"),
                ImportDispatchAttempted = true,
            };
        Tripo.HostUi.TripoPanelPresentation importRetry =
            Present(
                dispatchedWithoutReceipt,
                importSource: "glb",
                directGlbCreateStage:
                    Tripo.HostUi.DirectGlbCreateUiStage
                        .ImportRetryRequired);
        Tripo.HostUi.TripoPanelPresentation manualReview =
            Present(
                dispatchedWithoutReceipt with
                {
                    ImportFailureCode =
                        Tripo.Bridge.BridgeConstants
                            .MutationStateUncertainError,
                },
                importSource: "glb",
                directGlbCreateStage:
                    Tripo.HostUi.DirectGlbCreateUiStage
                        .ManualReviewRequired);
        Tripo.HostUi.TripoPanelPresentation committed =
            Present(
                dispatchedWithoutReceipt with
                {
                    ImportReceipt = DirectGlbImportReceipt(
                        importOperationId,
                        "task_source123",
                        "committed"),
                },
                importSource: "glb",
                directGlbCreateStage:
                    Tripo.HostUi.DirectGlbCreateUiStage.Completed);
        Tripo.HostUi.TripoPanelPresentation alreadyExists =
            Present(
                dispatchedWithoutReceipt with
                {
                    ImportReceipt = DirectGlbImportReceipt(
                        importOperationId,
                        "task_source123",
                        "already_exists"),
                },
                importSource: "glb",
                directGlbCreateStage:
                    Tripo.HostUi.DirectGlbCreateUiStage.Completed);
        Tripo.HostUi.TripoPanelPresentation unknownReceipt =
            Present(
                dispatchedWithoutReceipt with
                {
                    ImportReceipt = DirectGlbImportReceipt(
                        importOperationId,
                        "task_source123",
                        "future_status"),
                },
                importSource: "glb",
                directGlbCreateStage:
                    Tripo.HostUi.DirectGlbCreateUiStage.Completed);

        Assert.Equal("Importing GLB…", importing.CreateInRhinoText);
        Assert.Contains("Importing GLB", importing.ResultStatus);
        Assert.False(importing.DirectGlbWaitActionVisible);
        Assert.Equal("Review required", terminal.CreateInRhinoText);
        Assert.Contains("failed", terminal.ResultStatus);
        Assert.Contains("Nothing was imported", terminal.ResultStatus);
        Assert.Equal(
            "Review required",
            recoveryBlocked.CreateInRhinoText);
        Assert.Contains(
            "before Rhino mutation",
            recoveryBlocked.ResultStatus);
        Assert.Contains(
            "Nothing was imported",
            recoveryBlocked.ResultStatus);
        Assert.True(recoveryBlocked.RecoveryHasBlock);
        Assert.True(recoveryBlocked.CheckRecoveryEnabled);
        Assert.False(recoveryBlocked.DirectGlbWaitActionVisible);
        Assert.Equal("Review required", refused.CreateInRhinoText);
        Assert.Contains("evidence did not match", refused.ResultStatus);
        Assert.Contains("Nothing was imported", refused.ResultStatus);
        Assert.Equal("Review required", importFailed.CreateInRhinoText);
        Assert.Contains("Nothing was imported", importFailed.ResultStatus);
        Assert.Equal("Review required", importRetry.CreateInRhinoText);
        Assert.Contains("may already", importRetry.ResultStatus);
        Assert.DoesNotContain(
            "Nothing was imported",
            importRetry.ResultStatus);
        Assert.True(importRetry.ImportEnabled);
        Assert.Equal("Retry same UUID", importRetry.ImportText);
        Assert.False(importRetry.ResetEnabled);
        Assert.Equal(
            "Manual review required",
            manualReview.CreateInRhinoText);
        Assert.Contains("could not prove", manualReview.ResultStatus);
        Assert.DoesNotContain(
            "Nothing was imported",
            manualReview.CreateInRhinoHelp);
        Assert.Equal("Created in Rhino", committed.CreateInRhinoText);
        Assert.Equal(
            "Already in Rhino",
            alreadyExists.CreateInRhinoText);
        Assert.Equal(
            "Review required",
            unknownReceipt.CreateInRhinoText);
    }

    [Fact]
    public void DirectGlbIsRecommendedAfterGenerationWithoutObjConversion()
    {
        Tripo.HostUi.TripoPanelState state =
            ReadyState() with
            {
                Context = ReadyState().Context! with
                {
                    Capabilities =
                    [
                        Tripo.Bridge.BridgeConstants.ContextMethod,
                        Tripo.Bridge.BridgeConstants.ImportMeshMethod,
                        Tripo.Bridge.BridgeConstants.ImportGlbMethod,
                    ],
                },
                GenerationStatus = TaskStatus(
                    "task_source123",
                    "text_to_model",
                    "success"),
            };

        Tripo.HostUi.TripoPanelPresentation presentation =
            Present(state, importSource: "glb");

        Assert.True(presentation.ImportEnabled);
        Assert.Equal(
            "Import GLB (recommended)",
            presentation.ImportText);
        Assert.True(presentation.ImportSourceEnabled);
        Assert.False(presentation.ImportModeEnabled);
        Assert.False(presentation.ApplyMaterialsEnabled);
        Assert.Contains("No OBJ conversion", presentation.ImportGuidance);
    }

    [Fact]
    public void UncertainDirectGlbImportRequiresManualReviewWithoutRetry()
    {
        Tripo.HostUi.TripoPanelState state =
            ReadyState() with
            {
                Context = ReadyState().Context! with
                {
                    Capabilities =
                    [
                        Tripo.Bridge.BridgeConstants.ContextMethod,
                        Tripo.Bridge.BridgeConstants.ImportMeshMethod,
                        Tripo.Bridge.BridgeConstants.ImportGlbMethod,
                    ],
                },
                GenerationStatus = TaskStatus(
                    "task_source123",
                    "text_to_model",
                    "success"),
                PreparedImport = new Tripo.HostUi.PreparedObjImport(
                    "task_source123",
                    "Chair",
                    ReadyState().Context!.DocumentSessionId,
                    "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
                    "glb_instance",
                    ApplyMaterials: true,
                    ArtifactFormat: "glb"),
                ImportDispatchAttempted = true,
                ImportFailureCode =
                    Tripo.Bridge.BridgeConstants.MutationStateUncertainError,
            };

        Tripo.HostUi.TripoPanelPresentation presentation =
            Present(state, importSource: "glb");

        Assert.False(presentation.ImportEnabled);
        Assert.Equal("Manual review required", presentation.ImportText);
        Assert.Contains("Do not retry", presentation.ImportGuidance);
        Assert.False(presentation.ResetEnabled);
    }

    [Fact]
    public void ObjCompatibilityStillRequiresSuccessfulConversion()
    {
        Tripo.HostUi.TripoPanelState generated =
            ReadyState() with
            {
                GenerationStatus = TaskStatus(
                    "task_source123",
                    "text_to_model",
                    "success"),
            };
        Tripo.HostUi.TripoPanelPresentation beforeConversion =
            Present(generated, importSource: "obj");
        Tripo.HostUi.TripoPanelPresentation afterConversion =
            Present(
                generated with
                {
                    ConversionStatus = TaskStatus(
                        "task_convert123",
                        "convert_model",
                        "success"),
                },
                importSource: "obj");

        Assert.False(beforeConversion.ImportEnabled);
        Assert.True(afterConversion.ImportEnabled);
        Assert.Equal(
            "Import OBJ into Rhino",
            afterConversion.ImportText);
        Assert.True(afterConversion.ImportModeEnabled);
        Assert.True(afterConversion.ApplyMaterialsEnabled);
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

    private const string RhinoObjectId =
        "44444444-4444-4444-8444-444444444444";

    private static Tripo.HostUi.TripoPanelPresentation Present(
        Tripo.HostUi.TripoPanelState state,
        Tripo.HostUi.TripoPanelRecoveryLoadResult? recovery = null,
        string prompt = "a chair",
        string objectName = "Chair",
        string importSource = "obj",
        Tripo.HostUi.DirectGlbCreateUiStage directGlbCreateStage =
            Tripo.HostUi.DirectGlbCreateUiStage.Inactive) =>
        Tripo.HostUi.TripoPanelPresentation.Create(
            state,
            recovery ??
                Tripo.HostUi.TripoPanelRecoveryLoadResult.Empty,
            recoveryInspection: null,
            prompt,
            objectName,
            importSource,
            directGlbCreateStage);

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

    private static Tripo.Bridge.HostControlOperationStatusReceipt
        DurableOperation(
            string operationId,
            string kind,
            string taskId,
            string? sourceTaskId = null) =>
        new(
            operationId,
            kind,
            "task_id_persisted",
            sourceTaskId,
            taskId,
            null,
            null,
            true,
            true,
            true,
            "Query the durable task ID.",
            DateTimeOffset.UnixEpoch);

    private static Tripo.HostUi.TripoPanelImportReceipt
        DirectGlbImportReceipt(
            string operationId,
            string sourceTaskId,
            string transactionStatus) =>
        new(
            operationId,
            sourceTaskId,
            "glb",
            null,
            new Tripo.Bridge.HostImportReceipt(
                "rhino",
                DocumentSessionId,
                operationId,
                RhinoObjectId,
                12,
                10,
                0,
                transactionStatus,
                "glb_instance",
                1,
                1,
                null));

    private static Tripo.HostUi.TripoPanelState
        CompletedDirectGlbImportState(
            string transactionStatus = "committed")
    {
        const string generationOperationId =
            "11111111-1111-4111-8111-111111111111";
        const string importOperationId =
            "33333333-3333-4333-8333-333333333333";
        const string generationTaskId = "task_source123";
        return ReadyState() with
        {
            PreparedGeneration =
                new Tripo.HostUi.PreparedTextGeneration(
                    "a chair",
                    10_000,
                    false,
                    DocumentSessionId,
                    generationOperationId),
            GenerationDispatchAttempted = true,
            GenerationReceipt =
                new Tripo.Bridge.HostControlTextTaskCreationReceipt(
                    generationOperationId,
                    generationTaskId,
                    "v3"),
            GenerationStatus = TaskStatus(
                generationTaskId,
                "text_to_model",
                "success"),
            PreparedImport =
                new Tripo.HostUi.PreparedObjImport(
                    generationTaskId,
                    "Chair",
                    DocumentSessionId,
                    importOperationId,
                    "glb_instance",
                    ApplyMaterials: true,
                    ArtifactFormat: "glb"),
            ImportDispatchAttempted = true,
            ImportReceipt = DirectGlbImportReceipt(
                importOperationId,
                generationTaskId,
                transactionStatus),
        };
    }

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
