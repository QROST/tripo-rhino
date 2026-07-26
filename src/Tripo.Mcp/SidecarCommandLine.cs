using System.Globalization;

namespace Tripo.Mcp;

internal sealed record HostControlCommandLineOptions(int HostProcessId);

internal static class SidecarCommandLine
{
    public static bool TryParseHostControl(
        IReadOnlyList<string> args,
        out HostControlCommandLineOptions? options)
    {
        options = null;
        if (!args.Contains("--host-control", StringComparer.Ordinal))
        {
            return false;
        }

        int? hostProcessId = null;
        for (int index = 0; index < args.Count; index++)
        {
            string argument = args[index];
            switch (argument)
            {
                case "--host-control":
                    break;
                case "--host-pid":
                    if (hostProcessId is not null ||
                        index + 1 >= args.Count ||
                        !int.TryParse(
                            args[++index],
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out int parsedProcessId) ||
                        parsedProcessId <= 0)
                    {
                        throw new ArgumentException(
                            "--host-pid requires one positive process ID.");
                    }

                    hostProcessId = parsedProcessId;
                    break;
                default:
                    throw new ArgumentException(
                        $"Unsupported host-control argument: {argument}");
            }
        }

        options = new HostControlCommandLineOptions(
            hostProcessId ??
            throw new ArgumentException(
                "--host-control requires --host-pid <positive PID>."));
        return true;
    }
}
