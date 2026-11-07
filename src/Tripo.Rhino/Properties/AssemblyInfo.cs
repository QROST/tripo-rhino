using System.Runtime.InteropServices;
using Rhino.PlugIns;

// RhinoCommon initializes PlugIn.Id from the assembly-level GuidAttribute.
// Keep this identifier stable for the lifetime of the Rhino plug-in.
[assembly: Guid("626D164C-A15C-45DE-B8A1-0718C81305DE")]
[assembly: PlugInDescription(DescriptionType.Organization, "CuriousArc")]
[assembly: PlugInDescription(DescriptionType.WebSite, "https://github.com/QROST/tripo-rhino")]
