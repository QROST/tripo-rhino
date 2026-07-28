namespace Tripo.Bridge;

public interface IHostControlClient
{
    Task<HostControlHealthReceipt> GetHealthAsync(
        CancellationToken cancellationToken);

    Task<HostControlCredentialStatusReceipt> GetCredentialStatusAsync(
        CancellationToken cancellationToken);

    Task<HostControlCredentialMutationReceipt> SetApiKeyAsync(
        string apiKey,
        bool persist,
        CancellationToken cancellationToken);

    Task<HostControlCredentialMutationReceipt> ClearApiKeyAsync(
        CancellationToken cancellationToken);

    Task<HostContextReceipt> GetHostContextAsync(
        CancellationToken cancellationToken);

    Task<HostControlTextTaskCreationReceipt> CreateTextTaskAsync(
        HostControlCreateTextTaskRequest request,
        CancellationToken cancellationToken);

    Task<HostControlImageTaskCreationReceipt> CreateImageTaskAsync(
        HostControlCreateImageTaskRequest request,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "This host-control client does not support image task creation.");

    Task<HostControlTaskStatusReceipt> GetTaskStatusAsync(
        string taskId,
        CancellationToken cancellationToken);

    Task<HostControlOperationStatusReceipt> GetOperationStatusAsync(
        string operationId,
        CancellationToken cancellationToken);

    Task<HostControlObjConversionCreationReceipt> CreateObjConversionAsync(
        HostControlCreateObjConversionRequest request,
        CancellationToken cancellationToken);

    Task<HostControlGenerationGlbImportReceipt> ImportGenerationGlbAsync(
        HostControlImportGenerationGlbRequest request,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "This host-control client does not support direct GLB import.");

    Task<HostControlObjTaskImportReceipt> ImportObjTaskAsync(
        HostControlImportObjTaskRequest request,
        CancellationToken cancellationToken);

    Task<HostControlObjTaskStageReceipt> StageObjTaskAsync(
        HostControlStageObjTaskRequest request,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "This host-control client does not support OBJ staging.");

    Task<HostControlHealthReceipt> ShutdownAsync(
        CancellationToken cancellationToken);
}

public sealed class HostControlClient : IHostControlClient
{
    private readonly NamedPipeHostControlClient _client;

    public HostControlClient(
        string host,
        int hostProcessId,
        TimeSpan? timeout = null)
    {
        _client = new NamedPipeHostControlClient(
            host,
            hostProcessId,
            timeout);
    }

    public Task<HostControlHealthReceipt> GetHealthAsync(
        CancellationToken cancellationToken) =>
        CallAsync<object, HostControlHealthReceipt>(
            HostControlConstants.HealthMethod,
            new { },
            cancellationToken);

    public Task<HostControlCredentialStatusReceipt> GetCredentialStatusAsync(
        CancellationToken cancellationToken) =>
        CallAsync<object, HostControlCredentialStatusReceipt>(
            HostControlConstants.CredentialStatusMethod,
            new { },
            cancellationToken);

    public Task<HostControlCredentialMutationReceipt> SetApiKeyAsync(
        string apiKey,
        bool persist,
        CancellationToken cancellationToken) =>
        CallAsync<
            HostControlSetApiKeyRequest,
            HostControlCredentialMutationReceipt>(
            HostControlConstants.CredentialSetMethod,
            new HostControlSetApiKeyRequest(apiKey, persist),
            cancellationToken);

    public Task<HostControlCredentialMutationReceipt> ClearApiKeyAsync(
        CancellationToken cancellationToken) =>
        CallAsync<object, HostControlCredentialMutationReceipt>(
            HostControlConstants.CredentialClearMethod,
            new { },
            cancellationToken);

    public Task<HostContextReceipt> GetHostContextAsync(
        CancellationToken cancellationToken) =>
        CallAsync<object, HostContextReceipt>(
            HostControlConstants.HostContextMethod,
            new { },
            cancellationToken);

    public Task<HostControlTextTaskCreationReceipt> CreateTextTaskAsync(
        HostControlCreateTextTaskRequest request,
        CancellationToken cancellationToken) =>
        CallAsync<
            HostControlCreateTextTaskRequest,
            HostControlTextTaskCreationReceipt>(
            HostControlConstants.CreateTextTaskMethod,
            request,
            cancellationToken);

    public Task<HostControlImageTaskCreationReceipt> CreateImageTaskAsync(
        HostControlCreateImageTaskRequest request,
        CancellationToken cancellationToken) =>
        CallAsync<
            HostControlCreateImageTaskRequest,
            HostControlImageTaskCreationReceipt>(
            HostControlConstants.CreateImageTaskMethod,
            request,
            cancellationToken);

    public Task<HostControlTaskStatusReceipt> GetTaskStatusAsync(
        string taskId,
        CancellationToken cancellationToken) =>
        CallAsync<
            HostControlTaskStatusRequest,
            HostControlTaskStatusReceipt>(
            HostControlConstants.TaskStatusMethod,
            new HostControlTaskStatusRequest(taskId),
            cancellationToken);

    public Task<HostControlOperationStatusReceipt> GetOperationStatusAsync(
        string operationId,
        CancellationToken cancellationToken) =>
        CallAsync<
            HostControlOperationStatusRequest,
            HostControlOperationStatusReceipt>(
            HostControlConstants.OperationStatusMethod,
            new HostControlOperationStatusRequest(operationId),
            cancellationToken);

    public Task<HostControlObjConversionCreationReceipt>
        CreateObjConversionAsync(
            HostControlCreateObjConversionRequest request,
            CancellationToken cancellationToken) =>
        CallAsync<
            HostControlCreateObjConversionRequest,
            HostControlObjConversionCreationReceipt>(
            HostControlConstants.CreateObjConversionMethod,
            request,
            cancellationToken);

    public Task<HostControlObjTaskImportReceipt> ImportObjTaskAsync(
        HostControlImportObjTaskRequest request,
        CancellationToken cancellationToken) =>
        CallAsync<
            HostControlImportObjTaskRequest,
            HostControlObjTaskImportReceipt>(
            HostControlConstants.ImportObjTaskMethod,
            request,
            cancellationToken);

    public Task<HostControlGenerationGlbImportReceipt>
        ImportGenerationGlbAsync(
            HostControlImportGenerationGlbRequest request,
            CancellationToken cancellationToken) =>
        CallAsync<
            HostControlImportGenerationGlbRequest,
            HostControlGenerationGlbImportReceipt>(
            HostControlConstants.ImportGenerationGlbMethod,
            request,
            cancellationToken);

    public Task<HostControlObjTaskStageReceipt> StageObjTaskAsync(
        HostControlStageObjTaskRequest request,
        CancellationToken cancellationToken) =>
        CallAsync<
            HostControlStageObjTaskRequest,
            HostControlObjTaskStageReceipt>(
            HostControlConstants.StageObjTaskMethod,
            request,
            cancellationToken);

    public Task<HostControlHealthReceipt> ShutdownAsync(
        CancellationToken cancellationToken) =>
        CallAsync<object, HostControlHealthReceipt>(
            HostControlConstants.ShutdownMethod,
            new { },
            cancellationToken);

    private Task<TResponse> CallAsync<TRequest, TResponse>(
        string method,
        TRequest request,
        CancellationToken cancellationToken) =>
        _client.CallAsync<TRequest, TResponse>(
            method,
            request,
            cancellationToken);
}
