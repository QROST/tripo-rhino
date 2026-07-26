using System.IO.Pipes;
using System.Text.Json;

namespace Tripo.Bridge;

public sealed class NamedPipeBridgeClient
{
    private static readonly TimeSpan TransientRetryDelay = TimeSpan.FromMilliseconds(250);

    private readonly string _host;
    private readonly int? _processId;
    private readonly TimeSpan _timeout;

    public NamedPipeBridgeClient(string host, TimeSpan? timeout = null)
    {
        _host = BridgePaths.NormalizeHost(host);
        _processId = null;
        _timeout = ValidateTimeout(timeout);
    }

    public NamedPipeBridgeClient(
        string host,
        int processId,
        TimeSpan? timeout = null)
    {
        _host = BridgePaths.NormalizeHost(host);
        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(processId),
                "The host process ID must be positive.");
        }

        _processId = processId;
        _timeout = ValidateTimeout(timeout);
    }

    private static TimeSpan ValidateTimeout(TimeSpan? timeout)
    {
        TimeSpan resolved = timeout ?? BridgeConstants.DefaultCallTimeout;
        if (resolved <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        return resolved;
    }

    private BridgeSessionDescriptor FindSession()
    {
        if (_processId is { } processId)
        {
            return BridgeDiscovery.FindSession(_host, processId);
        }

        return BridgeDiscovery.FindSession(_host);
    }

    public async Task<TResponse> CallAsync<TRequest, TResponse>(
        string method,
        TRequest payload,
        CancellationToken cancellationToken)
    {
        BridgeSessionDescriptor descriptor = FindSession();
        using CancellationTokenSource deadline =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await AttemptCallAsync<TRequest, TResponse>(
                        descriptor,
                        method,
                        payload,
                        cancellationToken,
                        deadline.Token)
                    .ConfigureAwait(false);
            }
            catch (BridgeCallException exception)
                when (attempt == 1 && exception.InnerException is EndOfStreamException)
            {
                // The host tore down an accepted connection before sending one
                // byte (Unix named-pipe emulation accept races). Bridge methods
                // are idempotent by protocol contract, so one retry is safe.
                try
                {
                    await Task.Delay(TransientRetryDelay, deadline.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                    when (!cancellationToken.IsCancellationRequested)
                {
                    throw new BridgeCallException(
                        "host_timeout",
                        $"The {_host} bridge did not respond before the deadline.");
                }
            }
        }
    }

    private async Task<TResponse> AttemptCallAsync<TRequest, TResponse>(
        BridgeSessionDescriptor descriptor,
        string method,
        TRequest payload,
        CancellationToken cancellationToken,
        CancellationToken deadlineToken)
    {
        await using NamedPipeClientStream pipe = new(
            ".",
            descriptor.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        try
        {
            await pipe.ConnectAsync(deadlineToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new BridgeCallException(
                "host_timeout",
                $"The {_host} bridge did not accept the connection before the deadline.");
        }
        catch (IOException exception)
        {
            throw new BridgeCallException(
                "host_unavailable",
                $"The {_host} bridge could not be reached.",
                exception);
        }

        string requestId = Guid.NewGuid().ToString("D");
        BridgeRequest request = new(
            BridgeConstants.ProtocolVersion,
            requestId,
            descriptor.SessionToken,
            method,
            BridgeJson.ToElement(payload));

        BridgeResponse response;
        try
        {
            await BoundedJsonLine.WriteAsync(
                    pipe,
                    request,
                    BridgeConstants.MaximumMessageBytes,
                    deadlineToken)
                .ConfigureAwait(false);
            response = await BoundedJsonLine.ReadAsync<BridgeResponse>(
                    pipe,
                    BridgeConstants.MaximumMessageBytes,
                    deadlineToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new BridgeCallException(
                "host_timeout",
                $"The {_host} bridge did not respond before the deadline.");
        }
        catch (IOException exception)
        {
            throw new BridgeCallException(
                "host_unavailable",
                $"The {_host} bridge connection dropped during the call.",
                exception);
        }

        if (!string.Equals(response.RequestId, requestId, StringComparison.Ordinal) ||
            !string.Equals(
                response.ProtocolVersion,
                BridgeConstants.ProtocolVersion,
                StringComparison.Ordinal))
        {
            throw new BridgeCallException(
                "invalid_response",
                "The host bridge returned a mismatched response.");
        }

        if (!response.Ok)
        {
            throw new BridgeCallException(
                response.Error?.Code ?? "host_error",
                response.Error?.Message ?? "The host bridge returned an unspecified error.");
        }

        if (response.Result is null)
        {
            throw new BridgeCallException(
                "invalid_response",
                "The host bridge returned no result.");
        }

        try
        {
            return response.Result.Value.Deserialize<TResponse>(BridgeJson.Options)
                ?? throw new BridgeCallException(
                    "invalid_response",
                    "The host bridge returned a null result.");
        }
        catch (JsonException exception)
        {
            throw new BridgeCallException(
                "invalid_response",
                "The host bridge returned an invalid result.",
                exception);
        }
    }
}
