namespace Tripo.HostUi;

public sealed record PreparedTextGeneration(
    string Prompt,
    int FaceLimit,
    bool WithMaterials,
    string DocumentSessionId,
    string OperationId);

public sealed record PreparedObjConversion(
    string SourceTaskId,
    int FaceLimit,
    bool WithMaterials,
    string DocumentSessionId,
    string OperationId);

public sealed record PreparedObjImport(
    string ConversionTaskId,
    string Name,
    string DocumentSessionId,
    string OperationId,
    string ImportMode,
    bool ApplyMaterials,
    string ArtifactFormat = "obj")
{
    public bool IsDirectGlb =>
        string.Equals(ArtifactFormat, "glb", StringComparison.Ordinal);
}

public sealed record TripoPanelImportReceipt(
    string OperationId,
    string SourceTaskId,
    string ArtifactFormat,
    decimal? SourceCreditsConsumed,
    Tripo.Bridge.HostImportReceipt HostReceipt)
{
    public static implicit operator TripoPanelImportReceipt(
        Tripo.Bridge.HostControlObjTaskImportReceipt receipt) =>
        new(
            receipt.OperationId,
            receipt.ConversionTaskId,
            "obj",
            receipt.ConversionCreditsConsumed,
            receipt.HostReceipt);

    public static implicit operator TripoPanelImportReceipt(
        Tripo.Bridge.HostControlGenerationGlbImportReceipt receipt) =>
        new(
            receipt.OperationId,
            receipt.GenerationTaskId,
            "glb",
            receipt.GenerationCreditsConsumed,
            receipt.HostReceipt);
}

public sealed record TripoPanelRecoveryOperationInspection(
    string OperationId,
    Tripo.Bridge.HostControlOperationStatusReceipt? Receipt,
    string? ErrorCode,
    string? ErrorMessage)
{
    public bool Available => Receipt is not null;
}

public sealed class TripoPanelRecoveryReviewSnapshot
{
    private TripoPanelRecoveryReviewSnapshot(
        TripoPanelRecoveryLoadResult recovery,
        IReadOnlyList<TripoPanelRecoveryOperationInspection> paidOperations,
        string evidenceToken)
    {
        Recovery = recovery;
        PaidOperations = paidOperations;
        EvidenceToken = evidenceToken;
    }

    public TripoPanelRecoveryLoadResult Recovery { get; }

    public IReadOnlyList<TripoPanelRecoveryOperationInspection>
        PaidOperations
    {
        get;
    }

    public string RecoveryToken => Recovery.PresentationToken;

    public string EvidenceToken { get; }

    public bool HasOperationInProgress =>
        PaidOperations.Any(operation =>
            operation.Receipt?.OperationInProgress == true ||
            string.Equals(
                operation.Receipt?.State,
                "operation_in_progress",
                StringComparison.Ordinal));

    internal static TripoPanelRecoveryReviewSnapshot Create(
        TripoPanelRecoveryLoadResult recovery,
        IReadOnlyList<TripoPanelRecoveryOperationInspection> paidOperations)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        ArgumentNullException.ThrowIfNull(paidOperations);
        System.Text.StringBuilder material = new();
        AppendTokenPart(material, recovery.PresentationToken);
        foreach (TripoPanelRecoveryOperationInspection operation in
                 paidOperations.OrderBy(
                     item => item.OperationId,
                     StringComparer.Ordinal))
        {
            AppendTokenPart(material, operation.OperationId);
            AppendTokenPart(
                material,
                System.Text.Json.JsonSerializer.Serialize(
                    operation,
                    Tripo.Bridge.BridgeJson.Options));
        }

        string token = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(material.ToString())));
        return new TripoPanelRecoveryReviewSnapshot(
            recovery,
            paidOperations.ToArray(),
            token);
    }

    private static void AppendTokenPart(
        System.Text.StringBuilder material,
        string value)
    {
        material
            .Append(
                value.Length.ToString(
                    System.Globalization.CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value);
    }
}

public sealed record TripoPanelState(
    bool Connected,
    bool Busy,
    Tripo.Bridge.HostContextReceipt? Context,
    Tripo.Bridge.HostControlCredentialStatusReceipt? CredentialStatus,
    PreparedTextGeneration? PreparedGeneration,
    bool GenerationDispatchAttempted,
    Tripo.Bridge.HostControlTextTaskCreationReceipt? GenerationReceipt,
    Tripo.Bridge.HostControlOperationStatusReceipt? GenerationOperationStatus,
    Tripo.Bridge.HostControlTaskStatusReceipt? GenerationStatus,
    PreparedObjConversion? PreparedConversion,
    bool ConversionDispatchAttempted,
    Tripo.Bridge.HostControlObjConversionCreationReceipt? ConversionReceipt,
    Tripo.Bridge.HostControlOperationStatusReceipt? ConversionOperationStatus,
    Tripo.Bridge.HostControlTaskStatusReceipt? ConversionStatus,
    PreparedObjImport? PreparedImport,
    bool ImportDispatchAttempted,
    TripoPanelImportReceipt? ImportReceipt,
    string? LastError,
    string? ImportFailureCode = null,
    string? LastErrorCode = null)
{
    public static TripoPanelState Initial { get; } = new(
        Connected: false,
        Busy: false,
        Context: null,
        CredentialStatus: null,
        PreparedGeneration: null,
        GenerationDispatchAttempted: false,
        GenerationReceipt: null,
        GenerationOperationStatus: null,
        GenerationStatus: null,
        PreparedConversion: null,
        ConversionDispatchAttempted: false,
        ConversionReceipt: null,
        ConversionOperationStatus: null,
        ConversionStatus: null,
        PreparedImport: null,
        ImportDispatchAttempted: false,
        ImportReceipt: null,
        LastError: null,
        ImportFailureCode: null,
        LastErrorCode: null);

    public bool HasWorkflowState =>
        PreparedGeneration is not null ||
        GenerationDispatchAttempted ||
        GenerationReceipt is not null ||
        GenerationOperationStatus is not null ||
        GenerationStatus is not null ||
        PreparedConversion is not null ||
        ConversionDispatchAttempted ||
        ConversionReceipt is not null ||
        ConversionOperationStatus is not null ||
        ConversionStatus is not null ||
        PreparedImport is not null ||
        ImportDispatchAttempted ||
        ImportReceipt is not null;

    public bool HasUnresolvedPaidDispatch =>
        (GenerationDispatchAttempted &&
         GenerationReceipt is null &&
         GenerationOperationStatus?.TaskIdDurable != true &&
         !IsDefinitiveRequestRejection(GenerationOperationStatus)) ||
        (ConversionDispatchAttempted &&
         ConversionReceipt is null &&
         ConversionOperationStatus?.TaskIdDurable != true &&
         !IsDefinitiveRequestRejection(ConversionOperationStatus));

    public bool HasUnresolvedDispatch =>
        HasUnresolvedPaidDispatch ||
        (ImportDispatchAttempted && ImportReceipt is null);

    public bool HasCredentialBoundWorkflow =>
        GenerationDispatchAttempted ||
        GenerationReceipt is not null ||
        GenerationOperationStatus is not null ||
        GenerationStatus is not null ||
        ConversionDispatchAttempted ||
        ConversionReceipt is not null ||
        ConversionOperationStatus is not null ||
        ConversionStatus is not null ||
        ImportDispatchAttempted ||
        ImportReceipt is not null;

    public bool RequiresCredentialRecovery =>
        HasCredentialBoundWorkflow;

    public bool HasDurableGenerationTask =>
        GenerationReceipt is not null ||
        GenerationOperationStatus?.TaskIdDurable == true;

    public bool HasCredentialRefreshFailure =>
        string.Equals(
            LastErrorCode,
            Tripo.Bridge.HostControlConstants.CredentialInvalidError,
            StringComparison.Ordinal) ||
        string.Equals(
            LastErrorCode,
            Tripo.Bridge.HostControlConstants.CredentialRejectedError,
            StringComparison.Ordinal);

    public bool CanDispatchPreparedGeneration =>
        PreparedGeneration is not null &&
        !HasDurableGenerationTask &&
        !IsDefinitiveRequestRejection(GenerationOperationStatus);

    public bool GenerationRetryRequired =>
        CanDispatchPreparedGeneration &&
        GenerationDispatchAttempted;

    public bool GenerationRetryAllowed =>
        GenerationRetryRequired &&
        GenerationOperationStatus?.CanResumeCreation == true;

    public bool HasDurableConversionTask =>
        ConversionReceipt is not null ||
        ConversionOperationStatus?.TaskIdDurable == true;

    public bool CanDispatchPreparedConversion =>
        PreparedConversion is not null &&
        !HasDurableConversionTask &&
        !IsDefinitiveRequestRejection(ConversionOperationStatus);

    public bool ConversionRetryRequired =>
        CanDispatchPreparedConversion &&
        ConversionDispatchAttempted;

    public bool ConversionRetryAllowed =>
        ConversionRetryRequired &&
        ConversionOperationStatus?.CanResumeCreation == true;

    internal static bool IsDefinitiveRequestRejection(
        Tripo.Bridge.HostControlOperationStatusReceipt? status) =>
        status is
        {
            State: Tripo.Bridge.HostControlConstants.RequestRejectedState,
            CreatedTaskId: null,
            TaskIdDurable: false,
            MayHaveCreatedRemoteTask: false,
            CanResumeCreation: false,
            OperationInProgress: false,
            FailureStage: null,
        };

    public bool CanDispatchPreparedImport =>
        PreparedImport is not null &&
        ImportReceipt is null &&
        !ImportRequiresManualReview;

    public bool ImportRetryRequired =>
        CanDispatchPreparedImport &&
        ImportDispatchAttempted;

    public bool ImportRequiresManualReview =>
        ImportDispatchAttempted &&
        ImportReceipt is null &&
        string.Equals(
            ImportFailureCode,
            Tripo.Bridge.BridgeConstants.MutationStateUncertainError,
            StringComparison.Ordinal);
}

