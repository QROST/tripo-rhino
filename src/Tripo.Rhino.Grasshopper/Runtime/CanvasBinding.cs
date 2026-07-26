using Grasshopper.Kernel;

namespace Tripo.Rhino.Grasshopper.Runtime;

public sealed record CanvasBinding(
    Guid GrasshopperDocumentId,
    ulong GrasshopperRuntimeId,
    uint RhinoDocumentRuntimeSerialNumber,
    global::Rhino.UnitSystem RhinoUnitSystem,
    string DocumentSessionId)
{
    public static CanvasBinding Capture(GH_Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (global::Grasshopper.Instances.RunningHeadless)
        {
            throw new InvalidOperationException(
                "Paid Tripo canvas actions are disabled in headless Grasshopper. " +
                "Use the MCP workflow instead.");
        }

        if (document.RunningAsCommand() is not null)
        {
            throw new InvalidOperationException(
                "Paid Tripo canvas actions are disabled in Grasshopper Player " +
                "and compiled-command execution. Use the interactive canvas or MCP.");
        }

        global::Rhino.RhinoDoc rhinoDocument =
            document.RhinoDocument ??
            throw new InvalidOperationException(
                "This Grasshopper document is not associated with a Rhino document.");
        string documentSessionId =
            global::Tripo.Rhino.TripoRhinoPlugin.GetDocumentSessionId(
                rhinoDocument);
        return new CanvasBinding(
            document.DocumentID,
            document.RuntimeID,
            rhinoDocument.RuntimeSerialNumber,
            rhinoDocument.ModelUnitSystem,
            documentSessionId);
    }

    public bool Matches(GH_Document? document)
    {
        if (document is null ||
            document.DocumentID != GrasshopperDocumentId ||
            document.RuntimeID != GrasshopperRuntimeId)
        {
            return false;
        }

        global::Rhino.RhinoDoc? rhinoDocument = document.RhinoDocument;
        return rhinoDocument is not null &&
               rhinoDocument.RuntimeSerialNumber ==
               RhinoDocumentRuntimeSerialNumber &&
               rhinoDocument.ModelUnitSystem == RhinoUnitSystem &&
               string.Equals(
                   global::Tripo.Rhino.TripoRhinoPlugin.GetDocumentSessionId(
                       rhinoDocument),
                   DocumentSessionId,
                   StringComparison.Ordinal);
    }
}
