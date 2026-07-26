using System.Diagnostics;
using System.Globalization;

namespace Tripo.Bridge;

public interface IHostSidecarConnector
{
    Task<IHostControlClient> EnsureConnectedAsync(
        CancellationToken cancellationToken);
}

public sealed class HostSidecarProcessManager :
    IHostSidecarConnector,
    IAsyncDisposable
{
    public const string SidecarPathEnvironmentVariable = "TRIPO_SIDECAR_PATH";

    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StartupPollDelay =
        TimeSpan.FromMilliseconds(100);

    private readonly string _host;
    private readonly int _hostProcessId;
    private readonly string _pluginDirectory;
    private readonly string _sidecarBaseName;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private Process? _ownedProcess;
    private volatile bool _disposed;
    private int _disposeStarted;

    public HostSidecarProcessManager(
        string host,
        int hostProcessId,
        string pluginDirectory,
        string sidecarBaseName)
    {
        _host = BridgePaths.NormalizeHost(host);
        if (hostProcessId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(hostProcessId),
                "The host process ID must be positive.");
        }

        if (string.IsNullOrWhiteSpace(pluginDirectory) ||
            !Path.IsPathFullyQualified(pluginDirectory))
        {
            throw new ArgumentException(
                "The plug-in directory must be absolute.",
                nameof(pluginDirectory));
        }

        if (string.IsNullOrWhiteSpace(sidecarBaseName) ||
            sidecarBaseName.IndexOfAny(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            throw new ArgumentException(
                "The sidecar base name cannot contain a path separator.",
                nameof(sidecarBaseName));
        }

        _hostProcessId = hostProcessId;
        _pluginDirectory = Path.GetFullPath(pluginDirectory);
        _sidecarBaseName = sidecarBaseName;
    }

    public async Task<IHostControlClient> EnsureConnectedAsync(
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        using CancellationTokenSource operationCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetime.Token);
        CancellationToken operationToken = operationCancellation.Token;
        HostControlClient probeClient = CreateProbeClient();
        if (await IsHealthyAsync(probeClient, operationToken).ConfigureAwait(false))
        {
            return CreateWorkflowClient();
        }

        await _gate.WaitAsync(operationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            probeClient = CreateProbeClient();
            if (await IsHealthyAsync(probeClient, operationToken).ConfigureAwait(false))
            {
                return CreateWorkflowClient();
            }

            StartSidecar();
            using CancellationTokenSource startupDeadline =
                CancellationTokenSource.CreateLinkedTokenSource(
                    operationToken);
            startupDeadline.CancelAfter(StartupTimeout);
            while (true)
            {
                operationToken.ThrowIfCancellationRequested();
                if (_ownedProcess is { HasExited: true } process)
                {
                    throw new HostControlCallException(
                        "sidecar_start_failed",
                        $"The {_host} sidecar exited with code " +
                        $"{process.ExitCode.ToString(CultureInfo.InvariantCulture)} " +
                        "before publishing its control endpoint.");
                }

                probeClient = CreateProbeClient();
                if (await IsHealthyAsync(
                        probeClient,
                        startupDeadline.Token)
                    .ConfigureAwait(false))
                {
                    return CreateWorkflowClient();
                }

                try
                {
                    await Task.Delay(
                            StartupPollDelay,
                            startupDeadline.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                    when (!operationToken.IsCancellationRequested)
                {
                    throw new HostControlCallException(
                        "sidecar_start_timeout",
                        $"The {_host} sidecar did not become ready within " +
                        $"{StartupTimeout.TotalSeconds} seconds.");
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposeStarted, 1, 0) != 0)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Process? ownedProcess = Interlocked.Exchange(ref _ownedProcess, null);
            if (ownedProcess is not null)
            {
                try
                {
                    if (!ownedProcess.HasExited)
                    {
                        using CancellationTokenSource shutdownDeadline =
                            new(TimeSpan.FromSeconds(3));
                        try
                        {
                            await CreateWorkflowClient()
                                .ShutdownAsync(shutdownDeadline.Token)
                                .ConfigureAwait(false);
                        }
                        catch (Exception exception)
                            when (exception is HostControlCallException or
                                  OperationCanceledException)
                        {
                            // The parent-process monitor remains the final cleanup.
                        }

                        using CancellationTokenSource exitDeadline =
                            new(TimeSpan.FromSeconds(5));
                        try
                        {
                            await ownedProcess.WaitForExitAsync(exitDeadline.Token)
                                .ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            // Never force-kill a process that may be checkpointing a
                            // paid request. It will exit when the host process exits.
                        }
                    }
                }
                finally
                {
                    ownedProcess.Dispose();
                }
            }
        }
        finally
        {
            _gate.Release();
            _lifetime.Dispose();
            _gate.Dispose();
        }
    }

    private HostControlClient CreateProbeClient() =>
        new(_host, _hostProcessId, TimeSpan.FromSeconds(5));

    private HostControlClient CreateWorkflowClient() =>
        new(_host, _hostProcessId);

    private static async Task<bool> IsHealthyAsync(
        HostControlClient client,
        CancellationToken cancellationToken)
    {
        try
        {
            await client.GetHealthAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (HostControlCallException exception)
            when (exception.Code is
                "sidecar_unavailable" or
                "sidecar_timeout")
        {
            return false;
        }
    }

    private void StartSidecar()
    {
        if (_ownedProcess is not null)
        {
            if (!_ownedProcess.HasExited)
            {
                // A prior startup attempt may have outlived its caller's
                // deadline. Keep tracking and polling that exact process
                // instead of spawning an unowned duplicate.
                return;
            }

            _ownedProcess.Dispose();
            _ownedProcess = null;
        }

        string sidecarPath = ResolveSidecarPath();
        ProcessStartInfo startInfo = CreateStartInfo(sidecarPath);
        _ownedProcess = Process.Start(startInfo)
            ?? throw new HostControlCallException(
                "sidecar_start_failed",
                $"The {_host} sidecar process could not be started.");
    }

    private ProcessStartInfo CreateStartInfo(string sidecarPath)
    {
        bool managedDll = string.Equals(
            Path.GetExtension(sidecarPath),
            ".dll",
            StringComparison.OrdinalIgnoreCase);
        ProcessStartInfo startInfo = new()
        {
            CreateNoWindow = true,
            FileName = managedDll ? ResolveDotnetHost() : sidecarPath,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(sidecarPath)!,
        };
        if (managedDll)
        {
            startInfo.ArgumentList.Add(sidecarPath);
        }

        startInfo.ArgumentList.Add("--host-control");
        startInfo.ArgumentList.Add("--host-pid");
        startInfo.ArgumentList.Add(
            _hostProcessId.ToString(CultureInfo.InvariantCulture));
        startInfo.Environment[
            BridgeDiscovery.HostProcessEnvironmentVariable] =
            _hostProcessId.ToString(CultureInfo.InvariantCulture);
        startInfo.Environment[BridgePaths.LocalDataEnvironmentVariable] =
            BridgePaths.GetRootDirectory();
        return startInfo;
    }

    private string ResolveSidecarPath()
    {
        string? configured = Environment.GetEnvironmentVariable(
            SidecarPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (!Path.IsPathFullyQualified(configured))
            {
                throw new HostControlCallException(
                    "invalid_configuration",
                    $"{SidecarPathEnvironmentVariable} must be an absolute path.");
            }

            string fullConfigured = Path.GetFullPath(configured);
            if (!File.Exists(fullConfigured))
            {
                throw new HostControlCallException(
                    "sidecar_missing",
                    $"The configured sidecar does not exist at {fullConfigured}.");
            }

            return fullConfigured;
        }

        string[] directories =
        [
            Path.Combine(_pluginDirectory, "sidecar"),
            _pluginDirectory,
        ];
        string[] fileNames = OperatingSystem.IsWindows()
            ? [$"{_sidecarBaseName}.dll", $"{_sidecarBaseName}.exe"]
            : [$"{_sidecarBaseName}.dll", _sidecarBaseName];
        foreach (string directory in directories)
        {
            foreach (string fileName in fileNames)
            {
                string candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new HostControlCallException(
            "sidecar_missing",
            $"The {_host} sidecar was not found under the plug-in's sidecar " +
            $"directory. Set {SidecarPathEnvironmentVariable} to an absolute " +
            "development override if the packaged layout is unavailable.");
    }

    private static string ResolveDotnetHost()
    {
        string? configured = Environment.GetEnvironmentVariable(
            "DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(configured) &&
            Path.IsPathFullyQualified(configured) &&
            File.Exists(configured))
        {
            return configured;
        }

        string? userProfile = Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            string userInstall = Path.Combine(userProfile, ".dotnet", "dotnet");
            if (OperatingSystem.IsWindows())
            {
                userInstall += ".exe";
            }

            if (File.Exists(userInstall))
            {
                return userInstall;
            }
        }

        return OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
    }
}