public sealed class TripoPanelSession : IAsyncDisposable
{
    private readonly Tripo.Bridge.IHostSidecarConnector _connector;
    private readonly TripoPanelRecoveryStore? _recoveryStore;
    private readonly Tripo.Bridge.ICredentialWorkflowExecutionGate?
        _recoveryArchiveGate;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _stateGate = new();
    private Tripo.Bridge.IHostControlClient? _client;
    private TripoPanelState _state = TripoPanelState.Initial;
    private TripoPanelRecoveryLoadResult _recovery;
    private long _recoveryRevision;
    private Task? _disposeTask;
    private volatile bool _disposed;

    public TripoPanelSession(
        Tripo.Bridge.IHostSidecarConnector connector,
        TripoPanelRecoveryStore? recoveryStore = null,
        Tripo.Bridge.ICredentialWorkflowExecutionGate?
            recoveryArchiveGate = null)
    {
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
        _recoveryStore = recoveryStore;
        _recoveryArchiveGate = recoveryStore is null
            ? recoveryArchiveGate
            : recoveryArchiveGate ??
              new Tripo.Bridge.CredentialWorkflowExecutionGate(
                  recoveryStore.RootDirectory);
        _recovery =
            recoveryStore?.LoadStale() ??
            TripoPanelRecoveryLoadResult.Empty;
    }

    public event EventHandler<TripoPanelState>? StateChanged;

    public event EventHandler<TripoPanelRecoveryLoadResult>? RecoveryChanged;

    public TripoPanelState State
    {
        get
        {
            lock (_stateGate)
            {
                return _state;
            }
        }
    }

