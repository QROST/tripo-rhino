using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;

namespace Tripo.Bridge;

public sealed class NamedPipeBridgeServer : IAsyncDisposable
{
    private static readonly TimeSpan InitialCreatePipeRetryDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MaximumCreatePipeRetryDelay = TimeSpan.FromSeconds(2);
    private readonly string _host;
    private readonly string _hostVersion;
    private readonly IReadOnlyList<string> _capabilities;
    private readonly IHostBridgeDispatcher _dispatcher;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _clientLimit = new(
        BridgeConstants.MaximumConcurrentClients,
        BridgeConstants.MaximumConcurrentClients);
    private readonly ConcurrentDictionary<int, Task> _clients = new();
    private Task? _listenerTask;
    private BridgeSessionDescriptor? _descriptor;
    private int _clientSequence;
    private bool _disposed;

    public NamedPipeBridgeServer(
        string host,
        string hostVersion,
        IReadOnlyList<string> capabilities,
        IHostBridgeDispatcher dispatcher)
    {
        _host = BridgePaths.NormalizeHost(host);
        _hostVersion = string.IsNullOrWhiteSpace(hostVersion) ? "unknown" : hostVersion;
        _capabilities = capabilities;
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public BridgeSessionDescriptor Descriptor =>
        _descriptor ?? throw new InvalidOperationException("The bridge server has not started.");

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
#pragma warning disable CA1513 // ObjectDisposedException.ThrowIf is unavailable on net7.0.
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(NamedPipeBridgeServer));
        }
