using System.IO.Pipes;
using Xunit;

namespace Tripo.Bridge.Tests;

public sealed class HostControlTests
{
    [Fact]
    public void CredentialWorkflowExecutionGateIsExclusiveAcrossInstances()
    {
        using TemporaryDataRoot dataRoot = new();
        Tripo.Bridge.CredentialWorkflowExecutionGate first = new(dataRoot.Path);
        Tripo.Bridge.CredentialWorkflowExecutionGate second = new(dataRoot.Path);

        using (first.Acquire())
        {
            Tripo.Bridge.BridgeCallException exception = Assert.Throws<
                Tripo.Bridge.BridgeCallException>(
                () =>
                {
                    using IDisposable unexpectedLease = second.Acquire();
                });

            Assert.Equal("credential_workflow_unavailable", exception.Code);
        }

        using IDisposable recoveredLease = second.Acquire();
    }

    [Fact]
    public async Task AuthenticatedClientCanReadHealth()
    {
        using TemporaryDataRoot dataRoot = new();
        RecordingDispatcher dispatcher = new();
        await using Tripo.Bridge.NamedPipeHostControlServer server =
            CreateServer(dispatcher);
        await server.StartAsync();
        Tripo.Bridge.NamedPipeHostControlClient client = new(
            "rhino",
            Environment.ProcessId);

        Tripo.Bridge.HostControlHealthReceipt receipt =
            await client.CallAsync<object, Tripo.Bridge.HostControlHealthReceipt>(
                Tripo.Bridge.HostControlConstants.HealthMethod,
                new { },
                CancellationToken.None);

        Assert.Equal("rhino", receipt.Host);
        Assert.Equal(Environment.ProcessId, receipt.HostProcessId);
        Assert.Equal(1, dispatcher.CallCount);
    }

    [Fact]
    public async Task WrongTokenIsRejectedWithoutDispatch()
    {
        using TemporaryDataRoot dataRoot = new();
        RecordingDispatcher dispatcher = new();
        await using Tripo.Bridge.NamedPipeHostControlServer server =
            CreateServer(dispatcher);
        await server.StartAsync();
        Tripo.Bridge.HostControlSessionDescriptor descriptor = server.Descriptor;
        Tripo.Bridge.HostControlRequest request = new(
            Tripo.Bridge.HostControlConstants.ProtocolVersion,
            Tripo.Bridge.HostControlConstants.Channel,
            Guid.NewGuid().ToString("D"),
            new string('0', descriptor.SessionToken.Length),
            Tripo.Bridge.HostControlConstants.HealthMethod,
            Tripo.Bridge.BridgeJson.ToElement(new { }));

        Tripo.Bridge.HostControlResponse response =
            await SendRawAsync(descriptor, request);

        Assert.False(response.Ok);
        Assert.Equal("unauthorized", response.Error?.Code);
        Assert.Equal(0, dispatcher.CallCount);
    }

    [Fact]
    public async Task BridgeTokenCannotAuthenticateToHostControl()
    {
        using TemporaryDataRoot dataRoot = new();
        RecordingDispatcher dispatcher = new();
        await using Tripo.Bridge.NamedPipeHostControlServer controlServer =
            CreateServer(dispatcher);
        await using Tripo.Bridge.NamedPipeBridgeServer bridgeServer = new(
            "rhino",
            "8-test",
            [Tripo.Bridge.BridgeConstants.ContextMethod],
            new BridgeDispatcher());
        await controlServer.StartAsync();
        await bridgeServer.StartAsync();
        Tripo.Bridge.HostControlRequest request = new(
            Tripo.Bridge.HostControlConstants.ProtocolVersion,
            Tripo.Bridge.HostControlConstants.Channel,
            Guid.NewGuid().ToString("D"),
            bridgeServer.Descriptor.SessionToken,
            Tripo.Bridge.HostControlConstants.HealthMethod,
            Tripo.Bridge.BridgeJson.ToElement(new { }));

        Tripo.Bridge.HostControlResponse response =
            await SendRawAsync(controlServer.Descriptor, request);

        Assert.False(response.Ok);
        Assert.Equal("unauthorized", response.Error?.Code);
        Assert.Equal(0, dispatcher.CallCount);
    }

