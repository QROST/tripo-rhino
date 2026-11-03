using Xunit;

namespace Tripo.Bridge.Tests;

public sealed class BridgePathsPrivacyTests
{
    [Fact]
    public void DiscoveryDirectoryRejectsASymbolicLink()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDataRoot dataRoot = new();
        string real = Path.Combine(dataRoot.Path, "real-bridges");
        Directory.CreateDirectory(real);
        Directory.CreateSymbolicLink(
            Path.Combine(dataRoot.Path, "bridges"),
            real);

        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(
                Tripo.Bridge.BridgePaths.GetDiscoveryDirectory);

        Assert.Contains("symbolic link", exception.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(real));
    }

    [Fact]
    public void HostControlDirectoryRejectsASymbolicLink()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDataRoot dataRoot = new();
        string real = Path.Combine(dataRoot.Path, "real-controls");
        Directory.CreateDirectory(real);
        Directory.CreateSymbolicLink(
            Path.Combine(dataRoot.Path, "controls"),
            real);

        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(
                Tripo.Bridge.HostControlPaths.GetDiscoveryDirectory);

        Assert.Contains("symbolic link", exception.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(real));
    }
}
