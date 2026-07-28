namespace Tripo.Bridge.Tests;

internal sealed class TemporaryDataRoot : IDisposable
{
    private readonly string? _previous;

    public TemporaryDataRoot()
    {
        string temporaryDirectory = CanonicalTemporaryDirectory();
        Path = System.IO.Path.Combine(
            temporaryDirectory,
            "tripo-bridge-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
        _previous = Environment.GetEnvironmentVariable(
            Tripo.Bridge.BridgePaths.LocalDataEnvironmentVariable);
        Environment.SetEnvironmentVariable(
            Tripo.Bridge.BridgePaths.LocalDataEnvironmentVariable,
            Path);
    }

    public string Path { get; }

    private static string CanonicalTemporaryDirectory()
    {
        string temporaryDirectory = System.IO.Path.GetTempPath();
        if (!OperatingSystem.IsMacOS())
        {
            return temporaryDirectory;
        }

        return temporaryDirectory switch
        {
            "/tmp/" => "/private/tmp/",
            _ when temporaryDirectory.StartsWith(
                "/var/",
                StringComparison.Ordinal) =>
                "/private" + temporaryDirectory,
            _ => temporaryDirectory,
        };
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(
            Tripo.Bridge.BridgePaths.LocalDataEnvironmentVariable,
            _previous);
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
