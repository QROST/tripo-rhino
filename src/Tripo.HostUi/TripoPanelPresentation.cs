namespace Tripo.HostUi;

public sealed record TripoApiKeyPromptPolicy(
    bool Replacing,
    bool RecoveryMode,
    bool ExactOriginalKeyRequired,
    bool PersistAllowed,
    bool RequiresReplacementConfirmation,
    string? WorkflowOperationId)
{
    public static TripoApiKeyPromptPolicy Create(
        TripoPanelState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        bool replacing =
            state.CredentialStatus?.HasApiKey == true;
        bool recoveryMode =
            state.RequiresCredentialRecovery;
        string? workflowOperationId =
            state.ImportDispatchAttempted ||
            state.ImportReceipt is not null
                ? state.PreparedImport?.OperationId
                : state.ConversionDispatchAttempted ||
                  state.ConversionReceipt is not null ||
                  state.ConversionOperationStatus is not null ||
                  state.ConversionStatus is not null
                    ? state.PreparedConversion?.OperationId
                    : state.GenerationDispatchAttempted ||
                      state.GenerationReceipt is not null ||
                      state.ImageGenerationReceipt is not null ||
                      state.GenerationOperationStatus is not null ||
                      state.GenerationStatus is not null
                        ? state.PreparedGenerationOperationId
                        : null;
        return new TripoApiKeyPromptPolicy(
            replacing,
            recoveryMode,
            state.HasUnresolvedPaidDispatch,
            PersistAllowed: !recoveryMode,
            RequiresReplacementConfirmation:
                replacing && !recoveryMode,
            workflowOperationId);
    }
}

public static class TripoPanelRecoveryReviewFormatter
{
    public static string Format(
        TripoPanelRecoveryReviewSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Dictionary<string, TripoPanelRecoveryOperationInspection>
            inspections = snapshot.PaidOperations.ToDictionary(
                operation => operation.OperationId,
                StringComparer.Ordinal);
        List<string> lines = [];
        foreach (LoadedTripoPanelRecoveryHint loaded in
                 snapshot.Recovery.Hints)
        {
            lines.Add(
                $"Recovered document: {loaded.Hint.DocumentSessionId}");
            AppendPaidStatus(
                lines,
                inspections,
                "Generation",
                loaded.Hint.Generation);
            AppendPaidStatus(
                lines,
                inspections,
                "Conversion",
                loaded.Hint.Conversion);
            if (loaded.Hint.Import is not null)
            {
                lines.Add("Import");
                lines.Add(
                    "Operation ID: " +
                    loaded.Hint.Import.OperationId);
                lines.Add(
                    "Safety: The Rhino import may already have changed the " +
                    "original document. Check that document before unlocking.");
            }

            lines.Add(string.Empty);
        }

        return string.Join(Environment.NewLine, lines).TrimEnd();
    }

    private static void AppendPaidStatus(
        List<string> lines,
        Dictionary<
            string,
            TripoPanelRecoveryOperationInspection> inspections,
        string stage,
        TripoPanelPaidRecoveryHint? hint)
    {
        if (hint is null)
        {
            return;
        }

        lines.Add(stage);
        lines.Add($"Operation ID: {hint.OperationId}");
        if (!string.IsNullOrWhiteSpace(hint.TaskId))
        {
            lines.Add($"Recovered task ID: {hint.TaskId}");
        }

        if (!inspections.TryGetValue(
                hint.OperationId,
                out TripoPanelRecoveryOperationInspection? inspection) ||
            inspection.Receipt is null)
        {
            lines.Add(
                "Local status: unavailable" +
                (string.IsNullOrWhiteSpace(inspection?.ErrorCode)
                    ? string.Empty
                    : $" ({inspection.ErrorCode})"));
            lines.Add(
                "Safety: Local evidence cannot prove whether Tripo accepted " +
                "or charged this request. Check Tripo task and billing history " +
                "before unlocking.");
            if (!string.IsNullOrWhiteSpace(inspection?.ErrorMessage))
            {
                lines.Add(
                    "Technical detail: " +
                    inspection.ErrorMessage);
            }

            return;
        }

        Tripo.Bridge.HostControlOperationStatusReceipt status =
            inspection.Receipt;
        lines.Add($"Request kind: {status.Kind}");
        lines.Add($"Local state: {status.State}");
        lines.Add(
            $"Last local journal update: {status.UpdatedAtUtc:O}");
        if (!string.IsNullOrWhiteSpace(status.SourceTaskId))
        {
            lines.Add($"Source task ID: {status.SourceTaskId}");
        }

        lines.Add(
            string.IsNullOrWhiteSpace(status.CreatedTaskId)
                ? "Task ID: No task ID was recorded"
                : $"Task ID: {status.CreatedTaskId}");
        if (!string.IsNullOrWhiteSpace(status.FailureCode))
        {
            lines.Add($"Failure code: {status.FailureCode}");
        }

        if (!string.IsNullOrWhiteSpace(status.FailureStage))
        {
            lines.Add($"Failure stage: {status.FailureStage}");
        }

        if (!string.IsNullOrWhiteSpace(status.FailureMessage))
        {
            lines.Add($"Failure detail: {status.FailureMessage}");
        }

        if (status.OperationInProgress ||
            string.Equals(
                status.State,
                "operation_in_progress",
                StringComparison.Ordinal))
        {
            lines.Add(
                "Safety: This operation is still active. Wait and refresh; " +
                "Tripo will not unlock recovery while it is in progress.");
        }
        else if (status.TaskIdDurable)
        {
            lines.Add(
                "Recovery: Refresh this task. Do not create a replacement " +
                "request or UUID.");
        }
        else if (status.MayHaveCreatedRemoteTask ||
                 string.Equals(
                     status.State,
                     "outcome_unknown",
                     StringComparison.Ordinal))
        {
            lines.Add(
                "Safety: Tripo may have accepted and charged this request. " +
                "Do not send a replacement request.");
            lines.Add(
                "Check Tripo task and billing history before unlocking.");
        }
        else if (status.CanResumeCreation)
        {
            lines.Add(
                "Recovery: Resume only with this same operation ID. A retry " +
                "still requires explicit cost confirmation.");
        }
        else
        {
            lines.Add("Next action: " + status.NextAction);
        }
    }
}

