using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Tripo.Mcp;

public static class TripoMcpApplication
{
    public static async Task RunAsync(
        string host,
        string[] args,
        CancellationToken cancellationToken = default)
    {
        string normalizedHost = Tripo.Bridge.BridgePaths.NormalizeHost(host);
        if (SidecarCommandLine.TryParseHostControl(
                args,
                out HostControlCommandLineOptions? hostControlOptions))
        {
            await TripoHostControlApplication.RunAsync(
                    normalizedHost,
                    hostControlOptions!,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options =>
            options.LogToStandardErrorThreshold = LogLevel.Trace);

        builder.Services.AddTripoExecutionCore(normalizedHost);
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly(typeof(TripoTools).Assembly);

        using IHost application = builder.Build();
        await application.RunAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static SocketsHttpHandler CreatePublicNetworkHandler() =>
        new()
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            ConnectCallback = PublicNetworkConnector.ConnectAsync,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            UseCookies = false,
            UseProxy = false,
        };
}
