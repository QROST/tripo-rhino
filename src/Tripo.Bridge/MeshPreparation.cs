namespace Tripo.Bridge;

public static class MeshPreparation
{
    private const double MaximumCoordinateMeters = 1_000_000;
    private const double MinimumAreaSquared = 1e-24;

    public static PreparedMesh Prepare(
        ParsedObjMesh mesh,
        string sourceUnit,
        string upAxis,
        string handedness,
        string bundleRoot,
        IReadOnlyList<ObjMaterial> materials,
        IReadOnlyList<StagedBundleEntry> entries,
        bool applyMaterials)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(materials);
        ArgumentNullException.ThrowIfNull(entries);
        double unitScale = ParseUnitScale(sourceUnit);
        string normalizedAxis = upAxis.Trim().ToUpperInvariant();
        string normalizedHandedness = handedness.Trim().ToLowerInvariant();
        if (normalizedAxis is not "Y" and not "Z")
        {
            throw new BridgeCallException(
                "coordinate_frame_invalid",
                "upAxis must be Y or Z.");
        }

        if (normalizedHandedness is not "right" and not "left")
        {
            throw new BridgeCallException(
                "coordinate_frame_invalid",
                "handedness must be right or left.");
        }

        List<MeshPoint3> transformedPositions = new(mesh.Positions.Count);
        foreach (MeshPoint3 point in mesh.Positions)
        {
            MeshPoint3 transformed = TransformPoint(
                point,
                unitScale,
                normalizedAxis,
                normalizedHandedness);
            if (Math.Abs(transformed.X) > MaximumCoordinateMeters ||
                Math.Abs(transformed.Y) > MaximumCoordinateMeters ||
                Math.Abs(transformed.Z) > MaximumCoordinateMeters)
            {
                throw new BridgeCallException(
                    "coordinate_limit",
                    "The transformed mesh exceeds the coordinate magnitude limit.");
            }

            transformedPositions.Add(transformed);
        }

        bool leftHanded = normalizedHandedness == "left";
        bool hasUvs = mesh.Uvs.Count > 0;
        int faceCount = mesh.FaceMaterialSlots.Count;

        List<MeshPoint3> vertices;
        List<MeshPoint2> uvs;
        List<MeshTriangle> triangles = new(faceCount);
        int rejected = 0;

        if (!hasUvs)
        {
            vertices = transformedPositions;
            uvs = [];
            for (int face = 0; face < faceCount; face++)
            {
                int i0 = mesh.Corners[(face * 3) + 0].Position;
                int i1 = mesh.Corners[(face * 3) + 1].Position;
                int i2 = mesh.Corners[(face * 3) + 2].Position;
                EmitTriangle(
                    i0,
                    i1,
                    i2,
                    mesh.FaceMaterialSlots[face],
                    leftHanded,
                    vertices,
                    triangles,
                    ref rejected);
            }
        }
        else
        {
            vertices = [];
            uvs = [];
            Dictionary<(int Position, int Uv), int> welded = new(faceCount * 3);
            for (int face = 0; face < faceCount; face++)
            {
                int i0 = WeldCorner(mesh.Corners[(face * 3) + 0], transformedPositions, mesh.Uvs, vertices, uvs, welded);
                int i1 = WeldCorner(mesh.Corners[(face * 3) + 1], transformedPositions, mesh.Uvs, vertices, uvs, welded);
                int i2 = WeldCorner(mesh.Corners[(face * 3) + 2], transformedPositions, mesh.Uvs, vertices, uvs, welded);
                EmitTriangle(
                    i0,
                    i1,
                    i2,
                    mesh.FaceMaterialSlots[face],
                    leftHanded,
                    vertices,
                    triangles,
                    ref rejected);
            }
        }

        int allowedRejected = Math.Max(1, Math.Min(1000, faceCount / 100));
        if (triangles.Count == 0 || rejected > allowedRejected)
        {
            throw new BridgeCallException(
                "mesh_degenerate",
                "The mesh contains too many degenerate triangles.");
        }

        IReadOnlyList<PreparedMaterial> preparedMaterials;
        if (applyMaterials)
        {
            preparedMaterials = ResolveMaterials(
                mesh.MaterialNames,
                materials,
                entries,
                bundleRoot);
        }
        else
        {
            preparedMaterials = [];
        }

