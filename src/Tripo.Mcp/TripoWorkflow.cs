namespace Tripo.Mcp;

public sealed record TripoWorkflowOptions(
    TimeSpan ReadOperationDeadline,
    TimeSpan InitialReadRetryDelay,
    TimeSpan MaximumReadRetryDelay,
    int MaximumReadRetries)
{
    public static TripoWorkflowOptions Default { get; } = new(
        TimeSpan.FromMinutes(3),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(8),
        3);

    public void Validate()
    {
        if (ReadOperationDeadline <= TimeSpan.Zero ||
            InitialReadRetryDelay <= TimeSpan.Zero ||
            MaximumReadRetryDelay < InitialReadRetryDelay ||
            MaximumReadRetries < 0)
        {
            throw new ArgumentException("The workflow retry options are invalid.");
        }
    }
}

public sealed class TripoWorkflowException : Exception
{
    public TripoWorkflowException(string message)
        : base(message)
    {
    }

    public TripoWorkflowException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class TripoWorkflow : ITripoWorkflow
{
    private static readonly HashSet<string> PendingStates =
        new(StringComparer.Ordinal)
        {
            "queued",
            "running",
        };

    private static readonly HashSet<string> TerminalFailureStates =
        new(StringComparer.Ordinal)
        {
            "failed",
            "cancelled",
            "banned",
            "expired",
        };

    private readonly ITripoApiClient _apiClient;
    private readonly IArtifactStager _artifactStager;
    private readonly IHostConnection _hostConnection;
    private readonly IPaidOperationJournal _operationJournal;
    private readonly Tripo.Bridge.ICredentialWorkflowExecutionGate _executionGate;
    private readonly TripoWorkflowOptions _options;
    private readonly TimeProvider _timeProvider;

    public TripoWorkflow(
        ITripoApiClient apiClient,
        IArtifactStager artifactStager,
        IHostConnection hostConnection,
        TripoWorkflowOptions? options = null,
        TimeProvider? timeProvider = null)
        : this(
            apiClient,
            artifactStager,
            hostConnection,
            new PaidOperationJournal(),
            options,
            timeProvider,
            new Tripo.Bridge.CredentialWorkflowExecutionGate())
    {
    }

    internal TripoWorkflow(
        ITripoApiClient apiClient,
        IArtifactStager artifactStager,
        IHostConnection hostConnection,
        IPaidOperationJournal operationJournal,
        TripoWorkflowOptions? options = null,
        TimeProvider? timeProvider = null,
        Tripo.Bridge.ICredentialWorkflowExecutionGate? executionGate = null)
    {
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _artifactStager =
            artifactStager ?? throw new ArgumentNullException(nameof(artifactStager));
        _hostConnection =
            hostConnection ?? throw new ArgumentNullException(nameof(hostConnection));
        _operationJournal =
            operationJournal ?? throw new ArgumentNullException(nameof(operationJournal));
        _executionGate =
            executionGate ?? NoOpCredentialWorkflowExecutionGate.Instance;
        _options = options ?? TripoWorkflowOptions.Default;
        _options.Validate();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<Tripo.Bridge.HostContextReceipt> GetHostContextAsync(
        CancellationToken cancellationToken) =>
        _hostConnection.GetContextAsync(cancellationToken);

    public async Task<TaskStatusReceipt> GetTaskStatusAsync(
        string taskId,
        CancellationToken cancellationToken)
    {
        TripoV3Client.ValidateTaskId(taskId);
        TripoTaskSnapshot task = await GetTaskWithReadRetryAsync(
                taskId,
                cancellationToken)
            .ConfigureAwait(false);
        return ToStatusReceipt(task);
    }

    public Task<PaidOperationStatusReceipt> GetPaidOperationStatusAsync(
        string operationId,
        CancellationToken cancellationToken) =>
        _operationJournal.GetStatusAsync(operationId, cancellationToken);

    public Task<Tripo.Bridge.StagedImageTransfer> StageLocalImageAsync(
        string localImagePath,
        CancellationToken cancellationToken) =>
        Tripo.Bridge.ImageTransferStore.StageAsync(
            localImagePath,
            cancellationToken);

    public async Task<TextTaskCreationReceipt> CreateTextTaskAsync(
        string prompt,
        int faceLimit,
        bool withMaterials,
        string documentSessionId,
        string operationId,
        bool confirmExternalCost,
        CancellationToken cancellationToken,
        bool requireExistingOperation = false)
    {
        EnsureCostConfirmed(confirmExternalCost);
        TripoV3Client.ValidatePrompt(prompt);
        TripoV3Client.ValidateFaceLimit(faceLimit);
        ValidateDocumentSessionId(documentSessionId);
        string canonicalOperationId =
            PaidOperationDescriptor.CanonicalizeOperationId(operationId);
        using IDisposable executionLease = _executionGate.Acquire();
        string effectiveModel = _apiClient.ResolveEffectiveModel();
        TextGenerationOptions generationOptions =
            new(prompt, faceLimit, effectiveModel, withMaterials);
        string requestFingerprint =
            _apiClient.GetTextTaskOperationFingerprint(
                generationOptions,
                documentSessionId);
        PaidOperationDescriptor descriptor = PaidOperationDescriptor.ForTextTask(
            canonicalOperationId,
            documentSessionId,
            requestFingerprint);
        await using PaidOperationLease operation =
            await _operationJournal.AcquireAsync(
                    descriptor,
                    cancellationToken,
                    requireExistingOperation)
                .ConfigureAwait(false);
        if (operation.Status.TaskIdDurable)
        {
            return new TextTaskCreationReceipt(
                descriptor.OperationId,
                RequirePersistedTaskId(operation.Status),
                effectiveModel);
        }

        EnsureCreationCanDispatch(operation.Status);
        await EnsureDocumentSessionAsync(documentSessionId, cancellationToken)
            .ConfigureAwait(false);

        string taskId = await _apiClient.CreateTextModelAsync(
                generationOptions,
                documentSessionId,
                operation,
                cancellationToken)
            .ConfigureAwait(false);
        return new TextTaskCreationReceipt(
            descriptor.OperationId,
            taskId,
            effectiveModel);
    }

    public async Task<ImageTaskCreationReceipt> CreateImageTaskAsync(
        Tripo.Bridge.StagedImageTransfer image,
        int faceLimit,
        bool withMaterials,
        string documentSessionId,
        string operationId,
        bool confirmExternalCost,
        CancellationToken cancellationToken,
        bool requireExistingOperation = false)
    {
        EnsureCostConfirmed(confirmExternalCost);
        Tripo.Bridge.ImageTransferStore.ValidateDescriptor(image);
        TripoV3Client.ValidateFaceLimit(faceLimit);
        ValidateDocumentSessionId(documentSessionId);
        string canonicalOperationId =
            PaidOperationDescriptor.CanonicalizeOperationId(operationId);
        using IDisposable executionLease = _executionGate.Acquire();
        string effectiveModel = _apiClient.ResolveEffectiveModel();
        ImageGenerationOptions generationOptions =
            new(image, faceLimit, effectiveModel, withMaterials);
        string requestFingerprint =
            _apiClient.GetImageTaskOperationFingerprint(
                generationOptions,
                documentSessionId);
        PaidOperationDescriptor descriptor =
            PaidOperationDescriptor.ForImageTask(
                canonicalOperationId,
                documentSessionId,
                requestFingerprint,
                image);
        await using PaidOperationLease operation =
            await _operationJournal.AcquireAsync(
                    descriptor,
                    cancellationToken,
                    requireExistingOperation)
                .ConfigureAwait(false);
        if (operation.Status.TaskIdDurable)
        {
            return new ImageTaskCreationReceipt(
                descriptor.OperationId,
                RequirePersistedTaskId(operation.Status),
                effectiveModel,
                image.Sha256);
        }

        EnsureCreationCanDispatch(operation.Status);
        await EnsureDocumentSessionAsync(documentSessionId, cancellationToken)
            .ConfigureAwait(false);

        DocumentCheckingImageCheckpoint checkpoint = new(
            operation,
            token => EnsureDocumentSessionAsync(documentSessionId, token));
        string taskId = await _apiClient.CreateImageModelAsync(
                generationOptions,
                documentSessionId,
                checkpoint,
                cancellationToken)
            .ConfigureAwait(false);
        return new ImageTaskCreationReceipt(
            descriptor.OperationId,
            taskId,
            effectiveModel,
            image.Sha256);
    }

    public async Task<ObjConversionCreationReceipt> CreateObjConversionAsync(
        string sourceTaskId,
        int faceLimit,
        bool withMaterials,
        string documentSessionId,
        string operationId,
        bool confirmExternalCost,
        CancellationToken cancellationToken,
        bool requireExistingOperation = false)
    {
        EnsureCostConfirmed(confirmExternalCost);
        TripoV3Client.ValidateTaskId(sourceTaskId);
        TripoV3Client.ValidateFaceLimit(faceLimit);
        ValidateDocumentSessionId(documentSessionId);
        string canonicalOperationId =
            PaidOperationDescriptor.CanonicalizeOperationId(operationId);
        using IDisposable executionLease = _executionGate.Acquire();
        string requestFingerprint =
            _apiClient.GetObjConversionOperationFingerprint(
                sourceTaskId,
                faceLimit,
                withMaterials,
                documentSessionId);
        PaidOperationDescriptor descriptor = PaidOperationDescriptor.ForObjConversion(
            canonicalOperationId,
            sourceTaskId,
            documentSessionId,
            requestFingerprint);
        await using PaidOperationLease operation =
            await _operationJournal.AcquireAsync(
                    descriptor,
                    cancellationToken,
                    requireExistingOperation)
                .ConfigureAwait(false);
        if (operation.Status.TaskIdDurable)
        {
            return new ObjConversionCreationReceipt(
                descriptor.OperationId,
                sourceTaskId,
                RequirePersistedTaskId(operation.Status),
                "OBJ");
        }

        EnsureCreationCanDispatch(operation.Status);
        await EnsureDocumentSessionAsync(documentSessionId, cancellationToken)
            .ConfigureAwait(false);

        TripoTaskSnapshot sourceTask = await GetTaskWithReadRetryAsync(
                sourceTaskId,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccessfulTask(sourceTask);
        EnsureGenerationTask(sourceTask);

        await EnsureDocumentSessionAsync(documentSessionId, cancellationToken)
            .ConfigureAwait(false);
        string conversionTaskId = await _apiClient.CreateObjConversionAsync(
                sourceTaskId,
                faceLimit,
                withMaterials,
                documentSessionId,
                operation,
                cancellationToken)
            .ConfigureAwait(false);
        return new ObjConversionCreationReceipt(
            descriptor.OperationId,
            sourceTaskId,
            conversionTaskId,
            "OBJ");
    }

    public async Task<ObjTaskImportReceipt> ImportObjTaskAsync(
        string conversionTaskId,
        string name,
        string documentSessionId,
        string operationId,
        string importMode,
        bool applyMaterials,
        CancellationToken cancellationToken)
    {
        TripoV3Client.ValidateTaskId(conversionTaskId);
        string canonicalOperationId = ValidateImportArguments(
            name,
            documentSessionId,
            operationId);
        string requestedMode = NormalizeImportMode(importMode);
        Tripo.Bridge.HostContextReceipt context =
            await RequireActiveDocumentAsync(documentSessionId, cancellationToken)
                .ConfigureAwait(false);
        string resolvedMode = ResolveImportMode(requestedMode, context.Host);

        (TripoTaskSnapshot conversionTask, Tripo.Bridge.StagedBundle bundle) =
            await StageSuccessfulObjTaskAsync(
                    conversionTaskId,
                    cancellationToken)
                .ConfigureAwait(false);
        if (applyMaterials && bundle.MtlEntry is null)
        {
            throw new TripoWorkflowException(
                "applyMaterials was requested but the converted bundle contains no " +
                "MTL material library. Retry the conversion with withMaterials enabled.");
        }

        await EnsureDocumentSessionAsync(documentSessionId, cancellationToken)
            .ConfigureAwait(false);
        Tripo.Bridge.ImportMeshRequest importRequest = new(
            documentSessionId,
            bundle.BundleId,
            bundle.ObjEntry,
            bundle.MtlEntry,
            bundle.Entries,
            SourceUnit: "meters",
            UpAxis: "Y",
            Handedness: "right",
            name,
            canonicalOperationId,
            resolvedMode,
            applyMaterials);
        Tripo.Bridge.HostImportReceipt hostReceipt =
            await _hostConnection.ImportMeshAsync(importRequest, cancellationToken)
                .ConfigureAwait(false);
        return new ObjTaskImportReceipt(
            canonicalOperationId,
            conversionTaskId,
            conversionTask.CreditsConsumed,
            hostReceipt);
    }

    public async Task<ObjTaskStageReceipt> StageObjTaskAsync(
        string conversionTaskId,
        string documentSessionId,
        bool includeMaterials,
        CancellationToken cancellationToken)
    {
        TripoV3Client.ValidateTaskId(conversionTaskId);
        ValidateDocumentSessionId(documentSessionId);
        await EnsureDocumentSessionAsync(documentSessionId, cancellationToken)
            .ConfigureAwait(false);
        (TripoTaskSnapshot conversionTask, Tripo.Bridge.StagedBundle bundle) =
            await StageSuccessfulObjTaskAsync(
                    conversionTaskId,
                    cancellationToken)
                .ConfigureAwait(false);
        if (includeMaterials && bundle.MtlEntry is null)
        {
            throw new TripoWorkflowException(
                "includeMaterials was requested but the converted bundle contains " +
                "no MTL material library. Retry conversion with withMaterials enabled.");
        }

        await EnsureDocumentSessionAsync(documentSessionId, cancellationToken)
            .ConfigureAwait(false);
        Tripo.Bridge.StagedMeshLoadRequest mesh = new(
            bundle.BundleId,
            bundle.ObjEntry,
            bundle.MtlEntry,
            bundle.Entries,
            SourceUnit: "meters",
            UpAxis: "Y",
            Handedness: "right",
            ApplyMaterials: includeMaterials);
        return new ObjTaskStageReceipt(
            conversionTaskId,
            conversionTask.CreditsConsumed,
            mesh);
    }

    private async Task<(
        TripoTaskSnapshot Task,
        Tripo.Bridge.StagedBundle Bundle)> StageSuccessfulObjTaskAsync(
        string conversionTaskId,
        CancellationToken cancellationToken)
    {
        TripoTaskSnapshot conversionTask = await GetTaskWithReadRetryAsync(
                conversionTaskId,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccessfulTask(conversionTask);
        EnsureConversionTask(conversionTask);
        string? modelUrl = conversionTask.Output?.ModelUrl;
        if (!Uri.TryCreate(modelUrl, UriKind.Absolute, out Uri? modelUri))
        {
            throw new TripoWorkflowException(
                "The successful OBJ conversion did not return a valid model URL.");
        }

        Tripo.Bridge.StagedBundle bundle = await _artifactStager.StageBundleAsync(
                modelUri,
                cancellationToken)
            .ConfigureAwait(false);
        return (conversionTask, bundle);
    }

    private async Task EnsureDocumentSessionAsync(
        string documentSessionId,
        CancellationToken cancellationToken) =>
        await RequireActiveDocumentAsync(documentSessionId, cancellationToken)
            .ConfigureAwait(false);

    private async Task<Tripo.Bridge.HostContextReceipt> RequireActiveDocumentAsync(
        string documentSessionId,
        CancellationToken cancellationToken)
    {
        Tripo.Bridge.HostContextReceipt context =
            await _hostConnection.GetContextAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(
                context.DocumentSessionId,
                documentSessionId,
                StringComparison.Ordinal))
        {
            throw new TripoWorkflowException(
                "The requested document session is not the host's active document.");
        }

        return context;
    }

    private static string NormalizeImportMode(string importMode)
    {
        string normalized = (importMode ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized is not "native" and
            not "mesh" and
            not "instance" and
            not "family")
        {
            throw new ArgumentException(
                "importMode must be native, mesh, instance, or family.",
                nameof(importMode));
        }

        return normalized;
    }

    private static string ResolveImportMode(string requestedMode, string hostName)
    {
        if (requestedMode != "native")
        {
            return requestedMode;
        }

        return hostName.Trim().ToLowerInvariant() switch
        {
            "rhino" => "instance",
            "revit" => "family",
            _ => throw new TripoWorkflowException(
                $"The active host {RemoteText.Bound(hostName, 64, "unknown")} does " +
                "not define a native import mode."),
        };
    }

    private async Task<TripoTaskSnapshot> GetTaskWithReadRetryAsync(
        string taskId,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource readDeadline =
            new(_options.ReadOperationDeadline, _timeProvider);
        using CancellationTokenSource readCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                readDeadline.Token);
        CancellationToken readToken = readCancellation.Token;
        TimeSpan delay = _options.InitialReadRetryDelay;
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    return await _apiClient
                        .GetTaskAsync(taskId, readToken)
                        .WaitAsync(readToken)
                        .ConfigureAwait(false);
                }
                catch (TripoApiException exception)
                    when (exception.IsRetryableReadFailure &&
                          attempt < _options.MaximumReadRetries)
                {
                    TimeSpan retryDelay = exception.RetryAfter ?? delay;
                    if (retryDelay > _options.MaximumReadRetryDelay)
                    {
                        retryDelay = _options.MaximumReadRetryDelay;
                    }

                    await Task.Delay(retryDelay, _timeProvider, readToken)
                        .ConfigureAwait(false);
                    delay = TimeSpan.FromMilliseconds(
                        Math.Min(
                            delay.TotalMilliseconds * 2,
                            _options.MaximumReadRetryDelay.TotalMilliseconds));
                }
            }
        }
        catch (OperationCanceledException exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (readDeadline.IsCancellationRequested)
            {
                throw new TripoWorkflowException(
                    $"The local query for Tripo task {taskId} reached its deadline. " +
                    "The remote task was not cancelled.",
                    exception);
            }

            throw;
        }
    }

    private static void EnsureSuccessfulTask(TripoTaskSnapshot task)
    {
        string status = NormalizeState(task);
        if (status == "success")
        {
            return;
        }

        if (TerminalFailureStates.Contains(status))
        {
            string suffix = string.IsNullOrWhiteSpace(task.ErrorMessage)
                ? string.Empty
                : $": {RemoteText.Bound(task.ErrorMessage, 512)}";
            throw new TripoWorkflowException(
                $"Tripo task {task.TaskId} ended with status {status}{suffix}.");
        }

        if (PendingStates.Contains(status))
        {
            throw new TripoWorkflowException(
                $"Tripo task {task.TaskId} is still {status}. " +
                "Poll tripo_task_status before starting the next stage.");
        }

        throw new TripoWorkflowException(
            $"Tripo task {task.TaskId} returned unsupported status {status}.");
    }

    private static string NormalizeState(TripoTaskSnapshot task)
    {
        if (string.IsNullOrWhiteSpace(task.Status))
        {
            throw new TripoWorkflowException(
                $"Tripo task {task.TaskId} returned an empty status.");
        }

        return RemoteText.Bound(task.Status.Trim(), 64).ToLowerInvariant();
    }

    private static TaskStatusReceipt ToStatusReceipt(TripoTaskSnapshot task) =>
        new(
            task.TaskId,
            RemoteText.Bound(task.Type, 128, "unknown"),
            NormalizeState(task),
            task.Progress,
            task.CreditsConsumed,
            task.CreatedAt,
            task.CompletedAt,
            task.ErrorCode,
            string.IsNullOrWhiteSpace(task.ErrorMessage)
                ? null
                : RemoteText.Bound(task.ErrorMessage, 512));

    private static void EnsureCostConfirmed(bool confirmExternalCost)
    {
        if (!confirmExternalCost)
        {
            throw new TripoWorkflowException(
                "This operation can create a billable Tripo task. " +
                "Set confirmExternalCost to true only after user confirmation.");
        }
    }

    private static void EnsureCreationCanDispatch(
        PaidOperationStatusReceipt status)
    {
        if (status.CanResumeCreation)
        {
            return;
        }

        throw new TripoWorkflowException(
            $"Paid operation {status.OperationId} is {status.State}. " +
            $"{status.NextAction} Query tripo_operation_status for the durable local record.");
    }

    private static string RequirePersistedTaskId(
        PaidOperationStatusReceipt status)
    {
        if (!status.TaskIdDurable ||
            string.IsNullOrWhiteSpace(status.CreatedTaskId))
        {
            throw new TripoWorkflowException(
                $"Paid operation {status.OperationId} does not have a durable task ID.");
        }

        TripoV3Client.ValidateTaskId(status.CreatedTaskId);
        return status.CreatedTaskId;
    }

    private static void ValidateDocumentSessionId(string documentSessionId)
    {
        if (!Guid.TryParseExact(documentSessionId, "D", out Guid parsed) ||
            !string.Equals(
                parsed.ToString("D"),
                documentSessionId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "documentSessionId must be the exact UUID returned by tripo_host_context.",
                nameof(documentSessionId));
        }
    }

    private static string ValidateImportArguments(
        string name,
        string documentSessionId,
        string operationId)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128)
        {
            throw new ArgumentException(
                "The object name must contain 1 to 128 characters.",
                nameof(name));
        }

        ValidateDocumentSessionId(documentSessionId);
        if (!Guid.TryParseExact(operationId, "D", out Guid parsed))
        {
            throw new ArgumentException(
                "operationId must be a caller-generated UUID reused across import retries.",
                nameof(operationId));
        }

        return parsed.ToString("D");
    }

    private static void EnsureGenerationTask(TripoTaskSnapshot task)
    {
        string normalizedType = task.Type?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalizedType is not "text_to_model" and
            not "image_to_model" and
            not "multiview_to_model")
        {
            throw new TripoWorkflowException(
                $"Tripo task {task.TaskId} is not a supported generation task.");
        }
    }

    private static void EnsureConversionTask(TripoTaskSnapshot task)
    {
        string normalizedType = task.Type?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalizedType != "convert_model")
        {
            throw new TripoWorkflowException(
                $"Tripo task {task.TaskId} is not a supported OBJ conversion task.");
        }
    }

    private sealed class DocumentCheckingImageCheckpoint :
        IImageTaskCreationCheckpoint
    {
        private readonly IImageTaskCreationCheckpoint _inner;
        private readonly Func<CancellationToken, Task> _ensureDocumentSession;

        public DocumentCheckingImageCheckpoint(
            IImageTaskCreationCheckpoint inner,
            Func<CancellationToken, Task> ensureDocumentSession)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _ensureDocumentSession =
                ensureDocumentSession ??
                throw new ArgumentNullException(nameof(ensureDocumentSession));
        }

        public string RequestFingerprint => _inner.RequestFingerprint;

        public string? FileToken => _inner.FileToken;

        public string? GenerationRequestFingerprint =>
            _inner.GenerationRequestFingerprint;

        public Task BeforeSendAsync(CancellationToken cancellationToken) =>
            _inner.BeforeSendAsync(cancellationToken);

        public Task TaskIdReceivedAsync(string taskId) =>
            _inner.TaskIdReceivedAsync(taskId);

        public Task OutcomeUnknownAsync(string code, string message) =>
            _inner.OutcomeUnknownAsync(code, message);

        public async Task BeforeImageUploadAsync(
            CancellationToken cancellationToken)
        {
            await _ensureDocumentSession(cancellationToken)
                .ConfigureAwait(false);
            await _inner.BeforeImageUploadAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        public Task ImageFileTokenReceivedAsync(
            string fileToken,
            string generationRequestFingerprint) =>
            _inner.ImageFileTokenReceivedAsync(
                fileToken,
                generationRequestFingerprint);

        public async Task BeforeImageGenerationAsync(
            CancellationToken cancellationToken)
        {
            await _ensureDocumentSession(cancellationToken)
                .ConfigureAwait(false);
            await _inner.BeforeImageGenerationAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        public Task ImageOutcomeUnknownAsync(
            string stage,
            string code,
            string message) =>
            _inner.ImageOutcomeUnknownAsync(stage, code, message);
    }
}
