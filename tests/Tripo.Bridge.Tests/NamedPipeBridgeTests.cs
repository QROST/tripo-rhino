using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Tripo.Bridge.Tests;

public sealed class NamedPipeBridgeTests
{
    [Fact]
    public async Task ValidClientCanReadContext()
    {
        using TemporaryDataRoot dataRoot = new();
        RecordingDispatcher dispatcher = new();
        await using Tripo.Bridge.NamedPipeBridgeServer server = CreateServer(dispatcher);
        await server.StartAsync();
        Tripo.Bridge.NamedPipeBridgeClient client = new("rhino");

        Tripo.Bridge.HostContextReceipt receipt =
            await client.CallAsync<object, Tripo.Bridge.HostContextReceipt>(
                Tripo.Bridge.BridgeConstants.ContextMethod,
                new { },
                CancellationToken.None);

        Assert.Equal("rhino", receipt.Host);
        Assert.Equal(1, dispatcher.CallCount);
    }

    [Fact]
    public async Task DirectGlbMethodIsInTheBridgeAllowlist()
    {
        using TemporaryDataRoot dataRoot = new();
        RecordingDispatcher dispatcher = new();
        await using Tripo.Bridge.NamedPipeBridgeServer server =
            CreateServer(dispatcher);
        await server.StartAsync();
        Tripo.Bridge.NamedPipeBridgeClient client = new("rhino");

        _ = await client.CallAsync<object, Tripo.Bridge.HostContextReceipt>(
            Tripo.Bridge.BridgeConstants.ImportGlbMethod,
            new { },
            CancellationToken.None);

        Assert.Equal(
            Tripo.Bridge.BridgeConstants.ImportGlbMethod,
            dispatcher.LastMethod);
        Assert.Equal(1, dispatcher.CallCount);
    }

