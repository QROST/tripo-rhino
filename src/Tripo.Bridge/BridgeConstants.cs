namespace Tripo.Bridge;

public static class BridgeConstants
{
    public const string ProtocolVersion = "2";
    public const int MaximumMessageBytes = 64 * 1024;
    public const long MaximumImageTransferBytes = 20_000_000;
    public const long MaximumArtifactBytes = 128L * 1024 * 1024;
    public const long MaximumGlbArtifactBytes = 64L * 1024 * 1024;
    public const int MaximumBundleFiles = 32;
    public const long MaximumBundleBytes = 256L * 1024 * 1024;
    public const int MaximumVertices = 250_000;
    public const int MaximumTriangles = 500_000;
    public const int MaximumConcurrentClients = 4;
    public static readonly TimeSpan DefaultCallTimeout = TimeSpan.FromMinutes(2);

    public const string ContextMethod = "host.context";
    public const string ImportMeshMethod = "host.import_mesh";
    public const string ImportGlbMethod = "host.import_glb";
    public const string MutationStateUncertainError =
        "mutation_state_uncertain";
}
