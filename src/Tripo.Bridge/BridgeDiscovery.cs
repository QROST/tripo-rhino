using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Tripo.Bridge;

public static class BridgeDiscovery
{
    public const string HostProcessEnvironmentVariable = "TRIPO_HOST_PID";

    public static BridgeSessionDescriptor FindSession(string host) =>
        FindSession(host, ReadRequestedProcessId());

    public static BridgeSessionDescriptor FindSession(string host, int processId)
    {
        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(processId),
                "The host process ID must be positive.");
        }

        return FindSession(host, (int?)processId);
    }

    private static BridgeSessionDescriptor FindSession(
        string host,
        int? requestedProcessId)
    {
        string normalizedHost = BridgePaths.NormalizeHost(host);
        string searchPattern = $"{normalizedHost}-*.json";

        List<BridgeSessionDescriptor> sessions = [];
        foreach (string path in Directory.EnumerateFiles(
                     BridgePaths.GetDiscoveryDirectory(),
                     searchPattern,
                     SearchOption.TopDirectoryOnly))
        {
            BridgeSessionDescriptor? descriptor = TryRead(path);
            if (descriptor is null ||
                !string.Equals(descriptor.Host, normalizedHost, StringComparison.Ordinal) ||
                !string.Equals(
                    descriptor.ProtocolVersion,
                    BridgeConstants.ProtocolVersion,
                    StringComparison.Ordinal) ||
                !IsProcessAlive(descriptor.ProcessId))
            {
                continue;
            }

            if (requestedProcessId is null || descriptor.ProcessId == requestedProcessId.Value)
            {
                sessions.Add(descriptor);
            }
        }

        if (sessions.Count == 1)
        {
            return sessions[0];
        }

        if (sessions.Count == 0)
        {
            string suffix = requestedProcessId is null
                ? string.Empty
                : $" for PID {requestedProcessId.Value.ToString(CultureInfo.InvariantCulture)}";
            throw new BridgeCallException(
                "host_unavailable",
                $"No live {normalizedHost} bridge was found{suffix}.");
        }

        string processIds = string.Join(
            ", ",
            sessions
                .Select(session => session.ProcessId)
                .OrderBy(processId => processId));
        throw new BridgeCallException(
            "host_ambiguous",
            $"Multiple {normalizedHost} bridges are live ({processIds}). Set {HostProcessEnvironmentVariable}.");
    }

    private static int? ReadRequestedProcessId()
    {
        string? raw = Environment.GetEnvironmentVariable(HostProcessEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int processId) ||
            processId <= 0)
        {
            throw new BridgeCallException(
                "invalid_configuration",
                $"{HostProcessEnvironmentVariable} must be a positive process ID.");
        }

        return processId;
    }

    private static BridgeSessionDescriptor? TryRead(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<BridgeSessionDescriptor>(
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
}
