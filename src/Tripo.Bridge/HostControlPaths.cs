using System.Security.Cryptography;
using System.Text.Json;

namespace Tripo.Bridge;

public static class HostControlPaths
{
    public static string GetDiscoveryDirectory()
    {
        string path = Path.Combine(BridgePaths.GetRootDirectory(), "controls");
        BridgePaths.EnsurePrivateDirectory(path);
        return path;
    }

    public static string GetDescriptorPath(string host, int hostProcessId)
    {
        string normalizedHost = BridgePaths.NormalizeHost(host);
        ValidateProcessId(hostProcessId);
        return Path.Combine(
            GetDiscoveryDirectory(),
            $"{normalizedHost}-{hostProcessId}.json");
    }

    public static string GetInstanceLockPath(string host, int hostProcessId)
    {
        string normalizedHost = BridgePaths.NormalizeHost(host);
        ValidateProcessId(hostProcessId);
        return Path.Combine(
            GetDiscoveryDirectory(),
            $"{normalizedHost}-{hostProcessId}.lock");
    }

    public static async Task WriteDescriptorAsync(
        HostControlSessionDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        string path = GetDescriptorPath(descriptor.Host, descriptor.HostProcessId);
        string temporaryPath =
            path + "." +
            Convert.ToHexString(RandomNumberGenerator.GetBytes(8)) +
            ".tmp";
        string json = JsonSerializer.Serialize(descriptor, BridgeJson.Options);

        try
        {
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken)
                .ConfigureAwait(false);
            BridgePaths.SetPrivateFileMode(temporaryPath);
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            BridgePaths.TryDelete(temporaryPath);
        }
    }

    public static void DeleteDescriptorIfOwned(
        HostControlSessionDescriptor descriptor)
    {
        string path = GetDescriptorPath(descriptor.Host, descriptor.HostProcessId);
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            HostControlSessionDescriptor? current =
                JsonSerializer.Deserialize<HostControlSessionDescriptor>(
                    File.ReadAllText(path),
                    BridgeJson.Options);
            if (current is not null &&
                string.Equals(
                    current.SessionId,
                    descriptor.SessionId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    current.SessionToken,
                    descriptor.SessionToken,
                    StringComparison.Ordinal))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup during sidecar shutdown.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup during sidecar shutdown.
        }
        catch (JsonException)
        {
            // A corrupt or replaced descriptor is not ours to delete.
        }
    }

    private static void ValidateProcessId(int processId)
    {
        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(processId),
                "The host process ID must be positive.");
        }
    }
}
