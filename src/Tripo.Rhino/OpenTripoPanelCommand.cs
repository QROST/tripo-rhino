using System.Runtime.InteropServices;
using Rhino.Commands;

namespace Tripo.Rhino;

[Guid("6740B29B-4F43-481C-9D3F-1D73C9DCB814")]
public sealed class OpenTripoPanelCommand : Command
{
    public override string EnglishName => "TripoPanel";

    protected override Result RunCommand(
        global::Rhino.RhinoDoc doc,
        RunMode mode)
    {
        global::Rhino.UI.Panels.OpenPanel(typeof(TripoRhinoPanel));
        return Result.Success;
    }
}
