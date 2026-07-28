using System.Text.Json.Serialization;

namespace Tripo.Mcp;

public sealed record TextGenerationOptions(
    string Prompt,
    int FaceLimit,
    string Model = TripoV3Client.DefaultModel,
    bool WithMaterials = false);

public sealed record ImageGenerationOptions(
    Tripo.Bridge.StagedImageTransfer Image,
    int FaceLimit,
    string Model = TripoV3Client.DefaultModel,
    bool WithMaterials = false);

internal sealed record TextGenerationRequest(
    [property: JsonPropertyName("prompt")] string Prompt,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("face_limit")] int FaceLimit,
    [property: JsonPropertyName("texture")] bool Texture,
    [property: JsonPropertyName("pbr")] bool Pbr,
    [property: JsonPropertyName("auto_size")] bool AutoSize,
    [property: JsonPropertyName("quad")] bool Quad,
    [property: JsonPropertyName("export_uv")] bool ExportUv);

internal sealed record ImageGenerationRequest(
    [property: JsonPropertyName("input")] string Input,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("face_limit")] int FaceLimit,
    [property: JsonPropertyName("texture")] bool Texture,
    [property: JsonPropertyName("pbr")] bool Pbr,
    [property: JsonPropertyName("auto_size")] bool AutoSize,
    [property: JsonPropertyName("quad")] bool Quad,
    [property: JsonPropertyName("export_uv")] bool ExportUv);

internal sealed record ImageOperationIdentity(
    [property: JsonPropertyName("upload_endpoint")] string UploadEndpoint,
    [property: JsonPropertyName("generation_endpoint")] string GenerationEndpoint,
    [property: JsonPropertyName("image_sha256")] string ImageSha256,
    [property: JsonPropertyName("image_byte_length")] long ImageByteLength,
    [property: JsonPropertyName("image_media_type")] string ImageMediaType,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("face_limit")] int FaceLimit,
    [property: JsonPropertyName("texture")] bool Texture,
    [property: JsonPropertyName("pbr")] bool Pbr,
    [property: JsonPropertyName("auto_size")] bool AutoSize,
    [property: JsonPropertyName("quad")] bool Quad,
    [property: JsonPropertyName("export_uv")] bool ExportUv);

internal sealed record ConvertModelRequest(
    [property: JsonPropertyName("input")] string Input,
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("quad")] bool Quad,
    [property: JsonPropertyName("face_limit")] int FaceLimit,
    [property: JsonPropertyName("bake")] bool Bake,
    [property: JsonPropertyName("with_animation")] bool WithAnimation);

internal sealed record ApiEnvelope<T>(
    [property: JsonPropertyName("code")] int? Code,
    [property: JsonPropertyName("data")] T? Data,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("suggestion")] string? Suggestion,
    [property: JsonPropertyName("request_id")] string? RequestId);

internal sealed record CreateTaskData(
    [property: JsonPropertyName("task_id")] string TaskId);

internal sealed record UploadFileData(
    [property: JsonPropertyName("file_token")] string FileToken);

public sealed record TripoTaskOutput(
    [property: JsonPropertyName("model_url")] string? ModelUrl,
    [property: JsonPropertyName("rendered_image_url")] string? RenderedImageUrl);

public sealed record TripoTaskSnapshot(
    [property: JsonPropertyName("task_id")] string TaskId,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("progress")] int Progress,
    [property: JsonPropertyName("output")] TripoTaskOutput? Output,
    [property: JsonPropertyName("credits_consumed")] decimal? CreditsConsumed,
    [property: JsonPropertyName("created_at")] DateTimeOffset? CreatedAt,
    [property: JsonPropertyName("completed_at")] DateTimeOffset? CompletedAt,
    [property: JsonPropertyName("error_code")] int? ErrorCode,
    [property: JsonPropertyName("error_message")] string? ErrorMessage);

public sealed record TaskStatusReceipt(
    string TaskId,
    string Type,
    string Status,
    int Progress,
    decimal? CreditsConsumed,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? CompletedAt,
    int? ErrorCode,
    string? ErrorMessage);

public sealed record TextTaskCreationReceipt(
    string OperationId,
    string TaskId,
    string Model);

public sealed record ImageTaskCreationReceipt(
    string OperationId,
    string TaskId,
    string Model,
    string ImageSha256);

public sealed record ObjConversionCreationReceipt(
    string OperationId,
    string SourceTaskId,
    string ConversionTaskId,
    string Format);

public sealed record ObjTaskImportReceipt(
    string OperationId,
    string ConversionTaskId,
    decimal? ConversionCreditsConsumed,
    Tripo.Bridge.HostImportReceipt HostReceipt);

public sealed record GenerationGlbImportReceipt(
    string OperationId,
    string GenerationTaskId,
    decimal? GenerationCreditsConsumed,
    Tripo.Bridge.HostImportReceipt HostReceipt);

public sealed record ObjTaskStageReceipt(
    string ConversionTaskId,
    decimal? ConversionCreditsConsumed,
    Tripo.Bridge.StagedMeshLoadRequest Mesh);