internal enum DirectGlbCreateUiStage
{
    Inactive,
    Preflighting,
    WaitingForGeneration,
    WaitingPaused,
    Importing,
    Completed,
    RecoveryBlocked,
    TerminalWithoutImport,
    Refused,
    ImportFailed,
    ImportRetryRequired,
    ManualReviewRequired,
}

internal sealed record DirectGlbCreateConfirmation(
    string Title,
    string Message,
    bool DefaultToNo)
{
    internal static DirectGlbCreateConfirmation Create(
        string operationId,
        string documentTitle,
        string objectName,
        bool imageGeneration = false)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            throw new ArgumentException(
                "The operation ID is required.",
                nameof(operationId));
        }

        if (string.IsNullOrWhiteSpace(documentTitle))
        {
            throw new ArgumentException(
                "The document title is required.",
                nameof(documentTitle));
        }

        if (string.IsNullOrWhiteSpace(objectName))
        {
            throw new ArgumentException(
                "The object name is required.",
                nameof(objectName));
        }
        return new DirectGlbCreateConfirmation(
            "Create model and import direct GLB?",
            "This sends one Tripo " +
            (imageGeneration ? "image-to-model" : "text-to-model") +
            " generation request, which can " +
            "consume Tripo credits. " +
            "After that task reports success, this panel will import its GLB " +
            $"directly into \"{documentTitle}\" as \"{objectName}\".\n\n" +
            "No separate OBJ conversion request is sent.\n\n" +
            $"Durable generation operation ID:\n{operationId}\n\n" +
            "Keep this ID if the response is lost. Hiding the panel does not " +
            "cancel the remote Tripo task.",
            DefaultToNo: true);
    }
}

internal static class DirectGlbFirstDispatchGuard
{
    internal static string? GetBlockingReason(
        TripoPanelState state,
        TripoPanelRecoveryLoadResult recovery,
        PreparedTextGeneration prepared,
        bool directGlbSelected)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (!Equals(state.PreparedGeneration, prepared))
        {
            return "The prepared generation no longer matches the confirmed " +
                   "direct GLB workflow. Nothing was sent.";
        }

        return GetBlockingReason(
            state,
            recovery,
            prepared.OperationId,
            prepared.DocumentSessionId,
            imageGeneration: false,
            directGlbSelected);
    }

    internal static string? GetBlockingReason(
        TripoPanelState state,
        TripoPanelRecoveryLoadResult recovery,
        PreparedImageGeneration prepared,
        bool directGlbSelected)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (!Equals(state.PreparedImageGeneration, prepared))
        {
            return "The prepared generation no longer matches the confirmed " +
                   "direct GLB workflow. Nothing was sent.";
        }

        return GetBlockingReason(
            state,
            recovery,
            prepared.OperationId,
            prepared.DocumentSessionId,
            imageGeneration: true,
            directGlbSelected);
    }

    private static string? GetBlockingReason(
        TripoPanelState state,
        TripoPanelRecoveryLoadResult recovery,
        string operationId,
        string documentSessionId,
        bool imageGeneration,
        bool directGlbSelected)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(recovery);
        if (recovery.HasBlock)
        {
            return "Review recovered operation IDs before starting a " +
                   "generation request that can consume credits.";
        }

        if (!directGlbSelected)
        {
            return "Direct GLB is no longer selected. Nothing was sent.";
        }

        if (!state.Connected ||
            state.Busy ||
            state.CredentialStatus?.HasApiKey != true)
        {
            return "The refreshed Rhino connection is not ready for generation.";
        }

        Tripo.Bridge.HostContextReceipt? context = state.Context;
        if (context is null)
        {
            return "The active Rhino document context is unavailable.";
        }

        if (!string.Equals(
                context.Host,
                "rhino",
                StringComparison.OrdinalIgnoreCase) ||
            !context.Capabilities.Contains(
                Tripo.Bridge.BridgeConstants.ImportGlbMethod,
                StringComparer.Ordinal))
        {
            return "Direct GLB is unavailable in the refreshed plugin/sidecar " +
                   "pair. No generation request was sent.";
        }

        if (!string.Equals(
                context.DocumentSessionId,
                documentSessionId,
                StringComparison.Ordinal) ||
            !string.Equals(
                state.PreparedGenerationOperationId,
                operationId,
                StringComparison.Ordinal) ||
            !string.Equals(
                state.PreparedGenerationDocumentSessionId,
                documentSessionId,
                StringComparison.Ordinal) ||
            state.PreparedGenerationIsImage != imageGeneration ||
            state.GenerationDispatchAttempted ||
            !state.CanDispatchPreparedGeneration)
        {
            return "The prepared generation no longer matches the confirmed " +
                   "direct GLB workflow. Nothing was sent.";
        }

        return null;
    }
}

public sealed class TripoPanelPresentation
{
    private TripoPanelPresentation()
    {
    }

    public string DocumentStatus { get; private init; } = string.Empty;

    public string DocumentSessionId { get; private init; } = string.Empty;

    public string CredentialStatus { get; private init; } = string.Empty;

    public string ApiKeyText { get; private init; } = "API key…";

    public string ApiKeyHelp { get; private init; } = string.Empty;

    public string RecoveryHeader { get; private init; } = string.Empty;

    public string RecoveryDetails { get; private init; } = string.Empty;

    public string RecoveryActionText { get; private init; } =
        "Review recovery…";

    public bool RecoveryHasBlock { get; private init; }

    public string RecoveryToken { get; private init; } = string.Empty;

    public string? LatestPreparedOperationId { get; private init; }

    public string GenerationOperationId { get; private init; } = string.Empty;

    public string GenerationTaskId { get; private init; } = string.Empty;

    public string GenerationStatus { get; private init; } = string.Empty;

    public bool WorkflowStatusVisible { get; private init; }

    public string GenerationDiagnostic { get; private init; } = string.Empty;