    [Fact]
    public async Task WrongChannelIsRejectedWithoutDispatch()
    {
        using TemporaryDataRoot dataRoot = new();
        RecordingDispatcher dispatcher = new();
        await using Tripo.Bridge.NamedPipeHostControlServer server =
            CreateServer(dispatcher);
        await server.StartAsync();
        Tripo.Bridge.HostControlSessionDescriptor descriptor = server.Descriptor;
        Tripo.Bridge.HostControlRequest request = new(
            Tripo.Bridge.HostControlConstants.ProtocolVersion,
            "bridge",
            Guid.NewGuid().ToString("D"),
            descriptor.SessionToken,
            Tripo.Bridge.HostControlConstants.HealthMethod,
            Tripo.Bridge.BridgeJson.ToElement(new { }));

        Tripo.Bridge.HostControlResponse response =
            await SendRawAsync(descriptor, request);

        Assert.False(response.Ok);
        Assert.Equal("unsupported_protocol", response.Error?.Code);
        Assert.Equal(0, dispatcher.CallCount);
    }

    [Fact]
    public async Task LegacyV1PaidRequestIsRejectedBeforeDispatch()
    {
        using TemporaryDataRoot dataRoot = new();
        RecordingDispatcher dispatcher = new();
        await using Tripo.Bridge.NamedPipeHostControlServer server =
            CreateServer(dispatcher);
        await server.StartAsync();
        Tripo.Bridge.HostControlSessionDescriptor descriptor = server.Descriptor;
        Tripo.Bridge.HostControlRequest request = new(
            "1",
            Tripo.Bridge.HostControlConstants.Channel,
            Guid.NewGuid().ToString("D"),
            descriptor.SessionToken,
            Tripo.Bridge.HostControlConstants.CreateTextTaskMethod,
            Tripo.Bridge.BridgeJson.ToElement(
                new Tripo.Bridge.HostControlCreateTextTaskRequest(
                    "a chair",
                    10_000,
                    false,
                    Guid.NewGuid().ToString("D"),
                    Guid.NewGuid().ToString("D"),
                    true,
                    true)));

        Tripo.Bridge.HostControlResponse response =
            await SendRawAsync(descriptor, request);

        Assert.Equal("2", Tripo.Bridge.HostControlConstants.ProtocolVersion);
        Assert.False(response.Ok);
        Assert.Equal("unsupported_protocol", response.Error?.Code);
        Assert.Equal(0, dispatcher.CallCount);
    }

    [Fact]
    public async Task MethodOutsideAllowlistIsRejected()
    {
        using TemporaryDataRoot dataRoot = new();
        RecordingDispatcher dispatcher = new();
        await using Tripo.Bridge.NamedPipeHostControlServer server =
            CreateServer(dispatcher);
        await server.StartAsync();
        Tripo.Bridge.HostControlSessionDescriptor descriptor = server.Descriptor;
        Tripo.Bridge.HostControlRequest request = new(
            Tripo.Bridge.HostControlConstants.ProtocolVersion,
            Tripo.Bridge.HostControlConstants.Channel,
            Guid.NewGuid().ToString("D"),
            descriptor.SessionToken,
            "host.import_mesh",
            Tripo.Bridge.BridgeJson.ToElement(new { }));

        Tripo.Bridge.HostControlResponse response =
            await SendRawAsync(descriptor, request);

        Assert.False(response.Ok);
        Assert.Equal("method_not_allowed", response.Error?.Code);
        Assert.Equal(0, dispatcher.CallCount);
    }

    [Fact]
    public async Task DiscoveryRequiresExactHostProcessId()
    {
        using TemporaryDataRoot dataRoot = new();
        await using Tripo.Bridge.NamedPipeHostControlServer server =
            CreateServer(new RecordingDispatcher());
        await server.StartAsync();

        Tripo.Bridge.HostControlSessionDescriptor exact =
            Tripo.Bridge.HostControlDiscovery.FindSession(
                "rhino",
                Environment.ProcessId);
        Tripo.Bridge.HostControlCallException exception = Assert.Throws<
            Tripo.Bridge.HostControlCallException>(
            () => Tripo.Bridge.HostControlDiscovery.FindSession(
                "rhino",
                Environment.ProcessId + 1));

        Assert.Equal(server.Descriptor.SessionId, exact.SessionId);
        Assert.Equal(server.Descriptor.SessionToken, exact.SessionToken);
        Assert.Equal(server.Descriptor.PipeName, exact.PipeName);
        Assert.Equal(server.Descriptor.Capabilities, exact.Capabilities);
        Assert.Equal("sidecar_unavailable", exception.Code);
    }

