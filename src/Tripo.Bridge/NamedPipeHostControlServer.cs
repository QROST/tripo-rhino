using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Cryptography;

namespace Tripo.Bridge;

public sealed class NamedPipeHostControlServer : IAsyncDisposable
{
    private static readonly TimeSpan InitialRetryDelay =
        TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(2);

    private readonly string _host;
    private readonly int _hostProcessId;
    private readonly IReadOnlyList<string> _capabilities;
    private readonly HashSet<string> _methodAllowlist;
    private readonly IHostControlDispatcher _dispatcher;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _clientLimit = new(
        HostControlConstants.MaximumConcurrentClients,
        HostControlConstants.MaximumConcurrentClients);
    private readonly ConcurrentDictionary<int, Task> _clients = new();
    private Task? _listenerTask;
    private HostControlSessionDescriptor? _descriptor;
    private FileStream? _instanceLock;
    private int _clientSequence;
    private bool _disposed;

    public NamedPipeHostControlServer(
        string host,
        int hostProcessId,
        IReadOnlyList<string> capabilities,
        IHostControlDispatcher dispatcher)
    {
        _host = BridgePaths.NormalizeHost(host);
        if (hostProcessId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(hostProcessId),
                "The host process ID must be positive.");
        }

        _hostProcessId = hostProcessId;
        _capabilities = capabilities ?? throw new ArgumentNullException(
            nameof(capabilities));
        if (_capabilities.Count is < 1 or > 32 ||
            _capabilities.Any(capability =>
                string.IsNullOrWhiteSpace(capability) ||
                capability.Length > 128))
        {
            throw new ArgumentException(
                "Host-control capabilities must contain 1 through 32 bounded methods.",
                nameof(capabilities));
        }

        _methodAllowlist = new HashSet<string>(
            _capabilities,
            StringComparer.Ordinal);
        if (_methodAllowlist.Count != _capabilities.Count)
        {
            throw new ArgumentException(
                "Host-control capabilities must be unique.",
                nameof(capabilities));
        }

        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public HostControlSessionDescriptor Descriptor =>
        _descriptor ??
        throw new InvalidOperationException(
            "The host-control server has not started.");

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
#pragma warning disable CA1513 // ObjectDisposedException.ThrowIf is unavailable on net7.0.
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(NamedPipeHostControlServer));
        }