        return new PreparedMesh(vertices, uvs, triangles, preparedMaterials, rejected);
    }

    private static int WeldCorner(
        ObjFaceCorner corner,
        List<MeshPoint3> transformedPositions,
        IReadOnlyList<MeshPoint2> sourceUvs,
        List<MeshPoint3> vertices,
        List<MeshPoint2> uvs,
        Dictionary<(int Position, int Uv), int> welded)
    {
        (int Position, int Uv) key = (corner.Position, corner.Uv);
        if (welded.TryGetValue(key, out int existing))
        {
            return existing;
        }

        int index = vertices.Count;
        vertices.Add(transformedPositions[corner.Position]);
        uvs.Add(corner.Uv >= 0 ? sourceUvs[corner.Uv] : new MeshPoint2(0, 0));
        welded[key] = index;
        return index;
    }

    private static void EmitTriangle(
        int i0,
        int i1,
        int i2,
        int materialSlot,
        bool leftHanded,
        List<MeshPoint3> vertices,
        List<MeshTriangle> triangles,
        ref int rejected)
    {
        // The left-handed swap moves whole corners (A,C,B), so both the position
        // and the welded UV travel together.
        (int a, int b, int c) = leftHanded ? (i0, i2, i1) : (i0, i1, i2);
        if (IsDegenerate(a, b, c, vertices))
        {
            rejected++;
            return;
        }

        triangles.Add(new MeshTriangle(a, b, c, materialSlot));
    }

    private static List<PreparedMaterial> ResolveMaterials(
        IReadOnlyList<string> materialNames,
        IReadOnlyList<ObjMaterial> materials,
        IReadOnlyList<StagedBundleEntry> entries,
        string bundleRoot)
    {
        string fullBundleRoot = Path.GetFullPath(bundleRoot);
        string rootPrefix = fullBundleRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullBundleRoot
            : fullBundleRoot + Path.DirectorySeparatorChar;
        Dictionary<string, ObjMaterial> byName = new(StringComparer.Ordinal);
        foreach (ObjMaterial material in materials)
        {
            byName.TryAdd(material.Name, material);
        }

        List<PreparedMaterial> prepared = new(materialNames.Count);
        foreach (string name in materialNames)
        {
            if (byName.TryGetValue(name, out ObjMaterial? source))
            {
                string? texture = ResolveTexture(
                    source.DiffuseTextureRelativePath,
                    entries,
                    fullBundleRoot,
                    rootPrefix);
                prepared.Add(new PreparedMaterial(name, source.DiffuseArgb, texture));
            }
            else
            {
                prepared.Add(new PreparedMaterial(name, null, null));
            }
        }

        return prepared;
    }

    private static string? ResolveTexture(
        string? reference,
        IReadOnlyList<StagedBundleEntry> entries,
        string fullBundleRoot,
        string rootPrefix)
    {
        if (reference is null)
        {
            return null;
        }

        string? matched = MatchEntry(reference, entries);
        if (matched is null)
        {
            throw new BridgeCallException(
                "mtl_invalid",
                "The MTL references a texture that is not present in the bundle.");
        }

        string absolute = Path.GetFullPath(Path.Combine(fullBundleRoot, matched));
        if (!absolute.StartsWith(rootPrefix, StringComparison.Ordinal))
        {
            throw new BridgeCallException(
                "mtl_invalid",
                "The MTL texture resolved outside the bundle directory.");
        }

        return absolute;
    }

    private static string? MatchEntry(
        string reference,
        IReadOnlyList<StagedBundleEntry> entries)
    {
        string normalized = NormalizeReference(reference);
        if (normalized.Length == 0)
        {
            return null;
        }

        foreach (StagedBundleEntry entry in entries)
        {
            if (string.Equals(
                    NormalizeReference(entry.RelativePath),
                    normalized,
                    StringComparison.Ordinal))
            {
                return entry.RelativePath;
            }
        }

        if (normalized.Contains('/'))
        {
            return null;
        }

        string? found = null;
        foreach (StagedBundleEntry entry in entries)
        {
            string entryBaseName = LastSegment(NormalizeReference(entry.RelativePath));
            if (string.Equals(entryBaseName, normalized, StringComparison.Ordinal))
            {
                if (found is not null)
                {
                    return null;
                }

                found = entry.RelativePath;
            }
        }

        return found;
    }

    private static string NormalizeReference(string value)
    {
        string forward = value.Replace('\\', '/').Trim();
        string[] segments = forward.Split('/');
        List<string> kept = new(segments.Length);
        foreach (string segment in segments)
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                return string.Empty;
            }

            kept.Add(segment);
        }

        if (forward.StartsWith('/') ||
            (kept.Count > 0 && kept[0].Contains(':', StringComparison.Ordinal)))
        {
            return string.Empty;
        }

        return string.Join('/', kept);
    }

    private static string LastSegment(string normalized)
    {
        int slash = normalized.LastIndexOf('/');
        return slash >= 0 ? normalized[(slash + 1)..] : normalized;
    }

    private static MeshPoint3 TransformPoint(
        MeshPoint3 point,
        double unitScale,
        string upAxis,
        string handedness)
    {
        MeshPoint3 zUp = upAxis == "Y"
            ? new MeshPoint3(point.X, -point.Z, point.Y)
            : point;
        double x = handedness == "left" ? -zUp.X : zUp.X;
        return new MeshPoint3(
            x * unitScale,
            zUp.Y * unitScale,
            zUp.Z * unitScale);
    }

    private static double ParseUnitScale(string sourceUnit) =>
        sourceUnit.Trim().ToLowerInvariant() switch
        {
            "meters" or "meter" or "m" => 1,
            "millimeters" or "millimeter" or "mm" => 0.001,
            "centimeters" or "centimeter" or "cm" => 0.01,
            "feet" or "foot" or "ft" => 0.3048,
            "inches" or "inch" or "in" => 0.0254,
            _ => throw new BridgeCallException(
                "source_unit_invalid",
                "sourceUnit must be meters, millimeters, centimeters, feet, or inches."),
        };

    private static bool IsDegenerate(
        int indexA,
        int indexB,
        int indexC,
        List<MeshPoint3> vertices)
    {
        if (indexA == indexB ||
            indexB == indexC ||
            indexA == indexC)
        {
            return true;
        }

        MeshPoint3 a = vertices[indexA];
        MeshPoint3 b = vertices[indexB];
        MeshPoint3 c = vertices[indexC];
        double abX = b.X - a.X;
        double abY = b.Y - a.Y;
        double abZ = b.Z - a.Z;
        double acX = c.X - a.X;
        double acY = c.Y - a.Y;
        double acZ = c.Z - a.Z;
        double crossX = (abY * acZ) - (abZ * acY);
        double crossY = (abZ * acX) - (abX * acZ);
        double crossZ = (abX * acY) - (abY * acX);
        double areaSquared =
            (crossX * crossX) +
            (crossY * crossY) +
            (crossZ * crossZ);
        return !double.IsFinite(areaSquared) || areaSquared <= MinimumAreaSquared;
    }
}
