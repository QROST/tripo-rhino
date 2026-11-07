using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;

namespace Tripo.Mcp.Tests;

public sealed class RhinoPluginMetadataTests
{
    private static readonly Guid ExpectedPluginId =
        new("626D164C-A15C-45DE-B8A1-0718C81305DE");

    [Fact]
    public void BuiltRhinoPluginCarriesStableAssemblyGuid()
    {
        using FileStream stream = File.OpenRead(GetPluginPath());
        using PEReader pe = new(stream);
        MetadataReader metadata = pe.GetMetadataReader();
        CustomAttribute attribute = Assert.Single(
            GetAttributes(metadata, "System.Runtime.InteropServices", "GuidAttribute"));
        BlobReader value = metadata.GetBlobReader(attribute.Value);

        Assert.Equal((ushort)1, value.ReadUInt16());
        string? guid = value.ReadSerializedString();
        Assert.NotNull(guid);
        Assert.Equal(ExpectedPluginId, Guid.Parse(guid));
        Assert.Equal((ushort)0, value.ReadUInt16());
        Assert.Equal(0, value.RemainingBytes);
    }

    [Theory]
    // RhinoCommon 8.32 DescriptionType: Organization = 0, WebSite = 4.
    // Keep this test independent of the host SDK at runtime.
    [InlineData(0, "CuriousArc")]
    [InlineData(4, "https://github.com/QROST/tripo-rhino")]
    public void BuiltRhinoPluginCarriesPublisherMetadata(int descriptionType, string expected)
    {
        using FileStream stream = File.OpenRead(GetPluginPath());
        using PEReader pe = new(stream);
        MetadataReader metadata = pe.GetMetadataReader();
        List<(int Type, string? Text)> descriptions = [];
        foreach (CustomAttribute attribute in GetAttributes(
            metadata, "Rhino.PlugIns", "PlugInDescriptionAttribute"))
        {
            BlobReader value = metadata.GetBlobReader(attribute.Value);
            Assert.Equal((ushort)1, value.ReadUInt16());
            descriptions.Add((value.ReadInt32(), value.ReadSerializedString()));
            Assert.Equal((ushort)0, value.ReadUInt16());
            Assert.Equal(0, value.RemainingBytes);
        }

        Assert.Equal(expected, Assert.Single(descriptions, item => item.Type == descriptionType).Text);
    }

    // Inspect the compiled PE without loading the plug-in or resolving RhinoCommon.
    // Runtime reflection can resolve unrelated custom attribute types even when
    // requesting only GuidAttribute, so it is unsuitable for this portable test.
    private static IEnumerable<CustomAttribute> GetAttributes(
        MetadataReader metadata, string typeNamespace, string typeName)
    {
        foreach (CustomAttributeHandle handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
        {
            CustomAttribute attribute = metadata.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference)
            {
                continue;
            }

            MemberReference constructor = metadata.GetMemberReference(
                (MemberReferenceHandle)attribute.Constructor);
            if (constructor.Parent.Kind != HandleKind.TypeReference)
            {
                continue;
            }

            TypeReference type = metadata.GetTypeReference((TypeReferenceHandle)constructor.Parent);
            if (metadata.StringComparer.Equals(type.Namespace, typeNamespace) &&
                metadata.StringComparer.Equals(type.Name, typeName))
            {
                yield return attribute;
            }
        }
    }

    private static string GetPluginPath()
    {
        string root = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                ".."));
        string configuration = Directory
            .GetParent(
                AppContext.BaseDirectory.TrimEnd(
                    Path.DirectorySeparatorChar))!
            .Name;
        string pluginPath = Path.Combine(
            root,
            "src",
            "Tripo.Rhino",
            "bin",
            configuration,
            "net7.0",
            "Tripo.Rhino.rhp");
        Assert.True(
            File.Exists(pluginPath),
            $"The Rhino plug-in build output was not found at {pluginPath}.");

        return pluginPath;
    }
}
