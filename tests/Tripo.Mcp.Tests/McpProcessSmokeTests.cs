using ModelContextProtocol.Client;
using System.Diagnostics;
using System.Globalization;
using Xunit;

namespace Tripo.Mcp.Tests;

public sealed class McpProcessSmokeTests
{
    public static TheoryData<string, string, string, string>
        PackagedSidecars
    {
        get
        {
            TheoryData<string, string, string, string> data = new()
            {
                {
                    "src/Tripo.Rhino",
                    "rhino",
                    "net7.0",
                    "Tripo.Rhino.Mcp.dll"
                },
            };
            return data;
        }
    }

    [Theory]
    [InlineData("src/Tripo.Rhino.Mcp", "Tripo.Rhino.Mcp.dll")]
    public async Task ExecutableCompletesARealStdioHandshakeAndListsTools(
        string projectDirectory,
        string assemblyName)
    {
        string root = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                ".."));
        string configuration = Directory
            .GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!
            .Name;
        string serverAssembly = Path.Combine(
            root,
            projectDirectory,
            "bin",
            configuration,
            "net8.0",
            assemblyName);
        Assert.True(
            File.Exists(serverAssembly),
            $"The MCP server build output was not found at {serverAssembly}.");

        string dotnetHost = ResolveDotnetHost();
        StdioClientTransportOptions options = new()
        {
            Name = projectDirectory + "-stdio-smoke",
            Command = dotnetHost,
            Arguments = [serverAssembly],
            WorkingDirectory = root,
            InheritEnvironmentVariables = false,
            EnvironmentVariables =
                StdioClientTransportOptions.GetDefaultEnvironmentVariables(),
            ShutdownTimeout = TimeSpan.FromSeconds(3),
        };
        StdioClientTransport transport = new(options);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        await using McpClient client = await McpClient.CreateAsync(
            transport,
            cancellationToken: deadline.Token);

        IList<McpClientTool> tools = await client.ListToolsAsync(
            cancellationToken: deadline.Token);

        Assert.Equal(
            [
                "tripo_create_image_task",
                "tripo_create_obj_conversion",
                "tripo_create_text_task",
                "tripo_host_context",
                "tripo_import_obj_task",
                "tripo_operation_status",
                "tripo_stage_local_image",
                "tripo_task_status",
            ],
            tools
                .Select(tool => tool.Name)
                .OrderBy(name => name, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("src/Tripo.Rhino.Mcp", "rhino", "Tripo.Rhino.Mcp.dll")]
    public async Task ExecutableCompletesARealHostControlHandshakeAndShutdown(
        string projectDirectory,
        string host,
        string assemblyName)
    {
        string root = FindRepositoryRoot();
        string configuration = Directory
            .GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!
            .Name;
        string serverAssembly = Path.Combine(
            root,
            projectDirectory,
            "bin",
            configuration,
            "net8.0",
            assemblyName);
        Assert.True(File.Exists(serverAssembly));
        await AssertHostControlProcessAsync(host, serverAssembly);
    }

    [Theory]
    [MemberData(nameof(PackagedSidecars))]
    public async Task ProcessManagerLaunchesPackagedSidecarAndCompletesHandshake(
        string projectDirectory,
        string host,
        string hostTargetFramework,
        string assemblyName)
    {
        string root = FindRepositoryRoot();
        string configuration = Directory
            .GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!
            .Name;
        string serverAssembly = Path.Combine(
            root,
            projectDirectory,
            "bin",
            configuration,
            hostTargetFramework,
            "sidecar",
            assemblyName);
        Assert.True(
            File.Exists(serverAssembly),
            $"The packaged sidecar was not found at {serverAssembly}.");
        Assert.True(
            File.Exists(Path.ChangeExtension(serverAssembly, ".deps.json")));
        Assert.True(
            File.Exists(Path.ChangeExtension(serverAssembly, ".runtimeconfig.json")));
        string pluginDirectory = Directory.GetParent(
            Path.GetDirectoryName(serverAssembly)!)!.FullName;
        string? previousSidecarOverride = Environment.GetEnvironmentVariable(
            Tripo.Bridge.HostSidecarProcessManager.SidecarPathEnvironmentVariable);
        Environment.SetEnvironmentVariable(
            Tripo.Bridge.HostSidecarProcessManager.SidecarPathEnvironmentVariable,
            null);
        using TemporaryDataRoot dataRoot = new();
        try
        {
            await using Tripo.Bridge.HostSidecarProcessManager manager = new(
                host,
                Environment.ProcessId,
                pluginDirectory,
                Path.GetFileNameWithoutExtension(assemblyName));
            Tripo.Bridge.IHostControlClient client =
                await manager.EnsureConnectedAsync(CancellationToken.None);
            Tripo.Bridge.HostControlHealthReceipt health =
                await client.GetHealthAsync(CancellationToken.None);

            Assert.Equal(host, health.Host);
            Assert.Equal(Environment.ProcessId, health.HostProcessId);
            Assert.Contains(
                Tripo.Bridge.HostControlConstants.CreateTextTaskMethod,
                health.Capabilities);
            Assert.Contains(
                Tripo.Bridge.HostControlConstants.CreateImageTaskMethod,
                health.Capabilities);
            Assert.Contains(
                Tripo.Bridge.HostControlConstants.StageObjTaskMethod,
                health.Capabilities);

            await manager.DisposeAsync();
            Assert.False(
                File.Exists(
                    Tripo.Bridge.HostControlPaths.GetDescriptorPath(
                        host,
                        Environment.ProcessId)));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                Tripo.Bridge.HostSidecarProcessManager.SidecarPathEnvironmentVariable,
                previousSidecarOverride);
        }
    }

    private static async Task AssertHostControlProcessAsync(
        string host,
        string serverEntryPoint)
    {
        using TemporaryDataRoot dataRoot = new();
        bool managedAssembly = string.Equals(
            Path.GetExtension(serverEntryPoint),
            ".dll",
            StringComparison.OrdinalIgnoreCase);
        ProcessStartInfo startInfo = new(
            managedAssembly ? ResolveDotnetHost() : serverEntryPoint)
        {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(serverEntryPoint)!,
        };
        if (managedAssembly)
        {
            startInfo.ArgumentList.Add(serverEntryPoint);
        }

        startInfo.ArgumentList.Add("--host-control");
        startInfo.ArgumentList.Add("--host-pid");
        startInfo.ArgumentList.Add(
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        startInfo.Environment[
            Tripo.Bridge.BridgePaths.LocalDataEnvironmentVariable] =
            dataRoot.Path;
        startInfo.Environment.Remove(Tripo.Mcp.TripoV3Client.ApiKeyEnvironmentVariable);
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The sidecar process did not start.");
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        try
        {
            Tripo.Bridge.HostControlClient client = new(
                host,
                Environment.ProcessId,
                TimeSpan.FromSeconds(2));
            using CancellationTokenSource startupDeadline =
                new(TimeSpan.FromSeconds(15));
            Tripo.Bridge.HostControlHealthReceipt? health = null;
            while (health is null)
            {
                startupDeadline.Token.ThrowIfCancellationRequested();
                try
                {
                    health = await client.GetHealthAsync(startupDeadline.Token);
                }
                catch (Tripo.Bridge.HostControlCallException exception)
                    when (exception.Code is
                        "sidecar_unavailable" or
                        "sidecar_timeout")
                {
                    await Task.Delay(100, startupDeadline.Token);
                }
            }

            Assert.Equal(host, health.Host);
            Assert.Equal(Environment.ProcessId, health.HostProcessId);
            Assert.Contains(
                Tripo.Bridge.HostControlConstants.CreateTextTaskMethod,
                health.Capabilities);
            Assert.Contains(
                Tripo.Bridge.HostControlConstants.CreateImageTaskMethod,
                health.Capabilities);
            Assert.Contains(
                Tripo.Bridge.HostControlConstants.StageObjTaskMethod,
                health.Capabilities);

            await client.ShutdownAsync(startupDeadline.Token);
            await process.WaitForExitAsync(startupDeadline.Token);

            Assert.Equal(0, process.ExitCode);
            Assert.False(
                File.Exists(
                    Tripo.Bridge.HostControlPaths.GetDescriptorPath(
                        host,
                        Environment.ProcessId)));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            string output = await standardOutput;
            string error = await standardError;
            Assert.DoesNotContain(
                "Authorization: Bearer",
                output + error,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string FindRepositoryRoot() =>
        Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                ".."));

    private static string ResolveDotnetHost()
    {
        string? configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        string? processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) &&
            string.Equals(
                Path.GetFileNameWithoutExtension(processPath),
                "dotnet",
                StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }

        return "dotnet";
    }
}
