using Grasshopper.Kernel;

namespace Tripo.Rhino.Grasshopper;

public sealed class TripoGrasshopperInfo : GH_AssemblyInfo
{
    public override string Name => "Tripo";

    public override string Description =>
        "Explicit, recoverable Tripo text/image generation and Grasshopper mesh output.";

    public override Guid Id =>
        new("cc53b1d7-60d0-4f6a-a43c-bb1f4b68112d");

    public override string AuthorName => "tripo-rhino contributors";

    public override string AuthorContact =>
        "https://github.com/QROST/tripo-rhino";
}
