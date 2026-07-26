using System.Text;
using Xunit;

namespace Tripo.Bridge.Tests;

public sealed class ObjParserTests
{
    [Fact]
    public async Task ParseAsyncTriangulatesQuadAndSupportsNegativeIndices()
    {
        const string obj = """
            # square
            v 0 0 0
            v 1 0 0
            v 1 1 0
            v 0 1 0
            f -4/-1 -3/-1 -2/-1 -1/-1 # two triangles
            """;

        Tripo.Bridge.ParsedObjMesh mesh = await ParseAsync(obj);

        Assert.Equal(4, mesh.Positions.Count);
        Assert.Equal(
            [
                new Tripo.Bridge.ObjFaceCorner(0, -1),
                new Tripo.Bridge.ObjFaceCorner(1, -1),
                new Tripo.Bridge.ObjFaceCorner(2, -1),
                new Tripo.Bridge.ObjFaceCorner(0, -1),
                new Tripo.Bridge.ObjFaceCorner(2, -1),
                new Tripo.Bridge.ObjFaceCorner(3, -1),
            ],
            mesh.Corners);
        Assert.Equal([-1, -1], mesh.FaceMaterialSlots);
        Assert.Empty(mesh.Uvs);
    }

    [Fact]
    public async Task ParseAsyncParsesTextureCoordinatesAndCornerUvs()
    {
        const string obj = """
            v 0 0 0
            v 1 0 0
            v 0 1 0
            vt 0 0
            vt 1 0
            vt 0 1
            vn 0 0 1
            f 1/1/1 2/2/1 3/3/1
            """;

        Tripo.Bridge.ParsedObjMesh mesh = await ParseAsync(obj);

        Assert.Equal(
            [
                new Tripo.Bridge.MeshPoint2(0, 0),
                new Tripo.Bridge.MeshPoint2(1, 0),
                new Tripo.Bridge.MeshPoint2(0, 1),
            ],
            mesh.Uvs);
        Assert.Equal(
            [
                new Tripo.Bridge.ObjFaceCorner(0, 0),
                new Tripo.Bridge.ObjFaceCorner(1, 1),
                new Tripo.Bridge.ObjFaceCorner(2, 2),
            ],
            mesh.Corners);
    }

    [Fact]
    public async Task ParseAsyncMapsOutOfRangeCornerUvToMinusOne()
    {
        const string obj = """
            v 0 0 0
            v 1 0 0
            v 0 1 0
            vt 0 0
            f 1/9 2/1 3
            """;

        Tripo.Bridge.ParsedObjMesh mesh = await ParseAsync(obj);

        Assert.Equal(
            [
                new Tripo.Bridge.ObjFaceCorner(0, -1),
                new Tripo.Bridge.ObjFaceCorner(1, 0),
                new Tripo.Bridge.ObjFaceCorner(2, -1),
            ],
            mesh.Corners);
    }

    [Fact]
    public async Task ParseAsyncTracksUsemtlSlotsInOrderOfFirstUse()
    {
        const string obj = """
            v 0 0 0
            v 1 0 0
            v 0 1 0
            v 1 1 0
            f 1 2 3
            usemtl red
            f 1 2 4
            usemtl blue
            f 2 3 4
            usemtl red
            f 1 3 4
            """;

        Tripo.Bridge.ParsedObjMesh mesh = await ParseAsync(obj);

        Assert.Equal(["red", "blue"], mesh.MaterialNames);
        Assert.Equal([-1, 0, 1, 0], mesh.FaceMaterialSlots);
    }

