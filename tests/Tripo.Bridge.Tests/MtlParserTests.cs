using System.Text;
using Xunit;

namespace Tripo.Bridge.Tests;

public sealed class MtlParserTests
{
    [Fact]
    public async Task ParseAsyncReadsDiffuseColorAndTexture()
    {
        const string mtl = """
            # baked material
            newmtl material_0
            Ka 0.2 0.2 0.2
            Kd 1 0 0
            Ns 32
            map_Kd baked_diffuse.png
            """;

        IReadOnlyList<Tripo.Bridge.ObjMaterial> materials = await ParseAsync(mtl);

        Tripo.Bridge.ObjMaterial material = Assert.Single(materials);
        Assert.Equal("material_0", material.Name);
        Assert.Equal(unchecked((int)0xFFFF0000), material.DiffuseArgb);
        Assert.Equal("baked_diffuse.png", material.DiffuseTextureRelativePath);
    }

    [Fact]
    public async Task ParseAsyncAppliesOpacityAlphaFromD()
    {
        const string mtl = "newmtl m\nKd 1 0 0\nd 0.5\n";

        IReadOnlyList<Tripo.Bridge.ObjMaterial> materials = await ParseAsync(mtl);

        Assert.Equal(unchecked((int)0x80FF0000), Assert.Single(materials).DiffuseArgb);
    }

    [Fact]
    public async Task ParseAsyncAppliesTransparencyAlphaFromTr()
    {
        const string mtl = "newmtl m\nKd 0 1 0\nTr 0.25\n";

        IReadOnlyList<Tripo.Bridge.ObjMaterial> materials = await ParseAsync(mtl);

        Assert.Equal(unchecked((int)0xBF00FF00), Assert.Single(materials).DiffuseArgb);
    }

    [Fact]
    public async Task ParseAsyncUsesLastWhitespaceTokenAsTextureFilename()
    {
        const string mtl = "newmtl m\nmap_Kd -o 1 1 0 -s 2 2 2 textures/wood.png\n";

        IReadOnlyList<Tripo.Bridge.ObjMaterial> materials = await ParseAsync(mtl);

        Assert.Equal(
            "textures/wood.png",
            Assert.Single(materials).DiffuseTextureRelativePath);
    }

    [Fact]
    public async Task ParseAsyncReturnsNullArgbWhenKdAbsent()
    {
        const string mtl = "newmtl m\nmap_Kd tex.png\n";

        IReadOnlyList<Tripo.Bridge.ObjMaterial> materials = await ParseAsync(mtl);

        Tripo.Bridge.ObjMaterial material = Assert.Single(materials);
        Assert.Null(material.DiffuseArgb);
        Assert.Equal("tex.png", material.DiffuseTextureRelativePath);
    }

    [Fact]
    public async Task ParseAsyncReadsMultipleMaterials()
    {
        const string mtl = """
            newmtl a
            Kd 1 1 1
            newmtl b
            Kd 0 0 0
            """;

        IReadOnlyList<Tripo.Bridge.ObjMaterial> materials = await ParseAsync(mtl);

        Assert.Equal(2, materials.Count);
        Assert.Equal("a", materials[0].Name);
        Assert.Equal(unchecked((int)0xFFFFFFFF), materials[0].DiffuseArgb);
        Assert.Equal("b", materials[1].Name);
        Assert.Equal(unchecked((int)0xFF000000), materials[1].DiffuseArgb);
    }

    [Fact]
    public async Task ParseAsyncRejectsTooManyMaterials()
    {
        StringBuilder builder = new();
        for (int index = 0; index < 65; index++)
        {
            builder.Append("newmtl m").Append(index).Append('\n');
        }

        Tripo.Bridge.BridgeCallException exception =
            await Assert.ThrowsAsync<Tripo.Bridge.BridgeCallException>(
                () => ParseAsync(builder.ToString()));

        Assert.Equal("mtl_invalid", exception.Code);
    }

    [Fact]
    public async Task ParseAsyncRejectsTooManyLines()
    {
        StringBuilder builder = new();
        for (int index = 0; index < 1025; index++)
        {
            builder.Append("Ns 10\n");
        }

        Tripo.Bridge.BridgeCallException exception =
            await Assert.ThrowsAsync<Tripo.Bridge.BridgeCallException>(
                () => ParseAsync(builder.ToString()));

        Assert.Equal("mtl_invalid", exception.Code);
    }

    [Fact]
    public async Task ParseAsyncRejectsInvalidDiffuseColor()
    {
        const string mtl = "newmtl m\nKd red green blue\n";

        Tripo.Bridge.BridgeCallException exception =
            await Assert.ThrowsAsync<Tripo.Bridge.BridgeCallException>(
                () => ParseAsync(mtl));

        Assert.Equal("mtl_invalid", exception.Code);
    }

    private static async Task<IReadOnlyList<Tripo.Bridge.ObjMaterial>> ParseAsync(
        string mtl)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(mtl);
        await using MemoryStream stream = new(bytes);
        return await Tripo.Bridge.MtlParser.ParseAsync(stream, bytes.Length);
    }
}
