using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Xunit;

namespace Tripo.Mcp.Tests;

public sealed class McpToolSchemaTests
{
    [Fact]
    public void ToolDiscoveryExposesOnlyTheNineRecoverableTools()
    {
        ServiceCollection services = new();
        services
            .AddMcpServer()
            .WithToolsFromAssembly(typeof(Tripo.Mcp.TripoTools).Assembly);
        using ServiceProvider provider = services.BuildServiceProvider();

        McpServerTool[] tools = provider
            .GetServices<McpServerTool>()
            .OrderBy(tool => tool.ProtocolTool.Name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "tripo_create_image_task",
                "tripo_create_obj_conversion",
                "tripo_create_text_task",
                "tripo_host_context",
                "tripo_import_generation_glb",
                "tripo_import_obj_task",
                "tripo_operation_status",
                "tripo_stage_local_image",
                "tripo_task_status",
            ],
            tools.Select(tool => tool.ProtocolTool.Name));
        McpServerTool create = tools.Single(
            tool => tool.ProtocolTool.Name == "tripo_create_text_task");
        string schema = create.ProtocolTool.InputSchema.ToString();
        Assert.Contains("confirmExternalCost", schema, StringComparison.Ordinal);
        Assert.Contains("documentSessionId", schema, StringComparison.Ordinal);
        Assert.Contains("operationId", schema, StringComparison.Ordinal);
        Assert.Contains("withMaterials", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("CancellationToken", schema, StringComparison.Ordinal);
        Assert.True(create.ProtocolTool.Annotations?.IdempotentHint);

        McpServerTool conversion = tools.Single(
            tool => tool.ProtocolTool.Name == "tripo_create_obj_conversion");
        Assert.Contains(
            "withMaterials",
            conversion.ProtocolTool.InputSchema.ToString(),
            StringComparison.Ordinal);

        McpServerTool createImage = tools.Single(
            tool => tool.ProtocolTool.Name == "tripo_create_image_task");
        string imageSchema = createImage.ProtocolTool.InputSchema.ToString();
        Assert.Contains("transferId", imageSchema, StringComparison.Ordinal);
        Assert.Contains("sha256", imageSchema, StringComparison.Ordinal);
        Assert.Contains("byteLength", imageSchema, StringComparison.Ordinal);
        Assert.Contains("mediaType", imageSchema, StringComparison.Ordinal);
        Assert.Contains(
            "confirmExternalCost",
            imageSchema,
            StringComparison.Ordinal);
        Assert.Contains(
            "documentSessionId",
            imageSchema,
            StringComparison.Ordinal);
        Assert.Contains("operationId", imageSchema, StringComparison.Ordinal);
        Assert.True(createImage.ProtocolTool.Annotations?.IdempotentHint);

        McpServerTool stageImage = tools.Single(
            tool => tool.ProtocolTool.Name == "tripo_stage_local_image");
        string stageImageSchema =
            stageImage.ProtocolTool.InputSchema.ToString();
        Assert.Contains(
            "localImagePath",
            stageImageSchema,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "confirmExternalCost",
            stageImageSchema,
            StringComparison.Ordinal);
        Assert.False(stageImage.ProtocolTool.Annotations?.OpenWorldHint);

        McpServerTool operationStatus = tools.Single(
            tool => tool.ProtocolTool.Name == "tripo_operation_status");
        Assert.True(operationStatus.ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.False(operationStatus.ProtocolTool.Annotations?.OpenWorldHint);

        McpServerTool import = tools.Single(
            tool => tool.ProtocolTool.Name == "tripo_import_obj_task");
        string importSchema = import.ProtocolTool.InputSchema.ToString();
        Assert.Contains("operationId", importSchema, StringComparison.Ordinal);
        Assert.Contains("importMode", importSchema, StringComparison.Ordinal);
        Assert.Contains("applyMaterials", importSchema, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "confirmExternalCost",
            importSchema,
            StringComparison.Ordinal);

        McpServerTool glbImport = tools.Single(
            tool => tool.ProtocolTool.Name == "tripo_import_generation_glb");
        string glbSchema = glbImport.ProtocolTool.InputSchema.ToString();
        Assert.Contains("generationTaskId", glbSchema, StringComparison.Ordinal);
        Assert.Contains("operationId", glbSchema, StringComparison.Ordinal);
        Assert.Contains("applyMaterials", glbSchema, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "confirmExternalCost",
            glbSchema,
            StringComparison.Ordinal);
        Assert.True(glbImport.ProtocolTool.Annotations?.IdempotentHint);
    }
}