#pragma warning restore CA1513

        if (_listenerTask is not null)
        {
            throw new InvalidOperationException("The bridge server is already running.");
        }

        string randomSuffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(8))
            .ToLowerInvariant();
        string pipeName = $"tripo-{_host}-{Environment.ProcessId}-{randomSuffix}";
        _descriptor = new BridgeSessionDescriptor(
            BridgeConstants.ProtocolVersion,
            _host,
            _hostVersion,
            Environment.ProcessId,
            Guid.NewGuid().ToString("D"),
            pipeName,
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
            DateTimeOffset.UtcNow,
            _capabilities);

        TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _listenerTask = ListenAsync(pipeName, ready, _shutdown.Token);
        await ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        await BridgePaths.WriteSessionDescriptorAsync(_descriptor, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shutdown.Cancel();
        if (_descriptor is not null)
        {
            BridgePaths.DeleteSessionDescriptorIfOwned(_descriptor);
        }

        if (_listenerTask is not null)
        {
            try
            {
                await _listenerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
        }

        Task[] clients = _clients.Values.ToArray();
        if (clients.Length > 0)
        {
            try
            {
                await Task.WhenAll(clients).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
        }

        _clientLimit.Dispose();
        _shutdown.Dispose();
    }

    private async Task ListenAsync(
        string pipeName,
        TaskCompletionSource ready,
        CancellationToken cancellationToken)
    {
        bool signalledReady = false;
        TimeSpan createPipeRetryDelay = InitialCreatePipeRetryDelay;
        TimeSpan acceptRetryDelay = InitialCreatePipeRetryDelay;
        while (!cancellationToken.IsCancellationRequested)
        {
            await _clientLimit.WaitAsync(cancellationToken).ConfigureAwait(false);

            NamedPipeServerStream pipe;
            try
            {
                pipe = CreatePipe(pipeName);
            }
            catch (Exception exception)
            {
                _clientLimit.Release();
                if (!signalledReady)
                {
                    ready.TrySetException(exception);
                    return;
                }

                await Task.Delay(createPipeRetryDelay, cancellationToken).ConfigureAwait(false);
                TimeSpan doubledDelay = createPipeRetryDelay * 2;
                createPipeRetryDelay = doubledDelay > MaximumCreatePipeRetryDelay
                    ? MaximumCreatePipeRetryDelay
                    : doubledDelay;
                continue;
            }

            createPipeRetryDelay = InitialCreatePipeRetryDelay;
            if (!signalledReady)
            {
                ready.TrySetResult();
                signalledReady = true;
            }

            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                _clientLimit.Release();
                throw;
            }
            catch (Exception)
            {
                // A client that connects and vanishes surfaces as IOException on
                // Windows but as SocketException from the Unix socket emulation;
                // no accept-path failure may kill the listener.
                await pipe.DisposeAsync().ConfigureAwait(false);
                _clientLimit.Release();
                await Task.Delay(acceptRetryDelay, cancellationToken).ConfigureAwait(false);
                TimeSpan doubledAcceptDelay = acceptRetryDelay * 2;
                acceptRetryDelay = doubledAcceptDelay > MaximumCreatePipeRetryDelay
                    ? MaximumCreatePipeRetryDelay
                    : doubledAcceptDelay;
                continue;
            }

            acceptRetryDelay = InitialCreatePipeRetryDelay;

            int clientId = Interlocked.Increment(ref _clientSequence);
            Task clientTask = HandleClientSafelyAsync(pipe, cancellationToken);
            _clients[clientId] = clientTask;
            _ = clientTask.ContinueWith(
                completedTask =>
                {
                    _clients.TryRemove(clientId, out _);
                    _clientLimit.Release();
                    _ = completedTask.Exception;
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private static NamedPipeServerStream CreatePipe(string pipeName) =>
        new(
            pipeName,
            PipeDirection.InOut,
            BridgeConstants.MaximumConcurrentClients,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            BridgeConstants.MaximumMessageBytes,
            BridgeConstants.MaximumMessageBytes);

    private async Task HandleClientSafelyAsync(
        NamedPipeServerStream pipe,
        CancellationToken serverCancellationToken)
    {
        await using (pipe.ConfigureAwait(false))
        {
            BridgeRequest? request = null;
            BridgeResponse response;
            using CancellationTokenSource callTimeout =
                CancellationTokenSource.CreateLinkedTokenSource(serverCancellationToken);
            callTimeout.CancelAfter(BridgeConstants.DefaultCallTimeout);

            try
            {
                request = await BoundedJsonLine.ReadAsync<BridgeRequest>(
                        pipe,
                        BridgeConstants.MaximumMessageBytes,
                        callTimeout.Token)
                    .ConfigureAwait(false);
                ValidateRequest(request);
                object result = await _dispatcher.DispatchAsync(
                        request.Method,
                        request.Payload,
                        callTimeout.Token)
                    .ConfigureAwait(false);
                response = BridgeResponse.Success(request.RequestId, result);
            }
            catch (BridgeCallException exception)
            {
                response = BridgeResponse.Failure(
                    request?.RequestId ?? "unknown",
                    exception.Code,
                    exception.Message);
            }
            catch (OperationCanceledException) when (!serverCancellationToken.IsCancellationRequested)
            {
                response = BridgeResponse.Failure(
                    request?.RequestId ?? "unknown",
                    "deadline_exceeded",
                    "The host operation exceeded its local deadline.");
            }
            catch (Exception)
            {
                response = BridgeResponse.Failure(
                    request?.RequestId ?? "unknown",
                    "host_error",
                    "The host operation failed.");
            }

            if (serverCancellationToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await BoundedJsonLine.WriteAsync(
                        pipe,
                        response,
                        BridgeConstants.MaximumMessageBytes,
                        serverCancellationToken)
                    .ConfigureAwait(false);
            }
            catch (IOException)
            {
                // The client disconnected before receiving its response.
            }
        }
    }

    private void ValidateRequest(BridgeRequest request)
    {
        if (!string.Equals(
                request.ProtocolVersion,
                BridgeConstants.ProtocolVersion,
                StringComparison.Ordinal))
        {
            throw new BridgeCallException(
                "unsupported_protocol",
                $"Bridge protocol {request.ProtocolVersion} is not supported.");
        }

        if (string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > 128)
        {
            throw new BridgeCallException("invalid_request", "A bounded request ID is required.");
        }

        if (string.IsNullOrWhiteSpace(request.SessionToken) ||
            !TokensMatch(_descriptor!.SessionToken, request.SessionToken))
        {
            throw new BridgeCallException("unauthorized", "Bridge authentication failed.");
        }

        if (request.Method is not BridgeConstants.ContextMethod and not BridgeConstants.ImportMeshMethod)
        {
            throw new BridgeCallException("method_not_allowed", "The requested host method is not allowed.");
        }
    }

    private static bool TokensMatch(string expected, string? actual)
    {
        if (string.IsNullOrEmpty(actual))
        {
            return false;
        }

        try
        {
            byte[] expectedBytes = Convert.FromHexString(expected);
            byte[] actualBytes = Convert.FromHexString(actual);
            return expectedBytes.Length == actualBytes.Length &&
                   CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
