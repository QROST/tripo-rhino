using Xunit;

namespace Tripo.Mcp.Tests;

public sealed class ApiCredentialServiceTests
{
    [Fact]
    public void CredentialMutationsHoldTheSidecarExecutionGate()
    {
        TrackingExecutionGate executionGate = new();
        FakeStore store = new()
        {
            OnWrite = () => Assert.True(executionGate.IsHeld),
            OnDelete = () => Assert.True(executionGate.IsHeld),
        };
        Tripo.Mcp.ApiCredentialService service = new(
            () => null,
            store,
            executionGate);

        service.SetApiKey("persistent-key", persist: true);
        service.ClearApiKey();

        Assert.Equal(2, executionGate.AcquireCalls);
        Assert.False(executionGate.IsHeld);
    }

    [Fact]
    public void EnvironmentCredentialWinsOverSessionAndStore()
    {
        const string environmentKey = "environment-key";
        FakeStore store = new()
        {
            StoredValue = "stored-key",
        };
        Tripo.Mcp.ApiCredentialService service = new(
            () => environmentKey,
            store);
        service.SetApiKey("session-key", persist: false);

        string? effective = service.GetApiKey();
        Tripo.Bridge.HostControlCredentialStatusReceipt status =
            service.GetStatus();

        Assert.Equal(environmentKey, effective);
        Assert.True(status.HasApiKey);
        Assert.Equal("environment", status.Source);
        Assert.False(status.StoredKeyPresenceKnown);
    }

    [Fact]
    public void SessionCredentialIsNotPersisted()
    {
        FakeStore store = new();
        Tripo.Mcp.ApiCredentialService service = new(() => null, store);

        Tripo.Bridge.HostControlCredentialMutationReceipt receipt =
            service.SetApiKey("session-key", persist: false);

        Assert.Equal("session-key", service.GetApiKey());
        Assert.Equal("session", receipt.Status.Source);
        Assert.Equal(0, store.WriteCalls);
    }

    [Fact]
    public void UnavailablePersistentStoreDoesNotBlockSessionCredential()
    {
        FakeStore store = new()
        {
            ThrowOnRead = true,
        };
        Tripo.Mcp.ApiCredentialService service = new(() => null, store);

        Tripo.Bridge.HostControlCredentialStatusReceipt initial =
            service.GetStatus();
        Tripo.Bridge.HostControlCredentialMutationReceipt receipt =
            service.SetApiKey("session-key", persist: false);

        Assert.False(initial.HasApiKey);
        Assert.False(initial.StoredKeyPresenceKnown);
        Assert.Equal("session-key", service.GetApiKey());
        Assert.Equal("session", receipt.Status.Source);
        Assert.False(receipt.Status.StoredKeyPresenceKnown);
    }

    [Fact]
    public void PersistentCredentialUsesTheSelectedStore()
    {
        FakeStore store = new();
        Tripo.Mcp.ApiCredentialService service = new(() => null, store);

        Tripo.Bridge.HostControlCredentialMutationReceipt receipt =
            service.SetApiKey("persistent-key", persist: true);

        Assert.Equal("persistent-key", store.StoredValue);
        Assert.Equal("persistent-key", service.GetApiKey());
        Assert.Equal("store", receipt.Status.Source);
        Assert.True(receipt.Status.StoredKeyPresent);
        Assert.Equal(1, store.WriteCalls);
    }

    [Fact]
    public void RunningServicesObservePersistentRotationAndRevocation()
    {
        FakeStore sharedStore = new()
        {
            StoredValue = "first-persistent-key",
        };
        Tripo.Mcp.ApiCredentialService first = new(() => null, sharedStore);
        Tripo.Mcp.ApiCredentialService second = new(() => null, sharedStore);

        Assert.Equal("first-persistent-key", first.GetApiKey());

        second.SetApiKey("rotated-persistent-key", persist: true);

        Assert.Equal("rotated-persistent-key", first.GetApiKey());
        Assert.Equal("store", first.GetStatus().Source);

        second.ClearApiKey();

        Assert.Null(first.GetApiKey());
        Assert.False(first.GetStatus().HasApiKey);
        Assert.False(first.GetStatus().StoredKeyPresent);
    }

