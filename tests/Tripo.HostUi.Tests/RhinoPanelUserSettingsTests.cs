using Xunit;

namespace Tripo.HostUi.Tests;

public sealed class RhinoPanelUserSettingsTests
{
    [Fact]
    public void LoadReturnsDirectGlbFriendlyDefaultsWhenFileIsMissing()
    {
        string root = CreateTemporaryRoot();
        try
        {
            string path = Path.Combine(root, "missing.json");

            Tripo.HostUi.RhinoPanelUserSettings settings =
                Tripo.HostUi.RhinoPanelUserSettings.Load(path);

            Assert.Equal(20_000, settings.FaceLimit);
            Assert.True(settings.WithMaterials);
            Assert.Equal("Tripo Model", settings.ObjectName);
            Assert.Equal(1, settings.SchemaVersion);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SaveAndLoadRoundTripOnlyNonSensitivePanelPreferences()
    {
        string root = CreateTemporaryRoot();
        try
        {
            string path = Path.Combine(root, "ui-settings", "rhino-panel.json");
            Tripo.HostUi.RhinoPanelUserSettings expected = new(
                48_000,
                WithMaterials: false,
                ObjectName: "  Pavilion Study  ");

            expected.Save(path);
            Tripo.HostUi.RhinoPanelUserSettings actual =
                Tripo.HostUi.RhinoPanelUserSettings.Load(path);
            string json = File.ReadAllText(path);

            Assert.Equal(48_000, actual.FaceLimit);
            Assert.False(actual.WithMaterials);
            Assert.Equal("Pavilion Study", actual.ObjectName);
            Assert.DoesNotContain("prompt", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("apiKey", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("operation", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("taskId", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("import", json, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(
                Directory.GetFiles(
                    Path.GetDirectoryName(path)!,
                    "*.tmp",
                    SearchOption.TopDirectoryOnly));
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(path));
                Assert.Equal(
                    UnixFileMode.UserRead |
                    UnixFileMode.UserWrite |
                    UnixFileMode.UserExecute,
                    File.GetUnixFileMode(Path.GetDirectoryName(path)!));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LoadFallsBackToDefaultsForCorruptJson()
    {
        string root = CreateTemporaryRoot();
        try
        {
            string path = Path.Combine(root, "rhino-panel.json");
            File.WriteAllText(path, "{");

            Tripo.HostUi.RhinoPanelUserSettings settings =
                Tripo.HostUi.RhinoPanelUserSettings.Load(path);

            Assert.Equal(new Tripo.HostUi.RhinoPanelUserSettings(), settings);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LoadTreatsAnInvalidPathAsARecoverablePreferenceFailure()
    {
        Tripo.HostUi.RhinoPanelUserSettings settings =
            Tripo.HostUi.RhinoPanelUserSettings.Load("\0");

        Assert.Equal(new Tripo.HostUi.RhinoPanelUserSettings(), settings);
    }

    [Theory]
    [InlineData(1, 500)]
    [InlineData(999_999, 200_000)]
    public void LoadSnapsLegacyOutOfRangeFaceLimitToTheNearestEndpoint(
        int faceLimit,
        int expectedFaceLimit)
    {
        string root = CreateTemporaryRoot();
        try
        {
            string path = Path.Combine(root, "rhino-panel.json");
            File.WriteAllText(path, $$"""
                {
                  "faceLimit": {{faceLimit}},
                  "withMaterials": false,
                  "objectName": "                                                                                                                                 ",
                  "importMode": "family",
                  "applyMaterials": true
                }
                """);

            Tripo.HostUi.RhinoPanelUserSettings settings =
                Tripo.HostUi.RhinoPanelUserSettings.Load(path);

            Assert.Equal(expectedFaceLimit, settings.FaceLimit);
            Assert.False(settings.WithMaterials);
            Assert.Equal("Tripo Model", settings.ObjectName);
            Assert.Equal(1, settings.SchemaVersion);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(int.MinValue, 500)]
    [InlineData(499, 500)]
    [InlineData(500, 500)]
    [InlineData(48_000, 48_000)]
    [InlineData(200_000, 200_000)]
    [InlineData(200_001, 200_000)]
    [InlineData(int.MaxValue, 200_000)]
    public void SaveAndLoadRoundTripSnapsFaceLimitToTheNearestEndpoint(
        int faceLimit,
        int expected)
    {
        string root = CreateTemporaryRoot();
        try
        {
            string path = Path.Combine(root, "rhino-panel.json");

            new Tripo.HostUi.RhinoPanelUserSettings(
                faceLimit,
                WithMaterials: true,
                ObjectName: "Boundary model").Save(path);

            Tripo.HostUi.RhinoPanelUserSettings settings =
                Tripo.HostUi.RhinoPanelUserSettings.Load(path);

            Assert.Equal(expected, settings.FaceLimit);
            Assert.Contains(
                $"\"faceLimit\": {expected}",
                File.ReadAllText(path),
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ObjectNameNormalizationRejectsBlankValues(string value)
    {
        bool valid =
            Tripo.HostUi.RhinoPanelUserSettings.TryNormalizeObjectName(
                value,
                out string normalized);

        Assert.False(valid);
        Assert.Empty(normalized);
    }

    [Fact]
    public void ObjectNameNormalizationTrimsValidValuesAndRejectsOverlongValues()
    {
        Assert.True(
            Tripo.HostUi.RhinoPanelUserSettings.TryNormalizeObjectName(
                "  Pavilion Study  ",
                out string normalized));
        Assert.Equal("Pavilion Study", normalized);
        Assert.False(
            Tripo.HostUi.RhinoPanelUserSettings.TryNormalizeObjectName(
                new string('x', 129),
                out _));
    }

    [Fact]
    public void LoadRejectsUnknownSchemaAndOversizedFilesWithoutRewritingThem()
    {
        string root = CreateTemporaryRoot();
        try
        {
            string unknownSchemaPath = Path.Combine(root, "unknown.json");
            string oversizedPath = Path.Combine(root, "oversized.json");
            File.WriteAllText(
                unknownSchemaPath,
                """
                {
                  "schemaVersion": 2,
                  "faceLimit": 48000,
                  "withMaterials": false,
                  "objectName": "Unexpected"
                }
                """);
            File.WriteAllText(
                oversizedPath,
                new string(
                    'x',
                    Tripo.HostUi.RhinoPanelUserSettings
                        .MaximumSettingsBytes + 1));
            string unknownBefore = File.ReadAllText(unknownSchemaPath);
            long oversizedBefore = new FileInfo(oversizedPath).Length;

            Tripo.HostUi.RhinoPanelUserSettings unknown =
                Tripo.HostUi.RhinoPanelUserSettings.Load(unknownSchemaPath);
            Tripo.HostUi.RhinoPanelUserSettings oversized =
                Tripo.HostUi.RhinoPanelUserSettings.Load(oversizedPath);

            Assert.Equal(new Tripo.HostUi.RhinoPanelUserSettings(), unknown);
            Assert.Equal(new Tripo.HostUi.RhinoPanelUserSettings(), oversized);
            Assert.Equal(unknownBefore, File.ReadAllText(unknownSchemaPath));
            Assert.Equal(oversizedBefore, new FileInfo(oversizedPath).Length);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LoadRejectsSymbolicLinkWithoutTouchingItsTarget()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string root = CreateTemporaryRoot();
        try
        {
            string targetPath = Path.Combine(root, "target.json");
            string linkPath = Path.Combine(root, "rhino-panel.json");
            string json =
                """
                {
                  "schemaVersion": 1,
                  "faceLimit": 48000,
                  "withMaterials": false,
                  "objectName": "Linked target"
                }
                """;
            File.WriteAllText(targetPath, json);
            File.CreateSymbolicLink(linkPath, targetPath);

            Tripo.HostUi.RhinoPanelUserSettings settings =
                Tripo.HostUi.RhinoPanelUserSettings.Load(linkPath);

            Assert.Equal(new Tripo.HostUi.RhinoPanelUserSettings(), settings);
            Assert.Equal(json, File.ReadAllText(targetPath));
            Assert.NotNull(new FileInfo(linkPath).LinkTarget);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LoadRejectsLinkedParentDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string root = CreateTemporaryRoot();
        try
        {
            string realDirectory = Path.Combine(root, "real-settings");
            string linkedDirectory = Path.Combine(root, "linked-settings");
            string targetPath = Path.Combine(realDirectory, "rhino-panel.json");
            Directory.CreateDirectory(realDirectory);
            new Tripo.HostUi.RhinoPanelUserSettings(
                48_000,
                WithMaterials: false,
                ObjectName: "Linked parent").Save(targetPath);
            Directory.CreateSymbolicLink(linkedDirectory, realDirectory);

            Tripo.HostUi.RhinoPanelUserSettings settings =
                Tripo.HostUi.RhinoPanelUserSettings.Load(
                    Path.Combine(linkedDirectory, "rhino-panel.json"));

            Assert.Equal(new Tripo.HostUi.RhinoPanelUserSettings(), settings);
            Assert.Equal(
                48_000,
                Tripo.HostUi.RhinoPanelUserSettings.Load(targetPath).FaceLimit);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentReadersObserveOnlyCompleteSettingsSnapshots()
    {
        string root = CreateTemporaryRoot();
        try
        {
            string path = Path.Combine(root, "ui-settings", "rhino-panel.json");
            Tripo.HostUi.RhinoPanelUserSettings first = new(
                20_000,
                WithMaterials: true,
                ObjectName: "First");
            Tripo.HostUi.RhinoPanelUserSettings second = new(
                48_000,
                WithMaterials: false,
                ObjectName: "Second");
            first.Save(path);
            List<Tripo.HostUi.RhinoPanelUserSettings> observed = [];

            Task reader = Task.Run(() =>
            {
                for (int index = 0; index < 250; index++)
                {
                    observed.Add(
                        Tripo.HostUi.RhinoPanelUserSettings.Load(path));
                }
            });
            Task writer = Task.Run(() =>
            {
                for (int index = 0; index < 100; index++)
                {
                    (index % 2 == 0 ? second : first).Save(path);
                }
            });

            await Task.WhenAll(reader, writer);

            Assert.NotEmpty(observed);
            Assert.All(
                observed,
                settings => Assert.True(
                    settings == first || settings == second,
                    $"Observed a partial settings snapshot: {settings}"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTemporaryRoot()
    {
        string temporaryDirectory = Path.GetTempPath();
        if (OperatingSystem.IsMacOS())
        {
            temporaryDirectory = temporaryDirectory switch
            {
                "/tmp/" => "/private/tmp/",
                _ when temporaryDirectory.StartsWith(
                    "/var/",
                    StringComparison.Ordinal) =>
                    "/private" + temporaryDirectory,
                _ => temporaryDirectory,
            };
        }

        string root = Path.Combine(
            temporaryDirectory,
            "tripo-rhino-settings-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
