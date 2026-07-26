using System.Buffers;
using System.Text.Json;

namespace Tripo.Bridge;

internal static class BoundedJsonLine
{
    public static async Task<T> ReadAsync<T>(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (maximumBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            using MemoryStream message = new(Math.Min(maximumBytes, 4096));
            bool completed = false;

            while (!completed)
            {
                int remaining = maximumBytes - checked((int)message.Length);
                int read = await stream.ReadAsync(
                        buffer.AsMemory(0, Math.Min(buffer.Length, remaining + 1)),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    if (message.Length == 0)
                    {
                        throw new EndOfStreamException("The bridge connection closed before a message arrived.");
                    }

                    completed = true;
                    continue;
                }

                int newlineIndex = Array.IndexOf(buffer, (byte)'\n', 0, read);
                int bytesToCopy = newlineIndex >= 0 ? newlineIndex : read;
                if (message.Length + bytesToCopy > maximumBytes)
                {
                    throw new BridgeCallException(
                        "message_too_large",
                        $"Bridge messages may not exceed {maximumBytes} bytes.");
                }

                await message.WriteAsync(
                        buffer.AsMemory(0, bytesToCopy),
                        cancellationToken)
                    .ConfigureAwait(false);
                completed = newlineIndex >= 0;
            }

            byte[] payload = message.ToArray();
            if (payload.Length > 0 && payload[^1] == (byte)'\r')
            {
                Array.Resize(ref payload, payload.Length - 1);
            }

            if (payload.Length == 0)
            {
                throw new BridgeCallException("invalid_json", "The bridge message was empty.");
            }

            return JsonSerializer.Deserialize<T>(payload, BridgeJson.Options)
                ?? throw new BridgeCallException("invalid_json", "The bridge message was null.");
        }
        catch (JsonException exception)
        {
            throw new BridgeCallException("invalid_json", "The bridge message was not valid JSON.", exception);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    public static async Task WriteAsync<T>(
        Stream stream,
        T value,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value, BridgeJson.Options);
        if (payload.Length > maximumBytes)
        {
            throw new BridgeCallException(
                "message_too_large",
                $"Bridge messages may not exceed {maximumBytes} bytes.");
        }

        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(new byte[] { (byte)'\n' }, cancellationToken)
            .ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