    public bool GenerationDiagnosticVisible { get; private init; }

    public int? GenerationProgress { get; private init; }

    public string ConversionOperationId { get; private init; } = string.Empty;

    public string ConversionTaskId { get; private init; } = string.Empty;

    public string ConversionStatus { get; private init; } = string.Empty;

    public string ConversionDiagnostic { get; private init; } = string.Empty;

    public bool ConversionDiagnosticVisible { get; private init; }

    public int? ConversionProgress { get; private init; }

    public string ImportOperationId { get; private init; } = string.Empty;

    public string ImportCreatedObjectId { get; private init; } = string.Empty;

    public string ImportTransactionStatus { get; private init; } = string.Empty;

    public bool ImportReceiptDetailsVisible { get; private init; }

    public string ResultStatus { get; private init; } = string.Empty;

    public bool ResultVisible { get; private init; }

    public bool ConnectEnabled { get; private init; }

    public bool ApiKeyEnabled { get; private init; }

    public bool ClearApiKeyEnabled { get; private init; }

    public string ClearApiKeyHelp { get; private init; } = string.Empty;

    public bool CheckRecoveryEnabled { get; private init; }

    public bool ReviewRecoveryEnabled { get; private init; }

    public bool CreateInRhinoEnabled { get; private init; }

    internal bool CanStartDirectGlbCreate { get; private init; }

    public string CreateInRhinoText { get; private init; } =
        "Create in Rhino";

    public string CreateInRhinoHelp { get; private init; } = string.Empty;

    public bool CreateInRhinoGuidanceVisible { get; private init; }

    public bool DirectGlbWaitActionVisible { get; private init; }

    public bool DirectGlbWaitActionEnabled { get; private init; }

    public string DirectGlbWaitActionText { get; private init; } =
        "Stop waiting";

    public string DirectGlbWaitActionHelp { get; private init; } =
        string.Empty;

    public bool GenerateEnabled { get; private init; }

    public string GenerateText { get; private init; } = string.Empty;

    public bool RefreshGenerationEnabled { get; private init; }

    public bool ConvertEnabled { get; private init; }

    public string ConvertText { get; private init; } = string.Empty;

    public bool RefreshConversionEnabled { get; private init; }

    public bool ImportEnabled { get; private init; }

    public string ImportText { get; private init; } = string.Empty;

    public bool ImportSourceEnabled { get; private init; }

    public string ImportGuidance { get; private init; } = string.Empty;

    public bool ResetEnabled { get; private init; }

    public bool ResetVisible { get; private init; }

    public bool PromptEnabled { get; private init; }

    public bool InputModeEnabled { get; private init; }

    public bool FaceLimitEnabled { get; private init; }

    public bool WithMaterialsEnabled { get; private init; }

    public bool NameEnabled { get; private init; }

    public bool ImportModeEnabled { get; private init; }

    public bool ApplyMaterialsEnabled { get; private init; }

    /// <summary>
    /// True when the panel is in image-input mode (image picker visible, prompt
    /// box hidden). Drives input-control visibility in the panel.
    /// </summary>
    public bool ImageMode { get; private init; }

    /// <summary>
    /// True when an image has been staged locally and is ready to prepare, or a
    /// prepared image generation exists. Used to enable the Generate button in
    /// image mode (analogous to a non-empty prompt in text mode).
    /// </summary>
    public bool HasImage { get; private init; }

    /// <summary>
    /// Filename of the currently staged image, for the preview label.
    /// </summary>
    public string? ImageName { get; private init; }

    /// <summary>
    /// True when the "Choose image" button should accept input.
    /// </summary>
    public bool PickImageEnabled { get; private init; }

    /// <summary>
    /// True when a staged image can be cleared (image present and editing allowed).
    /// </summary>
    public bool ClearImageVisible { get; private init; }

    public bool ClearImageEnabled { get; private init; }

    public static TripoPanelPresentation Create(
        TripoPanelState state,
        TripoPanelRecoveryLoadResult recovery,
        string? recoveryInspection,
        string? prompt,
        string? objectName,
        string? importSource = null) =>
        Create(
            state,
            recovery,
            recoveryInspection,
            prompt,
            objectName,
            importSource,
            DirectGlbCreateUiStage.Inactive);