    public TripoPanelRecoveryLoadResult Recovery
    {
        get
        {
            lock (_stateGate)
            {
                return _recovery;
            }
        }
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(
            async token =>
            {
                Tripo.Bridge.IHostControlClient client =
                    await _connector.EnsureConnectedAsync(token)
                        .ConfigureAwait(false);
                Tripo.Bridge.HostControlHealthReceipt health =
                    await client.GetHealthAsync(token).ConfigureAwait(false);
                Tripo.Bridge.HostContextReceipt context =
                    await client.GetHostContextAsync(token).ConfigureAwait(false);
                EnsureHealthMatchesContext(health, context);
                if (!health.Capabilities.Contains(
                        Tripo.Bridge.HostControlConstants
                            .ImportGenerationGlbMethod,
                        StringComparer.Ordinal))
                {
                    context = context with
                    {
                        Capabilities = context.Capabilities
                            .Where(capability => !string.Equals(
                                capability,
                                Tripo.Bridge.BridgeConstants.ImportGlbMethod,
                                StringComparison.Ordinal))
                            .ToArray(),
                    };
                }

                TripoPanelState current = State;
                if (current.Context is not null &&
                    !IsSameDocumentSession(current.Context, context) &&
                    current.HasWorkflowState)
                {
                    throw new InvalidOperationException(
                        "The active host document changed while this panel still " +
                        "owns workflow state. Return to the original document, or " +
                        "resolve the existing operation before starting a new workflow.");
                }

                Tripo.Bridge.HostControlCredentialStatusReceipt credentials =
                    await client.GetCredentialStatusAsync(token)
                        .ConfigureAwait(false);
                _client = client;
                UpdateState(state => state with
                {
                    Connected = true,
                    Context = context,
                    CredentialStatus = credentials,
                    LastError = null,
                    LastErrorCode = null,
                });
            },
            cancellationToken);

    public Task SetApiKeyAsync(
        string apiKey,
        bool persist,
        CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(
            async token =>
            {
                using IDisposable? lease =
                    _recoveryStore?.AcquireCredentialWorkflowLease();
                EnsureNoStaleRecovery();
                if (persist && State.RequiresCredentialRecovery)
                {
                    throw new InvalidOperationException(
                        "A recovery API key must remain session-only until " +
                        "the account-bound workflow is reconciled and " +
                        "explicitly reset.");
                }

                EnsureCredentialMutationAllowed(
                    allowCredentialRecovery: true);
                Tripo.Bridge.IHostControlClient client = RequireClient();
                Tripo.Bridge.HostControlCredentialMutationReceipt receipt =
                    await client.SetApiKeyAsync(apiKey, persist, token)
                        .ConfigureAwait(false);
                UpdateState(state => state with
                {
                    CredentialStatus = receipt.Status,
                    LastError = null,
                    LastErrorCode = null,
                });
            },
            cancellationToken);

    public Task ClearApiKeyAsync(
        CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(
            async token =>
            {
                using IDisposable? lease =
                    _recoveryStore?.AcquireCredentialWorkflowLease();
                EnsureNoStaleRecovery();
                EnsureCredentialMutationAllowed(
                    allowCredentialRecovery: false);
                Tripo.Bridge.IHostControlClient client = RequireClient();
                Tripo.Bridge.HostControlCredentialMutationReceipt receipt =
                    await client.ClearApiKeyAsync(token).ConfigureAwait(false);
                UpdateState(state => state with
                {
                    CredentialStatus = receipt.Status,
                    LastError = null,
                    LastErrorCode = null,
                });
            },
            cancellationToken);

    public PreparedTextGeneration PrepareGeneration(
        string prompt,
        int faceLimit,
        bool withMaterials)
    {
        EnterSynchronousMutation();
        try
        {
            EnsureNoStaleRecovery();
            ValidatePrompt(prompt);
            ValidateFaceLimit(faceLimit);
            if (State.HasWorkflowState)
            {
                throw new InvalidOperationException(
                    "Use the prepared generation operation or start a new workflow " +
                    "before creating another operation ID.");
            }

            Tripo.Bridge.HostContextReceipt context = RequireContext();
            PreparedTextGeneration prepared = new(
                prompt,
                faceLimit,
                withMaterials,
                context.DocumentSessionId,
                Guid.NewGuid().ToString("D"));
            UpdateState(state => state with
            {
                PreparedGeneration = prepared,
                GenerationDispatchAttempted = false,
                GenerationReceipt = null,
                GenerationOperationStatus = null,
                GenerationStatus = null,
                PreparedConversion = null,
                ConversionDispatchAttempted = false,
                ConversionReceipt = null,
                ConversionOperationStatus = null,
                ConversionStatus = null,
                PreparedImport = null,
                ImportDispatchAttempted = false,
                ImportReceipt = null,
                LastError = null,
                LastErrorCode = null,
                ImportFailureCode = null,
            });
            return prepared;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public Task DispatchPreparedGenerationAsync(
        bool userConfirmedExternalCost,
        CancellationToken cancellationToken = default) =>
        DispatchPreparedGenerationCoreAsync(
            userConfirmedExternalCost,
            requiredHostCapability: null,
            requiredSidecarCapability: null,
            cancellationToken);

    internal Task DispatchPreparedGenerationRequiringCapabilityAsync(
        bool userConfirmedExternalCost,
        string requiredHostCapability,
        string requiredSidecarCapability,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(requiredHostCapability))
        {
            throw new ArgumentException(
                "The required host capability is missing.",
                nameof(requiredHostCapability));
        }

        if (string.IsNullOrWhiteSpace(requiredSidecarCapability))
        {
            throw new ArgumentException(
                "The required sidecar capability is missing.",
                nameof(requiredSidecarCapability));
        }

        return DispatchPreparedGenerationCoreAsync(
            userConfirmedExternalCost,
            requiredHostCapability,
            requiredSidecarCapability,
            cancellationToken);
    }

    private Task DispatchPreparedGenerationCoreAsync(
        bool userConfirmedExternalCost,
        string? requiredHostCapability,
        string? requiredSidecarCapability,
        CancellationToken cancellationToken)
    {
        if (!userConfirmedExternalCost)
        {
            throw new InvalidOperationException(
                "Generation was not sent because external cost was not confirmed.");
        }

        return RunExclusiveAsync(
            async token =>
            {
                using IDisposable? lease =
                    _recoveryStore?.AcquireCredentialWorkflowLease();
                EnsureNoStaleRecovery();
                Tripo.Bridge.IHostControlClient client = RequireClient();
                TripoPanelState current = State;
                PreparedTextGeneration prepared =
                    current.PreparedGeneration ??
                    throw new InvalidOperationException(
                        "Prepare a generation operation before dispatch.");
                EnsurePaidDispatchAllowed(
                    current.CanDispatchPreparedGeneration,
                    current.GenerationRetryRequired,
                    current.GenerationOperationStatus,
                    "generation");
                await EnsureSessionStillActiveAsync(
                        client,
                        prepared.DocumentSessionId,
                        requiredHostCapability,
                        requiredSidecarCapability,
                        token)
                    .ConfigureAwait(false);
                UpdateState(state => state with
                {
                    GenerationDispatchAttempted = true,
                });
                Tripo.Bridge.HostControlTextTaskCreationReceipt receipt;
                try
                {
                    receipt = await client.CreateTextTaskAsync(
                                new Tripo.Bridge.HostControlCreateTextTaskRequest(
                                    prepared.Prompt,
                                    prepared.FaceLimit,
                                    prepared.WithMaterials,
                                    prepared.DocumentSessionId,
                                    prepared.OperationId,
                                    ConfirmExternalCost: true,
                                    RequireExistingOperation:
                                        current.GenerationRetryRequired),
                                token)
                            .ConfigureAwait(false);
                }
                catch (Tripo.Bridge.HostControlCallException exception)
                    when (string.Equals(
                        exception.Code,
                        Tripo.Bridge.HostControlConstants.CredentialInvalidError,
                        StringComparison.Ordinal) &&
                        !current.GenerationRetryRequired)
                {
                    UpdateState(state => state with
                    {
                        GenerationDispatchAttempted = false,
                        GenerationOperationStatus = null,
                    });
                    throw;
                }
                catch (Tripo.Bridge.HostControlCallException exception)
                    when (string.Equals(
                        exception.Code,
                        Tripo.Bridge.HostControlConstants.CredentialRejectedError,
                        StringComparison.Ordinal))
                {
                    Tripo.Bridge.HostControlOperationStatusReceipt? status =
                        await TryGetDefinitiveRequestRejectionAsync(
                                client,
                                prepared.OperationId,
                                "text_task_creation",
                                token)
                            .ConfigureAwait(false);
                    if (status is not null)
                    {
                        ClearRejectedGenerationStage();
                    }

                    throw;
                }

                UpdateState(state => state with
                {
                    GenerationReceipt = receipt,
                    GenerationOperationStatus = null,
                    LastError = null,
                    LastErrorCode = null,
                });
            },
            cancellationToken);
    }

    public Task RefreshGenerationStatusAsync(
        CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(
            async token =>
            {
                Tripo.Bridge.IHostControlClient client = RequireClient();
                string taskId =
                    await ResolveGenerationTaskIdAsync(client, token)
                        .ConfigureAwait(false);
                Tripo.Bridge.HostControlTaskStatusReceipt status =
                    await client.GetTaskStatusAsync(taskId, token)
                        .ConfigureAwait(false);
                UpdateState(state => state with
                {
                    GenerationStatus = status,
                    LastError = null,
                    LastErrorCode = null,
                });
            },
            cancellationToken);

    public PreparedObjConversion PrepareConversion(
        int faceLimit,
        bool withMaterials)
    {
        EnterSynchronousMutation();
        try
        {
            EnsureNoStaleRecovery();
            ValidateFaceLimit(faceLimit);
            TripoPanelState state = State;
            EnsureSuccessfulTask(state.GenerationStatus, "generation");
            if (state.PreparedConversion is not null ||
                state.ConversionDispatchAttempted ||
                state.ConversionReceipt is not null ||
                state.ConversionOperationStatus is not null ||
                state.ConversionStatus is not null)
            {
                throw new InvalidOperationException(
                    "Use the prepared conversion operation or start a new workflow " +
                    "before creating another operation ID.");
            }

            PreparedObjConversion prepared = new(
                state.GenerationStatus!.TaskId,
                faceLimit,
                withMaterials,
                state.PreparedGeneration!.DocumentSessionId,
                Guid.NewGuid().ToString("D"));
            UpdateState(current => current with
            {
                PreparedConversion = prepared,
                ConversionDispatchAttempted = false,
                ConversionReceipt = null,
                ConversionOperationStatus = null,
                ConversionStatus = null,
                PreparedImport = null,
                ImportDispatchAttempted = false,
                ImportReceipt = null,
                LastError = null,
                LastErrorCode = null,
                ImportFailureCode = null,
            });
            return prepared;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public Task DispatchPreparedConversionAsync(
        bool userConfirmedExternalCost,
        CancellationToken cancellationToken = default)
    {
        if (!userConfirmedExternalCost)
        {
            throw new InvalidOperationException(
                "Conversion was not sent because external cost was not confirmed.");
        }

        return RunExclusiveAsync(
            async token =>
            {
                using IDisposable? lease =
                    _recoveryStore?.AcquireCredentialWorkflowLease();
                EnsureNoStaleRecovery();
                Tripo.Bridge.IHostControlClient client = RequireClient();
                TripoPanelState current = State;
                PreparedObjConversion prepared =
                    current.PreparedConversion ??
                    throw new InvalidOperationException(
                        "Prepare a conversion operation before dispatch.");
                EnsurePaidDispatchAllowed(
                    current.CanDispatchPreparedConversion,
                    current.ConversionRetryRequired,
                    current.ConversionOperationStatus,
                    "conversion");
                await EnsureSessionStillActiveAsync(
                        client,
                        prepared.DocumentSessionId,
                        requiredHostCapability: null,
                        requiredSidecarCapability: null,
                        cancellationToken: token)
                    .ConfigureAwait(false);
                UpdateState(state => state with
                {
                    ConversionDispatchAttempted = true,
                });
                Tripo.Bridge.HostControlObjConversionCreationReceipt receipt;
                try
                {
                    receipt = await client.CreateObjConversionAsync(
                                new Tripo.Bridge
                                    .HostControlCreateObjConversionRequest(
                                        prepared.SourceTaskId,
                                        prepared.FaceLimit,
                                        prepared.WithMaterials,
                                        prepared.DocumentSessionId,
                                        prepared.OperationId,
                                        ConfirmExternalCost: true,
                                        RequireExistingOperation:
                                            current.ConversionRetryRequired),
                                token)
                            .ConfigureAwait(false);
                }
                catch (Tripo.Bridge.HostControlCallException exception)
                    when (string.Equals(
                        exception.Code,
                        Tripo.Bridge.HostControlConstants.CredentialInvalidError,
                        StringComparison.Ordinal) &&
                        !current.ConversionRetryRequired)
                {
                    UpdateState(state => state with
                    {
                        ConversionDispatchAttempted = false,
                        ConversionOperationStatus = null,
                    });
                    throw;
                }
                catch (Tripo.Bridge.HostControlCallException exception)
                    when (string.Equals(
                        exception.Code,
                        Tripo.Bridge.HostControlConstants.CredentialRejectedError,
                        StringComparison.Ordinal))
                {
                    Tripo.Bridge.HostControlOperationStatusReceipt? status =
                        await TryGetDefinitiveRequestRejectionAsync(
                                client,
                                prepared.OperationId,
                                "obj_conversion_creation",
                                token)
                            .ConfigureAwait(false);
                    if (status is not null)
                    {
                        ClearRejectedConversionStage();
                    }

                    throw;
                }

                UpdateState(state => state with
                {
                    ConversionReceipt = receipt,
                    ConversionOperationStatus = null,
                    LastError = null,
                    LastErrorCode = null,
                });
            },
            cancellationToken);
    }

    public Task RefreshConversionStatusAsync(
        CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(
            async token =>
            {
                Tripo.Bridge.IHostControlClient client = RequireClient();
                string taskId =
                    await ResolveConversionTaskIdAsync(client, token)
                        .ConfigureAwait(false);
                Tripo.Bridge.HostControlTaskStatusReceipt status =
                    await client.GetTaskStatusAsync(taskId, token)
                        .ConfigureAwait(false);
                UpdateState(state => state with
                {
                    ConversionStatus = status,
                    LastError = null,
                    LastErrorCode = null,
                });
            },
            cancellationToken);

    public PreparedObjImport PrepareImport(
        string name,
        string importMode,
        bool applyMaterials)
    {
        EnterSynchronousMutation();
        try
        {
            EnsureNoStaleRecovery();
            ValidateName(name);
            string normalizedMode = ValidateImportMode(importMode);
            TripoPanelState state = State;
            EnsureSuccessfulTask(state.ConversionStatus, "conversion");
            if (state.PreparedImport is not null ||
                state.ImportDispatchAttempted ||
                state.ImportReceipt is not null)
            {
                throw new InvalidOperationException(
                    "Use the prepared import operation or start a new workflow before " +
                    "creating another operation ID.");
            }

            PreparedObjImport prepared = new(
                state.ConversionStatus!.TaskId,
                name,
                state.PreparedConversion!.DocumentSessionId,
                Guid.NewGuid().ToString("D"),
                normalizedMode,
                applyMaterials);
            UpdateState(current => current with
            {
                PreparedImport = prepared,
                ImportDispatchAttempted = false,
                ImportReceipt = null,
                LastError = null,
                LastErrorCode = null,
                ImportFailureCode = null,
            });
            return prepared;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public PreparedObjImport PrepareGlbImport(string name)
    {
        EnterSynchronousMutation();
        try
        {
            EnsureNoStaleRecovery();
            ValidateName(name);
            TripoPanelState state = State;
            EnsureSuccessfulTask(state.GenerationStatus, "generation");
            Tripo.Bridge.HostContextReceipt context =
                state.Context ??
                throw new InvalidOperationException(
                    "Connect to Rhino before preparing direct GLB import.");
            if (!string.Equals(
                    context.Host,
                    "rhino",
                    StringComparison.OrdinalIgnoreCase) ||
                !context.Capabilities.Contains(
                    Tripo.Bridge.BridgeConstants.ImportGlbMethod,
                    StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    "The connected Rhino plugin does not advertise direct GLB " +
                    "import. Install the matching build or use the OBJ fallback.");
            }

            if (state.PreparedImport is not null ||
                state.ImportDispatchAttempted ||
                state.ImportReceipt is not null)
            {
                throw new InvalidOperationException(
                    "Use the prepared import operation or start a new workflow before " +
                    "creating another operation ID.");
            }

            PreparedObjImport prepared = new(
                state.GenerationStatus!.TaskId,
                name,
                state.PreparedGeneration!.DocumentSessionId,
                Guid.NewGuid().ToString("D"),
                "glb_instance",
                ApplyMaterials: true,
                ArtifactFormat: "glb");
            UpdateState(current => current with
            {
                PreparedImport = prepared,
                ImportDispatchAttempted = false,
                ImportReceipt = null,
                LastError = null,
                LastErrorCode = null,
                ImportFailureCode = null,
            });
            return prepared;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public Task ImportPreparedAsync(
        CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(
            async token =>
            {
                EnsureNoStaleRecovery();
                Tripo.Bridge.IHostControlClient client = RequireClient();
                TripoPanelState current = State;
                PreparedObjImport prepared =
                    current.PreparedImport ??
                    throw new InvalidOperationException(
                        "Prepare an import operation before dispatch.");
                if (!current.CanDispatchPreparedImport)
                {
                    throw new InvalidOperationException(
                        current.ImportRequiresManualReview
                            ? "The import state is uncertain; manual document " +
                              "review is required and this operation must not be retried."
                            : "The prepared import already has a durable receipt.");
                }
                await EnsureSessionStillActiveAsync(
                        client,
                        prepared.DocumentSessionId,
                        requiredHostCapability: null,
                        requiredSidecarCapability: null,
                        cancellationToken: token)
                    .ConfigureAwait(false);
                UpdateState(state => state with
                {
                    ImportDispatchAttempted = true,
                    ImportFailureCode = null,
                });
                TripoPanelImportReceipt receipt;
                try
                {
                    if (prepared.IsDirectGlb)
                    {
                        Tripo.Bridge.HostControlGenerationGlbImportReceipt
                            glbReceipt =
                            await client.ImportGenerationGlbAsync(
                                    new Tripo.Bridge
                                        .HostControlImportGenerationGlbRequest(
                                            prepared.ConversionTaskId,
                                            prepared.Name,
                                            prepared.DocumentSessionId,
                                            prepared.OperationId,
                                            ApplyMaterials: true),
                                    token)
                                .ConfigureAwait(false);
                        receipt = glbReceipt;
                    }
                    else if (string.Equals(
                                 prepared.ArtifactFormat,
                                 "obj",
                                 StringComparison.Ordinal))
                    {
                        Tripo.Bridge.HostControlObjTaskImportReceipt objReceipt =
                            await client.ImportObjTaskAsync(
                                    new Tripo.Bridge
                                        .HostControlImportObjTaskRequest(
                                            prepared.ConversionTaskId,
                                            prepared.Name,
                                            prepared.DocumentSessionId,
                                            prepared.OperationId,
                                            prepared.ImportMode,
                                            prepared.ApplyMaterials),
                                    token)
                                .ConfigureAwait(false);
                        receipt = objReceipt;
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            "The prepared import format is not supported.");
                    }
                }
                catch (Tripo.Bridge.HostControlCallException exception)
                    when (string.Equals(
                        exception.Code,
                        Tripo.Bridge.HostControlConstants.CredentialInvalidError,
                        StringComparison.Ordinal))
                {
                    UpdateState(state => state with
                    {
                        ImportDispatchAttempted = false,
                        ImportReceipt = null,
                        ImportFailureCode = null,
                    });
                    throw;
                }
                catch (Tripo.Bridge.HostControlCallException exception)
                    when (string.Equals(
                        exception.Code,
                        Tripo.Bridge.BridgeConstants
                            .MutationStateUncertainError,
                        StringComparison.Ordinal))
                {
                    UpdateState(state => state with
                    {
                        ImportFailureCode = exception.Code,
                    });
                    throw;
                }

                UpdateState(state => state with
                {
                    ImportReceipt = receipt,
                    LastError = null,
                    LastErrorCode = null,
                    ImportFailureCode = null,
                });
            },
            cancellationToken);

    public void ResetWorkflow()
    {
        EnterSynchronousMutation();
        try
        {
            EnsureNoStaleRecovery();
            if (State.HasUnresolvedDispatch)
            {
                throw new InvalidOperationException(
                    "This panel has an unresolved dispatched operation. Keep its " +
                    "displayed operation ID and use Refresh or retry the same stage; " +
                    "a new workflow cannot discard that recovery identity.");
            }

            UpdateState(state => state with
            {
                PreparedGeneration = null,
                GenerationDispatchAttempted = false,
                GenerationReceipt = null,
                GenerationOperationStatus = null,
                GenerationStatus = null,
                PreparedConversion = null,
                ConversionDispatchAttempted = false,
                ConversionReceipt = null,
                ConversionOperationStatus = null,
                ConversionStatus = null,
                PreparedImport = null,
                ImportDispatchAttempted = false,
                ImportReceipt = null,
                LastError = null,
                LastErrorCode = null,
                ImportFailureCode = null,
            });
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<Tripo.Bridge.HostControlOperationStatusReceipt>
        InspectPaidRecoveryAsync(
            string operationId,
            CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParseExact(operationId, "D", out Guid parsed) ||
            !string.Equals(
                parsed.ToString("D"),
                operationId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "operationId must be a canonical lowercase D-format UUID.",
                nameof(operationId));
        }

        Tripo.Bridge.HostControlOperationStatusReceipt? receipt = null;
        await RunExclusiveAsync(
                async token =>
                {
                    receipt = await RequireClient()
                        .GetOperationStatusAsync(operationId, token)
                        .ConfigureAwait(false);
                },
                cancellationToken)
            .ConfigureAwait(false);
        return receipt ??
            throw new InvalidOperationException(
                "The paid-operation inspection returned no receipt.");
    }

    public async Task<TripoPanelRecoveryReviewSnapshot>
        CreateRecoveryReviewSnapshotAsync(
            CancellationToken cancellationToken = default)
    {
        TripoPanelRecoveryReviewSnapshot? snapshot = null;
        await RunExclusiveAsync(
                async token =>
                {
                    TripoPanelRecoveryStore store =
                        _recoveryStore ??
                        throw new InvalidOperationException(
                            "This panel has no recovery store.");
                    TripoPanelRecoveryLoadResult recovery =
                        store.LoadStale();
                    PublishRecovery(recovery);
                    IReadOnlyList<TripoPanelRecoveryOperationInspection>
                        inspections =
                            await InspectRecoveryOperationsAsync(
                                    recovery,
                                    RequireClient(),
                                    token)
                                .ConfigureAwait(false);
                    TripoPanelRecoveryLoadResult afterInspection =
                        store.LoadStale();
                    PublishRecovery(afterInspection);
                    if (!string.Equals(
                            recovery.PresentationToken,
                            afterInspection.PresentationToken,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "The recovered operation list changed during " +
                            "inspection. Review the refreshed list again.");
                    }

                    token.ThrowIfCancellationRequested();
                    snapshot =
                        TripoPanelRecoveryReviewSnapshot.Create(
                            afterInspection,
                            inspections);
                },
                cancellationToken)
            .ConfigureAwait(false);
        return snapshot ??
            throw new InvalidOperationException(
                "The recovery inspection produced no review snapshot.");
    }

    public Task UnlockRecoveredOperationsAsync(
        bool userConfirmed,
        TripoPanelRecoveryReviewSnapshot displayedSnapshot,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!userConfirmed)
        {
            throw new InvalidOperationException(
                "Confirm that every recovered operation has been reviewed " +
                "before unlocking Tripo.");
        }

        ArgumentNullException.ThrowIfNull(displayedSnapshot);

        TripoPanelRecoveryStore store =
            _recoveryStore ??
            throw new InvalidOperationException(
                "This panel has no recovery store.");
        return RunExclusiveAsync(
            async token =>
            {
                Tripo.Bridge.ICredentialWorkflowExecutionGate executionGate =
                    _recoveryArchiveGate ??
                    throw new InvalidOperationException(
                        "This panel has no recovery archival execution gate.");
                using IDisposable intentLease =
                    store.AcquireCredentialWorkflowLease();
                using IDisposable executionLease = executionGate.Acquire();
                token.ThrowIfCancellationRequested();
                TripoPanelRecoveryLoadResult current = store.LoadStale();
                PublishRecovery(current);
                if (!string.Equals(
                        displayedSnapshot.RecoveryToken,
                        current.PresentationToken,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The recovered operation list changed. Review the " +
                        "refreshed list before unlocking again.");
                }

                if (current.Issues.Count > 0)
                {
                    throw new InvalidOperationException(
                        "Invalid recovery files must be inspected and moved " +
                        "aside manually; the panel will not overwrite or " +
                        "archive them.");
                }

                if (State.HasWorkflowState)
                {
                    throw new InvalidOperationException(
                        "This panel still owns current workflow state. Reload " +
                        "the panel session so current and recovered operations " +
                        "can be reviewed together.");
                }

                Tripo.Bridge.IHostControlClient client = RequireClient();
                IReadOnlyList<TripoPanelRecoveryOperationInspection>
                    firstPass =
                        await InspectRecoveryOperationsAsync(
                                current,
                                client,
                                token)
                            .ConfigureAwait(false);
                TripoPanelRecoveryLoadResult afterFirstPass =
                    store.LoadStale();
                PublishRecovery(afterFirstPass);
                EnsureRecoveryTokenUnchanged(current, afterFirstPass);
                TripoPanelRecoveryReviewSnapshot firstSnapshot =
                    TripoPanelRecoveryReviewSnapshot.Create(
                        afterFirstPass,
                        firstPass);
                if (!string.Equals(
                        displayedSnapshot.EvidenceToken,
                        firstSnapshot.EvidenceToken,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The local paid-operation status changed after it was " +
                        "reviewed. Review the refreshed evidence before " +
                        "unlocking again.");
                }

                IReadOnlyList<TripoPanelRecoveryOperationInspection>
                    secondPass =
                        await InspectRecoveryOperationsAsync(
                                afterFirstPass,
                                client,
                                token)
                            .ConfigureAwait(false);
                TripoPanelRecoveryLoadResult beforeArchive =
                    store.LoadStale();
                PublishRecovery(beforeArchive);
                EnsureRecoveryTokenUnchanged(afterFirstPass, beforeArchive);
                TripoPanelRecoveryReviewSnapshot confirmedSnapshot =
                    TripoPanelRecoveryReviewSnapshot.Create(
                        beforeArchive,
                        secondPass);
                if (!string.Equals(
                        displayedSnapshot.EvidenceToken,
                        confirmedSnapshot.EvidenceToken,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The local paid-operation status changed while Tripo " +
                        "was confirming it. Review the refreshed evidence " +
                        "before unlocking again.");
                }

                if (confirmedSnapshot.HasOperationInProgress)
                {
                    throw new InvalidOperationException(
                        "A recovered paid operation is still active. Wait for " +
                        "it to finish, then refresh and review recovery again.");
                }

                token.ThrowIfCancellationRequested();
                // Cancellation linearizes before this synchronous archive
                // batch so it cannot deliberately split a multi-hint review.
                try
                {
                    foreach (LoadedTripoPanelRecoveryHint hint in
                             beforeArchive.Hints)
                    {
                        store.Archive(hint);
                    }
                }
                finally
                {
                    PublishRecovery(store.LoadStale());
                }
            },
            cancellationToken);
    }

    private static async Task<IReadOnlyList<
        TripoPanelRecoveryOperationInspection>>
        InspectRecoveryOperationsAsync(
            TripoPanelRecoveryLoadResult recovery,
            Tripo.Bridge.IHostControlClient client,
            CancellationToken cancellationToken)
    {
        string[] operationIds = recovery.Hints
            .SelectMany(loaded => new[]
            {
                loaded.Hint.Generation?.OperationId,
                loaded.Hint.Conversion?.OperationId,
            })
            .Where(operationId => operationId is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .OrderBy(operationId => operationId, StringComparer.Ordinal)
            .ToArray();
        List<TripoPanelRecoveryOperationInspection> inspections = [];
        foreach (string operationId in operationIds)
        {
            try
            {
                Tripo.Bridge.HostControlOperationStatusReceipt receipt =
                    await client.GetOperationStatusAsync(
                            operationId,
                            cancellationToken)
                        .ConfigureAwait(false);
                if (!string.Equals(
                        receipt.OperationId,
                        operationId,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "The local operation-status receipt returned a " +
                        "different operation ID.");
                }

                inspections.Add(
                    new TripoPanelRecoveryOperationInspection(
                        operationId,
                        receipt,
                        ErrorCode: null,
                        ErrorMessage: null));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ObjectDisposedException)
            {
                throw;
            }
            catch (Exception exception)
            {
                inspections.Add(
                    new TripoPanelRecoveryOperationInspection(
                        operationId,
                        Receipt: null,
                        ErrorCode: exception switch
                        {
                            Tripo.Bridge.HostControlCallException call =>
                                call.Code,
                            InvalidDataException =>
                                "invalid_operation_status",
                            _ => exception.GetType().Name,
                        },
                        ErrorMessage: BoundError(exception.Message)));
            }
        }

        return inspections;
    }

    private static void EnsureRecoveryTokenUnchanged(
        TripoPanelRecoveryLoadResult expected,
        TripoPanelRecoveryLoadResult current)
    {
        if (!string.Equals(
                expected.PresentationToken,
                current.PresentationToken,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The recovered operation list changed. Review the refreshed " +
                "list before unlocking again.");
        }
    }

    private void PublishRecovery(TripoPanelRecoveryLoadResult recovery)
    {
        lock (_stateGate)
        {
            _recovery = recovery;
            _recoveryRevision++;
        }

        RecoveryChanged?.Invoke(this, recovery);
    }

    public TripoPanelRecoveryLoadResult RefreshRecovery()
    {
        ThrowIfDisposed();
        long observedRevision;
        lock (_stateGate)
        {
            observedRevision = _recoveryRevision;
        }

        TripoPanelRecoveryLoadResult next =
            _recoveryStore?.LoadStale() ??
            TripoPanelRecoveryLoadResult.Empty;
        lock (_stateGate)
        {
            if (_recoveryRevision != observedRevision)
            {
                return _recovery;
            }

            _recovery = next;
            _recoveryRevision++;
        }

        RecoveryChanged?.Invoke(this, next);
        return next;
    }

    public ValueTask DisposeAsync()
    {
        Task disposeTask;
        lock (_stateGate)
        {
            _disposeTask ??= DisposeCoreAsync();
            disposeTask = _disposeTask;
        }

        return new ValueTask(disposeTask);
    }

    private async Task DisposeCoreAsync()
    {
        _disposed = true;
        _lifetime.Cancel();
        await _operationGate.WaitAsync().ConfigureAwait(false);
        _operationGate.Release();
        _lifetime.Dispose();
        _operationGate.Dispose();
        _recoveryStore?.Dispose();
        StateChanged = null;
        RecoveryChanged = null;
    }

    private void EnsureNoStaleRecovery()
    {
        TripoPanelRecoveryLoadResult recovery =
            _recoveryStore is null
                ? Recovery
                : RefreshRecovery();

        if (recovery.HasBlock)
        {
            throw new InvalidOperationException(
                "Recovered operation IDs require reconciliation before a new " +
                "workflow or credential change can proceed.");
        }
    }

    private static void EnsurePaidDispatchAllowed(
        bool canDispatch,
        bool retryRequired,
        Tripo.Bridge.HostControlOperationStatusReceipt? operationStatus,
        string stage)
    {
        if (!canDispatch)
        {
            throw new InvalidOperationException(
                $"The prepared {stage} operation already has a durable task ID.");
        }

        if (retryRequired && operationStatus?.CanResumeCreation != true)
        {
            throw new InvalidOperationException(
                $"Refresh the {stage} operation status before retrying the " +
                "same UUID. A retry is enabled only when the journal reports " +
                "that creation can resume.");
        }
    }

    private void EnsureCredentialMutationAllowed(
        bool allowCredentialRecovery)
    {
        TripoPanelState state = State;
        if (state.HasCredentialBoundWorkflow &&
            !allowCredentialRecovery)
        {
            throw new InvalidOperationException(
                "The API key cannot change or be cleared while an " +
                "account-bound workflow remains active. Refresh it to a " +
                "terminal status or reconcile it, then explicitly reset first.");
        }

        TripoPanelRecoveryLoadResult global =
            _recoveryStore?.LoadCredentialMutationBlocks(
                excludeCurrentStoreHint: true) ??
            TripoPanelRecoveryLoadResult.Empty;
        if (global.HasBlock)
        {
            throw new InvalidOperationException(
                "The API key cannot change while another Rhino/Revit panel has " +
                "an unresolved paid recovery hint, an unverifiable live owner, " +
                "or invalid recovery storage. Reconcile it in the owning host " +
                "first.");
        }
    }

    private async Task RunExclusiveAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken callerCancellationToken)
    {
        ThrowIfDisposed();
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(
                callerCancellationToken,
                _lifetime.Token);
        await _operationGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            UpdateState(state => state with
            {
                Busy = true,
                LastError = null,
                LastErrorCode = null,
            });
            await operation(linked.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
            when (!_disposed &&
                  (exception is not OperationCanceledException ||
                   !callerCancellationToken.IsCancellationRequested))
        {
            UpdateTransientState(state => state with
            {
                LastError = BoundError(exception.Message),
                LastErrorCode = BoundErrorCode(exception),
            });
            throw;
        }
        finally
        {
            try
            {
                if (!_disposed)
                {
                    UpdateTransientState(
                        state => state with { Busy = false });
                }
            }
            finally
            {
                _operationGate.Release();
            }
        }
    }

    private void EnterSynchronousMutation()
    {
        ThrowIfDisposed();
        if (!_operationGate.Wait(0))
        {
            throw new InvalidOperationException(
                "Wait for the current panel operation to finish before " +
                "changing workflow state.");
        }

        try
        {
            ThrowIfDisposed();
        }
        catch
        {
            _operationGate.Release();
            throw;
        }
    }

    private async Task EnsureSessionStillActiveAsync(
        Tripo.Bridge.IHostControlClient client,
        string expectedSessionId,
        string? requiredHostCapability,
        string? requiredSidecarCapability,
        CancellationToken cancellationToken)
    {
        Tripo.Bridge.HostControlHealthReceipt? health = null;
        if (!string.IsNullOrWhiteSpace(requiredSidecarCapability))
        {
            health = await client.GetHealthAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        Tripo.Bridge.HostContextReceipt current =
            await client.GetHostContextAsync(cancellationToken)
                .ConfigureAwait(false);
        if (health is not null)
        {
            EnsureHealthMatchesContext(health, current);
        }

        Tripo.Bridge.HostContextReceipt original = RequireContext();
        if (current.ProcessId != original.ProcessId ||
            !string.Equals(
                current.Host,
                original.Host,
                StringComparison.Ordinal) ||
            !string.Equals(
                current.DocumentSessionId,
                expectedSessionId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The active host document changed. The prepared operation was " +
                "not dispatched; refresh the panel and prepare a new operation.");
        }

        if (!string.IsNullOrWhiteSpace(requiredHostCapability) &&
            !current.Capabilities.Contains(
                requiredHostCapability,
                StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "The active host no longer advertises the required " +
                $"'{requiredHostCapability}' capability. The prepared operation " +
                "was not dispatched.");
        }

        if (!string.IsNullOrWhiteSpace(requiredSidecarCapability) &&
            (health is null ||
             !health.Capabilities.Contains(
                 requiredSidecarCapability,
                 StringComparer.Ordinal)))
        {
            throw new InvalidOperationException(
                "The active sidecar no longer advertises the required " +
                $"'{requiredSidecarCapability}' capability. The prepared " +
                "operation was not dispatched.");
        }
    }

    private async Task<string> ResolveGenerationTaskIdAsync(
        Tripo.Bridge.IHostControlClient client,
        CancellationToken cancellationToken)
    {
        TripoPanelState state = State;
        if (state.GenerationReceipt is not null)
        {
            return state.GenerationReceipt.TaskId;
        }

        PreparedTextGeneration prepared =
            state.PreparedGeneration ??
            throw new InvalidOperationException(
                "No generation operation is available to refresh.");
        if (!state.GenerationDispatchAttempted)
        {
            throw new InvalidOperationException(
                "Dispatch the prepared generation before refreshing it.");
        }

        Tripo.Bridge.HostControlOperationStatusReceipt operationStatus =
            await client.GetOperationStatusAsync(
                    prepared.OperationId,
                    cancellationToken)
                .ConfigureAwait(false);
        EnsurePaidOperationStatusIdentity(
            operationStatus,
            prepared.OperationId,
            "text_task_creation");
        if (TripoPanelState.IsDefinitiveRequestRejection(operationStatus))
        {
            ClearRejectedGenerationStage();
            throw new InvalidOperationException(
                "The generation request was definitively rejected. Correct the " +
                "API credential and prepare a new generation operation.");
        }

        UpdateState(current => current with
        {
            GenerationOperationStatus = operationStatus,
        });
        return RequireDurableTaskId(operationStatus, "generation");
    }

    private static async Task<
        Tripo.Bridge.HostControlOperationStatusReceipt?>
        TryGetDefinitiveRequestRejectionAsync(
            Tripo.Bridge.IHostControlClient client,
            string operationId,
            string expectedKind,
            CancellationToken cancellationToken)
    {
        try
        {
            Tripo.Bridge.HostControlOperationStatusReceipt status =
                await client.GetOperationStatusAsync(
                        operationId,
                        cancellationToken)
                    .ConfigureAwait(false);
            return IsMatchingPaidOperationStatus(
                       status,
                       operationId,
                       expectedKind) &&
                   TripoPanelState.IsDefinitiveRequestRejection(status)
                ? status
                : null;
        }
        catch (Tripo.Bridge.HostControlCallException)
        {
            return null;
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    private async Task<string> ResolveConversionTaskIdAsync(
        Tripo.Bridge.IHostControlClient client,
        CancellationToken cancellationToken)
    {
        TripoPanelState state = State;
        if (state.ConversionReceipt is not null)
        {
            return state.ConversionReceipt.ConversionTaskId;
        }

        PreparedObjConversion prepared =
            state.PreparedConversion ??
            throw new InvalidOperationException(
                "No conversion operation is available to refresh.");
        if (!state.ConversionDispatchAttempted)
        {
            throw new InvalidOperationException(
                "Dispatch the prepared conversion before refreshing it.");
        }

        Tripo.Bridge.HostControlOperationStatusReceipt operationStatus =
            await client.GetOperationStatusAsync(
                    prepared.OperationId,
                    cancellationToken)
                .ConfigureAwait(false);
        EnsurePaidOperationStatusIdentity(
            operationStatus,
            prepared.OperationId,
            "obj_conversion_creation");
        if (TripoPanelState.IsDefinitiveRequestRejection(operationStatus))
        {
            ClearRejectedConversionStage();
            throw new InvalidOperationException(
                "The conversion request was definitively rejected. Correct the " +
                "API credential and prepare a new conversion operation.");
        }

        UpdateState(current => current with
        {
            ConversionOperationStatus = operationStatus,
        });
        return RequireDurableTaskId(operationStatus, "conversion");
    }

    private void ClearRejectedGenerationStage()
    {
        UpdateState(state => state with
        {
            PreparedGeneration = null,
            GenerationDispatchAttempted = false,
            GenerationReceipt = null,
            GenerationOperationStatus = null,
            GenerationStatus = null,
            PreparedConversion = null,
            ConversionDispatchAttempted = false,
            ConversionReceipt = null,
            ConversionOperationStatus = null,
            ConversionStatus = null,
            PreparedImport = null,
            ImportDispatchAttempted = false,
            ImportReceipt = null,
            ImportFailureCode = null,
        });
    }

    private void ClearRejectedConversionStage()
    {
        UpdateState(state => state with
        {
            PreparedConversion = null,
            ConversionDispatchAttempted = false,
            ConversionReceipt = null,
            ConversionOperationStatus = null,
            ConversionStatus = null,
            PreparedImport = null,
            ImportDispatchAttempted = false,
            ImportReceipt = null,
            ImportFailureCode = null,
        });
    }

    private static void EnsurePaidOperationStatusIdentity(
        Tripo.Bridge.HostControlOperationStatusReceipt status,
        string operationId,
        string expectedKind)
    {
        if (!IsMatchingPaidOperationStatus(
                status,
                operationId,
                expectedKind))
        {
            throw new InvalidDataException(
                "The local operation-status receipt did not match the requested " +
                "operation identity and kind.");
        }
    }

    private static bool IsMatchingPaidOperationStatus(
        Tripo.Bridge.HostControlOperationStatusReceipt status,
        string operationId,
        string expectedKind) =>
        string.Equals(
            status.OperationId,
            operationId,
            StringComparison.Ordinal) &&
        string.Equals(
            status.Kind,
            expectedKind,
            StringComparison.Ordinal);

    private static string RequireDurableTaskId(
        Tripo.Bridge.HostControlOperationStatusReceipt status,
        string stage)
    {
        if (status.TaskIdDurable &&
            !string.IsNullOrWhiteSpace(status.CreatedTaskId))
        {
            return status.CreatedTaskId;
        }

        throw new InvalidOperationException(
            $"The {stage} operation is {status.State}; its task ID is not " +
            $"durable. {status.NextAction}");
    }

    private static bool IsSameDocumentSession(
        Tripo.Bridge.HostContextReceipt first,
        Tripo.Bridge.HostContextReceipt second) =>
        first.ProcessId == second.ProcessId &&
        string.Equals(first.Host, second.Host, StringComparison.Ordinal) &&
        string.Equals(
            first.DocumentSessionId,
            second.DocumentSessionId,
            StringComparison.Ordinal);

    private static void EnsureHealthMatchesContext(
        Tripo.Bridge.HostControlHealthReceipt health,
        Tripo.Bridge.HostContextReceipt context)
    {
        if (health.HostProcessId != context.ProcessId ||
            !string.Equals(
                health.Host,
                context.Host,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The sidecar is connected to a different host process.");
        }
    }

    private Tripo.Bridge.IHostControlClient RequireClient() =>
        _client ??
        throw new InvalidOperationException(
            "Connect the panel to its sidecar before continuing.");

    private Tripo.Bridge.HostContextReceipt RequireContext() =>
        State.Context ??
        throw new InvalidOperationException(
            "Refresh the active host document before continuing.");

    private static void EnsureSuccessfulTask(
        Tripo.Bridge.HostControlTaskStatusReceipt? status,
        string stage)
    {
        if (!string.Equals(status?.Status, "success", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The {stage} task must report success before continuing.");
        }
    }

    private static void ValidatePrompt(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 1024)
        {
            throw new ArgumentException(
                "The prompt must contain 1 to 1024 characters.",
                nameof(prompt));
        }
    }

    private static void ValidateFaceLimit(int faceLimit)
    {
        if (faceLimit is < 500 or > 200_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(faceLimit),
                "faceLimit must be between 500 and 200000.");
        }
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128)
        {
            throw new ArgumentException(
                "The imported object name must contain 1 to 128 characters.",
                nameof(name));
        }
    }

    private static string ValidateImportMode(string importMode)
    {
        string normalized = (importMode ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized is not "native" and
            not "mesh" and
            not "instance" and
            not "family" and
            not "glb_instance")
        {
            throw new ArgumentException(
                "Import mode must be native, mesh, instance, family, or " +
                "glb_instance.",
                nameof(importMode));
        }

        return normalized;
    }

    private void UpdateState(Func<TripoPanelState, TripoPanelState> update)
    {
        TripoPanelState next = PersistAndAssignState(update);
        StateChanged?.Invoke(this, next);
    }

    private void UpdateTransientState(
        Func<TripoPanelState, TripoPanelState> update)
    {
        TripoPanelState next;
        try
        {
            next = PersistAndAssignState(update);
        }
        catch (Exception exception)
            when (IsRecoveryPersistenceFailure(exception))
        {
            lock (_stateGate)
            {
                next = update(_state);
                _state = next;
            }
        }

        StateChanged?.Invoke(this, next);
    }

    private TripoPanelState PersistAndAssignState(
        Func<TripoPanelState, TripoPanelState> update)
    {
        lock (_stateGate)
        {
            TripoPanelState next = update(_state);
            _recoveryStore?.Save(next);
            _state = next;
            return next;
        }
    }

    private static bool IsRecoveryPersistenceFailure(Exception exception) =>
        exception is IOException or
        UnauthorizedAccessException or
        InvalidDataException or
        InvalidOperationException or
        System.Text.Json.JsonException or
        NotSupportedException;

    private void ThrowIfDisposed()
    {
#pragma warning disable CA1513 // ObjectDisposedException.ThrowIf is unavailable on net7.0.
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(TripoPanelSession));
        }
#pragma warning restore CA1513
    }

    private static string BoundError(string? message)
    {
        const string fallback = "The operation failed.";
        if (string.IsNullOrWhiteSpace(message))
        {
            return fallback;
        }

        string trimmed = message.Trim();
        return trimmed.Length <= 512 ? trimmed : trimmed[..512];
    }

    private static string? BoundErrorCode(Exception exception)
    {
        if (exception is not Tripo.Bridge.HostControlCallException call ||
            string.IsNullOrWhiteSpace(call.Code))
        {
            return null;
        }

        string code = call.Code.Trim();
        return code.Length <= 128 ? code : code[..128];
    }
}
