using Microsoft.Extensions.DependencyInjection;

namespace Tripo.Mcp;

public static class TripoExecutionServices
{
    public static IServiceCollection AddTripoExecutionCore(
        this IServiceCollection services,
        string host,
        int? explicitHostProcessId = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        string normalizedHost = Tripo.Bridge.BridgePaths.NormalizeHost(host);
        if (explicitHostProcessId is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(explicitHostProcessId),
                "The explicit host process ID must be positive.");
        }

        services.AddSingleton<ApiCredentialService>();
        services.AddSingleton<ITripoCredentialService>(
            provider => provider.GetRequiredService<ApiCredentialService>());
        services.AddSingleton<ITripoApiKeyProvider>(
            provider => provider.GetRequiredService<ApiCredentialService>());
        services
            .AddHttpClient<ITripoApiClient, TripoV3Client>(client =>
            {
                client.BaseAddress = TripoV3Client.BaseUri;
                client.Timeout = TimeSpan.FromMinutes(2);
            })
            .ConfigurePrimaryHttpMessageHandler(
                TripoMcpApplication.CreatePublicNetworkHandler);
        services
            .AddHttpClient<IArtifactStager, ArtifactStager>(client =>
            {
                client.Timeout = TimeSpan.FromMinutes(5);
            })
            .ConfigurePrimaryHttpMessageHandler(
                TripoMcpApplication.CreatePublicNetworkHandler);
        services.AddSingleton<IHostConnection>(
            _ => explicitHostProcessId is { } processId
                ? new HostConnection(normalizedHost, processId)
                : new HostConnection(normalizedHost));
        services.AddSingleton(TripoWorkflowOptions.Default);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<TripoWorkflow>();
        services.AddSingleton<ITripoWorkflow>(
            provider => provider.GetRequiredService<TripoWorkflow>());
        return services;
    }
}
