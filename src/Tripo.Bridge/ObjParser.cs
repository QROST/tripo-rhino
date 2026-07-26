using System.Globalization;
using System.Text;

namespace Tripo.Bridge;

public static class ObjParser
{
    private const int ReadChunkChars = 8192;
    private const int MaximumMaterials = 64;

    public static async Task<ParsedObjMesh> ParseAsync(
        Stream stream,
        long byteLength,
        ObjParseLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ObjParseLimits effectiveLimits = limits ?? ObjParseLimits.Default;
        if (byteLength <= 0 || byteLength > effectiveLimits.MaximumBytes)
        {
            throw new BridgeCallException(
                "artifact_size_invalid",
                $"OBJ byte length must be between 1 and {effectiveLimits.MaximumBytes}.");
        }

        ParseState state = new();
        using BoundedReadStream boundedStream = new(stream, byteLength);
        using StreamReader reader = new(
            boundedStream,
            detectEncodingFromByteOrderMarks: true,
            leaveOpen: true);

        char[] readBuffer = new char[ReadChunkChars];
        StringBuilder currentLine = new();
        int charsRead;
        while ((charsRead = await reader.ReadAsync(readBuffer, cancellationToken)
                   .ConfigureAwait(false)) > 0)
        {
            for (int i = 0; i < charsRead; i++)
            {
                char character = readBuffer[i];
                if (character == '\n')
                {
                    ProcessLine(
                        StripTrailingCarriageReturn(currentLine),
                        state,
                        effectiveLimits);
                    currentLine.Clear();
                    continue;
                }

                currentLine.Append(character);

                // A trailing \r may still be stripped as part of a \r\n terminator,
                // so it must not count against the line-length cap yet.
                int trailingCarriageReturn = character == '\r' ? 1 : 0;
                if (currentLine.Length - trailingCarriageReturn > effectiveLimits.MaximumLineCharacters)
                {
                    throw new BridgeCallException(
                        "obj_line_too_long",
                        "The OBJ contains a line that exceeds the parser limit.");
                }
            }
        }

        if (currentLine.Length > 0)
        {
            ProcessLine(
                StripTrailingCarriageReturn(currentLine),
                state,
                effectiveLimits);
        }

        if (boundedStream.BytesRead != byteLength)
        {
            throw new BridgeCallException(
                "artifact_length_mismatch",
                "The OBJ stream byte length did not match the declared length.");
        }

        if (state.Positions.Count == 0 || state.FaceMaterialSlots.Count == 0)
        {
            throw new BridgeCallException(
                "obj_empty",
                "The OBJ did not contain importable vertices and faces.");
        }

        return new ParsedObjMesh(
            state.Positions,
            state.Uvs,
            state.Corners,
            state.FaceMaterialSlots,
            state.MaterialNames);
    }