#pragma warning restore CA1513

        if (_listenerTask is not null)
        {
            throw new InvalidOperationException(
                "The host-control server is already running.");
        }

        AcquireInstanceLock();
        try
        {
            string randomSuffix =
                Convert.ToHexString(RandomNumberGenerator.GetBytes(8))
                    .ToLowerInvariant();
            string pipeName =
                $"tripo-control-{_host}-{_hostProcessId}-{randomSuffix}";
            _descriptor = new HostControlSessionDescriptor(
                HostControlConstants.ProtocolVersion,
                HostControlConstants.Channel,
                _host,
                _hostProcessId,
                Environment.ProcessId,
                Guid.NewGuid().ToString("D"),
                pipeName,
                Convert.ToHexString(RandomNumberGenerator.GetBytes(32))
                    .ToLowerInvariant(),
                DateTimeOffset.UtcNow,
                _capabilities);

            TaskCompletionSource ready = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _listenerTask = ListenAsync(pipeName, ready, _shutdown.Token);
            await ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            await HostControlPaths.WriteDescriptorAsync(
                    _descriptor,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            ReleaseInstanceLock();
            throw;
        }
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
            HostControlPaths.DeleteDescriptorIfOwned(_descriptor);
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

        ReleaseInstanceLock();
        _clientLimit.Dispose();
        _shutdown.Dispose();
    }

    private void AcquireInstanceLock()
    {
        string lockPath = HostControlPaths.GetInstanceLockPath(
            _host,
            _hostProcessId);
        try
        {
            _instanceLock = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
            BridgePaths.SetPrivateFileMode(lockPath);
        }
        catch (IOException exception)
        {
            throw new HostControlCallException(
                "sidecar_already_running",
                $"A sidecar control endpoint already owns {_host} host PID " +
                $"{_hostProcessId}.",
                exception);
        }
    }

    private void ReleaseInstanceLock()
    {
        _instanceLock?.Dispose();
        _instanceLock = null;
    }

    private async Task ListenAsync(
        string pipeName,
        TaskCompletionSource ready,
        CancellationToken cancellationToken)
    {
        bool signalledReady = false;
        TimeSpan createRetryDelay = InitialRetryDelay;
        TimeSpan acceptRetryDelay = InitialRetryDelay;
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

                await Task.Delay(createRetryDelay, cancellationToken)
                    .ConfigureAwait(false);
                createRetryDelay = DoubleDelay(createRetryDelay);
                continue;
            }

            createRetryDelay = InitialRetryDelay;
            if (!signalledReady)
            {
                ready.TrySetResult();
                signalledReady = true;
            }

            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                _clientLimit.Release();
                throw;
            }
            catch (Exception)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                _clientLimit.Release();
                await Task.Delay(acceptRetryDelay, cancellationToken)
                    .ConfigureAwait(false);
                acceptRetryDelay = DoubleDelay(acceptRetryDelay);
                continue;
            }

            acceptRetryDelay = InitialRetryDelay;
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

    private static TimeSpan DoubleDelay(TimeSpan delay)
    {
        TimeSpan doubled = delay * 2;
        return doubled > MaximumRetryDelay ? MaximumRetryDelay : doubled;
    }

    private static NamedPipeServerStream CreatePipe(string pipeName) =>
        new(
            pipeName,
            PipeDirection.InOut,
            HostControlConstants.MaximumConcurrentClients,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            HostControlConstants.MaximumMessageBytes,
            HostControlConstants.MaximumMessageBytes);

    private async Task HandleClientSafelyAsync(
        NamedPipeServerStream pipe,
        CancellationToken serverCancellationToken)
    {
        await using (pipe.ConfigureAwait(false))
        {
            HostControlRequest? request = null;
            HostControlResponse response;
            using CancellationTokenSource callTimeout =
                CancellationTokenSource.CreateLinkedTokenSource(
                    serverCancellationToken);
            callTimeout.CancelAfter(HostControlConstants.DefaultCallTimeout);

            try
            {
                request = await BoundedJsonLine.ReadAsync<HostControlRequest>(
                        pipe,
                        HostControlConstants.MaximumMessageBytes,
                        callTimeout.Token)
                    .ConfigureAwait(false);
                ValidateRequest(request);
                object result = await _dispatcher.DispatchAsync(
                        request.Method,
                        request.Payload,
                        callTimeout.Token)
                    .ConfigureAwait(false);
                response = HostControlResponse.Success(request.RequestId, result);
            }
            catch (HostControlCallException exception)
            {
                response = HostControlResponse.Failure(
                    request?.RequestId ?? "unknown",
                    exception.Code,
                    exception.Message);
            }
            catch (OperationCanceledException)
                when (!serverCancellationToken.IsCancellationRequested)
            {
                response = HostControlResponse.Failure(
                    request?.RequestId ?? "unknown",
                    "deadline_exceeded",
                    "The sidecar operation exceeded its local deadline.");
            }
            catch (Exception)
            {
                response = HostControlResponse.Failure(
                    request?.RequestId ?? "unknown",
                    "sidecar_error",
                    "The sidecar operation failed.");
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
                        HostControlConstants.MaximumMessageBytes,
                        serverCancellationToken)
                    .ConfigureAwait(false);
            }
            catch (IOException)
            {
                // The client disconnected before receiving its response.
            }
        }
    }

    private void ValidateRequest(HostControlRequest request)
    {
        if (!string.Equals(
                request.ProtocolVersion,
                HostControlConstants.ProtocolVersion,
                StringComparison.Ordinal) ||
            !string.Equals(
                request.Channel,
                HostControlConstants.Channel,
                StringComparison.Ordinal))
        {
            throw new HostControlCallException(
                "unsupported_protocol",
                "The host-control protocol or channel is not supported.");
        }

        if (!Guid.TryParseExact(request.RequestId, "D", out _))
        {
            throw new HostControlCallException(
                "invalid_request",
                "A canonical request UUID is required.");
        }

        if (!TokensMatch(_descriptor!.SessionToken, request.SessionToken))
        {
            throw new HostControlCallException(
                "unauthorized",
                "Host-control authentication failed.");
        }

        if (!_methodAllowlist.Contains(request.Method))
        {
            throw new HostControlCallException(
                "method_not_allowed",
                "The requested host-control method is not allowed.");
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
                   CryptographicOperations.FixedTimeEquals(
                       expectedBytes,
                       actualBytes);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
