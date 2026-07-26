namespace Tripo.Bridge.Tests;

internal sealed class TemporaryDataRoot : IDisposable
{
    private readonly string? _previous;

    public TemporaryDataRoot()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
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