    internal static TripoPanelPresentation Create(
        TripoPanelState state,
        TripoPanelRecoveryLoadResult recovery,
        string? recoveryInspection,
        string? prompt,
        string? objectName,
        string? importSource,
        DirectGlbCreateUiStage directGlbCreateStage,
        bool imageMode = false,
        bool hasImage = false,
        string? imageName = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(recovery);

        bool ready = state.Connected && !state.Busy;
        bool automaticCreateActive =
            directGlbCreateStage is
                DirectGlbCreateUiStage.Preflighting or
                DirectGlbCreateUiStage.WaitingForGeneration or
                DirectGlbCreateUiStage.WaitingPaused or
                DirectGlbCreateUiStage.Importing;
        bool controlsReady = ready && !automaticCreateActive;
        bool generationPrepared =
            state.PreparedGeneration is not null ||
            state.PreparedImageGeneration is not null;
        bool generationSucceeded =
            state.GenerationStatus?.Status == "success";
        bool conversionPrepared = state.PreparedConversion is not null;
        bool conversionSucceeded =
            state.ConversionStatus?.Status == "success";
        bool directGlbSelected =
            !string.Equals(
                importSource,
                "obj",
                StringComparison.OrdinalIgnoreCase);
        bool directGlbRoute =
            state.PreparedImport?.IsDirectGlb ?? directGlbSelected;
        bool directGlbSupported =
            state.Context is { } activeContext &&
            string.Equals(
                activeContext.Host,
                "rhino",
                StringComparison.OrdinalIgnoreCase) &&
            activeContext.Capabilities.Contains(
                Tripo.Bridge.BridgeConstants.ImportGlbMethod,
                StringComparer.Ordinal);
        bool importPrerequisite =
            directGlbRoute
                ? generationSucceeded && directGlbSupported
                : conversionSucceeded;
        bool recoveryBlocked = recovery.HasBlock;
        bool resetEnabled =
            controlsReady &&
            !recoveryBlocked &&
            state.CanResetWorkflow;
        bool directGlbCredentialRecoveryReady =
            ready &&
            !recoveryBlocked &&
            (directGlbCreateStage is
                DirectGlbCreateUiStage.WaitingForGeneration or
                DirectGlbCreateUiStage.WaitingPaused) &&
            state.HasDurableGenerationTask &&
            state.HasCredentialRefreshFailure;
        bool generationPending =
            state.GenerationStatus is not null &&
            GenerationStatusPoller.GetPendingTaskId(
                state,
                recovery) is not null;
        bool normalApiKeyInteractionReady =
            controlsReady &&
            (!recoveryBlocked ||
             (recovery.Hints.Count > 0 && recovery.Issues.Count == 0));
        bool environmentOverridesPanelKey =
            state.CredentialStatus is
            {
                HasApiKey: true,
                Source: "environment",
            };
        bool hasPrompt =
            !string.IsNullOrWhiteSpace(prompt) &&
            prompt.Length <= 1024;
        bool hasObjectName =
            !string.IsNullOrWhiteSpace(objectName) &&
            objectName.Length <= 128;
        bool canStartDirectGlbCreate =
            ready &&
            !recoveryBlocked &&
            state.CredentialStatus?.HasApiKey == true &&
            directGlbSelected &&
            directGlbSupported &&
            !state.HasWorkflowState &&
            (imageMode ? hasImage : hasPrompt) &&
            hasObjectName;
        string? generationTaskId = state.GenerationTaskId;
        string? conversionTaskId =
            state.ConversionReceipt?.ConversionTaskId ??
            state.ConversionOperationStatus?.CreatedTaskId;
        bool importReceiptVerified =
            state.HasVerifiedTerminalImportReceipt;
        string resultStatus = directGlbCreateStage switch
        {
            DirectGlbCreateUiStage.Preflighting =>
                "Checking the active Rhino document and direct GLB support…",
            DirectGlbCreateUiStage.WaitingForGeneration
                when state.LastError is not null =>
                "Generation refresh paused: " + state.LastError +
                " Click Refresh generation to continue. No import has run.",
            DirectGlbCreateUiStage.WaitingForGeneration =>
                "Waiting for Tripo generation… Rhino will import the GLB " +
                "automatically when it is ready.",
            DirectGlbCreateUiStage.WaitingPaused
                when state.HasCredentialRefreshFailure =>
                "Automatic waiting is paused and the generation credential " +
                "needs recovery. Restore a same-account session-only key, " +
                "then resume; nothing will import while paused.",
            DirectGlbCreateUiStage.WaitingPaused
                when generationSucceeded =>
                "Generation is ready, but automatic import is paused. Resume " +
                "automatic waiting to import this task's GLB.",
            DirectGlbCreateUiStage.WaitingPaused =>
                "Automatic waiting is paused. The remote Tripo task was not " +
                "canceled. Refresh manually or resume automatic waiting; " +
                "nothing will import while paused.",
            DirectGlbCreateUiStage.Importing =>
                "Generation complete. Importing GLB into Rhino…",
            DirectGlbCreateUiStage.Completed
                when importReceiptVerified =>
                FriendlyImportResult(
                    state.ImportReceipt!.HostReceipt.TransactionStatus),
            DirectGlbCreateUiStage.Completed
                when state.ImportReceipt is not null =>
                "Import receipt conflicts with this Rhino workflow · " +
                "Review details",
            DirectGlbCreateUiStage.RecoveryBlocked =>
                "Automatic import stopped before Rhino mutation because " +
                "recovery evidence requires review. Nothing was imported; " +
                "review Recovery before continuing.",
            DirectGlbCreateUiStage.TerminalWithoutImport =>
                "Generation ended with " +
                (state.GenerationStatus?.Status ?? "an unknown status") +
                ". Nothing was imported.",
            DirectGlbCreateUiStage.Refused =>
                "Automatic import was refused because the generation evidence " +
                "did not match this workflow. Nothing was imported; review " +
                "Workflow details.",
            DirectGlbCreateUiStage.ImportFailed =>
                state.LastError is null
                    ? "Automatic direct GLB import stopped before Rhino saved " +
                      "a durable import receipt. Nothing was imported by this " +
                      "attempt."
                    : "Automatic direct GLB import stopped: " + state.LastError,
            DirectGlbCreateUiStage.ImportRetryRequired =>
                "Rhino did not receive a durable import receipt. The same " +
                "operation ID may already have been applied; use Retry same " +
                "UUID instead of creating a replacement operation.",
            DirectGlbCreateUiStage.ManualReviewRequired =>
                "Rhino could not prove the final document state. Do not retry " +
                "this import; inspect the document and recovery operation ID.",
            _ when state.LastError is not null => state.LastError,
            _ when state.Busy => "Working…",
            _ when importReceiptVerified =>
                FriendlyImportResult(
                    state.ImportReceipt!.HostReceipt.TransactionStatus),
            _ when state.ImportReceipt is not null =>
                "Import receipt conflicts with this Rhino workflow · " +
                "Review details",
            _ => "Ready.",
        };

        return new TripoPanelPresentation
        {
            DocumentStatus = state.Context is null
                ? "Document: not connected"
                : $"Document: {state.Context.DocumentTitle}",
            DocumentSessionId =
                state.Context?.DocumentSessionId ?? "Not connected",
            CredentialStatus = FormatCredentialStatus(state),
            ApiKeyText = RecoveryApiKeyText(state, recovery),
            ApiKeyHelp = BuildApiKeyHelp(state, recoveryBlocked),
            RecoveryHeader = recoveryBlocked
                ? recovery.Issues.Count > 0
                    ? "Recovery · Manual attention required"
                    : "Recovery · Review before continuing"
                : "Recovery · Clear",
            RecoveryDetails =
                BuildRecoveryDetails(
                    state,
                    recovery,
                    recoveryInspection),
            RecoveryActionText = state.HasWorkflowState
                ? "Reload and review all work…"
                : "Review recovery…",
            RecoveryHasBlock = recoveryBlocked,
            RecoveryToken = recovery.PresentationToken,
            LatestPreparedOperationId =
                state.PreparedImport?.OperationId ??
                state.PreparedConversion?.OperationId ??
                state.PreparedGenerationOperationId,
            GenerationOperationId =
                state.PreparedGenerationOperationId ?? "Not prepared",
            GenerationTaskId = generationTaskId ?? "Not created",
            GenerationStatus = StageStatus(
                generationTaskId,
                state.GenerationOperationStatus,
                state.GenerationStatus),
            WorkflowStatusVisible = state.HasWorkflowState,
            GenerationDiagnostic = StageDiagnostic(
                state.GenerationOperationStatus,
                state.GenerationStatus),
            GenerationDiagnosticVisible =
                state.GenerationOperationStatus is not null ||
                state.GenerationStatus is not null,
            GenerationProgress = Progress(state.GenerationStatus),
            ConversionOperationId =
                state.PreparedConversion?.OperationId ?? "Not prepared",
            ConversionTaskId = conversionTaskId ?? "Not created",
            ConversionStatus = StageStatus(
                conversionTaskId,
                state.ConversionOperationStatus,
                state.ConversionStatus),
            ConversionDiagnostic = StageDiagnostic(
                state.ConversionOperationStatus,
                state.ConversionStatus),
            ConversionDiagnosticVisible =
                state.ConversionOperationStatus is not null ||
                state.ConversionStatus is not null,
            ConversionProgress = Progress(state.ConversionStatus),
            ImportOperationId =
                state.PreparedImport?.OperationId ?? "Not prepared",
            ImportCreatedObjectId =
                state.ImportReceipt?.HostReceipt.CreatedId ?? "Not created",
            ImportTransactionStatus =
                state.ImportReceipt?.HostReceipt.TransactionStatus ??
                "Not available",
            ImportReceiptDetailsVisible = state.ImportReceipt is not null,
            ResultStatus = resultStatus,
            ResultVisible =
                state.LastError is not null ||
                state.Busy ||
                state.ImportReceipt is not null ||
                directGlbCreateStage != DirectGlbCreateUiStage.Inactive,
            ConnectEnabled =
                !state.Busy &&
                !automaticCreateActive,
            ApiKeyEnabled =
                (normalApiKeyInteractionReady ||
                 directGlbCredentialRecoveryReady) &&
                !environmentOverridesPanelKey,
            ClearApiKeyEnabled =
                controlsReady &&
                !recoveryBlocked &&
                !environmentOverridesPanelKey &&
                !state.RequiresCredentialRecovery &&
                state.CredentialStatus?.StoredKeyPresenceKnown == true &&
                state.CredentialStatus.StoredKeyPresent &&
                state.CredentialStatus.CanClearStoredKey,
            ClearApiKeyHelp =
                BuildClearApiKeyHelp(state, recoveryBlocked),
            CheckRecoveryEnabled =
                !state.Busy &&
                !automaticCreateActive &&
                recovery.HasBlock,
            ReviewRecoveryEnabled =
                !state.Busy &&
                !automaticCreateActive &&
                recovery.Hints.Count > 0 &&
                recovery.Issues.Count == 0,
            CreateInRhinoEnabled =
                directGlbCreateStage == DirectGlbCreateUiStage.Inactive &&
                canStartDirectGlbCreate,
            CanStartDirectGlbCreate = canStartDirectGlbCreate,
            CreateInRhinoText = directGlbCreateStage switch
            {
                DirectGlbCreateUiStage.Preflighting =>
                    "Checking…",
                DirectGlbCreateUiStage.WaitingForGeneration =>
                    "Generating…",
                DirectGlbCreateUiStage.WaitingPaused =>
                    "Waiting paused",
                DirectGlbCreateUiStage.Importing =>
                    "Importing GLB…",
                DirectGlbCreateUiStage.Completed =>
                    !importReceiptVerified
                        ? "Review required"
                        : state.ImportReceipt?.HostReceipt.TransactionStatus switch
                        {
                            "committed" => "Created in Rhino",
                            "already_exists" => "Already in Rhino",
                            _ => "Review required",
                        },
                DirectGlbCreateUiStage.TerminalWithoutImport or
                DirectGlbCreateUiStage.RecoveryBlocked or
                DirectGlbCreateUiStage.Refused or
                DirectGlbCreateUiStage.ImportFailed or
                DirectGlbCreateUiStage.ImportRetryRequired =>
                    "Review required",
                DirectGlbCreateUiStage.ManualReviewRequired =>
                    "Manual review required",
                _ => "Create in Rhino",
            },
            CreateInRhinoHelp =
                BuildCreateInRhinoHelp(
                    state,
                    recoveryBlocked,
                    directGlbSelected,
                    directGlbSupported,
                    imageMode,
                    hasImage,
                    hasPrompt,
                    hasObjectName,
                    directGlbCreateStage),
            CreateInRhinoGuidanceVisible =
                directGlbCreateStage == DirectGlbCreateUiStage.Inactive &&
                !state.Busy &&
                !canStartDirectGlbCreate,
            DirectGlbWaitActionVisible =
                directGlbCreateStage is
                    DirectGlbCreateUiStage.WaitingForGeneration or
                    DirectGlbCreateUiStage.WaitingPaused,
            DirectGlbWaitActionEnabled =
                ready &&
                !recoveryBlocked &&
                state.HasDurableGenerationTask &&
                ((directGlbCreateStage ==
                     DirectGlbCreateUiStage.WaitingPaused &&
                   !state.HasCredentialRefreshFailure) ||
                 (directGlbCreateStage ==
                    DirectGlbCreateUiStage.WaitingForGeneration &&
                  generationPending)),
            DirectGlbWaitActionText =
                directGlbCreateStage ==
                    DirectGlbCreateUiStage.WaitingPaused
                    ? "Resume automatic waiting"
                    : "Stop waiting",
            DirectGlbWaitActionHelp =
                directGlbCreateStage ==
                    DirectGlbCreateUiStage.WaitingPaused
                    ? state.HasCredentialRefreshFailure
                        ? "Restore a same-account session-only API key before " +
                          "resuming this same generation task."
                        : "Resume local status polling and automatic direct GLB " +
                          "import for this same generation task."
                    : "Stop local status polling and automatic import. This " +
                      "does not cancel the remote Tripo task or refund credits; " +
                      "you can refresh manually or resume later.",
            GenerateEnabled =
                controlsReady &&
                !recoveryBlocked &&
                state.CredentialStatus?.HasApiKey == true &&
                ((!generationPrepared &&
                  (imageMode ? hasImage : hasPrompt)) ||
                 (generationPrepared &&
                  state.CanDispatchPreparedGeneration &&
                  (!state.GenerationDispatchAttempted ||
                   state.GenerationRetryAllowed))),
            GenerateText = !generationPrepared
                ? imageMode
                    ? "Generate from image"
                    : "Generate"
                : state.GenerationRetryAllowed
                    ? "Retry same UUID"
                    : state.GenerationRetryRequired
                        ? "Refresh before retry"
                        : state.CanDispatchPreparedGeneration
                            ? "Send prepared"
                            : "Request sent",
            RefreshGenerationEnabled =
                ready &&
                !recoveryBlocked &&
                !generationSucceeded &&
                (state.GenerationReceipt is not null ||
                 state.GenerationDispatchAttempted),
            ConvertEnabled =
                controlsReady &&
                !recoveryBlocked &&
                generationSucceeded &&
                (!conversionPrepared ||
                 (state.CanDispatchPreparedConversion &&
                  (!state.ConversionDispatchAttempted ||
                   state.ConversionRetryAllowed))),
            ConvertText = !conversionPrepared
                ? "Convert to OBJ"
                : state.ConversionRetryAllowed
                    ? "Retry same UUID"
                    : state.ConversionRetryRequired
                        ? "Refresh before retry"
                        : state.CanDispatchPreparedConversion
                            ? "Send prepared"
                            : "Request sent",
            RefreshConversionEnabled =
                ready &&
                !recoveryBlocked &&
                (state.ConversionReceipt is not null ||
                 state.ConversionDispatchAttempted),
            ImportEnabled =
                controlsReady &&
                !recoveryBlocked &&
                importPrerequisite &&
                (state.PreparedImport is null
                    ? hasObjectName
                    : state.CanDispatchPreparedImport),
            ImportText = state.PreparedImport is null
                ? directGlbRoute
                    ? "Import GLB (recommended)"
                    : "Import OBJ into Rhino"
                : state.ImportRequiresManualReview
                    ? "Manual review required"
                : state.ImportRetryRequired
                    ? "Retry same UUID"
                    : state.CanDispatchPreparedImport
                        ? "Import prepared"
                        : "Imported",
            ImportSourceEnabled =
                controlsReady && state.PreparedImport is null,
            ImportGuidance = state.ImportRequiresManualReview
                ? "Rhino could not prove the final document state. Do not " +
                  "retry this import; inspect the document and recovery " +
                  "operation ID manually."
                : directGlbRoute
                    ? directGlbSupported
                    ? "Recommended: import the generation GLB directly with " +
                      "Rhino-native materials when available. No OBJ conversion " +
                      "task is created."
                    : "Direct GLB is unavailable in this plugin/sidecar pair. " +
                      "Install the matching build or select OBJ compatibility."
                : "Compatibility path: create and finish a separate OBJ " +
                  "conversion before importing.",
            ResetEnabled = resetEnabled,
            ResetVisible =
                resetEnabled &&
                (state.HasWorkflowState ||
                 directGlbCreateStage != DirectGlbCreateUiStage.Inactive),
            PromptEnabled = controlsReady && !generationPrepared && !imageMode,
            InputModeEnabled = controlsReady && !generationPrepared,
            FaceLimitEnabled = controlsReady && !generationPrepared,
            WithMaterialsEnabled = controlsReady && !generationPrepared,
            NameEnabled =
                controlsReady && state.PreparedImport is null,
            ImportModeEnabled =
                controlsReady &&
                state.PreparedImport is null &&
                !directGlbRoute,
            ApplyMaterialsEnabled =
                controlsReady &&
                state.PreparedImport is null &&
                !directGlbRoute,
            ImageMode = imageMode,
            HasImage = hasImage || state.PreparedImageGeneration is not null,
            ImageName = imageName,
            PickImageEnabled = controlsReady && !generationPrepared,
            ClearImageVisible =
                imageMode &&
                (hasImage || state.PreparedImageGeneration is not null) &&
                controlsReady &&
                !generationPrepared,
            ClearImageEnabled =
                imageMode &&
                hasImage &&
                controlsReady &&
                !generationPrepared,
        };
    }

