using Xunit;

namespace Tripo.Bridge.Tests;

public sealed class BoundedJsonLineTests
{
    [Fact]
    public async Task ExactPayloadLimitRoundTrips()
    {
        const int maximumBytes = 32;
        string value = new('x', maximumBytes - 2);
        await using MemoryStream stream = new();

        await Tripo.Bridge.BoundedJsonLine.WriteAsync(
            stream,
            value,
            maximumBytes,
            CancellationToken.None);
        stream.Position = 0;
        string result = await Tripo.Bridge.BoundedJsonLine.ReadAsync<string>(
            stream,
            maximumBytes,
            CancellationToken.None);

        Assert.Equal(value, result);
    }

    [Fact]
    public async Task PayloadBeyondLimitIsRejected()
    {
        const int maximumBytes = 32;
        string value = new('x', maximumBytes - 1);
        await using MemoryStream stream = new();

        Tripo.Bridge.BridgeCallException exception =
            await Assert.ThrowsAsync<Tripo.Bridge.BridgeCallException>(
                () => Tripo.Bridge.BoundedJsonLine.WriteAsync(
                    stream,
                    value,
                    maximumBytes,
                    CancellationToken.None));

        Assert.Equal("message_too_large", exception.Code);
    }
}