    private static void ProcessLine(
        string line,
        ParseState state,
        ObjParseLimits limits)
    {
        string trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] == '#')
        {
            return;
        }

        if (StartsWithKeyword(trimmed, "vt"))
        {
            ParseUv(trimmed[2..], state, limits);
            return;
        }

        if (StartsWithKeyword(trimmed, "v"))
        {
            ParseVertex(trimmed[1..], state, limits);
            return;
        }

        if (StartsWithKeyword(trimmed, "f"))
        {
            ParseFace(trimmed[1..], state, limits);
            return;
        }

        if (StartsWithKeyword(trimmed, "usemtl"))
        {
            ParseUseMtl(trimmed[6..], state);
        }

        // vn, mtllib, vp, g, o, s, l and every other keyword are ignored deliberately:
        // normals are recomputed by both hosts, and the MTL entry is known from the bundle.
    }

    private static bool StartsWithKeyword(string line, string keyword)
    {
        if (!line.StartsWith(keyword, StringComparison.Ordinal))
        {
            return false;
        }

        if (line.Length == keyword.Length)
        {
            return true;
        }

        char next = line[keyword.Length];
        return next == ' ' || next == '\t';
    }

    private static string StripTrailingCarriageReturn(StringBuilder line)
    {
        int length = line.Length;
        if (length > 0 && line[length - 1] == '\r')
        {
            length--;
        }

        return line.ToString(0, length);
    }

    private static void ParseVertex(
        string content,
        ParseState state,
        ObjParseLimits limits)
    {
        if (state.Positions.Count >= limits.MaximumVertices)
        {
            throw new BridgeCallException(
                "obj_vertex_limit",
                $"The OBJ exceeds the {limits.MaximumVertices} vertex limit.");
        }

        string[] parts = content.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 ||
            !TryParseFinite(parts[0], out double x) ||
            !TryParseFinite(parts[1], out double y) ||
            !TryParseFinite(parts[2], out double z))
        {
            throw new BridgeCallException(
                "obj_vertex_invalid",
                "The OBJ contains an invalid vertex.");
        }

        state.Positions.Add(new MeshPoint3(x, y, z));
    }

    private static void ParseUv(string content, ParseState state, ObjParseLimits limits)
    {
        if (state.Uvs.Count >= limits.MaximumUvs)
        {
            throw new BridgeCallException(
                "obj_uv_limit",
                $"The OBJ exceeds the {limits.MaximumUvs} texture coordinate limit.");
        }

        string[] parts = content.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 ||
            !TryParseFinite(parts[0], out double u) ||
            !TryParseFinite(parts[1], out double v))
        {
            throw new BridgeCallException(
                "obj_uv_invalid",
                "The OBJ contains an invalid texture coordinate.");
        }

        state.Uvs.Add(new MeshPoint2(u, v));
    }

    private static void ParseUseMtl(string content, ParseState state)
    {
        string name = content.Trim();
        if (name.Length == 0)
        {
            return;
        }

        if (!state.SlotByName.TryGetValue(name, out int slot))
        {
            if (state.MaterialNames.Count >= MaximumMaterials)
            {
                throw new BridgeCallException(
                    "obj_material_limit",
                    $"The OBJ exceeds the {MaximumMaterials} material limit.");
            }

            slot = state.MaterialNames.Count;
            state.MaterialNames.Add(name);
            state.SlotByName[name] = slot;
        }

        state.CurrentSlot = slot;
    }

    private static void ParseFace(
        string content,
        ParseState state,
        ObjParseLimits limits)
    {
        string[] rawParts = content.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries);
        string[] parts = rawParts.TakeWhile(part => !part.StartsWith('#')).ToArray();
        if (parts.Length is < 3 or > 4)
        {
            throw new BridgeCallException(
                "obj_polygon_unsupported",
                "Only triangle and quad OBJ faces are supported in the first release.");
        }

        ObjFaceCorner[] corners = new ObjFaceCorner[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            corners[i] = ParseFaceCorner(
                parts[i],
                state.Positions.Count,
                state.Uvs.Count);
        }

        AddFace(corners[0], corners[1], corners[2], state, limits);
        if (parts.Length == 4)
        {
            AddFace(corners[0], corners[2], corners[3], state, limits);
        }
    }

    private static void AddFace(
        ObjFaceCorner a,
        ObjFaceCorner b,
        ObjFaceCorner c,
        ParseState state,
        ObjParseLimits limits)
    {
        if (state.FaceMaterialSlots.Count >= limits.MaximumTriangles)
        {
            throw new BridgeCallException(
                "obj_triangle_limit",
                $"The OBJ exceeds the {limits.MaximumTriangles} triangle limit.");
        }

        state.Corners.Add(a);
        state.Corners.Add(b);
        state.Corners.Add(c);
        state.FaceMaterialSlots.Add(state.CurrentSlot);
    }

    private static ObjFaceCorner ParseFaceCorner(
        string token,
        int positionCount,
        int uvCount)
    {
        int firstSlash = token.IndexOf('/');
        ReadOnlySpan<char> positionText = firstSlash >= 0
            ? token.AsSpan(0, firstSlash)
            : token.AsSpan();
        int position = ResolvePositionIndex(positionText, positionCount);

        int uv = -1;
        if (firstSlash >= 0)
        {
            ReadOnlySpan<char> rest = token.AsSpan(firstSlash + 1);
            int secondSlash = rest.IndexOf('/');
            ReadOnlySpan<char> uvText = secondSlash >= 0
                ? rest[..secondSlash]
                : rest;
            uv = ResolveUvIndex(uvText, uvCount);
        }

        return new ObjFaceCorner(position, uv);
    }

    private static int ResolvePositionIndex(
        ReadOnlySpan<char> indexText,
        int positionCount)
    {
        if (!int.TryParse(
                indexText,
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out int rawIndex) ||
            rawIndex == 0)
        {
            throw new BridgeCallException(
                "obj_index_invalid",
                "The OBJ contains an invalid face index.");
        }

        int index = rawIndex > 0 ? rawIndex - 1 : positionCount + rawIndex;
        if (index < 0 || index >= positionCount)
        {
            throw new BridgeCallException(
                "obj_index_out_of_range",
                "The OBJ contains a face index outside the available vertices.");
        }

        return index;
    }

    private static int ResolveUvIndex(
        ReadOnlySpan<char> indexText,
        int uvCount)
    {
        if (indexText.IsEmpty ||
            !int.TryParse(
                indexText,
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out int rawIndex) ||
            rawIndex == 0)
        {
            return -1;
        }

        int index = rawIndex > 0 ? rawIndex - 1 : uvCount + rawIndex;
        return index < 0 || index >= uvCount ? -1 : index;
    }

    private static bool TryParseFinite(string value, out double parsed)
    {
        bool success = double.TryParse(
            value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out parsed);
        return success && double.IsFinite(parsed);
    }

    private sealed class ParseState
    {
        public List<MeshPoint3> Positions { get; } = [];

        public List<MeshPoint2> Uvs { get; } = [];

        public List<ObjFaceCorner> Corners { get; } = [];

        public List<int> FaceMaterialSlots { get; } = [];

        public List<string> MaterialNames { get; } = [];

        public Dictionary<string, int> SlotByName { get; } =
            new(StringComparer.Ordinal);

        public int CurrentSlot { get; set; } = -1;
    }
}