    [Fact]
    public async Task ParseAsyncFansQuadPreservingCornerUvsAndMaterialSlot()
    {
        const string obj = """
            v 0 0 0
            v 1 0 0
            v 1 1 0
            v 0 1 0
            vt 0 0
            vt 1 0
            vt 1 1
            vt 0 1
            usemtl mat
            f 1/1 2/2 3/3 4/4
            """;

        Tripo.Bridge.ParsedObjMesh mesh = await ParseAsync(obj);

        Assert.Equal(["mat"], mesh.MaterialNames);
        Assert.Equal([0, 0], mesh.FaceMaterialSlots);
        Assert.Equal(
            [
                new Tripo.Bridge.ObjFaceCorner(0, 0),
                new Tripo.Bridge.ObjFaceCorner(1, 1),
                new Tripo.Bridge.ObjFaceCorner(2, 2),
                new Tripo.Bridge.ObjFaceCorner(0, 0),
                new Tripo.Bridge.ObjFaceCorner(2, 2),
                new Tripo.Bridge.ObjFaceCorner(3, 3),
            ],
            mesh.Corners);
    }

    [Theory]
    [InlineData("v NaN 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3", "obj_vertex_invalid")]
    [InlineData("v 0 0 0\nv 1 0 0\nv 0 1 0\nvt 0\nf 1/1 2/1 3/1", "obj_uv_invalid")]
    [InlineData("v 0 0 0\nv 1 0 0\nv 0 1 0\nf 0 2 3", "obj_index_invalid")]
    [InlineData("v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 4", "obj_index_out_of_range")]
    [InlineData(
        "v 0 0 0\nv 1 0 0\nv 1 1 0\nv 0 1 0\nv -1 0 0\nf 1 2 3 4 5",
        "obj_polygon_unsupported")]
    public async Task ParseAsyncRejectsMalformedGeometry(
        string obj,
        string expectedCode)
    {
        Tripo.Bridge.BridgeCallException exception =
            await Assert.ThrowsAsync<Tripo.Bridge.BridgeCallException>(
                () => ParseAsync(obj));

        Assert.Equal(expectedCode, exception.Code);
    }

    [Fact]
    public async Task ParseAsyncRejectsActualBytesBeyondDeclaration()
    {
        const string obj = "v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n";
        byte[] bytes = Encoding.UTF8.GetBytes(obj);
        await using MemoryStream stream = new(bytes);

        Tripo.Bridge.BridgeCallException exception =
            await Assert.ThrowsAsync<Tripo.Bridge.BridgeCallException>(
                () => Tripo.Bridge.ObjParser.ParseAsync(
                    stream,
                    byteLength: 1));

        Assert.Equal("artifact_length_mismatch", exception.Code);
    }

    [Fact]
    public async Task ParseAsyncEnforcesConfiguredLimits()
    {
        const string obj = "v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n";
        byte[] bytes = Encoding.UTF8.GetBytes(obj);
        await using MemoryStream stream = new(bytes);
        Tripo.Bridge.ObjParseLimits limits = new(
            bytes.Length,
            MaximumVertices: 2,
            MaximumUvs: 10,
            MaximumTriangles: 1,
            MaximumLineCharacters: 100);

        Tripo.Bridge.BridgeCallException exception =
            await Assert.ThrowsAsync<Tripo.Bridge.BridgeCallException>(
                () => Tripo.Bridge.ObjParser.ParseAsync(
                    stream,
                    bytes.Length,
                    limits));

        Assert.Equal("obj_vertex_limit", exception.Code);
    }

    [Fact]
    public async Task ParseAsyncEnforcesTheConfiguredUvLimit()
    {
        const string obj = "v 0 0 0\nv 1 0 0\nv 0 1 0\nvt 0 0\nvt 1 0\nf 1 2 3\n";
        byte[] bytes = Encoding.UTF8.GetBytes(obj);
        await using MemoryStream stream = new(bytes);
        Tripo.Bridge.ObjParseLimits limits = new(
            bytes.Length,
            MaximumVertices: 10,
            MaximumUvs: 1,
            MaximumTriangles: 10,
            MaximumLineCharacters: 100);

        Tripo.Bridge.BridgeCallException exception =
            await Assert.ThrowsAsync<Tripo.Bridge.BridgeCallException>(
                () => Tripo.Bridge.ObjParser.ParseAsync(
                    stream,
                    bytes.Length,
                    limits));

        Assert.Equal("obj_uv_limit", exception.Code);
    }

