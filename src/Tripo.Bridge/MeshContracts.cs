namespace Tripo.Bridge;

public readonly record struct MeshPoint3(double X, double Y, double Z);

public readonly record struct MeshPoint2(double U, double V);

public readonly record struct MeshTriangle(
    int A,
    int B,
    int C,
    int MaterialSlot);

public readonly record struct ObjFaceCorner(int Position, int Uv);

public sealed record ObjMaterial(
    string Name,
    int? DiffuseArgb,
    string? DiffuseTextureRelativePath);

public sealed record ParsedObjMesh(
    IReadOnlyList<MeshPoint3> Positions,
    IReadOnlyList<MeshPoint2> Uvs,
    IReadOnlyList<ObjFaceCorner> Corners,
    IReadOnlyList<int> FaceMaterialSlots,
    IReadOnlyList<string> MaterialNames);

public sealed record PreparedMaterial(
    string Name,
    int? DiffuseArgb,
    string? DiffuseTextureAbsolutePath);

public sealed record PreparedMesh(
    IReadOnlyList<MeshPoint3> VerticesInMeters,
    IReadOnlyList<MeshPoint2> Uvs,
    IReadOnlyList<MeshTriangle> Triangles,
    IReadOnlyList<PreparedMaterial> Materials,
    int RejectedTriangleCount);

public sealed record ObjParseLimits(
    long MaximumBytes,
    int MaximumVertices,
    int MaximumUvs,
    int MaximumTriangles,
    int MaximumLineCharacters)
{
    public static ObjParseLimits Default { get; } = new(
        BridgeConstants.MaximumArtifactBytes,
        BridgeConstants.MaximumVertices,
        BridgeConstants.MaximumVertices,
        BridgeConstants.MaximumTriangles,
        1024 * 1024);
}
