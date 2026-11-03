using Xunit;

namespace Tripo.Mcp.Tests;

public sealed class ProductVersionTests
{
    [Fact]
    public void UserAgentMatchesTheSingleRepositoryVersion()
    {
        string version = ReadRepositoryFile("VERSION").Trim();
        string props = ReadRepositoryFile("Directory.Build.props");
        string manifest = ReadRepositoryFile(
            Path.Combine("packaging", "manifest.yml"));

        Assert.Matches("^[0-9]+\\.[0-9]+\\.[0-9]+$", version);
        Assert.Contains(
            "<VersionPrefix>" + version + "</VersionPrefix>",
            props,
            StringComparison.Ordinal);
        Assert.Contains(
            "version: " + version,
            manifest,
            StringComparison.Ordinal);
        Assert.Equal(version, Tripo.Mcp.TripoV3Client.ProductVersion);
        Assert.Equal(
            "tripo-rhino/" + version,
            Tripo.Mcp.TripoV3Client.UserAgentValue);
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Could not find " + relativePath + " from the test output directory.");
    }
}
