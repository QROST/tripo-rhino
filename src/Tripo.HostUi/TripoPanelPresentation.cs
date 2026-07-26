namespace Tripo.HostUi;

public sealed class TripoPanelPresentation
{
    private TripoPanelPresentation()
    {
    }

    public string DocumentStatus { get; private init; } = string.Empty;

    public string DocumentSessionId { get; private init; } = string.Empty;

    public string CredentialStatus { get; private init; } = string.Empty;

    public string RecoveryHeader { get; private init; } = string.Empty;

    public string RecoveryDetails { get; private init; } = string.Empty;

    public bool RecoveryHasBlock { get; private init; }

    public string RecoveryToken { get; private init; } = string.Empty;

    public string? LatestPreparedOperationId { get; private init; }

    public string GenerationOperationId { get; private init; } = string.Empty;

    public string GenerationTaskId { get; private init; } = string.Empty;

    public string GenerationStatus { get; private init; } = string.Empty;

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

    public bool CheckRecoveryEnabled { get; private init; }

    public bool AcknowledgeRecoveryEnabled { get; private init; }

    public bool GenerateEnabled { get; private init; }

    public string GenerateText { get; private init; } = string.Empty;

    public bool RefreshGenerationEnabled { get; private init; }

    public bool ConvertEnabled { get; private init; }

    public string ConvertText { get; private init; } = string.Empty;

    public bool RefreshConversionEnabled { get; private init; }

    public bool ImportEnabled { get; private init; }

    public string ImportText { get; private init; } = string.Empty;

    public bool ResetEnabled { get; private init; }

    public bool PromptEnabled { get; private init; }

    public bool FaceLimitEnabled { get; private init; }

    public bool WithMaterialsEnabled { get; private init; }

    public bool NameEnabled { get; private init; }

    public bool ImportModeEnabled { get; private init; }

    public bool ApplyMaterialsEnabled { get; private init; }

    public static TripoPanelPresentation Create(
        TripoPanelState state,
        TripoPanelRecoveryLoadResult recovery,
        string? recoveryInspection,
        string? prompt,
        string? objectName)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(recovery);

        bool ready = state.Connected && !state.Busy;
        bool generationPrepared = state.PreparedGeneration is not null;
        bool generationSucceeded =
            state.GenerationStatus?.Status == "success";
        bool conversionPrepared = state.PreparedConversion is not null;
        bool conversionSucceeded =
            state.ConversionStatus?.Status == "success";
        bool recoveryBlocked = recovery.HasBlock;
        bool hasPrompt = !string.IsNullOrWhiteSpace(prompt);
        bool hasObjectName = !string.IsNullOrWhiteSpace(objectName);
        string? generationTaskId =
            state.GenerationReceipt?.TaskId ??
            state.GenerationOperationStatus?.CreatedTaskId;
        string? conversionTaskId =
            state.ConversionReceipt?.ConversionTaskId ??
            state.ConversionOperationStatus?.CreatedTaskId;
        string resultStatus = state.LastError is not null
            ? state.LastError
            : state.Busy
                ? "Working…"
                : state.ImportReceipt is not null
                    ? FriendlyImportResult(
                        state.ImportReceipt
                            .HostReceipt
                            .TransactionStatus)
                    : "Ready.";

        return new TripoPanelPresentation
        {
            DocumentStatus = state.Context is null
                ? "Document: not connected"
                : $"Document: {state.Context.DocumentTitle}",
            DocumentSessionId =
                state.Context?.DocumentSessionId ?? "Not connected",
            CredentialStatus = FormatCredentialStatus(state),
            RecoveryHeader = recoveryBlocked
                ? "Recovery · Action required"
                : "Recovery · Clear",
            RecoveryDetails =
                BuildRecoveryDetails(recovery, recoveryInspection),
            RecoveryHasBlock = recoveryBlocked,
            RecoveryToken = recovery.PresentationToken,
            LatestPreparedOperationId =
                state.PreparedImport?.OperationId ??
                state.PreparedConversion?.OperationId ??
                state.PreparedGeneration?.OperationId,
            GenerationOperationId =
                state.PreparedGeneration?.OperationId ?? "Not prepared",
            GenerationTaskId = generationTaskId ?? "Not created",
            GenerationStatus = StageStatus(
                generationTaskId,
                state.GenerationOperationStatus,
                state.GenerationStatus),
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
                state.ImportReceipt is not null,
            ConnectEnabled = !state.Busy,
            ApiKeyEnabled =
                ready &&
                !recoveryBlocked &&
                !state.HasUnresolvedPaidDispatch,
            CheckRecoveryEnabled =
                state.Connected &&
                !state.Busy &&
                recovery.Hints.Count > 0,
            AcknowledgeRecoveryEnabled =
                !state.Busy &&
                recovery.Hints.Count > 0 &&
                recovery.Issues.Count == 0,
            GenerateEnabled =
                ready &&
                !recoveryBlocked &&
                state.CredentialStatus?.HasApiKey == true &&
                ((!generationPrepared && hasPrompt) ||
                 (generationPrepared &&
                  state.CanDispatchPreparedGeneration &&
                  (!state.GenerationDispatchAttempted ||
                   state.GenerationRetryAllowed))),
            GenerateText = !generationPrepared
                ? "Generate"
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
                (state.GenerationReceipt is not null ||
                 state.GenerationDispatchAttempted),
            ConvertEnabled =
                ready &&
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
                (state.ConversionReceipt is not null ||
                 state.ConversionDispatchAttempted),
            ImportEnabled =
                ready &&
                !recoveryBlocked &&
                conversionSucceeded &&
                (state.PreparedImport is null
                    ? hasObjectName
                    : state.CanDispatchPreparedImport),
            ImportText = state.PreparedImport is null
                ? "Import into Rhino"
                : state.ImportRetryRequired
                    ? "Retry same UUID"
                    : state.CanDispatchPreparedImport
                        ? "Import prepared"
                        : "Imported",
            ResetEnabled =
                ready &&
                !recoveryBlocked &&
                !state.HasUnresolvedDispatch,
            PromptEnabled = ready && !generationPrepared,
            FaceLimitEnabled = ready && !generationPrepared,
            WithMaterialsEnabled = ready && !generationPrepared,
            NameEnabled = ready && state.PreparedImport is null,
            ImportModeEnabled = ready && state.PreparedImport is null,
            ApplyMaterialsEnabled =
                ready && state.PreparedImport is null,
        };
    }

    private static string FormatCredentialStatus(TripoPanelState state)
    {
        if (state.CredentialStatus is null)
        {
            return "API key: unknown";
        }

        if (!state.CredentialStatus.HasApiKey)
        {
            return "API key: not configured · Source: " +
                   state.CredentialStatus.Source +
                   (state.CredentialStatus.UsesWeakerFileFallback
                       ? " (private-file fallback)"
                       : string.Empty);
        }

        return $"API key: {state.CredentialStatus.Source}" +
               (state.CredentialStatus.UsesWeakerFileFallback
                   ? " (private-file fallback)"
                   : string.Empty);
    }

    private static string BuildRecoveryDetails(
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
            lines.Add(
                "Recovered IDs block new workflows and API-key changes until " +
                "they are checked and explicitly acknowledged.");
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
