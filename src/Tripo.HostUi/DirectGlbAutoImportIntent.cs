namespace Tripo.HostUi;

internal enum DirectGlbAutoImportPhase
{
    Waiting,
    Stopped,
    Importing,
    Finished,
}

internal enum DirectGlbAutoImportDecision
{
    NoAction,
    Waiting,
    Stopped,
    BeginImport,
    TerminalWithoutImport,
    Refused,
}

internal sealed class DirectGlbAutoImportIntent
{
    private const string ExpectedTaskType = "text_to_model";
    private const string ExpectedOperationKind = "text_task_creation";
    private readonly object _gate = new();
    private string? _taskId;
    private DirectGlbAutoImportPhase _phase =
        DirectGlbAutoImportPhase.Waiting;

    internal DirectGlbAutoImportIntent(
        long sessionGeneration,
        string generationOperationId,
        string documentSessionId,
        string objectName)
    {
        if (sessionGeneration <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sessionGeneration),
                "The panel session generation must be positive.");
        }

        SessionGeneration = sessionGeneration;
        GenerationOperationId = RequireCanonicalUuid(
            generationOperationId,
            nameof(generationOperationId));
        DocumentSessionId = RequireCanonicalUuid(
            documentSessionId,
            nameof(documentSessionId));
        if (!RhinoPanelUserSettings.TryNormalizeObjectName(
                objectName,
                out string normalizedName))
        {
            throw new ArgumentException(
                "The direct GLB object name must contain 1 to 128 characters.",
                nameof(objectName));
        }

        ObjectName = normalizedName;
    }

    internal long SessionGeneration { get; }

    internal string GenerationOperationId { get; }

    internal string DocumentSessionId { get; }

    internal string ObjectName { get; }

    internal string? TaskId
    {
        get
        {
            lock (_gate)
            {
                return _taskId;
            }
        }
    }

    internal DirectGlbAutoImportPhase Phase
    {
        get
        {
            lock (_gate)
            {
                return _phase;
            }
        }
    }

    internal bool TryBindDurableTask(
        long sessionGeneration,
        TripoPanelState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate)
        {
            if (!MatchesStateIdentity(sessionGeneration, state) ||
                _phase is
                    DirectGlbAutoImportPhase.Importing or
                    DirectGlbAutoImportPhase.Finished)
            {
                return false;
            }

            DurableTaskEvidence evidence = ResolveDurableTaskEvidence(state);
            if (evidence.Invalid)
            {
                _phase = DirectGlbAutoImportPhase.Finished;
                return false;
            }

            if (evidence.TaskId is null)
            {
                return false;
            }

            return TryBindResolvedTask(evidence.TaskId);
        }
    }

    internal DirectGlbAutoImportDecision ObserveState(
        long sessionGeneration,
        TripoPanelState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate)
        {
            if (!MatchesStateIdentity(sessionGeneration, state))
            {
                return DirectGlbAutoImportDecision.NoAction;
            }

            if (state.Busy)
            {
                return DirectGlbAutoImportDecision.NoAction;
            }

            if (state.HasCredentialRefreshFailure)
            {
                return _phase == DirectGlbAutoImportPhase.Stopped
                    ? DirectGlbAutoImportDecision.Stopped
                    : DirectGlbAutoImportDecision.Waiting;
            }

            if (_phase is
                DirectGlbAutoImportPhase.Importing or
                DirectGlbAutoImportPhase.Finished)
            {
                return DirectGlbAutoImportDecision.NoAction;
            }

            DurableTaskEvidence evidence = ResolveDurableTaskEvidence(state);
            if (evidence.Invalid)
            {
                _phase = DirectGlbAutoImportPhase.Finished;
                return DirectGlbAutoImportDecision.Refused;
            }

            if (evidence.TaskId is null)
            {
                return DirectGlbAutoImportDecision.NoAction;
            }

            if (!TryBindResolvedTask(evidence.TaskId))
            {
                return DirectGlbAutoImportDecision.Refused;
            }

            Tripo.Bridge.HostControlTaskStatusReceipt? status =
                state.GenerationStatus;
            if (status is null)
            {
                return _phase == DirectGlbAutoImportPhase.Stopped
                    ? DirectGlbAutoImportDecision.Stopped
                    : DirectGlbAutoImportDecision.Waiting;
            }

            if (!StatusMatchesBoundTask(status))
            {
                _phase = DirectGlbAutoImportPhase.Finished;
                return DirectGlbAutoImportDecision.Refused;
            }

            string normalized =
                status.Status?.Trim().ToLowerInvariant() ?? string.Empty;
            if (normalized is "queued" or "running")
            {
                return _phase == DirectGlbAutoImportPhase.Stopped
                    ? DirectGlbAutoImportDecision.Stopped
                    : DirectGlbAutoImportDecision.Waiting;
            }

            if (normalized == "success")
            {
                if (_phase == DirectGlbAutoImportPhase.Stopped)
                {
                    return DirectGlbAutoImportDecision.Stopped;
                }

                _phase = DirectGlbAutoImportPhase.Importing;
                return DirectGlbAutoImportDecision.BeginImport;
            }

            _phase = DirectGlbAutoImportPhase.Finished;
            return string.IsNullOrWhiteSpace(normalized)
                ? DirectGlbAutoImportDecision.Refused
                : DirectGlbAutoImportDecision.TerminalWithoutImport;
        }
    }

    internal bool TryStopWaiting(
        long sessionGeneration,
        TripoPanelState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate)
        {
            if (!MatchesStateIdentity(sessionGeneration, state) ||
                _phase != DirectGlbAutoImportPhase.Waiting)
            {
                return false;
            }

            DurableTaskEvidence evidence = ResolveDurableTaskEvidence(state);
            if (evidence.Invalid)
            {
                _phase = DirectGlbAutoImportPhase.Finished;
                return false;
            }

            if (evidence.TaskId is null ||
                !TryBindResolvedTask(evidence.TaskId))
            {
                return false;
            }

            Tripo.Bridge.HostControlTaskStatusReceipt? status =
                state.GenerationStatus;
            if (status is null || !StatusMatchesBoundTask(status))
            {
                return false;
            }

            string normalized = status.Status?.Trim() ?? string.Empty;
            if (!string.Equals(
                    normalized,
                    "queued",
                    StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(
                    normalized,
                    "running",
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            _phase = DirectGlbAutoImportPhase.Stopped;
            return true;
        }
    }

    internal bool TryResumeWaiting(
        long sessionGeneration,
        TripoPanelState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate)
        {
            if (!MatchesStateIdentity(sessionGeneration, state) ||
                _phase != DirectGlbAutoImportPhase.Stopped)
            {
                return false;
            }

            DurableTaskEvidence evidence = ResolveDurableTaskEvidence(state);
            if (evidence.Invalid ||
                evidence.TaskId is null ||
                !TryBindResolvedTask(evidence.TaskId))
            {
                _phase = evidence.Invalid
                    ? DirectGlbAutoImportPhase.Finished
                    : _phase;
                return false;
            }

            _phase = DirectGlbAutoImportPhase.Waiting;
            return true;
        }
    }

    internal bool TryFinishImport(
        long sessionGeneration,
        TripoPanelState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate)
        {
            if (!MatchesStateIdentity(sessionGeneration, state) ||
                _phase != DirectGlbAutoImportPhase.Importing)
            {
                return false;
            }

            DurableTaskEvidence evidence = ResolveDurableTaskEvidence(state);
            if (evidence.Invalid ||
                evidence.TaskId is null ||
                !TryBindResolvedTask(evidence.TaskId))
            {
                _phase = DirectGlbAutoImportPhase.Finished;
                return false;
            }

            _phase = DirectGlbAutoImportPhase.Finished;
            return true;
        }
    }

    internal bool TryDeferImport(
        long sessionGeneration,
        TripoPanelState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate)
        {
            if (!MatchesStateIdentity(sessionGeneration, state) ||
                _phase != DirectGlbAutoImportPhase.Importing ||
                state.PreparedImport is not null ||
                state.ImportDispatchAttempted)
            {
                return false;
            }

            DurableTaskEvidence evidence = ResolveDurableTaskEvidence(state);
            if (evidence.Invalid ||
                evidence.TaskId is null ||
                !TryBindResolvedTask(evidence.TaskId) ||
                state.GenerationStatus is not { } status ||
                !StatusMatchesBoundTask(status) ||
                !string.Equals(
                    status.Status?.Trim(),
                    "success",
                    StringComparison.OrdinalIgnoreCase))
            {
                _phase = DirectGlbAutoImportPhase.Finished;
                return false;
            }

            _phase = DirectGlbAutoImportPhase.Waiting;
            return true;
        }
    }

    private bool MatchesStateIdentity(
        long sessionGeneration,
        TripoPanelState state) =>
        sessionGeneration == SessionGeneration &&
        state.Connected &&
        state.Context is not null &&
        state.PreparedGeneration is not null &&
        string.Equals(
            state.Context.DocumentSessionId,
            DocumentSessionId,
            StringComparison.Ordinal) &&
        string.Equals(
            state.PreparedGeneration.DocumentSessionId,
            DocumentSessionId,
            StringComparison.Ordinal) &&
        string.Equals(
            state.PreparedGeneration.OperationId,
            GenerationOperationId,
            StringComparison.Ordinal);

    private bool TryBindResolvedTask(string taskId)
    {
        if (_taskId is null)
        {
            _taskId = taskId;
            return true;
        }

        if (string.Equals(_taskId, taskId, StringComparison.Ordinal))
        {
            return true;
        }

        _phase = DirectGlbAutoImportPhase.Finished;
        return false;
    }

    private DurableTaskEvidence ResolveDurableTaskEvidence(
        TripoPanelState state)
    {
        string? receiptTaskId = null;
        Tripo.Bridge.HostControlTextTaskCreationReceipt? receipt =
            state.GenerationReceipt;
        if (receipt is not null)
        {
            if (!string.Equals(
                    receipt.OperationId,
                    GenerationOperationId,
                    StringComparison.Ordinal) ||
                !Tripo.Bridge.TripoTaskId.IsValid(receipt.TaskId))
            {
                return DurableTaskEvidence.InvalidEvidence;
            }

            receiptTaskId = receipt.TaskId;
        }

        string? operationTaskId = null;
        Tripo.Bridge.HostControlOperationStatusReceipt? operationStatus =
            state.GenerationOperationStatus;
        if (operationStatus is not null)
        {
            if (!string.Equals(
                    operationStatus.OperationId,
                    GenerationOperationId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    operationStatus.Kind,
                    ExpectedOperationKind,
                    StringComparison.Ordinal) ||
                operationStatus.SourceTaskId is not null)
            {
                return DurableTaskEvidence.InvalidEvidence;
            }

            if (operationStatus.TaskIdDurable)
            {
                if (!Tripo.Bridge.TripoTaskId.IsValid(
                        operationStatus.CreatedTaskId))
                {
                    return DurableTaskEvidence.InvalidEvidence;
                }

                operationTaskId = operationStatus.CreatedTaskId;
            }
        }

        if (receiptTaskId is not null &&
            operationTaskId is not null &&
            !string.Equals(
                receiptTaskId,
                operationTaskId,
                StringComparison.Ordinal))
        {
            return DurableTaskEvidence.InvalidEvidence;
        }

        return new DurableTaskEvidence(
            receiptTaskId ?? operationTaskId,
            Invalid: false);
    }

    private bool StatusMatchesBoundTask(
        Tripo.Bridge.HostControlTaskStatusReceipt status) =>
        _taskId is not null &&
        string.Equals(status.TaskId, _taskId, StringComparison.Ordinal) &&
        Tripo.Bridge.TripoTaskId.IsValid(status.TaskId) &&
        string.Equals(
            status.Type,
            ExpectedTaskType,
            StringComparison.Ordinal);

    private static string RequireCanonicalUuid(string value, string paramName)
    {
        if (!Guid.TryParseExact(value, "D", out Guid parsed) ||
            !string.Equals(
                parsed.ToString("D"),
                value,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The identity must be a canonical lowercase UUID.",
                paramName);
        }

        return value;
    }

    private readonly record struct DurableTaskEvidence(
        string? TaskId,
        bool Invalid)
    {
        internal static DurableTaskEvidence InvalidEvidence =>
            new(null, Invalid: true);
    }
}
