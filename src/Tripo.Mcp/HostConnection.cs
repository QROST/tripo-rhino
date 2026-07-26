namespace Tripo.Mcp;

public interface IHostConnection
{
    Task<Tripo.Bridge.HostContextReceipt> GetContextAsync(
        CancellationToken cancellationToken);

    Task<Tripo.Bridge.HostImportReceipt> ImportMeshAsync(
        Tripo.Bridge.ImportMeshRequest request,
        CancellationToken cancellationToken);
}

public sealed class HostConnection : IHostConnection
{
    private readonly Tripo.Bridge.NamedPipeBridgeClient _client;

    public HostConnection(string host)
    {
        _client = new Tripo.Bridge.NamedPipeBridgeClient(host);
    }

    public HostConnection(string host, int processId)
    {
        _client = new Tripo.Bridge.NamedPipeBridgeClient(host, processId);
    }

    public Task<Tripo.Bridge.HostContextReceipt> GetContextAsync(
        CancellationToken cancellationToken) =>
        _client.CallAsync<object, Tripo.Bridge.HostContextReceipt>(
            Tripo.Bridge.BridgeConstants.ContextMethod,
            new { },
            cancellationToken);

    public Task<Tripo.Bridge.HostImportReceipt> ImportMeshAsync(
        Tripo.Bridge.ImportMeshRequest request,
        CancellationToken cancellationToken) =>
        _client.CallAsync<
            Tripo.Bridge.ImportMeshRequest,
            Tripo.Bridge.HostImportReceipt>(
            Tripo.Bridge.BridgeConstants.ImportMeshMethod,
            request,
            cancellationToken);
}