    private static string BuildCreateInRhinoHelp(
        TripoPanelState state,
        bool recoveryBlocked,
        bool directGlbSelected,
        bool directGlbSupported,
        bool imageMode,
        bool hasImage,
        bool hasPrompt,
        bool hasObjectName,
        DirectGlbCreateUiStage directGlbCreateStage)
    {
        if (directGlbCreateStage == DirectGlbCreateUiStage.Preflighting)
        {
            return "Refreshing the active document, credential, and direct " +
                   "GLB capability before any request that can consume credits.";
        }

        if (directGlbCreateStage ==
            DirectGlbCreateUiStage.WaitingForGeneration)
        {
            if (state.LastError is not null)
            {
                return state.HasCredentialRefreshFailure
                    ? "Generation refresh is paused. Restore a same-account " +
                      "API key for this workflow, or click Refresh generation " +
                      "to retry. The key remains session-only; no import has run."
                    : "Generation refresh is paused. Click Refresh generation " +
                      "to retry. API-key changes remain locked because the " +
                      "sidecar did not report a credential failure; no import " +
                      "has run.";
            }

            return "Waiting for the same generation task. Rhino will " +
                   "import its GLB directly when it reports success; no OBJ " +
                   "conversion task is created.";
        }

        if (directGlbCreateStage ==
            DirectGlbCreateUiStage.WaitingPaused)
        {
            return state.HasCredentialRefreshFailure
                ? "Local polling and automatic direct GLB import are paused. " +
                  "Restore a same-account session-only API key before resuming; " +
                  "the same task and durable operation ID are kept."
                : "Local polling and automatic direct GLB import are paused. " +
                   "The same remote task and durable operation ID are kept; " +
                   "resume waiting when you want Rhino to import on success.";
        }

        if (directGlbCreateStage == DirectGlbCreateUiStage.Importing)
        {
            return "The generation succeeded and Rhino is importing that " +
                   "task's GLB directly.";
        }

        if (directGlbCreateStage == DirectGlbCreateUiStage.Completed)
        {
            return "The confirmed generation GLB has a durable Rhino import " +
                   "receipt.";
        }

        if (directGlbCreateStage ==
            DirectGlbCreateUiStage.ManualReviewRequired)
        {
            return "Rhino could not prove whether the document mutation " +
                   "committed. Do not retry; inspect the owning document and " +
                   "recovery operation ID.";
        }

        if (directGlbCreateStage ==
            DirectGlbCreateUiStage.ImportRetryRequired)
        {
            return "The import receipt is missing. The existing import action " +
                   "can retry the same durable UUID; do not create a replacement " +
                   "operation.";
        }

        if (directGlbCreateStage ==
            DirectGlbCreateUiStage.RecoveryBlocked)
        {
            return "Automatic import stopped before Rhino mutation because " +
                   "recovery evidence requires review. Nothing was imported; " +
                   "open Recovery and reconcile the durable operation IDs.";
        }

        if (directGlbCreateStage is
            DirectGlbCreateUiStage.TerminalWithoutImport or
            DirectGlbCreateUiStage.Refused or
            DirectGlbCreateUiStage.ImportFailed)
        {
            return "Nothing was imported. Review the generation status and " +
                   "Workflow details, then start a new workflow when safe.";
        }

        if (state.Busy)
        {
            return "Wait for the current panel operation to finish.";
        }

        if (!state.Connected)
        {
            return "Connect to the active Rhino document first. The manual " +
                   "Connect / Refresh action is in Advanced.";
        }

        if (recoveryBlocked)
        {
            return "Review the recovered operation IDs before starting new " +
                   "generation work that can consume credits.";
        }

        if (state.CredentialStatus?.HasApiKey != true)
        {
            return "Open Settings and set a Tripo API key before starting " +
                   "generation that can consume credits.";
        }

        if (!directGlbSelected)
        {
            return "Direct GLB is required for the one-click Rhino workflow. " +
                   "OBJ compatibility is selected in Advanced and remains a " +
                   "separate manual path.";
        }

        if (!directGlbSupported)
        {
            return "Direct GLB is unavailable in this plugin/sidecar pair. " +
                   "Install the matching build before starting generation that " +
                   "can consume credits.";
        }

        if (state.HasWorkflowState)
        {
            return "Finish or reset the current workflow before starting " +
                   "another model.";
        }

        if (imageMode && !hasImage)
        {
            return "Choose an image before starting image-to-model generation.";
        }

        if (!imageMode && !hasPrompt)
        {
            return "The prompt must contain 1 to 1024 characters.";
        }

        if (!hasObjectName)
        {
            return "The Rhino object name in Settings must contain 1 to 128 " +
                   "characters.";
        }

        return "Creates one Tripo generation task, which can consume credits, " +
               "waits for it to finish, then imports its GLB directly into " +
               "Rhino. No separate OBJ conversion request is sent.";
    }

