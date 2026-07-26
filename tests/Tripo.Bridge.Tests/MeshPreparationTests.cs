using Xunit;

namespace Tripo.Bridge.Tests;

public sealed class MeshPreparationTests
{
    private static readonly IReadOnlyList<Tripo.Bridge.ObjMaterial> NoMaterials = [];
    private static readonly IReadOnlyList<Tripo.Bridge.StagedBundleEntry> NoEntries = [];

    [Fact]
    public void PrepareConvertsYUpMetersToZUp()
    {
        Tripo.Bridge.ParsedObjMesh mesh = SingleTriangle(
            new Tripo.Bridge.MeshPoint3(0, 0, 0),
            new Tripo.Bridge.MeshPoint3(1, 0, 0),
            new Tripo.Bridge.MeshPoint3(0, 2, 3));

        Tripo.Bridge.PreparedMesh prepared = PrepareGeometry(mesh, "meters", "Y", "right");

        Assert.Equal(new Tripo.Bridge.MeshPoint3(0, -3, 2), prepared.VerticesInMeters[2]);
        Assert.Equal(new Tripo.Bridge.MeshTriangle(0, 1, 2, -1), prepared.Triangles[0]);
        Assert.Empty(prepared.Uvs);
        Assert.Empty(prepared.Materials);
    }

    [Fact]
    public void PrepareConvertsMillimetersAndReversesLeftHandedWinding()
    {
        Tripo.Bridge.ParsedObjMesh mesh = SingleTriangle(
            new Tripo.Bridge.MeshPoint3(0, 0, 0),
            new Tripo.Bridge.MeshPoint3(1000, 0, 0),
            new Tripo.Bridge.MeshPoint3(0, 1000, 0));

        Tripo.Bridge.PreparedMesh prepared =
            PrepareGeometry(mesh, "millimeters", "Z", "left");

        Assert.Equal(new Tripo.Bridge.MeshPoint3(-1, 0, 0), prepared.VerticesInMeters[1]);
        Assert.Equal(new Tripo.Bridge.MeshTriangle(0, 2, 1, -1), prepared.Triangles[0]);
    }

    [Fact]
    public void PrepareRejectsTooManyDegenerateTriangles()
    {
        Tripo.Bridge.ParsedObjMesh mesh = SingleTriangle(
            new Tripo.Bridge.MeshPoint3(0, 0, 0),
            new Tripo.Bridge.MeshPoint3(1, 0, 0),
            new Tripo.Bridge.MeshPoint3(2, 0, 0));

        Tripo.Bridge.BridgeCallException exception =
            Assert.Throws<Tripo.Bridge.BridgeCallException>(
                () => PrepareGeometry(mesh, "meters", "Z", "right"));

        Assert.Equal("mesh_degenerate", exception.Code);
    }

    [Fact]
    public void PrepareWeldsUniquePositionUvPairsIntoDistinctVertices()
    {
        Tripo.Bridge.ParsedObjMesh mesh = new(
            [
                new Tripo.Bridge.MeshPoint3(0, 0, 0),
                new Tripo.Bridge.MeshPoint3(1, 0, 0),
                new Tripo.Bridge.MeshPoint3(0, 1, 0),
                new Tripo.Bridge.MeshPoint3(1, 1, 0),
            ],
            [
                new Tripo.Bridge.MeshPoint2(0, 0),
                new Tripo.Bridge.MeshPoint2(1, 0),
                new Tripo.Bridge.MeshPoint2(0, 1),
                new Tripo.Bridge.MeshPoint2(0.5, 0.5),
                new Tripo.Bridge.MeshPoint2(1, 1),
            ],
            [
                new Tripo.Bridge.ObjFaceCorner(0, 0),
                new Tripo.Bridge.ObjFaceCorner(1, 1),
                new Tripo.Bridge.ObjFaceCorner(2, 2),
                new Tripo.Bridge.ObjFaceCorner(0, 3),
                new Tripo.Bridge.ObjFaceCorner(2, 2),
                new Tripo.Bridge.ObjFaceCorner(3, 4),
            ],
            [0, 0],
            ["mat"]);

        Tripo.Bridge.PreparedMesh prepared = PrepareGeometry(mesh, "meters", "Z", "right");

        Assert.Equal(5, prepared.VerticesInMeters.Count);
        Assert.Equal(5, prepared.Uvs.Count);
        Assert.Equal(new Tripo.Bridge.MeshTriangle(0, 1, 2, 0), prepared.Triangles[0]);
        Assert.Equal(new Tripo.Bridge.MeshTriangle(3, 2, 4, 0), prepared.Triangles[1]);
        Assert.Equal(new Tripo.Bridge.MeshPoint2(0.5, 0.5), prepared.Uvs[3]);
    }

