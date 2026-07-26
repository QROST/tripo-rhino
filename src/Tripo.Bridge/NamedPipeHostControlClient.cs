using System.IO.Pipes;
using System.Text.Json;

namespace Tripo.Bridge;

public sealed class NamedPipeHostControlClient
{
    private static readonly TimeSpan TransientRetryDelay =
        TimeSpan.FromMilliseconds(250);

    private readonly string _host;
    private readonly int _hostProcessId;
    private readonly TimeSpan _timeout;

    public NamedPipeHostControlClient(
        string host,
        int hostProcessId,
        TimeSpan? timeout = null)
    {
        _host = BridgePaths.NormalizeHost(host);
        if (hostProcessId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(hostProcessId),
                "The host process ID must be positive.");
        }

        _hostProcessId = hostProcessId;
        _timeout = timeout ?? HostControlConstants.DefaultCallTimeout;
        if (_timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
    }

    public async Task<TResponse> CallAsync<TRequest, TResponse>(
        string method,
        TRequest payload,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);

        for (int attempt = 1; ; attempt++)
        {
            HostControlSessionDescriptor descriptor =
                HostControlDiscovery.FindSession(_host, _hostProcessId);
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
            catch (HostControlCallException exception)
                when (attempt == 1 &&
                      exception.InnerException is EndOfStreamException)
            {
                try
                {
                    await Task.Delay(TransientRetryDelay, deadline.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                    when (!cancellationToken.IsCancellationRequested)
                {
                    throw new HostControlCallException(
                        "sidecar_timeout",
                        "The sidecar did not respond before the local deadline.");
                }
            }
        }
    }

    private static async Task<TResponse> AttemptCallAsync<TRequest, TResponse>(
        HostControlSessionDescriptor descriptor,
        string method,
        TRequest payload,
        CancellationToken callerCancellationToken,
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
        catch (OperationCanceledException)
            when (!callerCancellationToken.IsCancellationRequested)
        {
            throw new HostControlCallException(
                "sidecar_timeout",
                "The sidecar did not accept the connection before the local deadline.");
        }
        catch (IOException exception)
        {
            throw new HostControlCallException(
                "sidecar_unavailable",
                "The sidecar control endpoint could not be reached.",
                exception);
        }

        string requestId = Guid.NewGuid().ToString("D");
        HostControlRequest request = new(
            HostControlConstants.ProtocolVersion,
            HostControlConstants.Channel,
            requestId,
            descriptor.SessionToken,
            method,
            BridgeJson.ToElement(payload));

        HostControlResponse response;
        try
        {
            await BoundedJsonLine.WriteAsync(
                    pipe,
                    request,
                    HostControlConstants.MaximumMessageBytes,
                    deadlineToken)
                .ConfigureAwait(false);
            response = await BoundedJsonLine.ReadAsync<HostControlResponse>(
                    pipe,
                    HostControlConstants.MaximumMessageBytes,
                    deadlineToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (!callerCancellationToken.IsCancellationRequested)
        {
            throw new HostControlCallException(
                "sidecar_timeout",
                "The sidecar did not respond before the local deadline.");
        }
        catch (IOException exception)
        {
            throw new HostControlCallException(
                "sidecar_unavailable",
                "The sidecar control connection dropped during the call.",
                exception);
        }

        if (!string.Equals(response.RequestId, requestId, StringComparison.Ordinal) ||
            !string.Equals(
                response.ProtocolVersion,
                HostControlConstants.ProtocolVersion,
                StringComparison.Ordinal) ||
            !string.Equals(
                response.Channel,
                HostControlConstants.Channel,
                StringComparison.Ordinal))
        {
            throw new HostControlCallException(
                "invalid_response",
                "The sidecar returned a mismatched response.");
        }

        if (!response.Ok)
        {
            throw new HostControlCallException(
                response.Error?.Code ?? "sidecar_error",
                response.Error?.Message ??
                "The sidecar returned an unspecified error.");
        }

        if (response.Result is null)
        {
            throw new HostControlCallException(
                "invalid_response",
                "The sidecar returned no result.");
        }

        try
        {
            return response.Result.Value.Deserialize<TResponse>(BridgeJson.Options)
                ?? throw new HostControlCallException(
                    "invalid_response",
                    "The sidecar returned a null result.");
        }
        catch (JsonException exception)
        {
            throw new HostControlCallException(
                "invalid_response",
                "The sidecar returned an invalid result.",
                exception);
        }
    }
}