    private static string BuildApiKeyHelp(
        TripoPanelState state,
        bool recoveryBlocked)
    {
        if (state.Busy)
        {
            return "Wait for the current panel operation to finish.";
        }

        if (!state.Connected)
        {
            return "Connect to the active Rhino document first.";
        }

        if (state.CredentialStatus is
            {
                HasApiKey: true,
                Source: "environment",
            })
        {
            return "TRIPO_API_KEY from the environment overrides panel keys. " +
                   "Change it outside Rhino, then restart Rhino.";
        }

        if (state.RequiresCredentialRecovery)
        {
            return state.HasUnresolvedPaidDispatch
                ? "Restore the exact original API key for this workflow. The " +
                  "recovery key remains session-only."
                : "Use a key for the same Tripo account. The recovery key " +
                  "remains session-only until reset.";
        }

        if (recoveryBlocked)
        {
            return "Review the previous request before setting or changing " +
                   "the key.";
        }

        return "Set or replace the Tripo v3 API key.";
    }

    private static string BuildClearApiKeyHelp(
        TripoPanelState state,
        bool recoveryBlocked)
    {
        if (state.Busy)
        {
            return "Wait for the current panel operation to finish.";
        }

        if (!state.Connected)
        {
            return "Connect to the active Rhino document first.";
        }

        if (recoveryBlocked)
        {
            return "Reconcile the recovered operation IDs before removing keys.";
        }

        if (state.HasCredentialBoundWorkflow)
        {
            return "Finish or reconcile this account-bound workflow and " +
                   "explicitly reset it before removing the saved key.";
        }

        if (state.CredentialStatus is
            {
                HasApiKey: true,
                Source: "environment",
            })
        {
            return "TRIPO_API_KEY is active. Change it outside Rhino and " +
                   "restart Rhino before managing saved keys.";
        }

        Tripo.Bridge.HostControlCredentialStatusReceipt? credentials =
            state.CredentialStatus;
        if (credentials is null ||
            !credentials.StoredKeyPresenceKnown)
        {
            return "Saved-key presence is unknown; refresh the connection first.";
        }

        if (!credentials.StoredKeyPresent)
        {
            return "No OS-stored Tripo API key is present.";
        }

        if (!credentials.CanClearStoredKey)
        {
            return "The current credential backend cannot clear the saved key.";
        }

        return credentials.Source switch
        {
            "environment" =>
                "Remove the OS-stored and session Tripo API keys. The " +
                "environment key remains effective.",
            "session" =>
                "Remove the current session key and OS-stored Tripo API key.",
            _ =>
                "Remove the known OS-stored Tripo API key. The same operation " +
                "also clears any active session override.",
        };
    }

