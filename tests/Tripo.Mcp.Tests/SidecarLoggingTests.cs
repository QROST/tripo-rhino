using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Tripo.Mcp.Tests;

public sealed class SidecarLoggingTests
{
    [Fact]
    public void HttpClientInformationIsFilteredWhileOtherInformationRemains()
    {
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(
            new HostApplicationBuilderSettings());
        Tripo.Mcp.TripoMcpApplication.ConfigureSidecarLogging(builder.Logging);
        using IHost host = builder.Build();
        ILoggerFactory factory = host.Services.GetRequiredService<ILoggerFactory>();
        ILogger http = factory.CreateLogger(
            "System.Net.Http.HttpClient.IArtifactStager.ClientHandler");
        ILogger workflow = factory.CreateLogger("Tripo.Mcp.TripoWorkflow");

        Assert.False(http.IsEnabled(LogLevel.Information));
        Assert.True(http.IsEnabled(LogLevel.Warning));
        Assert.True(workflow.IsEnabled(LogLevel.Information));
    }
}
