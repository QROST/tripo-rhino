using Rhino.Geometry;

namespace Tripo.Rhino.Grasshopper.Runtime;

internal static class RhinoMeshProjector
{
    public static Mesh Project(
        Tripo.Bridge.PreparedMesh prepared,
        global::Rhino.UnitSystem targetUnits)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        double scale = global::Rhino.RhinoMath.UnitScale(
            global::Rhino.UnitSystem.Meters,
            targetUnits);
        if (!double.IsFinite(scale) || scale <= 0)
        {
            throw new InvalidOperationException(
                "Rhino returned an invalid meter-to-document unit scale.");
        }

        Mesh mesh = new();
        try
        {
            foreach (Tripo.Bridge.MeshPoint3 point in prepared.VerticesInMeters)
            {
                double x = point.X * scale;
                double y = point.Y * scale;
                double z = point.Z * scale;
                if (!double.IsFinite(x) ||
                    !double.IsFinite(y) ||
                    !double.IsFinite(z))
                {
                    throw new InvalidOperationException(
                        "The prepared mesh contains a non-finite vertex.");
                }

                mesh.Vertices.Add(x, y, z);
            }

            foreach (Tripo.Bridge.MeshTriangle triangle in prepared.Triangles)
            {
                if (triangle.A < 0 ||
                    triangle.B < 0 ||
                    triangle.C < 0 ||
                    triangle.A >= mesh.Vertices.Count ||
                    triangle.B >= mesh.Vertices.Count ||
                    triangle.C >= mesh.Vertices.Count)
                {
                    throw new InvalidOperationException(
                        "The prepared mesh contains an invalid triangle index.");
                }

                mesh.Faces.AddFace(
                    triangle.A,
                    triangle.B,
                    triangle.C);
            }

            if (prepared.Uvs.Count > 0)
            {
                if (prepared.Uvs.Count != mesh.Vertices.Count)
                {
                    throw new InvalidOperationException(
                        "The prepared mesh UV count does not match its vertex count.");
                }

                foreach (Tripo.Bridge.MeshPoint2 uv in prepared.Uvs)
                {
                    mesh.TextureCoordinates.Add(uv.U, uv.V);
                }
            }

            mesh.Normals.ComputeNormals();
            mesh.Compact();
            if (!mesh.IsValid)
            {
                throw new InvalidOperationException(
                    "Rhino rejected the projected Grasshopper mesh as invalid.");
            }

            return mesh;
        }
        catch
        {
            mesh.Dispose();
            throw;
        }
    }
}
