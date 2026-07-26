using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tripo.Bridge;

public static partial class HostControlDiscovery
{
    public static HostControlSessionDescriptor FindSession(
        string host,
        int hostProcessId)
    {
        if (TryFindSession(host, hostProcessId, out HostControlSessionDescriptor? descriptor))
        {
            return descriptor!;
        }

        string normalizedHost = BridgePaths.NormalizeHost(host);
        throw new HostControlCallException(
            "sidecar_unavailable",
            $"No live {normalizedHost} sidecar control endpoint was found for " +
            $"host PID {hostProcessId}.");
    }

    public static bool TryFindSession(
        string host,
        int hostProcessId,
        out HostControlSessionDescriptor? descriptor)
    {
        string normalizedHost = BridgePaths.NormalizeHost(host);
        string path = HostControlPaths.GetDescriptorPath(
            normalizedHost,
            hostProcessId);
        descriptor = TryRead(path);
        if (descriptor is null ||
            !string.Equals(
                descriptor.ProtocolVersion,
                HostControlConstants.ProtocolVersion,
                StringComparison.Ordinal) ||
            !string.Equals(
                descriptor.Channel,
                HostControlConstants.Channel,
                StringComparison.Ordinal) ||
            !string.Equals(
                descriptor.Host,
                normalizedHost,
                StringComparison.Ordinal) ||
            descriptor.HostProcessId != hostProcessId ||
            descriptor.SidecarProcessId <= 0 ||
            !IsProcessAlive(descriptor.SidecarProcessId) ||
            !IsValidDescriptorIdentity(descriptor))
        {
            descriptor = null;
            return false;
        }

        return true;
    }

    private static HostControlSessionDescriptor? TryRead(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<HostControlSessionDescriptor>(
                File.ReadAllText(path),
                BridgeJson.Options);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsValidDescriptorIdentity(
        HostControlSessionDescriptor descriptor) =>
        descriptor.SessionId is not null &&
        descriptor.PipeName is not null &&
        descriptor.SessionToken is not null &&
        descriptor.Capabilities is not null &&
        Guid.TryParseExact(descriptor.SessionId, "D", out _) &&
        PipeNameRegex().IsMatch(descriptor.PipeName) &&
        TokenRegex().IsMatch(descriptor.SessionToken) &&
        descriptor.Capabilities.Count is > 0 and <= 32 &&
        descriptor.Capabilities.All(capability =>
            !string.IsNullOrWhiteSpace(capability) &&
            capability.Length <= 128);

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    [GeneratedRegex(
        "^[a-z0-9-]{1,128}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex PipeNameRegex();

    [GeneratedRegex(
        "^[a-f0-9]{64}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();
}