    [Fact]
    public void PrepareWithoutUvsPassesVerticesThrough()
    {
        Tripo.Bridge.ParsedObjMesh mesh = new(
            [
                new Tripo.Bridge.MeshPoint3(0, 0, 0),
                new Tripo.Bridge.MeshPoint3(1, 0, 0),
                new Tripo.Bridge.MeshPoint3(0, 1, 0),
                new Tripo.Bridge.MeshPoint3(1, 1, 0),
            ],
            [],
            [
                new Tripo.Bridge.ObjFaceCorner(0, -1),
                new Tripo.Bridge.ObjFaceCorner(1, -1),
                new Tripo.Bridge.ObjFaceCorner(2, -1),
                new Tripo.Bridge.ObjFaceCorner(1, -1),
                new Tripo.Bridge.ObjFaceCorner(3, -1),
                new Tripo.Bridge.ObjFaceCorner(2, -1),
            ],
            [-1, -1],
            []);

        Tripo.Bridge.PreparedMesh prepared = PrepareGeometry(mesh, "meters", "Z", "right");

        Assert.Equal(4, prepared.VerticesInMeters.Count);
        Assert.Empty(prepared.Uvs);
        Assert.Equal(2, prepared.Triangles.Count);
    }

    [Fact]
    public void PrepareLeftHandedSwapMovesWholeCornersIncludingUvs()
    {
        Tripo.Bridge.ParsedObjMesh mesh = new(
            [
                new Tripo.Bridge.MeshPoint3(0, 0, 0),
                new Tripo.Bridge.MeshPoint3(1, 0, 0),
                new Tripo.Bridge.MeshPoint3(0, 1, 0),
            ],
            [
                new Tripo.Bridge.MeshPoint2(0, 0),
                new Tripo.Bridge.MeshPoint2(1, 0),
                new Tripo.Bridge.MeshPoint2(0, 1),
            ],
            [
                new Tripo.Bridge.ObjFaceCorner(0, 0),
                new Tripo.Bridge.ObjFaceCorner(1, 1),
                new Tripo.Bridge.ObjFaceCorner(2, 2),
            ],
            [-1],
            []);

        Tripo.Bridge.PreparedMesh prepared = PrepareGeometry(mesh, "meters", "Z", "left");

        Tripo.Bridge.MeshTriangle triangle = prepared.Triangles[0];
        Assert.Equal(new Tripo.Bridge.MeshTriangle(0, 2, 1, -1), triangle);
        // The swapped second/third corners carry corner c2's / c1's UV and position.
        Assert.Equal(new Tripo.Bridge.MeshPoint2(0, 1), prepared.Uvs[triangle.B]);
        Assert.Equal(new Tripo.Bridge.MeshPoint2(1, 0), prepared.Uvs[triangle.C]);
        Assert.Equal(
            new Tripo.Bridge.MeshPoint3(0, 1, 0),
            prepared.VerticesInMeters[triangle.B]);
    }

    [Fact]
    public void PrepareResolvesTextureToContainedAbsolutePath()
    {
        string bundleRoot = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "tripo-prep-" + Guid.NewGuid().ToString("N"));
        Tripo.Bridge.ParsedObjMesh mesh = SingleTriangle(
            new Tripo.Bridge.MeshPoint3(0, 0, 0),
            new Tripo.Bridge.MeshPoint3(1, 0, 0),
            new Tripo.Bridge.MeshPoint3(0, 1, 0),
            slot: 0,
            "mat");
        IReadOnlyList<Tripo.Bridge.ObjMaterial> materials =
        [
            new Tripo.Bridge.ObjMaterial("mat", unchecked((int)0xFFFF0000), "wood.png"),
        ];
        IReadOnlyList<Tripo.Bridge.StagedBundleEntry> entries =
        [
            new Tripo.Bridge.StagedBundleEntry("textures/wood.png", new string('a', 64), 10),
        ];

        Tripo.Bridge.PreparedMesh prepared = Tripo.Bridge.MeshPreparation.Prepare(
            mesh,
            "meters",
            "Z",
            "right",
            bundleRoot,
            materials,
            entries,
            applyMaterials: true);

