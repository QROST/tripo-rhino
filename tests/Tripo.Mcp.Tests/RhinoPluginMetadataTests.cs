using System.Reflection;
using System.Runtime.InteropServices;
using Xunit;

namespace Tripo.Mcp.Tests;

public sealed class RhinoPluginMetadataTests
{
    private static readonly Guid ExpectedPluginId =
        new("626D164C-A15C-45DE-B8A1-0718C81305DE");

    [Fact]
    public void BuiltRhinoPluginCarriesStableAssemblyGuid()
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

        Assembly pluginAssembly = Assembly.LoadFile(pluginPath);
        GuidAttribute? guidAttribute =
            pluginAssembly.GetCustomAttribute<GuidAttribute>();

        Assert.NotNull(guidAttribute);
        Assert.Equal(ExpectedPluginId, Guid.Parse(guidAttribute.Value));
    }
}