    [Fact]
    public async Task ParseAsyncRejectsMoreThanSixtyFourDistinctMaterials()
    {
        StringBuilder builder = new();
        builder.Append("v 0 0 0\nv 1 0 0\nv 0 1 0\n");
        for (int index = 0; index <= 64; index++)
        {
            builder.Append("usemtl material_").Append(index).Append('\n');
        }

        Tripo.Bridge.BridgeCallException exception =
            await Assert.ThrowsAsync<Tripo.Bridge.BridgeCallException>(
                () => ParseAsync(builder.ToString()));

        Assert.Equal("obj_material_limit", exception.Code);
    }

    [Fact]
    public async Task ParseAsyncAcceptsLineJustUnderTheConfiguredLimit()
    {
        const int limit = 40;
        const string vertexPrefix = "v 0 0 0";
        string paddedVertexLine = vertexPrefix +
            new string(' ', limit - vertexPrefix.Length - 1);
        string obj = string.Join(
            "\n",
            paddedVertexLine,
            "v 1 0 0",
            "v 0 1 0",
            "f 1 2 3");
        byte[] bytes = Encoding.UTF8.GetBytes(obj);
        await using MemoryStream stream = new(bytes);
        Tripo.Bridge.ObjParseLimits limits = new(
            bytes.Length,
            MaximumVertices: 10,
            MaximumUvs: 10,
            MaximumTriangles: 10,
            MaximumLineCharacters: limit);

        Tripo.Bridge.ParsedObjMesh mesh = await Tripo.Bridge.ObjParser.ParseAsync(
            stream,
            bytes.Length,
            limits);

        Assert.Equal(3, mesh.Positions.Count);
        Assert.Single(mesh.FaceMaterialSlots);
    }

    [Fact]
    public async Task ParseAsyncRejectsLineOverTheConfiguredLimit()
    {
        const int limit = 40;
        const string vertexPrefix = "v 0 0 0";
        string oversizedVertexLine = vertexPrefix +
            new string(' ', limit - vertexPrefix.Length + 1);
        string obj = string.Join(
            "\n",
            oversizedVertexLine,
            "v 1 0 0",
            "v 0 1 0",
            "f 1 2 3");
        byte[] bytes = Encoding.UTF8.GetBytes(obj);
        await using MemoryStream stream = new(bytes);
        Tripo.Bridge.ObjParseLimits limits = new(
            bytes.Length,
            MaximumVertices: 10,
            MaximumUvs: 10,
            MaximumTriangles: 10,
            MaximumLineCharacters: limit);

        Tripo.Bridge.BridgeCallException exception =
            await Assert.ThrowsAsync<Tripo.Bridge.BridgeCallException>(
                () => Tripo.Bridge.ObjParser.ParseAsync(
                    stream,
                    bytes.Length,
                    limits));

        Assert.Equal("obj_line_too_long", exception.Code);
    }

    [Fact]
    public async Task ParseAsyncHandlesCrlfLineEndingsAndATrailingLineWithoutNewline()
    {
        const string lfObj = "v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3";
        const string crlfObj = "v 0 0 0\r\nv 1 0 0\r\nv 0 1 0\r\nf 1 2 3";

        Tripo.Bridge.ParsedObjMesh lfMesh = await ParseAsync(lfObj);
        Tripo.Bridge.ParsedObjMesh crlfMesh = await ParseAsync(crlfObj);

        Assert.Equal(lfMesh.Positions, crlfMesh.Positions);
        Assert.Equal(lfMesh.Corners, crlfMesh.Corners);
    }

    private static async Task<Tripo.Bridge.ParsedObjMesh> ParseAsync(string obj)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(obj);
        await using MemoryStream stream = new(bytes);
        return await Tripo.Bridge.ObjParser.ParseAsync(stream, bytes.Length);
    }
}