    [Fact]
    public void DiscoveryRejectsNullDescriptorFieldsAsUnavailable()
    {
        using TemporaryDataRoot dataRoot = new();
        string path = Tripo.Bridge.HostControlPaths.GetDescriptorPath(
            "rhino",
            Environment.ProcessId);
        File.WriteAllText(
            path,
            $$"""
            {
              "protocolVersion": "{{Tripo.Bridge.HostControlConstants.ProtocolVersion}}",
              "channel": "{{Tripo.Bridge.HostControlConstants.Channel}}",
              "host": "rhino",
              "hostProcessId": {{Environment.ProcessId}},
              "sidecarProcessId": {{Environment.ProcessId}},
              "sessionId": null,
              "pipeName": null,
              "sessionToken": null,
              "startedAtUtc": "2026-01-01T00:00:00Z",
              "capabilities": null
            }
            """);

        Tripo.Bridge.HostControlCallException exception = Assert.Throws<
            Tripo.Bridge.HostControlCallException>(
            () => Tripo.Bridge.HostControlDiscovery.FindSession(
                "rhino",
                Environment.ProcessId));

        Assert.Equal("sidecar_unavailable", exception.Code);
    }

    [Fact]
    public async Task DiscoveryRejectsLegacyV1Descriptor()
    {
        using TemporaryDataRoot dataRoot = new();
        await using Tripo.Bridge.NamedPipeHostControlServer server =
            CreateServer(new RecordingDispatcher());
        await server.StartAsync();
        string path = Tripo.Bridge.HostControlPaths.GetDescriptorPath(
            "rhino",
            Environment.ProcessId);
        Tripo.Bridge.HostControlSessionDescriptor legacy =
            server.Descriptor with
            {
                ProtocolVersion = "1",
            };
        File.WriteAllText(
            path,
            System.Text.Json.JsonSerializer.Serialize(
                legacy,
                Tripo.Bridge.BridgeJson.Options));

        Tripo.Bridge.HostControlCallException exception = Assert.Throws<
            Tripo.Bridge.HostControlCallException>(
            () => Tripo.Bridge.HostControlDiscovery.FindSession(
                "rhino",
                Environment.ProcessId));

        Assert.Equal("2", Tripo.Bridge.HostControlConstants.ProtocolVersion);
        Assert.Equal("sidecar_unavailable", exception.Code);
    }

    [Fact]
    public async Task SecondControlServerForSameHostIsRejected()
    {
        using TemporaryDataRoot dataRoot = new();
        await using Tripo.Bridge.NamedPipeHostControlServer first =
            CreateServer(new RecordingDispatcher());
        await using Tripo.Bridge.NamedPipeHostControlServer second =
            CreateServer(new RecordingDispatcher());
        await first.StartAsync();

        Tripo.Bridge.HostControlCallException exception =
            await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
                () => second.StartAsync());

