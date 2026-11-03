using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tripo.Bridge;

public static partial class BridgePaths
{
    public const string LocalDataEnvironmentVariable = "TRIPO_LOCAL_DATA_DIR";

    public static string GetRootDirectory()
    {
        string? configured = Environment.GetEnvironmentVariable(LocalDataEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (!Path.IsPathFullyQualified(configured))
            {
                throw new InvalidOperationException(
                    $"{LocalDataEnvironmentVariable} must be an absolute path.");
            }

            return Path.GetFullPath(configured);
        }

        string localData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            throw new InvalidOperationException(
                "The current user local application-data directory is unavailable.");
        }

        return Path.Combine(localData, "TripoMCP");
    }

    public static string GetDiscoveryDirectory()
    {
        string path = Path.Combine(GetRootDirectory(), "bridges");
        EnsurePrivateNonReparseDirectory(path);
        return path;
    }

    public static string GetStagingDirectory()
    {
        string root = GetRootDirectory();
        EnsurePrivateNonReparseDirectory(root);
        string path = Path.Combine(root, "staging");
        EnsurePrivateNonReparseDirectory(path);
        return path;
    }

    public static string GetImageTransferDirectory()
    {
        string root = GetRootDirectory();
        EnsurePrivateNonReparseDirectory(root);
        string path = Path.Combine(root, "image-transfers");
        EnsurePrivateNonReparseDirectory(path);
        return path;
    }

    public static string GetFamiliesDirectory()
    {
        string path = Path.Combine(GetRootDirectory(), "families");
        EnsurePrivateNonReparseDirectory(path);
        return path;
    }

    public static string GetSessionDescriptorPath(string host, int processId)
    {
        string normalizedHost = NormalizeHost(host);
        return Path.Combine(
            GetDiscoveryDirectory(),
            $"{normalizedHost}-{processId}.json");
    }

    public static string NormalizeHost(string host)
    {
        string normalized = host.Trim().ToLowerInvariant();
        if (!HostNameRegex().IsMatch(normalized))
        {
            throw new ArgumentException("Host names may contain only lowercase letters and digits.", nameof(host));
        }

        return normalized;
    }

    public static async Task WriteSessionDescriptorAsync(
        BridgeSessionDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        string path = GetSessionDescriptorPath(descriptor.Host, descriptor.ProcessId);
        string temporaryPath = path + "." + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)) + ".tmp";
        string json = JsonSerializer.Serialize(descriptor, BridgeJson.Options);

        try
        {
            await WriteNewPrivateTextAsync(temporaryPath, json, cancellationToken)
                .ConfigureAwait(false);
            SetPrivateFileMode(temporaryPath);
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    public static void DeleteSessionDescriptorIfOwned(BridgeSessionDescriptor descriptor)
    {
        string path = GetSessionDescriptorPath(descriptor.Host, descriptor.ProcessId);
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            BridgeSessionDescriptor? current = JsonSerializer.Deserialize<BridgeSessionDescriptor>(
                File.ReadAllText(path),
                BridgeJson.Options);
            if (current is not null &&
                string.Equals(current.SessionId, descriptor.SessionId, StringComparison.Ordinal) &&
                string.Equals(current.SessionToken, descriptor.SessionToken, StringComparison.Ordinal))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort stale descriptor cleanup during host shutdown.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort stale descriptor cleanup during host shutdown.
        }
        catch (JsonException)
        {
            // A corrupt or replaced descriptor is not ours to delete.
        }
    }

    public static void EnsurePrivateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute);
        }
    }

    public static void EnsurePrivateNonReparseDirectory(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string root = Path.GetPathRoot(fullPath) ??
            throw new InvalidOperationException(
                "A private data directory must have a filesystem root.");
        string current = root;
        foreach (string segment in fullPath[root.Length..].Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            DirectoryInfo component = new(current);
            component.Refresh();
            if (component.LinkTarget is not null ||
                component.Exists &&
                (component.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    "A private data directory cannot contain a symbolic link " +
                    "or reparse point.");
            }

            if (!component.Exists)
            {
                Directory.CreateDirectory(current);
                component.Refresh();
                if (component.LinkTarget is not null ||
                    (component.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException(
                        "A private data directory cannot contain a symbolic link " +
                        "or reparse point.");
                }
            }
        }

        DirectoryInfo info = new(fullPath);
        info.Refresh();
        if (!info.Exists)
        {
            throw new InvalidOperationException(
                "The private data directory could not be created.");
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                fullPath,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute);
        }
    }

    public static void SetPrivateFileMode(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite);
        }
    }

    public static void WriteNewPrivateText(string path, string contents)
    {
        using FileStream stream = OpenNewPrivateText(path);
        using StreamWriter writer = new(
            stream,
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(contents);
    }

    public static async Task WriteNewPrivateTextAsync(
        string path,
        string contents,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = OpenNewPrivateText(path);
        await using StreamWriter writer = new(
            stream,
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await writer.WriteAsync(contents.AsMemory(), cancellationToken)
            .ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Cleanup is best effort.
        }
        catch (UnauthorizedAccessException)
        {
            // Cleanup is best effort.
        }
    }

    private static FileStream OpenNewPrivateText(string path)
    {
        FileStreamOptions options = new()
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.WriteThrough,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode =
                UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return new FileStream(path, options);
    }

    [GeneratedRegex("^[a-z][a-z0-9]{1,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex HostNameRegex();
}
