using System.Globalization;
using System.Text;

namespace Tripo.Bridge;

public static class MtlParser
{
    private const int ReadChunkChars = 8192;
    private const int MaximumMaterials = 64;
    private const int MaximumLines = 1024;

    public static async Task<IReadOnlyList<ObjMaterial>> ParseAsync(
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
                "mtl_invalid",
                $"MTL byte length must be between 1 and {effectiveLimits.MaximumBytes}.");
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
                    ProcessLine(StripTrailingCarriageReturn(currentLine), state);
                    currentLine.Clear();
                    continue;
                }

                currentLine.Append(character);

                int trailingCarriageReturn = character == '\r' ? 1 : 0;
                if (currentLine.Length - trailingCarriageReturn > effectiveLimits.MaximumLineCharacters)
                {
                    throw new BridgeCallException(
                        "mtl_invalid",
                        "The MTL contains a line that exceeds the parser limit.");
                }
            }
        }

        if (currentLine.Length > 0)
        {
            ProcessLine(StripTrailingCarriageReturn(currentLine), state);
        }

        if (boundedStream.BytesRead != byteLength)
        {
            throw new BridgeCallException(
                "mtl_invalid",
                "The MTL stream byte length did not match the declared length.");
        }

        state.FinalizeCurrent();
        return state.Materials;
    }

    private static void ProcessLine(string line, ParseState state)
    {
        string trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] == '#')
        {
            return;
        }

        state.LineCount++;
        if (state.LineCount > MaximumLines)
        {
            throw new BridgeCallException(
                "mtl_invalid",
                $"The MTL exceeds the {MaximumLines} line limit.");
        }

        if (StartsWithKeyword(trimmed, "newmtl"))
        {
            ParseNewMaterial(trimmed[6..], state);
            return;
        }

        if (StartsWithKeyword(trimmed, "Kd"))
        {
            ParseDiffuseColor(trimmed[2..], state);
            return;
        }

        if (StartsWithKeyword(trimmed, "map_Kd"))
        {
            ParseDiffuseTexture(trimmed[6..], state);
            return;
        }

        if (StartsWithKeyword(trimmed, "Tr"))
        {
            ParseTransparency(trimmed[2..], state);
            return;
        }

        if (StartsWithKeyword(trimmed, "d"))
        {
            ParseOpacity(trimmed[1..], state);
        }

        // All other keywords (Ka, Ks, Ns, illum, map_Bump, ...) are ignored deliberately.
    }

    private static void ParseNewMaterial(string content, ParseState state)
    {
        string name = content.Trim();
        if (name.Length == 0)
        {
            throw new BridgeCallException(
                "mtl_invalid",
                "The MTL contains a newmtl declaration without a name.");
        }

        state.FinalizeCurrent();
        if (state.Materials.Count >= MaximumMaterials)
        {
            throw new BridgeCallException(
                "mtl_invalid",
                $"The MTL exceeds the {MaximumMaterials} material limit.");
        }

        state.Current = new MaterialBuilder(name);
    }

    private static void ParseDiffuseColor(string content, ParseState state)
    {
        if (state.Current is null)
        {
            return;
        }

        string[] parts = content.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 ||
            !TryParseFinite(parts[0], out double r) ||
            !TryParseFinite(parts[1], out double g) ||
            !TryParseFinite(parts[2], out double b))
        {
            throw new BridgeCallException(
                "mtl_invalid",
                "The MTL contains an invalid Kd diffuse color.");
        }

        state.Current.Rgb =
            (ToByte(r) << 16) |
            (ToByte(g) << 8) |
            ToByte(b);
    }

    private static void ParseOpacity(string content, ParseState state)
    {
        if (state.Current is null)
        {
            return;
        }

        if (!TryParseSingleValue(content, out double opacity))
        {
            throw new BridgeCallException(
                "mtl_invalid",
                "The MTL contains an invalid d opacity value.");
        }

        state.Current.Alpha = ToByte(opacity);
    }

    private static void ParseTransparency(string content, ParseState state)
    {
        if (state.Current is null)
        {
            return;
        }

        if (!TryParseSingleValue(content, out double transparency))
        {
            throw new BridgeCallException(
                "mtl_invalid",
                "The MTL contains an invalid Tr transparency value.");
        }

        state.Current.Alpha = ToByte(1.0 - transparency);
    }

    private static void ParseDiffuseTexture(string content, ParseState state)
    {
        if (state.Current is null)
        {
            return;
        }

        string[] parts = content.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            throw new BridgeCallException(
                "mtl_invalid",
                "The MTL contains a map_Kd declaration without a filename.");
        }

        state.Current.Texture = parts[^1];
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

    private static bool TryParseSingleValue(string content, out double value)
    {
        string[] parts = content.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 1)
        {
            value = 0;
            return false;
        }

        return TryParseFinite(parts[0], out value);
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

    private static int ToByte(double value01)
    {
        double clamped = Math.Clamp(value01, 0.0, 1.0);
        return (int)Math.Round(clamped * 255.0, MidpointRounding.AwayFromZero);
    }

    private sealed class MaterialBuilder(string name)
    {
        public string Name { get; } = name;

        public int? Rgb { get; set; }

        public int Alpha { get; set; } = 255;

        public string? Texture { get; set; }

        public ObjMaterial Build() =>
            new(
                Name,
                Rgb.HasValue ? ((Alpha & 0xFF) << 24) | (Rgb.Value & 0xFFFFFF) : null,
                Texture);
    }

    private sealed class ParseState
    {
        public List<ObjMaterial> Materials { get; } = [];

        public MaterialBuilder? Current { get; set; }

        public int LineCount { get; set; }

        public void FinalizeCurrent()
        {
            if (Current is null)
            {
                return;
            }

            Materials.Add(Current.Build());
            Current = null;
        }
    }
}
