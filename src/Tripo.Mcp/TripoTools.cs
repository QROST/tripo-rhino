using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Tripo.Mcp;

[McpServerToolType]
public sealed class TripoTools
{
    private readonly ITripoWorkflow _workflow;

    public TripoTools(ITripoWorkflow workflow)
    {
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
    }

    [McpServerTool(
        Name = "tripo_host_context",
        Title = "Get Tripo host context",
        ReadOnly = true,
        Destructive = false,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description(
        "Return the connected Rhino or Revit process and the exact active-document " +
        "session UUID required by later stages. This tool does not call the Tripo API.")]
    public Task<Tripo.Bridge.HostContextReceipt> GetHostContextAsync(
        CancellationToken cancellationToken) =>
        InvokeAsync(
            () => _workflow.GetHostContextAsync(cancellationToken),
            cancellationToken);

    [McpServerTool(
        Name = "tripo_task_status",
        Title = "Get Tripo task status",
        ReadOnly = true,
        Destructive = false,
        OpenWorld = true,
        UseStructuredContent = true)]
    [Description(
        "Query one existing Tripo v3 task. This read does not create a task or import geometry.")]
    public Task<TaskStatusReceipt> GetTaskStatusAsync(
        [Description("Tripo v3 task ID beginning with task_.")] string taskId,
        CancellationToken cancellationToken) =>
        InvokeAsync(
            () => _workflow.GetTaskStatusAsync(taskId, cancellationToken),
            cancellationToken);

    [McpServerTool(
        Name = "tripo_operation_status",
        Title = "Get local paid-operation status",
        ReadOnly = true,
        Destructive = false,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description(
        "Read the durable local recovery record for one paid creation operation. " +
        "This tool does not call the Tripo API or a Rhino/Revit host.")]
    public Task<PaidOperationStatusReceipt> GetPaidOperationStatusAsync(
        [Description("Caller-generated paid-operation UUID.")] string operationId,
        CancellationToken cancellationToken) =>
        InvokeAsync(
            () => _workflow.GetPaidOperationStatusAsync(
                operationId,
                cancellationToken),
            cancellationToken);

    [McpServerTool(
        Name = "tripo_create_text_task",
        Title = "Create a Tripo text task",
        ReadOnly = false,
        Destructive = false,
        Idempotent = true,
        OpenWorld = true,
        UseStructuredContent = true)]
    [Description(
        "Durably checkpoint and create one Tripo v3 text-to-model task. Reusing the " +
        "same operationId and identical arguments returns the same task ID without " +
        "another POST. This remote task may consume credits.")]
    public Task<TextTaskCreationReceipt> CreateTextTaskAsync(
        [Description("Text prompt containing 1 to 1024 characters.")] string prompt,
        [Description("Maximum mesh face count from 500 through 200000.")] int faceLimit,
        [Description(
            "When true, request textured PBR materials (texture and pbr); this raises " +
            "the credit cost and generation time. When false, generate geometry only.")] bool withMaterials,
        [Description(
            "Exact document-session UUID returned by tripo_host_context.")] string documentSessionId,
        [Description(
            "Caller-generated UUID. Reuse it with identical arguments after any " +
            "lost response; never replace it merely to retry.")] string operationId,
        [Description(
            "Must be true only after the user explicitly accepts possible external charges.")] bool confirmExternalCost,
        CancellationToken cancellationToken) =>
        InvokeAsync(
            () => _workflow.CreateTextTaskAsync(
                prompt,
                faceLimit,
                withMaterials,
                documentSessionId,
                operationId,
                confirmExternalCost,
                cancellationToken),
            cancellationToken);

    [McpServerTool(
        Name = "tripo_stage_local_image",
        Title = "Stage a local image for Tripo",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description(
        "Validate and copy one explicit local PNG or JPEG into the private Tripo " +
        "image-transfer store. This does not call Tripo or consume credits. The " +
        "returned opaque descriptor can be passed to tripo_create_image_task.")]
    public Task<Tripo.Bridge.StagedImageTransfer> StageLocalImageAsync(
        [Description(
            "Absolute path visible to this local sidecar. The source path is " +
            "validated but is not returned or written to the operation journal.")] string localImagePath,
        CancellationToken cancellationToken) =>
        InvokeAsync(
            () => _workflow.StageLocalImageAsync(
                localImagePath,
                cancellationToken),
            cancellationToken);

    [McpServerTool(
        Name = "tripo_create_image_task",
        Title = "Create a Tripo image task",
        ReadOnly = false,
        Destructive = false,
        Idempotent = true,
        OpenWorld = true,
        UseStructuredContent = true)]
    [Description(
        "Upload one private staged image and durably create one Tripo v3 " +
        "image-to-model task. The upload and paid generation dispatch are " +
        "checkpointed separately. Reuse the same operationId and exact descriptor " +
        "after a lost response; this remote task may consume credits.")]
    public Task<ImageTaskCreationReceipt> CreateImageTaskAsync(
        [Description("Opaque UUID returned by tripo_stage_local_image.")] string transferId,
        [Description("SHA-256 returned by tripo_stage_local_image.")] string sha256,
        [Description("Exact byte length returned by tripo_stage_local_image.")] long byteLength,
        [Description("image/png or image/jpeg as returned by staging.")] string mediaType,
        [Description("Maximum mesh face count from 500 through 200000.")] int faceLimit,
        [Description(
            "When true, request textured PBR materials; when false, generate " +
            "geometry only.")] bool withMaterials,
        [Description(
            "Exact document-session UUID returned by tripo_host_context.")] string documentSessionId,
        [Description(
            "Caller-generated UUID. Reuse it with identical arguments after a " +
            "lost response.")] string operationId,
        [Description(
            "Must be true only after the user explicitly accepts possible " +
            "external charges.")] bool confirmExternalCost,
        CancellationToken cancellationToken) =>
        InvokeAsync(
            () => _workflow.CreateImageTaskAsync(
                new Tripo.Bridge.StagedImageTransfer(
                    transferId,
                    sha256,
                    byteLength,
                    mediaType),
                faceLimit,
                withMaterials,
                documentSessionId,
                operationId,
                confirmExternalCost,
                cancellationToken),
            cancellationToken);

    [McpServerTool(
        Name = "tripo_create_obj_conversion",
        Title = "Create a Tripo OBJ conversion",
        ReadOnly = false,
        Destructive = false,
        Idempotent = true,
        OpenWorld = true,
        UseStructuredContent = true)]
    [Description(
        "Validate one successful Tripo generation task, durably checkpoint one OBJ " +
        "conversion, and return its task ID. Reusing the same operationId and " +
        "identical arguments does not send another POST. This may consume credits.")]
    public Task<ObjConversionCreationReceipt> CreateObjConversionAsync(
        [Description("Successful Tripo v3 generation task ID.")] string sourceTaskId,
        [Description("Maximum OBJ face count from 500 through 200000.")] int faceLimit,
        [Description(
            "When true, bake a diffuse material library (bake=true) so the OBJ ships " +
            "with an MTL and image textures. When false, convert geometry only.")] bool withMaterials,
        [Description(
            "Exact document-session UUID returned by tripo_host_context.")] string documentSessionId,
        [Description(
            "Caller-generated UUID dedicated to this conversion. Reuse it with " +
            "identical arguments after any lost response.")] string operationId,
        [Description(
            "Must be true only after the user explicitly accepts possible external charges.")] bool confirmExternalCost,
        CancellationToken cancellationToken) =>
        InvokeAsync(
            () => _workflow.CreateObjConversionAsync(
                sourceTaskId,
                faceLimit,
                withMaterials,
                documentSessionId,
                operationId,
                confirmExternalCost,
                cancellationToken),
            cancellationToken);

    [McpServerTool(
        Name = "tripo_import_obj_task",
        Title = "Import a Tripo OBJ task",
        ReadOnly = false,
        Destructive = false,
        Idempotent = true,
        OpenWorld = true,
        UseStructuredContent = true)]
    [Description(
        "Download, validate, and import one successful OBJ conversion task into the " +
        "exact active Rhino or Revit document. This stage creates no Tripo task.")]
    public Task<ObjTaskImportReceipt> ImportObjTaskAsync(
        [Description("Successful Tripo OBJ conversion task ID.")] string conversionTaskId,
        [Description("Object or DirectShape name containing 1 to 128 characters.")] string name,
        [Description(
            "Exact document-session UUID returned by tripo_host_context.")] string documentSessionId,
        [Description(
            "Caller-generated UUID. Reuse the exact UUID when retrying this import.")] string operationId,
        [Description(
            "Import target: native resolves to instance in Rhino and family in Revit; " +
            "or pass mesh, instance, or family explicitly. Hosts reject modes they do " +
            "not support.")] string importMode = "native",
        [Description(
            "When true, apply the baked OBJ/MTL diffuse materials and textures; this " +
            "fails closed when the converted bundle has no MTL. When false, import " +
            "geometry only.")] bool applyMaterials = false,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(
            () => _workflow.ImportObjTaskAsync(
                conversionTaskId,
                name,
                documentSessionId,
                operationId,
                importMode,
                applyMaterials,
                cancellationToken),
            cancellationToken);

    private static async Task<T> InvokeAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ArgumentException exception)
        {
            throw new McpException(exception.Message, exception);
        }
        catch (TripoWorkflowException exception)
        {
            throw new McpException(exception.Message, exception);
        }
        catch (TripoApiException exception)
        {
            throw new McpException(
                BuildApiMessage(exception),
                exception);
        }
        catch (Tripo.Bridge.BridgeCallException exception)
        {
            throw new McpException(
                $"Host bridge error ({exception.Code}): {exception.Message}",
                exception);
        }
    }

    private static string BuildApiMessage(TripoApiException exception)
    {
        string requestSuffix = string.IsNullOrWhiteSpace(exception.RequestId)
            ? string.Empty
            : $" Request ID: {exception.RequestId}.";
        return exception.Message + requestSuffix;
    }
}
