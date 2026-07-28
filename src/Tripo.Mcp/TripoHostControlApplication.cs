using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Tripo.Mcp;

internal static class TripoHostControlApplication
{
    public static async Task RunAsync(
        string host,
        HostControlCommandLineOptions options,
        CancellationToken cancellationToken)
    {
        string normalizedHost = Tripo.Bridge.BridgePaths.NormalizeHost(host);
        HostApplicationBuilder builder =
            Host.CreateEmptyApplicationBuilder(
                new HostApplicationBuilderSettings
                {
                    Args = Array.Empty<string>(),
                });
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(consoleOptions =>
            consoleOptions.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Services.AddTripoExecutionCore(
            normalizedHost,
            options.HostProcessId);

        using IHost application = builder.Build();

        using CancellationTokenSource shutdown =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        IReadOnlyList<string> capabilities =
            Tripo.Bridge.HostControlConstants.GetWorkflowCapabilities(
                normalizedHost);
        HostControlDispatcher dispatcher = new(
            normalizedHost,
            options.HostProcessId,
            capabilities,
            application.Services.GetRequiredService<ITripoCredentialService>(),
            application.Services.GetRequiredService<ITripoWorkflow>(),
            shutdown.Cancel);

        await using Tripo.Bridge.NamedPipeHostControlServer server = new(
            normalizedHost,
            options.HostProcessId,
            capabilities,
            dispatcher);

        await server.StartAsync(cancellationToken).ConfigureAwait(false);

        await MonitorHostAsync(
                options.HostProcessId,
                shutdown,
                shutdown.Token)
            .ConfigureAwait(false);
    }

    private static async Task MonitorHostAsync(
        int hostProcessId,
        CancellationTokenSource shutdown,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!IsProcessAlive(hostProcessId))
            {
                shutdown.Cancel();
                return;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
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
