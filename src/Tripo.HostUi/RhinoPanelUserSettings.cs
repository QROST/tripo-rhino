using System.Security.Cryptography;
using System.Text.Json;

namespace Tripo.HostUi;

internal sealed record RhinoPanelUserSettings(
    int FaceLimit = RhinoPanelFaceLimitPolicy.Default,
    bool WithMaterials = true,
    string ObjectName = "Tripo Model")
{
    internal const int CurrentSchemaVersion = 1;
    internal const int DefaultFaceLimit = RhinoPanelFaceLimitPolicy.Default;
    internal const string DefaultObjectName = "Tripo Model";
    internal const int MaximumSettingsBytes = 16 * 1024;

    private const int SaveContentionAttempts = 8;
    private const int SaveContentionMaxDelayMilliseconds = 25;

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
            MoveWithContentionRetry(temporaryPath, resolvedPath);
        }
        finally
        {
            Tripo.Bridge.BridgePaths.TryDelete(temporaryPath);
        }
    }

    // File.Move across an existing file occasionally surfaces
    // UnauthorizedAccessException on Windows when a concurrent reader or an
    // antivirus scanner still holds the destination for an instant. The
    // write-then-move contract is already atomic; this retries the transient
    // contention only, without weakening any of the security validation.
    private static void MoveWithContentionRetry(
        string sourcePath,
        string destinationPath)
    {
        int delay = 1;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(sourcePath, destinationPath, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < SaveContentionAttempts)
            {
                Thread.Sleep(delay);
                delay = Math.Min(delay * 2, SaveContentionMaxDelayMilliseconds);
            }
            catch (UnauthorizedAccessException) when (attempt < SaveContentionAttempts)
            {
                Thread.Sleep(delay);
                delay = Math.Min(delay * 2, SaveContentionMaxDelayMilliseconds);
            }
        }
    }

    private RhinoPanelUserSettings Normalize()
    {
        int faceLimit = RhinoPanelFaceLimitPolicy.Clamp(FaceLimit);
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
