namespace Tripo.Bridge;

public static class HostControlConstants
{
    public const string ProtocolVersion = "3";
    public const string Channel = "host-control";
    public const int MaximumMessageBytes = 64 * 1024;
    public const int MaximumConcurrentClients = 4;
    public static readonly TimeSpan DefaultCallTimeout = TimeSpan.FromMinutes(10);

    public const string HealthMethod = "control.health";
    public const string ShutdownMethod = "control.shutdown";
    public const string CredentialStatusMethod = "credential.status";
    public const string CredentialSetMethod = "credential.set";
    public const string CredentialClearMethod = "credential.clear";
    public const string HostContextMethod = "workflow.host_context";
    public const string CreateTextTaskMethod = "workflow.create_text_task";
    public const string CreateImageTaskMethod = "workflow.create_image_task";
    public const string TaskStatusMethod = "workflow.task_status";
    public const string OperationStatusMethod = "workflow.operation_status";
    public const string CreateObjConversionMethod =
        "workflow.create_obj_conversion";
    public const string ImportGenerationGlbMethod =
        "workflow.import_generation_glb";
    public const string ImportObjTaskMethod = "workflow.import_obj_task";
    public const string StageObjTaskMethod = "workflow.stage_obj_task";
    public const string CredentialInvalidError = "credential_invalid";
    public const string CredentialRejectedError = "credential_rejected";
    public const string RequestRejectedState = "request_rejected";

    public static IReadOnlyList<string> WorkflowCapabilities { get; } =
    [
        HealthMethod,
        ShutdownMethod,
        CredentialStatusMethod,
        CredentialSetMethod,
        CredentialClearMethod,
        HostContextMethod,
        CreateTextTaskMethod,
        CreateImageTaskMethod,
        TaskStatusMethod,
        OperationStatusMethod,
        CreateObjConversionMethod,
        ImportObjTaskMethod,
        StageObjTaskMethod,
    ];

    public static IReadOnlyList<string> RhinoWorkflowCapabilities { get; } =
    [
        .. WorkflowCapabilities,
        ImportGenerationGlbMethod,
    ];

    public static IReadOnlyList<string> GetWorkflowCapabilities(string host) =>
        string.Equals(
            BridgePaths.NormalizeHost(host),
            "rhino",
            StringComparison.Ordinal)
            ? RhinoWorkflowCapabilities
            : WorkflowCapabilities;
}
