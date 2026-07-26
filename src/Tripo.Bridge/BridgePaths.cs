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
        EnsurePrivateDirectory(path);
        return path;
    }

    public static string GetStagingDirectory()
    {
        string path = Path.Combine(GetRootDirectory(), "staging");
        EnsurePrivateDirectory(path);
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
        EnsurePrivateDirectory(path);
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
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken)
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

    private static void EnsurePrivateNonReparseDirectory(string path)
    {
        DirectoryInfo info = new(path);
        if (info.Exists &&
            (info.LinkTarget is not null ||
             (info.Attributes & FileAttributes.ReparsePoint) != 0))
        {
            throw new InvalidOperationException(
                "The image-transfer data directory cannot be a symbolic link " +
                "or reparse point.");
        }

        Directory.CreateDirectory(path);
        info.Refresh();
        if (info.LinkTarget is not null ||
            (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                "The image-transfer data directory cannot be a symbolic link " +
                "or reparse point.");
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
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

    [GeneratedRegex("^[a-z][a-z0-9]{1,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex HostNameRegex();
}
