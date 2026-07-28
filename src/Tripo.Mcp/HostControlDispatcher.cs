using System.Text.Json;

namespace Tripo.Mcp;

internal sealed class HostControlDispatcher :
    Tripo.Bridge.IHostControlDispatcher
{
    private readonly string _host;
    private readonly int _hostProcessId;
    private readonly IReadOnlyList<string> _capabilities;
    private readonly ITripoCredentialService _credentials;
    private readonly ITripoWorkflow _workflow;
    private readonly Action _requestShutdown;

    public HostControlDispatcher(
        string host,
        int hostProcessId,
        IReadOnlyList<string> capabilities,
        ITripoCredentialService credentials,
        ITripoWorkflow workflow,
        Action requestShutdown)
    {
        _host = Tripo.Bridge.BridgePaths.NormalizeHost(host);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(hostProcessId);
        _hostProcessId = hostProcessId;
        _capabilities = capabilities ??
            throw new ArgumentNullException(nameof(capabilities));
        _credentials = credentials ??
            throw new ArgumentNullException(nameof(credentials));
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        _requestShutdown = requestShutdown ??
            throw new ArgumentNullException(nameof(requestShutdown));
    }

    public async Task<object> DispatchAsync(
        string method,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        try
        {
            return method switch
            {
                Tripo.Bridge.HostControlConstants.HealthMethod =>
                    CreateHealthReceipt(),
                Tripo.Bridge.HostControlConstants.ShutdownMethod =>
                    RequestShutdown(),
                Tripo.Bridge.HostControlConstants.CredentialStatusMethod =>
                    _credentials.GetStatus(),
                Tripo.Bridge.HostControlConstants.CredentialSetMethod =>
                    SetCredential(payload),
                Tripo.Bridge.HostControlConstants.CredentialClearMethod =>
                    _credentials.ClearApiKey(),
                Tripo.Bridge.HostControlConstants.HostContextMethod =>
                    await _workflow.GetHostContextAsync(cancellationToken)
                        .ConfigureAwait(false),
                Tripo.Bridge.HostControlConstants.CreateTextTaskMethod =>
                    await CreateTextTaskAsync(payload, cancellationToken)
                        .ConfigureAwait(false),
                Tripo.Bridge.HostControlConstants.CreateImageTaskMethod =>
                    await CreateImageTaskAsync(payload, cancellationToken)
                        .ConfigureAwait(false),
                Tripo.Bridge.HostControlConstants.TaskStatusMethod =>
                    await GetTaskStatusAsync(payload, cancellationToken)
                        .ConfigureAwait(false),
                Tripo.Bridge.HostControlConstants.OperationStatusMethod =>
                    await GetOperationStatusAsync(payload, cancellationToken)
                        .ConfigureAwait(false),
                Tripo.Bridge.HostControlConstants.CreateObjConversionMethod =>
                    await CreateObjConversionAsync(payload, cancellationToken)
                        .ConfigureAwait(false),
                Tripo.Bridge.HostControlConstants.ImportGenerationGlbMethod =>
                    await ImportGenerationGlbAsync(payload, cancellationToken)
                        .ConfigureAwait(false),
                Tripo.Bridge.HostControlConstants.ImportObjTaskMethod =>
                    await ImportObjTaskAsync(payload, cancellationToken)
                        .ConfigureAwait(false),
                Tripo.Bridge.HostControlConstants.StageObjTaskMethod =>
                    await StageObjTaskAsync(payload, cancellationToken)
                        .ConfigureAwait(false),
                _ => throw new Tripo.Bridge.HostControlCallException(
                    "method_not_allowed",
                    "The requested host-control method is not allowed."),
            };
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Tripo.Bridge.HostControlCallException)
        {
            throw;
        }
        catch (ArgumentException exception)
        {
            throw new Tripo.Bridge.HostControlCallException(
                "invalid_argument",
                exception.Message,
                exception);
        }
        catch (TripoCredentialPreflightException exception)
        {
            throw new Tripo.Bridge.HostControlCallException(
                Tripo.Bridge.HostControlConstants.CredentialInvalidError,
                exception.Message,
                exception);
        }
        catch (TripoCredentialException exception)
            when (string.Equals(
                method,
                Tripo.Bridge.HostControlConstants.CredentialSetMethod,
                StringComparison.Ordinal))
        {
            throw new Tripo.Bridge.HostControlCallException(
                Tripo.Bridge.HostControlConstants.CredentialInvalidError,
                exception.Message,
                exception);
        }
        catch (TripoWorkflowException exception)
        {
            throw new Tripo.Bridge.HostControlCallException(
                "workflow_error",
                exception.Message,
                exception);
        }
        catch (TripoPaidRequestRejectedException exception)
        {
            string requestSuffix = string.IsNullOrWhiteSpace(exception.RequestId)
                ? string.Empty
                : $" Request ID: {exception.RequestId}.";
            throw new Tripo.Bridge.HostControlCallException(
                Tripo.Bridge.HostControlConstants.CredentialRejectedError,
                exception.Message + requestSuffix,
                exception);
        }
        catch (TripoApiException exception)
        {
            string requestSuffix = string.IsNullOrWhiteSpace(exception.RequestId)
                ? string.Empty
                : $" Request ID: {exception.RequestId}.";
            throw new Tripo.Bridge.HostControlCallException(
                IsCredentialFailureSafeToExpose(method, exception)
                    ? Tripo.Bridge.HostControlConstants.CredentialInvalidError
                    : "tripo_api_error",
                exception.Message + requestSuffix,
                exception);
        }
        catch (Tripo.Bridge.BridgeCallException exception)
        {
            throw new Tripo.Bridge.HostControlCallException(
                exception.Code,
                exception.Message,
                exception);
        }
    }

    private Tripo.Bridge.HostControlHealthReceipt CreateHealthReceipt() =>
        new(
            _host,
            _hostProcessId,
            Environment.ProcessId,
            _capabilities);

    private static bool IsCredentialFailureSafeToExpose(
        string method,
        TripoApiException exception) =>
        exception.StatusCode is
            System.Net.HttpStatusCode.Unauthorized or
            System.Net.HttpStatusCode.Forbidden &&
        (string.Equals(
             method,
             Tripo.Bridge.HostControlConstants.TaskStatusMethod,
             StringComparison.Ordinal) ||
         string.Equals(
             method,
             Tripo.Bridge.HostControlConstants.ImportGenerationGlbMethod,
             StringComparison.Ordinal) ||
         string.Equals(
             method,
             Tripo.Bridge.HostControlConstants.ImportObjTaskMethod,
             StringComparison.Ordinal));

    private Tripo.Bridge.HostControlHealthReceipt RequestShutdown()
    {
        Tripo.Bridge.HostControlHealthReceipt receipt = CreateHealthReceipt();
        _ = Task.Run(
            async () =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250))
                    .ConfigureAwait(false);
                _requestShutdown();
            });
        return receipt;
    }

    private Tripo.Bridge.HostControlCredentialMutationReceipt SetCredential(
        JsonElement payload)
    {
        Tripo.Bridge.HostControlSetApiKeyRequest request =
            Deserialize<Tripo.Bridge.HostControlSetApiKeyRequest>(payload);
        return _credentials.SetApiKey(request.ApiKey, request.Persist);
    }

    private async Task<Tripo.Bridge.HostControlTextTaskCreationReceipt>
        CreateTextTaskAsync(
            JsonElement payload,
            CancellationToken cancellationToken)
    {
        Tripo.Bridge.HostControlCreateTextTaskRequest request =
            Deserialize<Tripo.Bridge.HostControlCreateTextTaskRequest>(payload);
        TextTaskCreationReceipt receipt =
            await _workflow.CreateTextTaskAsync(
                    request.Prompt,
                    request.FaceLimit,
                    request.WithMaterials,
                    request.DocumentSessionId,
                    request.OperationId,
                    request.ConfirmExternalCost,
                    cancellationToken,
                    request.RequireExistingOperation)
                .ConfigureAwait(false);
        return new Tripo.Bridge.HostControlTextTaskCreationReceipt(
            receipt.OperationId,
            receipt.TaskId,
            receipt.Model);
    }

    private async Task<Tripo.Bridge.HostControlTaskStatusReceipt>
        GetTaskStatusAsync(
            JsonElement payload,
            CancellationToken cancellationToken)
    {
        Tripo.Bridge.HostControlTaskStatusRequest request =
            Deserialize<Tripo.Bridge.HostControlTaskStatusRequest>(payload);
        TaskStatusReceipt receipt =
            await _workflow.GetTaskStatusAsync(
                    request.TaskId,
                    cancellationToken)
                .ConfigureAwait(false);
        return new Tripo.Bridge.HostControlTaskStatusReceipt(
            receipt.TaskId,
            receipt.Type,
            receipt.Status,
            receipt.Progress,
            receipt.CreditsConsumed,
            receipt.CreatedAt,
            receipt.CompletedAt,
            receipt.ErrorCode,
            receipt.ErrorMessage);
    }

    private async Task<Tripo.Bridge.HostControlImageTaskCreationReceipt>
        CreateImageTaskAsync(
            JsonElement payload,
            CancellationToken cancellationToken)
    {
        Tripo.Bridge.HostControlCreateImageTaskRequest request =
            Deserialize<Tripo.Bridge.HostControlCreateImageTaskRequest>(
                payload);
        ImageTaskCreationReceipt receipt =
            await _workflow.CreateImageTaskAsync(
                    request.Image,
                    request.FaceLimit,
                    request.WithMaterials,
                    request.DocumentSessionId,
                    request.OperationId,
                    request.ConfirmExternalCost,
                    cancellationToken,
                    request.RequireExistingOperation)
                .ConfigureAwait(false);
        return new Tripo.Bridge.HostControlImageTaskCreationReceipt(
            receipt.OperationId,
            receipt.TaskId,
            receipt.Model,
            receipt.ImageSha256);
    }

    private async Task<Tripo.Bridge.HostControlOperationStatusReceipt>
        GetOperationStatusAsync(
            JsonElement payload,
            CancellationToken cancellationToken)
    {
        Tripo.Bridge.HostControlOperationStatusRequest request =
            Deserialize<Tripo.Bridge.HostControlOperationStatusRequest>(payload);
        PaidOperationStatusReceipt receipt =
            await _workflow.GetPaidOperationStatusAsync(
                    request.OperationId,
                    cancellationToken)
                .ConfigureAwait(false);
        return new Tripo.Bridge.HostControlOperationStatusReceipt(
            receipt.OperationId,
            receipt.Kind,
            receipt.State,
            receipt.SourceTaskId,
            receipt.CreatedTaskId,
            receipt.FailureCode,
            receipt.FailureMessage,
            receipt.TaskIdDurable,
            receipt.MayHaveCreatedRemoteTask,
            receipt.CanResumeCreation,
            receipt.NextAction,
            receipt.UpdatedAtUtc,
            receipt.FailureStage,
            receipt.OperationInProgress);
    }

    private async Task<Tripo.Bridge.HostControlObjConversionCreationReceipt>
        CreateObjConversionAsync(
            JsonElement payload,
            CancellationToken cancellationToken)
    {
        Tripo.Bridge.HostControlCreateObjConversionRequest request =
            Deserialize<Tripo.Bridge.HostControlCreateObjConversionRequest>(
                payload);
        ObjConversionCreationReceipt receipt =
            await _workflow.CreateObjConversionAsync(
                    request.SourceTaskId,
                    request.FaceLimit,
                    request.WithMaterials,
                    request.DocumentSessionId,
                    request.OperationId,
                    request.ConfirmExternalCost,
                    cancellationToken,
                    request.RequireExistingOperation)
                .ConfigureAwait(false);
        return new Tripo.Bridge.HostControlObjConversionCreationReceipt(
            receipt.OperationId,
            receipt.SourceTaskId,
            receipt.ConversionTaskId,
            receipt.Format);
    }

    private async Task<Tripo.Bridge.HostControlObjTaskImportReceipt>
        ImportObjTaskAsync(
            JsonElement payload,
            CancellationToken cancellationToken)
    {
        Tripo.Bridge.HostControlImportObjTaskRequest request =
            Deserialize<Tripo.Bridge.HostControlImportObjTaskRequest>(payload);
        ObjTaskImportReceipt receipt =
            await _workflow.ImportObjTaskAsync(
                    request.ConversionTaskId,
                    request.Name,
                    request.DocumentSessionId,
                    request.OperationId,
                    request.ImportMode,
                    request.ApplyMaterials,
                    cancellationToken)
                .ConfigureAwait(false);
        return new Tripo.Bridge.HostControlObjTaskImportReceipt(
            receipt.OperationId,
            receipt.ConversionTaskId,
            receipt.ConversionCreditsConsumed,
            receipt.HostReceipt);
    }

    private async Task<Tripo.Bridge.HostControlGenerationGlbImportReceipt>
        ImportGenerationGlbAsync(
            JsonElement payload,
            CancellationToken cancellationToken)
    {
        Tripo.Bridge.HostControlImportGenerationGlbRequest request =
            Deserialize<
                Tripo.Bridge.HostControlImportGenerationGlbRequest>(payload);
        GenerationGlbImportReceipt receipt =
            await _workflow.ImportGenerationGlbAsync(
                    request.GenerationTaskId,
                    request.Name,
                    request.DocumentSessionId,
                    request.OperationId,
                    request.ApplyMaterials,
                    cancellationToken)
                .ConfigureAwait(false);
        return new Tripo.Bridge.HostControlGenerationGlbImportReceipt(
            receipt.OperationId,
            receipt.GenerationTaskId,
            receipt.GenerationCreditsConsumed,
            receipt.HostReceipt);
    }

    private async Task<Tripo.Bridge.HostControlObjTaskStageReceipt>
        StageObjTaskAsync(
            JsonElement payload,
            CancellationToken cancellationToken)
    {
        Tripo.Bridge.HostControlStageObjTaskRequest request =
            Deserialize<Tripo.Bridge.HostControlStageObjTaskRequest>(payload);
        ObjTaskStageReceipt receipt =
            await _workflow.StageObjTaskAsync(
                    request.ConversionTaskId,
                    request.DocumentSessionId,
                    request.IncludeMaterials,
                    cancellationToken)
                .ConfigureAwait(false);
        return new Tripo.Bridge.HostControlObjTaskStageReceipt(
            receipt.ConversionTaskId,
            receipt.ConversionCreditsConsumed,
            receipt.Mesh);
    }

    private static T Deserialize<T>(JsonElement payload)
    {
        try
        {
            return payload.Deserialize<T>(Tripo.Bridge.BridgeJson.Options)
                ?? throw new Tripo.Bridge.HostControlCallException(
                    "invalid_request",
                    "The host-control request payload was null.");
        }
        catch (JsonException exception)
        {
            throw new Tripo.Bridge.HostControlCallException(
                "invalid_request",
                "The host-control request payload was invalid.",
                exception);
        }
    }
}
