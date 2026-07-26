using System.Text.Json;

namespace Tripo.Bridge;

public sealed record BridgeSessionDescriptor(
    string ProtocolVersion,
    string Host,
    string HostVersion,
    int ProcessId,
    string SessionId,
    string PipeName,
    string SessionToken,
    DateTimeOffset StartedAtUtc,
    IReadOnlyList<string> Capabilities);

public sealed record BridgeRequest(
    string ProtocolVersion,
    string RequestId,
    string SessionToken,
    string Method,
    JsonElement Payload);

public sealed record BridgeError(string Code, string Message);

public sealed record BridgeResponse(
    string ProtocolVersion,
    string RequestId,
    bool Ok,
    JsonElement? Result,
    BridgeError? Error)
{
    public static BridgeResponse Success<T>(string requestId, T result) =>
        new(
            BridgeConstants.ProtocolVersion,
            requestId,
            true,
            BridgeJson.ToElement(result),
            null);

    public static BridgeResponse Failure(string requestId, string code, string message) =>
        new(
            BridgeConstants.ProtocolVersion,
            requestId,
            false,
            null,
            new BridgeError(code, message));
}

public sealed record HostContextReceipt(
    string Host,
    string HostVersion,
    int ProcessId,
    string DocumentSessionId,
    string DocumentTitle,
    string DocumentUnits,
    IReadOnlyList<string> Capabilities);

public sealed record StagedBundleEntry(
    string RelativePath,
    string Sha256,
    long ByteLength);

public sealed record StagedBundle(
    string BundleId,
    string ObjEntry,
    string? MtlEntry,
    IReadOnlyList<StagedBundleEntry> Entries,
    string RootDirectory);

public sealed record StagedMeshLoadRequest(
    string BundleId,
    string ObjEntry,
    string? MtlEntry,
    IReadOnlyList<StagedBundleEntry> Entries,
    string SourceUnit,
    string UpAxis,
    string Handedness,
    bool ApplyMaterials);

public sealed record StagedImageTransfer(
    string TransferId,
    string Sha256,
    long ByteLength,
    string MediaType);

public sealed record ImportMeshRequest(
    string DocumentSessionId,
    string BundleId,
    string ObjEntry,
    string? MtlEntry,
    IReadOnlyList<StagedBundleEntry> Entries,
    string SourceUnit,
    string UpAxis,
    string Handedness,
    string Name,
    string IdempotencyKey,
    string ImportMode,
    bool ApplyMaterials);

public sealed record HostImportReceipt(
    string Host,
    string DocumentSessionId,
    string IdempotencyKey,
    string CreatedId,
    int VertexCount,
    int TriangleCount,
    int RejectedTriangleCount,
    string TransactionStatus,
    string ImportMode,
    int MaterialCount,
    int TextureCount,
    string? SavedFamilyPath);

public interface IHostBridgeDispatcher
{
    Task<object> DispatchAsync(
        string method,
        JsonElement payload,
        CancellationToken cancellationToken);
}

public sealed class BridgeCallException : Exception
{
    public BridgeCallException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public BridgeCallException(string code, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}