        Tripo.Bridge.PreparedMaterial material = Assert.Single(prepared.Materials);
        Assert.Equal("mat", material.Name);
        Assert.Equal(unchecked((int)0xFFFF0000), material.DiffuseArgb);
        Assert.Equal(
            System.IO.Path.GetFullPath(
                System.IO.Path.Combine(bundleRoot, "textures/wood.png")),
            material.DiffuseTextureAbsolutePath);
    }

    [Fact]
    public void PrepareKeepsColorOnlyMaterialWithoutTexture()
    {
        Tripo.Bridge.ParsedObjMesh mesh = SingleTriangle(
            new Tripo.Bridge.MeshPoint3(0, 0, 0),
            new Tripo.Bridge.MeshPoint3(1, 0, 0),
            new Tripo.Bridge.MeshPoint3(0, 1, 0),
            slot: 0,
            "mat");
        IReadOnlyList<Tripo.Bridge.ObjMaterial> materials =
        [
            new Tripo.Bridge.ObjMaterial("mat", unchecked((int)0xFF00FF00), null),
        ];

        Tripo.Bridge.PreparedMesh prepared = Tripo.Bridge.MeshPreparation.Prepare(
            mesh,
            "meters",
            "Z",
            "right",
            System.IO.Path.GetTempPath(),
            materials,
            NoEntries,
            applyMaterials: true);

        Tripo.Bridge.PreparedMaterial material = Assert.Single(prepared.Materials);
        Assert.Equal(unchecked((int)0xFF00FF00), material.DiffuseArgb);
        Assert.Null(material.DiffuseTextureAbsolutePath);
    }

    [Fact]
    public void PrepareRejectsTextureMissingFromBundle()
    {
        Tripo.Bridge.ParsedObjMesh mesh = SingleTriangle(
            new Tripo.Bridge.MeshPoint3(0, 0, 0),
            new Tripo.Bridge.MeshPoint3(1, 0, 0),
            new Tripo.Bridge.MeshPoint3(0, 1, 0),
            slot: 0,
            "mat");
        IReadOnlyList<Tripo.Bridge.ObjMaterial> materials =
        [
            new Tripo.Bridge.ObjMaterial("mat", null, "missing.png"),
        ];
        IReadOnlyList<Tripo.Bridge.StagedBundleEntry> entries =
        [
            new Tripo.Bridge.StagedBundleEntry("wood.png", new string('a', 64), 10),
        ];

        Tripo.Bridge.BridgeCallException exception =
            Assert.Throws<Tripo.Bridge.BridgeCallException>(
                () => Tripo.Bridge.MeshPreparation.Prepare(
                    mesh,
                    "meters",
                    "Z",
                    "right",
                    System.IO.Path.GetTempPath(),
                    materials,
                    entries,
                    applyMaterials: true));

        Assert.Equal("mtl_invalid", exception.Code);
    }

    [Fact]
    public void PrepareRejectsTextureEscapingBundleRoot()
    {
        Tripo.Bridge.ParsedObjMesh mesh = SingleTriangle(
            new Tripo.Bridge.MeshPoint3(0, 0, 0),
            new Tripo.Bridge.MeshPoint3(1, 0, 0),
            new Tripo.Bridge.MeshPoint3(0, 1, 0),
            slot: 0,
            "mat");
        IReadOnlyList<Tripo.Bridge.ObjMaterial> materials =
        [
            new Tripo.Bridge.ObjMaterial("mat", null, "../evil.png"),
        ];
        IReadOnlyList<Tripo.Bridge.StagedBundleEntry> entries =
        [
            new Tripo.Bridge.StagedBundleEntry("evil.png", new string('a', 64), 10),
        ];

        Tripo.Bridge.BridgeCallException exception =
            Assert.Throws<Tripo.Bridge.BridgeCallException>(
                () => Tripo.Bridge.MeshPreparation.Prepare(
                    mesh,
                    "meters",
                    "Z",
                    "right",
                    System.IO.Path.GetTempPath(),
                    materials,
                    entries,
                    applyMaterials: true));

        Assert.Equal("mtl_invalid", exception.Code);
    }

    private static Tripo.Bridge.PreparedMesh PrepareGeometry(
        Tripo.Bridge.ParsedObjMesh mesh,
        string sourceUnit,
        string upAxis,
        string handedness) =>
        Tripo.Bridge.MeshPreparation.Prepare(
            mesh,
            sourceUnit,
            upAxis,
            handedness,
            System.IO.Path.GetTempPath(),
            NoMaterials,
            NoEntries,
            applyMaterials: false);

    private static Tripo.Bridge.ParsedObjMesh SingleTriangle(
        Tripo.Bridge.MeshPoint3 a,
        Tripo.Bridge.MeshPoint3 b,
        Tripo.Bridge.MeshPoint3 c,
        int slot = -1,
        string? materialName = null) =>
        new(
            [a, b, c],
            [],
            [
                new Tripo.Bridge.ObjFaceCorner(0, -1),
                new Tripo.Bridge.ObjFaceCorner(1, -1),
                new Tripo.Bridge.ObjFaceCorner(2, -1),
            ],
            [slot],
            materialName is null ? [] : [materialName]);
}
