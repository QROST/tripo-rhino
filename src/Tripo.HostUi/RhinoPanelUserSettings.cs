using System.Security.Cryptography;
using System.Text.Json;

namespace Tripo.HostUi;

internal sealed record RhinoPanelUserSettings(
    int FaceLimit = 20_000,
    bool WithMaterials = true,
    string ObjectName = "Tripo Model")
{
    internal const int CurrentSchemaVersion = 1;
    internal const int DefaultFaceLimit = 20_000;
    internal const string DefaultObjectName = "Tripo Model";
    internal const int MaximumSettingsBytes = 16 * 1024;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    internal static string GetSettingsPath() =>
        Path.Combine(
            Tripo.Bridge.BridgePaths.GetRootDirectory(),
            "ui-settings",
            "rhino-panel.json");

    internal static RhinoPanelUserSettings Load(string? path = null)
    {
        try
        {
            string resolvedPath = Path.GetFullPath(path ?? GetSettingsPath());
            if (!File.Exists(resolvedPath))
            {
                return new RhinoPanelUserSettings();
            }

            string? directory = Path.GetDirectoryName(resolvedPath);
            if (string.IsNullOrWhiteSpace(directory) ||
                HasLinkedDirectoryComponent(directory))
            {
                return new RhinoPanelUserSettings();
            }

            FileInfo file = new(resolvedPath);
            file.Refresh();
            if (!file.Exists ||
                file.LinkTarget is not null ||
                (file.Attributes & FileAttributes.ReparsePoint) != 0 ||
                file.Length <= 0 ||
                file.Length > MaximumSettingsBytes)
            {
                return new RhinoPanelUserSettings();
            }

            byte[] json;
            using (FileStream stream = new(
                       resolvedPath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read | FileShare.Delete,
                       bufferSize: 4096,
                       FileOptions.SequentialScan))
            {
                long length = stream.Length;
                if (length <= 0 || length > MaximumSettingsBytes)
                {
                    return new RhinoPanelUserSettings();
                }

                json = new byte[checked((int)length)];
                int offset = 0;
                while (offset < json.Length)
                {
                    int read = stream.Read(json, offset, json.Length - offset);
                    if (read == 0)
                    {
                        return new RhinoPanelUserSettings();
                    }

                    offset += read;
                }

                if (stream.ReadByte() != -1)
                {
                    return new RhinoPanelUserSettings();
                }
            }

            RhinoPanelUserSettings? settings =
                JsonSerializer.Deserialize<RhinoPanelUserSettings>(
                    json,
                    JsonOptions);
            return settings?.SchemaVersion == CurrentSchemaVersion
                ? settings.Normalize()
                : new RhinoPanelUserSettings();
        }
        catch
        {
            return new RhinoPanelUserSettings();
        }
    }

    internal static bool TryNormalizeObjectName(
        string? value,
        out string normalized)
    {
        normalized = string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim();
        return normalized.Length is >= 1 and <= 128;
    }

    internal void Save(string? path = null)
    {
        string resolvedPath = path ?? GetSettingsPath();
        string? directory = Path.GetDirectoryName(resolvedPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException(
                "The Rhino panel settings path has no parent directory.");
        }

        Tripo.Bridge.BridgePaths.EnsurePrivateNonReparseDirectory(directory);
        string temporaryPath =
            resolvedPath + "." +
            Convert.ToHexString(RandomNumberGenerator.GetBytes(8)) +
            ".tmp";
        try
        {
            string json = JsonSerializer.Serialize(Normalize(), JsonOptions);
            File.WriteAllText(temporaryPath, json);
            Tripo.Bridge.BridgePaths.SetPrivateFileMode(temporaryPath);
            File.Move(temporaryPath, resolvedPath, overwrite: true);
        }
        finally
        {
            Tripo.Bridge.BridgePaths.TryDelete(temporaryPath);
        }
    }

    private RhinoPanelUserSettings Normalize()
    {
        int faceLimit = FaceLimit is >= 500 and <= 200_000
            ? FaceLimit
            : DefaultFaceLimit;
        if (!TryNormalizeObjectName(ObjectName, out string objectName))
        {
            objectName = DefaultObjectName;
        }

        return this with
        {
            SchemaVersion = CurrentSchemaVersion,
            FaceLimit = faceLimit,
            ObjectName = objectName,
        };
    }

    private static bool HasLinkedDirectoryComponent(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string root = Path.GetPathRoot(fullPath) ??
            throw new InvalidOperationException(
                "The Rhino panel settings directory has no filesystem root.");
        string current = root;
        foreach (string segment in fullPath[root.Length..].Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            DirectoryInfo component = new(current);
            component.Refresh();
            if (!component.Exists ||
                component.LinkTarget is not null ||
                (component.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
        }

        return false;
    }
}