    [Fact]
    public void ClearRemovesSessionAndStoredKeysButCannotClearEnvironment()
    {
        string? environmentKey = null;
        FakeStore store = new();
        Tripo.Mcp.ApiCredentialService service = new(
            () => environmentKey,
            store);
        service.SetApiKey("persistent-key", persist: true);
        service.SetApiKey("session-key", persist: false);
        environmentKey = "environment-key";

        Tripo.Bridge.HostControlCredentialMutationReceipt receipt =
            service.ClearApiKey();

        Assert.Equal("environment-key", service.GetApiKey());
        Assert.Equal("environment", receipt.Status.Source);
        Assert.False(receipt.Status.StoredKeyPresent);
        Assert.Equal(1, store.DeleteCalls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("contains whitespace")]
    [InlineData("contains\nnewline")]
    public void InvalidCredentialNeverAppearsInTheError(string invalidKey)
    {
        Tripo.Mcp.ApiCredentialService service = new(
            () => null,
            new FakeStore());

        Tripo.Mcp.TripoApiException exception = Assert.Throws<
            Tripo.Mcp.TripoApiException>(
            () => service.SetApiKey(invalidKey, persist: false));

        if (invalidKey.Length > 0)
        {
            Assert.DoesNotContain(
                invalidKey,
                exception.ToString(),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PrivateFileFallbackUsesPrivateUnixModes()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDataRoot dataRoot = new();
        Tripo.Mcp.PrivateFileApiKeyStore store = new();

        store.Write("private-file-key");

        string directory = Path.Combine(dataRoot.Path, "secrets");
        string path = Path.Combine(directory, "tripo-v3-api-key");
        Assert.Equal(
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute,
            File.GetUnixFileMode(directory));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(path));
        Assert.Equal("private-file-key", store.Read());
    }

    [Fact]
    public void PrivateFileFallbackRepairsWeakReadMode()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDataRoot dataRoot = new();
        Tripo.Mcp.PrivateFileApiKeyStore store = new();
        store.Write("private-file-key");
        string path = Path.Combine(
            dataRoot.Path,
            "secrets",
            "tripo-v3-api-key");
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.GroupRead |
            UnixFileMode.OtherRead);

        Assert.Equal("private-file-key", store.Read());
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(path));
    }

    [Fact]
    public void PrivateFileFallbackRejectsOversizedContent()
    {
        using TemporaryDataRoot dataRoot = new();
        string directory = Path.Combine(dataRoot.Path, "secrets");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "tripo-v3-api-key");
        File.WriteAllText(path, new string('x', 8 * 1024 + 1));
        Tripo.Mcp.PrivateFileApiKeyStore store = new();

        Tripo.Mcp.TripoApiException exception =
            Assert.Throws<Tripo.Mcp.TripoApiException>(store.Read);

        Assert.Contains("oversized", exception.Message);
    }

    [Fact]
    public async Task MacOsKeychainHelperOutputIsBoundedWithoutEchoingIt()
    {
        string oversized = new(
            'k',
            Tripo.Mcp.MacOsKeychainApiKeyStore
                .MaximumHelperOutputCharacters + 1);

        Tripo.Mcp.TripoApiException exception =
            await Assert.ThrowsAsync<Tripo.Mcp.TripoApiException>(
                () => Tripo.Mcp.MacOsKeychainApiKeyStore.ReadBoundedAsync(
                    new StringReader(oversized),
                    CancellationToken.None));

        Assert.Contains("oversized", exception.Message);
        Assert.DoesNotContain(oversized, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MacOsPlatformSelectionNeverDowngradesToPrivateFileFallback()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        Tripo.Mcp.IPlatformApiKeyStore store =
            Tripo.Mcp.PlatformApiKeyStore.Create();

        Assert.IsType<Tripo.Mcp.MacOsKeychainApiKeyStore>(store);
        Assert.False(store.UsesWeakerFileFallback);
    }

    private sealed class FakeStore : Tripo.Mcp.IPlatformApiKeyStore
    {
        public string BackendName => "fake-store";

        public bool UsesWeakerFileFallback => false;

        public string? StoredValue { get; set; }

        public int WriteCalls { get; private set; }

        public int DeleteCalls { get; private set; }

        public bool ThrowOnRead { get; set; }

        public Action? OnWrite { get; init; }

        public Action? OnDelete { get; init; }

        public string? Read()
        {
            if (ThrowOnRead)
            {
                throw new Tripo.Mcp.TripoApiException(
                    "The fake persistent store is unavailable.");
            }

            return StoredValue;
        }

        public void Write(string apiKey)
        {
            OnWrite?.Invoke();
            WriteCalls++;
            StoredValue = apiKey;
        }

        public void Delete()
        {
            OnDelete?.Invoke();
            DeleteCalls++;
            StoredValue = null;
        }
    }

    private sealed class TrackingExecutionGate :
        Tripo.Bridge.ICredentialWorkflowExecutionGate
    {
        private int _held;

        public int AcquireCalls { get; private set; }

        public bool IsHeld => Volatile.Read(ref _held) == 1;

        public IDisposable Acquire()
        {
            Assert.Equal(0, Interlocked.Exchange(ref _held, 1));
            AcquireCalls++;
            return new ReleaseLease(this);
        }

        private sealed class ReleaseLease : IDisposable
        {
            private TrackingExecutionGate? _owner;

            public ReleaseLease(TrackingExecutionGate owner)
            {
                _owner = owner;
            }

            public void Dispose()
            {
                TrackingExecutionGate? owner =
                    Interlocked.Exchange(ref _owner, null);
                if (owner is not null)
                {
                    Assert.Equal(1, Interlocked.Exchange(ref owner._held, 0));
                }
            }
        }
    }
}