    private static string FormatCredentialStatus(TripoPanelState state)
    {
        if (state.CredentialStatus is null)
        {
            return "API key: unknown";
        }

        string fallback = state.CredentialStatus.UsesWeakerFileFallback
            ? " · private-file fallback"
            : string.Empty;
        if (!state.CredentialStatus.HasApiKey)
        {
            return "API key: not configured" + fallback;
        }

        string source = state.CredentialStatus.Source switch
        {
            "store" => "saved",
            "session" => "session only",
            "environment" => "environment override",
            "macOS Keychain" => "saved in macOS Keychain",
            _ => state.CredentialStatus.Source,
        };
        return $"API key: {source}{fallback}";
    }

    private static string RecoveryApiKeyText(
        TripoPanelState state,
        TripoPanelRecoveryLoadResult recovery)
    {
        if (recovery.Issues.Count > 0)
        {
            return "Recovery needs attention…";
        }

        if (state.RequiresCredentialRecovery)
        {
            return recovery.HasBlock
                ? "Review, then restore key…"
                : "Restore API key…";
        }

        if (!recovery.HasBlock)
        {
            return "API key…";
        }

        return state.CredentialStatus?.HasApiKey == true
            ? "Review, then change key…"
            : "Review, then set key…";
    }

    private static string BuildRecoveryDetails(
        TripoPanelState state,
        TripoPanelRecoveryLoadResult recovery,
        string? recoveryInspection)
    {
        List<string> lines = [];
        if (!recovery.HasBlock)
        {
            lines.Add("No recovered operation IDs require reconciliation.");
        }
        else
        {
            if (recovery.Issues.Count > 0)
            {
                lines.Add(
                    "Tripo cannot safely read one or more local recovery " +
                    "records. API-key changes and new paid work remain paused.");
                lines.Add(
                    "The plug-in will not delete or overwrite this evidence. " +
                    "Inspect or move the named files aside manually, then " +
                    "refresh recovery.");
            }
            else
            {
                lines.Add(
                    "Tripo paused new paid work and API-key changes because an " +
                    "earlier request may have reached Tripo. This prevents an " +
                    "accidental duplicate charge.");
                if (state.HasWorkflowState)
                {
                    lines.Add(
                        "This panel also has current workflow state. Choose " +
                        "“Reload and review all work…” to preserve dispatched " +
                        "operation IDs, clear only unsent setup, and review " +
                        "everything together.");
                }
                else
                {
                    lines.Add(
                        "Choose “Review recovery…” to inspect the saved " +
                        "operation status, then confirm what you checked.");
                }
            }

            foreach (LoadedTripoPanelRecoveryHint loaded in recovery.Hints)
            {
                lines.Add(
                    $"Document session: {loaded.Hint.DocumentSessionId}");
                if (loaded.Hint.Generation is not null)
                {
                    lines.Add(
                        "Generation UUID: " +
                        loaded.Hint.Generation.OperationId);
                }

                if (loaded.Hint.Conversion is not null)
                {
                    lines.Add(
                        "Conversion UUID: " +
                        loaded.Hint.Conversion.OperationId);
                }

                if (loaded.Hint.Import is not null)
                {
                    lines.Add(
                        "Import UUID: " +
                        loaded.Hint.Import.OperationId);
                }
            }

            foreach (TripoPanelRecoveryIssue issue in recovery.Issues)
            {
                lines.Add(
                    $"Blocked recovery file {issue.FileName}: " +
                    $"{issue.Code}. {issue.Message}");
            }
        }

        if (!string.IsNullOrWhiteSpace(recoveryInspection))
        {
            lines.Add("Latest local inspection:");
            lines.Add(recoveryInspection);
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string StageStatus(
        string? taskId,
        Tripo.Bridge.HostControlOperationStatusReceipt? operation,
        Tripo.Bridge.HostControlTaskStatusReceipt? task)
    {
        if (task is not null)
        {
            return $"{TitleCase(task.Status)} · " +
                   $"{task.Progress}%";
        }

        if (taskId is not null)
        {
            return "Task created · Refresh to check status";
        }

        return operation is null
            ? "Not started"
            : FriendlyOperationStatus(operation.State);
    }

    private static int? Progress(
        Tripo.Bridge.HostControlTaskStatusReceipt? task) =>
        task?.Progress;

    private static string StageDiagnostic(
        Tripo.Bridge.HostControlOperationStatusReceipt? operation,
        Tripo.Bridge.HostControlTaskStatusReceipt? task)
    {
        List<string> lines = [];
        if (operation is not null)
        {
            lines.Add($"Operation state: {operation.State}");
            lines.Add($"Next action: {operation.NextAction}");
        }

        if (task is not null)
        {
            lines.Add($"Task status: {task.Status}");
            lines.Add($"Task progress: {task.Progress}%");
        }

        return lines.Count == 0
            ? "No diagnostic state available."
            : string.Join(Environment.NewLine, lines);
    }

    private static string FriendlyOperationStatus(string state) =>
        state switch
        {
            "prepared" => "Prepared · Ready to send",
            "operation_in_progress" or
                "dispatching" or
                "image_upload_dispatching" or
                "image_generation_dispatching" =>
                "Request in progress · Wait, then refresh",
            "image_file_token_persisted" =>
                "Upload saved · Ready to resume",
            "task_id_persisted" =>
                "Task created · Refresh status",
            "outcome_unknown" =>
                "Outcome unknown · Do not resend",
            _ => "Status requires inspection",
        };

    private static string FriendlyImportResult(string transactionStatus) =>
        transactionStatus switch
        {
            "committed" => "Imported into Rhino",
            "already_exists" => "Already imported in Rhino",
            _ => "Import receipt available · See details",
        };

    private static string TitleCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Unknown";
        }

        string trimmed = value.Trim();
        return trimmed.Length == 1
            ? trimmed.ToUpperInvariant()
            : char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
    }
}