        Assert.Equal("sidecar_already_running", exception.Code);
    }

    [Fact]
    public async Task DescriptorAndLockArePrivateOnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDataRoot dataRoot = new();
        await using Tripo.Bridge.NamedPipeHostControlServer server =
            CreateServer(new RecordingDispatcher());
        await server.StartAsync();

        UnixFileMode descriptorMode = File.GetUnixFileMode(
            Tripo.Bridge.HostControlPaths.GetDescriptorPath(
                "rhino",
                Environment.ProcessId));
        UnixFileMode lockMode = File.GetUnixFileMode(
            Tripo.Bridge.HostControlPaths.GetInstanceLockPath(
                "rhino",
                Environment.ProcessId));

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            descriptorMode);
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            lockMode);
    }

    [Fact]
    public void ApiKeyRequestStringIsAlwaysRedacted()
    {
        const string secret = "secret-value-that-must-not-appear";
        Tripo.Bridge.HostControlSetApiKeyRequest request = new(
            secret,
            persist: true);

        string rendered = request.ToString();

        Assert.DoesNotContain(secret, rendered, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void HostControlEnvelopeStringRedactsTokenAndPayload()
    {
        const string secret = "secret-value-that-must-not-appear";
        const string token = "0123456789abcdef";
        Tripo.Bridge.HostControlRequest request = new(
            Tripo.Bridge.HostControlConstants.ProtocolVersion,
            Tripo.Bridge.HostControlConstants.Channel,
            Guid.NewGuid().ToString("D"),
            token,
            Tripo.Bridge.HostControlConstants.CredentialSetMethod,
            Tripo.Bridge.BridgeJson.ToElement(
                new Tripo.Bridge.HostControlSetApiKeyRequest(
                    secret,
                    persist: true)));

        string rendered = request.ToString();

        Assert.DoesNotContain(secret, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(token, rendered, StringComparison.Ordinal);
        Assert.Contains("Payload = [REDACTED]", rendered, StringComparison.Ordinal);
        Assert.Contains("SessionToken = [REDACTED]", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void HostControlDescriptorStringRedactsSessionToken()
    {
        const string token =
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        Tripo.Bridge.HostControlSessionDescriptor descriptor = new(
            Tripo.Bridge.HostControlConstants.ProtocolVersion,
            Tripo.Bridge.HostControlConstants.Channel,
            "rhino",
            Environment.ProcessId,
            Environment.ProcessId,
            Guid.NewGuid().ToString("D"),
            "tripo-control-rhino-test",
            token,
            DateTimeOffset.UtcNow,
            [Tripo.Bridge.HostControlConstants.HealthMethod]);

        string rendered = descriptor.ToString();

        Assert.DoesNotContain(token, rendered, StringComparison.Ordinal);
        Assert.Contains("SessionToken = [REDACTED]", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessManagerReturnsAClientWithWorkflowLengthTimeout()
    {
        using TemporaryDataRoot dataRoot = new();
        await using Tripo.Bridge.NamedPipeHostControlServer server = new(
            "rhino",
            Environment.ProcessId,
            [
                Tripo.Bridge.HostControlConstants.HealthMethod,
                Tripo.Bridge.HostControlConstants.TaskStatusMethod,
            ],
            new SlowWorkflowDispatcher());
        await server.StartAsync();
        await using Tripo.Bridge.HostSidecarProcessManager manager = new(
            "rhino",
            Environment.ProcessId,
            Path.GetFullPath(AppContext.BaseDirectory),
            "unused-while-server-is-live");

        Tripo.Bridge.IHostControlClient client =
            await manager.EnsureConnectedAsync(CancellationToken.None);
        using CancellationTokenSource deadline =
            new(TimeSpan.FromSeconds(8));

        Tripo.Bridge.HostControlTaskStatusReceipt receipt =
            await client.GetTaskStatusAsync("task_slow", deadline.Token);

        Assert.Equal("task_slow", receipt.TaskId);
        Assert.Equal("success", receipt.Status);
    }

    private static Tripo.Bridge.NamedPipeHostControlServer CreateServer(
        RecordingDispatcher dispatcher) =>
        new(
            "rhino",
            Environment.ProcessId,
            [Tripo.Bridge.HostControlConstants.HealthMethod],
            dispatcher);

    private static async Task<Tripo.Bridge.HostControlResponse> SendRawAsync(
        Tripo.Bridge.HostControlSessionDescriptor descriptor,
        Tripo.Bridge.HostControlRequest request)
    {
        await using NamedPipeClientStream pipe = new(
            ".",
            descriptor.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync();
        await Tripo.Bridge.BoundedJsonLine.WriteAsync(
            pipe,
            request,
            Tripo.Bridge.HostControlConstants.MaximumMessageBytes,
            CancellationToken.None);
        return await Tripo.Bridge.BoundedJsonLine.ReadAsync<
            Tripo.Bridge.HostControlResponse>(
            pipe,
            Tripo.Bridge.HostControlConstants.MaximumMessageBytes,
            CancellationToken.None);
    }

    private sealed class RecordingDispatcher :
        Tripo.Bridge.IHostControlDispatcher
    {
        public int CallCount { get; private set; }

        public Task<object> DispatchAsync(
            string method,
            System.Text.Json.JsonElement payload,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult<object>(
                new Tripo.Bridge.HostControlHealthReceipt(
                    "rhino",
                    Environment.ProcessId,
                    Environment.ProcessId,
                    [Tripo.Bridge.HostControlConstants.HealthMethod]));
        }
    }

    private sealed class SlowWorkflowDispatcher :
        Tripo.Bridge.IHostControlDispatcher
    {
        public async Task<object> DispatchAsync(
            string method,
            System.Text.Json.JsonElement payload,
            CancellationToken cancellationToken)
        {
            if (method == Tripo.Bridge.HostControlConstants.HealthMethod)
            {
                return new Tripo.Bridge.HostControlHealthReceipt(
                    "rhino",
                    Environment.ProcessId,
                    Environment.ProcessId,
                    [
                        Tripo.Bridge.HostControlConstants.HealthMethod,
                        Tripo.Bridge.HostControlConstants.TaskStatusMethod,
                    ]);
            }

            if (method == Tripo.Bridge.HostControlConstants.TaskStatusMethod)
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds(5_250),
                    cancellationToken);
                return new Tripo.Bridge.HostControlTaskStatusReceipt(
                    "task_slow",
                    "model",
                    "success",
                    100,
                    null,
                    null,
                    null,
                    null,
                    null);
            }

            throw new Tripo.Bridge.HostControlCallException(
                "method_not_allowed",
                "Unexpected test method.");
        }
    }

    private sealed class BridgeDispatcher : Tripo.Bridge.IHostBridgeDispatcher
    {
        public Task<object> DispatchAsync(
            string method,
            System.Text.Json.JsonElement payload,
            CancellationToken cancellationToken) =>
            Task.FromResult<object>(
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