    [Fact]
    public async Task WrongTokenIsRejectedWithoutDispatch()
    {
        using TemporaryDataRoot dataRoot = new();
        RecordingDispatcher dispatcher = new();
        await using Tripo.Bridge.NamedPipeBridgeServer server = CreateServer(dispatcher);
        await server.StartAsync();
        Tripo.Bridge.BridgeSessionDescriptor descriptor = server.Descriptor;
        await using NamedPipeClientStream pipe = new(
            ".",
            descriptor.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync();
        Tripo.Bridge.BridgeRequest request = new(
            Tripo.Bridge.BridgeConstants.ProtocolVersion,
            Guid.NewGuid().ToString("D"),
            new string('0', descriptor.SessionToken.Length),
            Tripo.Bridge.BridgeConstants.ContextMethod,
            Tripo.Bridge.BridgeJson.ToElement(new { }));

        await Tripo.Bridge.BoundedJsonLine.WriteAsync(
            pipe,
            request,
            Tripo.Bridge.BridgeConstants.MaximumMessageBytes,
            CancellationToken.None);
        Tripo.Bridge.BridgeResponse response =
            await Tripo.Bridge.BoundedJsonLine.ReadAsync<Tripo.Bridge.BridgeResponse>(
                pipe,
                Tripo.Bridge.BridgeConstants.MaximumMessageBytes,
                CancellationToken.None);

        Assert.False(response.Ok);
        Assert.Equal("unauthorized", response.Error?.Code);
        Assert.Equal(0, dispatcher.CallCount);
    }

    [Fact]
    public async Task SaturatedServerRejectsExtraClientButRecovers()
    {
        using TemporaryDataRoot dataRoot = new();
        GatedDispatcher dispatcher = new();
        await using Tripo.Bridge.NamedPipeBridgeServer server = CreateServer(dispatcher);
        await server.StartAsync();
        Tripo.Bridge.NamedPipeBridgeClient saturatingClient =
            new("rhino", TimeSpan.FromSeconds(30));

        Task<Tripo.Bridge.HostContextReceipt>[] saturatingCalls =
            new Task<Tripo.Bridge.HostContextReceipt>[
                Tripo.Bridge.BridgeConstants.MaximumConcurrentClients];
        for (int index = 0; index < saturatingCalls.Length; index++)
        {
            saturatingCalls[index] = saturatingClient.CallAsync<object, Tripo.Bridge.HostContextReceipt>(
                Tripo.Bridge.BridgeConstants.ContextMethod,
                new { },
                CancellationToken.None);
        }

        await dispatcher.WaitForConcurrentCallsAsync(
            Tripo.Bridge.BridgeConstants.MaximumConcurrentClients,
            TimeSpan.FromSeconds(15));

        Tripo.Bridge.NamedPipeBridgeClient overflowClient =
            new("rhino", TimeSpan.FromSeconds(8));
        Tripo.Bridge.BridgeCallException overflowException =
            await Assert.ThrowsAsync<Tripo.Bridge.BridgeCallException>(
                () => overflowClient.CallAsync<object, Tripo.Bridge.HostContextReceipt>(
                    Tripo.Bridge.BridgeConstants.ContextMethod,
                    new { },
                    CancellationToken.None));
        Assert.True(
            overflowException.Code is "host_timeout" or "host_unavailable",
            $"Unexpected overflow error code: {overflowException.Code}");

        dispatcher.ReleaseAll();
        await Task.WhenAll(saturatingCalls).WaitAsync(TimeSpan.FromSeconds(30));

        Tripo.Bridge.NamedPipeBridgeClient freshClient = new("rhino", TimeSpan.FromSeconds(30));
        Tripo.Bridge.HostContextReceipt receipt =
            await freshClient.CallAsync<object, Tripo.Bridge.HostContextReceipt>(
                Tripo.Bridge.BridgeConstants.ContextMethod,
                new { },
                CancellationToken.None);

        Assert.Equal("rhino", receipt.Host);
    }

    [Fact]
    public async Task MissingSessionTokenIsRejectedAsUnauthorized()
    {
        using TemporaryDataRoot dataRoot = new();
        RecordingDispatcher dispatcher = new();
        await using Tripo.Bridge.NamedPipeBridgeServer server = CreateServer(dispatcher);
        await server.StartAsync();
        Tripo.Bridge.BridgeSessionDescriptor descriptor = server.Descriptor;
        await using NamedPipeClientStream pipe = new(
            ".",
            descriptor.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync();
        string requestId = Guid.NewGuid().ToString("D");
        string rawRequest =
            "{\"protocolVersion\":\"" + Tripo.Bridge.BridgeConstants.ProtocolVersion + "\"," +
            "\"requestId\":\"" + requestId + "\"," +
            "\"method\":\"" + Tripo.Bridge.BridgeConstants.ContextMethod + "\"," +
            "\"payload\":{}}\n";
        byte[] rawBytes = Encoding.UTF8.GetBytes(rawRequest);

        await pipe.WriteAsync(rawBytes, CancellationToken.None);
        await pipe.FlushAsync(CancellationToken.None);
        Tripo.Bridge.BridgeResponse response =
            await Tripo.Bridge.BoundedJsonLine.ReadAsync<Tripo.Bridge.BridgeResponse>(
                pipe,
                Tripo.Bridge.BridgeConstants.MaximumMessageBytes,
                CancellationToken.None);

        Assert.False(response.Ok);
        Assert.Equal("unauthorized", response.Error?.Code);
        Assert.Equal(0, dispatcher.CallCount);
    }

    private static Tripo.Bridge.NamedPipeBridgeServer CreateServer(
        RecordingDispatcher dispatcher) =>
        new(
            "rhino",
            "8-test",
            [Tripo.Bridge.BridgeConstants.ContextMethod],
            dispatcher);

    private static Tripo.Bridge.NamedPipeBridgeServer CreateServer(
        GatedDispatcher dispatcher) =>
        new(
            "rhino",
            "8-test",
            [Tripo.Bridge.BridgeConstants.ContextMethod],
            dispatcher);

    private sealed class RecordingDispatcher : Tripo.Bridge.IHostBridgeDispatcher
    {
        public int CallCount { get; private set; }

        public string? LastMethod { get; private set; }

        public Task<object> DispatchAsync(
            string method,
            JsonElement payload,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastMethod = method;
            return Task.FromResult<object>(
                new Tripo.Bridge.HostContextReceipt(
                    "rhino",
                    "8-test",
                    Environment.ProcessId,
                    Guid.NewGuid().ToString("D"),
                    "Test",
                    "Meters",
                    [Tripo.Bridge.BridgeConstants.ContextMethod]));
        }
    }

    private sealed class GatedDispatcher : Tripo.Bridge.IHostBridgeDispatcher
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _concurrentCallCount;

        public async Task<object> DispatchAsync(
            string method,
            JsonElement payload,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _concurrentCallCount);
            await _release.Task.WaitAsync(cancellationToken);
            return new Tripo.Bridge.HostContextReceipt(
                "rhino",
                "8-test",
                Environment.ProcessId,
                Guid.NewGuid().ToString("D"),
                "Test",
                "Meters",
                [Tripo.Bridge.BridgeConstants.ContextMethod]);
        }

        public async Task WaitForConcurrentCallsAsync(int expectedCount, TimeSpan timeout)
        {
            DateTime deadlineUtc = DateTime.UtcNow + timeout;
            while (Volatile.Read(ref _concurrentCallCount) < expectedCount)
            {
                if (DateTime.UtcNow > deadlineUtc)
                {
                    throw new TimeoutException(
                        $"Only {Volatile.Read(ref _concurrentCallCount)} of {expectedCount} " +
                        $"concurrent calls arrived within {timeout}.");
                }

                await Task.Delay(20);
            }
        }

        public void ReleaseAll() => _release.TrySetResult();
    }
}
