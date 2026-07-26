using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tripo.Bridge;

public sealed record HostControlSessionDescriptor(
    string ProtocolVersion,
    string Channel,
    string Host,
    int HostProcessId,
    int SidecarProcessId,
    string SessionId,
    string PipeName,
    string SessionToken,
    DateTimeOffset StartedAtUtc,
    IReadOnlyList<string> Capabilities)
{
    public override string ToString() =>
        $"{nameof(HostControlSessionDescriptor)} {{ ProtocolVersion = " +
        $"{ProtocolVersion}, Channel = {Channel}, Host = {Host}, " +
        $"HostProcessId = {HostProcessId}, SidecarProcessId = " +
        $"{SidecarProcessId}, SessionId = {SessionId}, PipeName = " +
        $"{PipeName}, SessionToken = [REDACTED], StartedAtUtc = " +
        $"{StartedAtUtc:O}, Capabilities = [REDACTED] }}";
}

public sealed class HostControlRequest
{
    [JsonConstructor]
    public HostControlRequest(
        string protocolVersion,
        string channel,
        string requestId,
        string sessionToken,
        string method,
        JsonElement payload)
    {
        ProtocolVersion = protocolVersion;
        Channel = channel;
        RequestId = requestId;
        SessionToken = sessionToken;
        Method = method;
        Payload = payload;
    }

    public string ProtocolVersion { get; }

    public string Channel { get; }

    public string RequestId { get; }

    public string SessionToken { get; }

    public string Method { get; }

    public JsonElement Payload { get; }

    public override string ToString() =>
        $"{nameof(HostControlRequest)} {{ ProtocolVersion = {ProtocolVersion}, " +
        $"Channel = {Channel}, RequestId = {RequestId}, SessionToken = " +
        $"[REDACTED], Method = {Method}, Payload = [REDACTED] }}";
}

public sealed record HostControlError(string Code, string Message);

public sealed record HostControlResponse(
    string ProtocolVersion,
    string Channel,
    string RequestId,
    bool Ok,
    JsonElement? Result,
    HostControlError? Error)
{
    public static HostControlResponse Success<T>(string requestId, T result) =>
        new(
            HostControlConstants.ProtocolVersion,
            HostControlConstants.Channel,
            requestId,
            true,
            BridgeJson.ToElement(result),
            null);

    public static HostControlResponse Failure(
        string requestId,
        string code,
        string message) =>
        new(
            HostControlConstants.ProtocolVersion,
            HostControlConstants.Channel,
            requestId,
            false,
            null,
            new HostControlError(code, message));
}

public sealed record HostControlHealthReceipt(
    string Host,
    int HostProcessId,
    int SidecarProcessId,
    IReadOnlyList<string> Capabilities);

public sealed record HostControlCredentialStatusReceipt(
    bool HasApiKey,
    string Source,
    bool StoredKeyPresent,
    bool CanClearStoredKey,
    string PersistenceBackend,
    bool UsesWeakerFileFallback,
    bool StoredKeyPresenceKnown = true);

public sealed class HostControlSetApiKeyRequest
{
    [JsonConstructor]
    public HostControlSetApiKeyRequest(string apiKey, bool persist)
    {
        ApiKey = apiKey;
        Persist = persist;
    }

    public string ApiKey { get; }

    public bool Persist { get; }

    public override string ToString() =>
        $"{nameof(HostControlSetApiKeyRequest)} {{ ApiKey = [REDACTED], " +
        $"Persist = {Persist} }}";
}

public sealed record HostControlCredentialMutationReceipt(
    HostControlCredentialStatusReceipt Status);

public sealed record HostControlCreateTextTaskRequest(
    string Prompt,
    int FaceLimit,
    bool WithMaterials,
    string DocumentSessionId,
    string OperationId,
    bool ConfirmExternalCost,
    bool RequireExistingOperation = false);

public sealed record HostControlTextTaskCreationReceipt(
    string OperationId,
    string TaskId,
    string Model);

public sealed record HostControlCreateImageTaskRequest(
    StagedImageTransfer Image,
    int FaceLimit,
    bool WithMaterials,
    string DocumentSessionId,
    string OperationId,
    bool ConfirmExternalCost,
    bool RequireExistingOperation = false);

public sealed record HostControlImageTaskCreationReceipt(
    string OperationId,
    string TaskId,
    string Model,
    string ImageSha256);

public sealed record HostControlTaskStatusRequest(string TaskId);

public sealed record HostControlTaskStatusReceipt(
    string TaskId,
    string Type,
    string Status,
    int Progress,
    decimal? CreditsConsumed,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? CompletedAt,
    int? ErrorCode,
    string? ErrorMessage);

public sealed record HostControlOperationStatusRequest(string OperationId);

public sealed record HostControlOperationStatusReceipt(
    string OperationId,
    string Kind,
    string State,
    string? SourceTaskId,
    string? CreatedTaskId,
    string? FailureCode,
    string? FailureMessage,
    bool TaskIdDurable,
    bool MayHaveCreatedRemoteTask,
    bool CanResumeCreation,
    string NextAction,
    DateTimeOffset UpdatedAtUtc,
    string? FailureStage = null);

public sealed record HostControlCreateObjConversionRequest(
    string SourceTaskId,
    int FaceLimit,
    bool WithMaterials,
    string DocumentSessionId,
    string OperationId,
    bool ConfirmExternalCost,
    bool RequireExistingOperation = false);

public sealed record HostControlObjConversionCreationReceipt(
    string OperationId,
    string SourceTaskId,
    string ConversionTaskId,
    string Format);

public sealed record HostControlImportObjTaskRequest(
    string ConversionTaskId,
    string Name,
    string DocumentSessionId,
    string OperationId,
    string ImportMode,
    bool ApplyMaterials);

public sealed record HostControlObjTaskImportReceipt(
    string OperationId,
    string ConversionTaskId,
    decimal? ConversionCreditsConsumed,
    HostImportReceipt HostReceipt);

public sealed record HostControlStageObjTaskRequest(
    string ConversionTaskId,
    string DocumentSessionId,
    bool IncludeMaterials);

public sealed record HostControlObjTaskStageReceipt(
    string ConversionTaskId,
    decimal? ConversionCreditsConsumed,
    StagedMeshLoadRequest Mesh);

public interface IHostControlDispatcher
{
    Task<object> DispatchAsync(
        string method,
        JsonElement payload,
        CancellationToken cancellationToken);
}

public sealed class HostControlCallException : Exception
{
    public HostControlCallException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public HostControlCallException(
        string code,
        string message,
        Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}
