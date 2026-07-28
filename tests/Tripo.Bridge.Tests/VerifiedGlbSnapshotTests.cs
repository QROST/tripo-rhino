using System.Globalization;
using System.Security.Cryptography;
using Xunit;

namespace Tripo.Bridge.Tests;

public sealed class VerifiedGlbSnapshotTests
{
    private const string SnapshotToken = "0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task SnapshotCopiesVerifiedBytesToPrivateRandomPath()
    {
        using TemporaryDataRoot root = new();
        byte[] glb = GlbContainerValidatorTests.BuildGlb(
            """{"asset":{"version":"2.0"}}""");
        Tripo.Bridge.PreparedGlbArtifact prepared = Prepared(glb);

        string path;
        using (Tripo.Bridge.VerifiedGlbSnapshot snapshot =
               await Tripo.Bridge.VerifiedGlbSnapshot.CreateAsync(
                   prepared,
                   CancellationToken.None))
        {
            path = snapshot.GlbPath;
            Assert.True(File.Exists(path));
            Assert.Equal("model.glb", Path.GetFileName(path));
            Assert.Equal(glb, await File.ReadAllBytesAsync(path));
            using (FileStream importerLikeReader = new(
                       path,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read))
            {
                Assert.Equal(glb.Length, importerLikeReader.Length);
            }

            snapshot.Verify();
        }

        Assert.False(File.Exists(path));
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
    }

    [Fact]
    public async Task SnapshotRevalidationRejectsChangedContentWhereOsAllowsIt()
    {
        using TemporaryDataRoot root = new();
        byte[] glb = GlbContainerValidatorTests.BuildGlb(
            """{"asset":{"version":"2.0"}}""");
        using Tripo.Bridge.VerifiedGlbSnapshot snapshot =
            await Tripo.Bridge.VerifiedGlbSnapshot.CreateAsync(
                Prepared(glb),
                CancellationToken.None);
        byte[] changed = (byte[])glb.Clone();
        changed[^1] ^= 1;

        try
        {
            await File.WriteAllBytesAsync(snapshot.GlbPath, changed);
        }
        catch (IOException) when (OperatingSystem.IsWindows())
        {
            snapshot.Verify();
            return;
        }

        Tripo.Bridge.BridgeCallException exception =
            Assert.Throws<Tripo.Bridge.BridgeCallException>(snapshot.Verify);
        Assert.Equal("artifact_hash_mismatch", exception.Code);
    }

    [Fact]
    public async Task SnapshotRejectsCallerModifiedVerifiedMemory()
    {
        using TemporaryDataRoot root = new();
        byte[] glb = GlbContainerValidatorTests.BuildGlb(
            """{"asset":{"version":"2.0"}}""");
        Tripo.Bridge.PreparedGlbArtifact prepared = Prepared(glb);
        glb[^1] ^= 1;

        Tripo.Bridge.BridgeCallException exception =
            await Assert.ThrowsAsync<Tripo.Bridge.BridgeCallException>(
                () => Tripo.Bridge.VerifiedGlbSnapshot.CreateAsync(
                    prepared,
                    CancellationToken.None));
        Assert.Equal("artifact_hash_mismatch", exception.Code);
    }

    [Fact]
    public async Task SnapshotCreationRemovesSafeStaleDeadProcessDirectory()
    {
        using TemporaryDataRoot root = new();
        string staleDirectory = CreateSnapshotDirectory(
            root,
            int.MaxValue.ToString(CultureInfo.InvariantCulture) +
            "-" +
            SnapshotToken);
        await File.WriteAllBytesAsync(
            Path.Combine(staleDirectory, "model.glb"),
            [1, 2, 3]);
        MakeSnapshotStale(staleDirectory);

        await CreateAndDisposeSnapshotAsync();

        Assert.False(Directory.Exists(staleDirectory));
    }

    [Fact]
    public async Task SnapshotCreationPreservesCurrentProcessDirectory()
    {
        using TemporaryDataRoot root = new();
        string currentDirectory = CreateSnapshotDirectory(
            root,
            Environment.ProcessId.ToString(
                CultureInfo.InvariantCulture) +
            "-" +
            SnapshotToken);
        await File.WriteAllBytesAsync(
            Path.Combine(currentDirectory, "model.glb"),
            [1, 2, 3]);
        MakeSnapshotStale(currentDirectory);

        await CreateAndDisposeSnapshotAsync();

        Assert.True(Directory.Exists(currentDirectory));
        Assert.True(File.Exists(
            Path.Combine(currentDirectory, "model.glb")));
    }

    [Fact]
    public async Task SnapshotCreationPreservesMalformedAndExtraContentDirectories()
    {
        using TemporaryDataRoot root = new();
        string malformedDirectory = CreateSnapshotDirectory(
            root,
            "not-a-process-stale");
        MakeSnapshotStale(malformedDirectory);
        string extraContentDirectory = CreateSnapshotDirectory(
            root,
            int.MaxValue.ToString(CultureInfo.InvariantCulture) +
            "-" +
            SnapshotToken);
        await File.WriteAllBytesAsync(
            Path.Combine(extraContentDirectory, "model.glb"),
            [1, 2, 3]);
        await File.WriteAllTextAsync(
            Path.Combine(extraContentDirectory, "unexpected.txt"),
            "preserve");
        MakeSnapshotStale(extraContentDirectory);

        await CreateAndDisposeSnapshotAsync();

        Assert.True(Directory.Exists(malformedDirectory));
        Assert.True(Directory.Exists(extraContentDirectory));
        Assert.True(File.Exists(
            Path.Combine(extraContentDirectory, "model.glb")));
        Assert.True(File.Exists(
            Path.Combine(extraContentDirectory, "unexpected.txt")));
    }

    [Fact]
    public async Task SnapshotCreationPreservesNearMissDirectoryName()
    {
        using TemporaryDataRoot root = new();
        string nearMissDirectory = CreateSnapshotDirectory(
            root,
            int.MaxValue.ToString(CultureInfo.InvariantCulture) +
            "-backup");
        await File.WriteAllBytesAsync(
            Path.Combine(nearMissDirectory, "model.glb"),
            [1, 2, 3]);
        MakeSnapshotStale(nearMissDirectory);
        string leadingZeroDirectory = CreateSnapshotDirectory(
            root,
            "0" +
            int.MaxValue.ToString(CultureInfo.InvariantCulture) +
            "-" +
            SnapshotToken);
        await File.WriteAllBytesAsync(
            Path.Combine(leadingZeroDirectory, "model.glb"),
            [1, 2, 3]);
        MakeSnapshotStale(leadingZeroDirectory);

        await CreateAndDisposeSnapshotAsync();

        Assert.True(Directory.Exists(nearMissDirectory));
        Assert.True(Directory.Exists(leadingZeroDirectory));
        Assert.True(File.Exists(
            Path.Combine(nearMissDirectory, "model.glb")));
    }

    [Fact]
    public async Task SnapshotCreationPreservesRecentFileInOldDirectory()
    {
        using TemporaryDataRoot root = new();
        string recentFileDirectory = CreateSnapshotDirectory(
            root,
            int.MaxValue.ToString(CultureInfo.InvariantCulture) +
            "-" +
            SnapshotToken);
        string modelPath =
            Path.Combine(recentFileDirectory, "model.glb");
        await File.WriteAllBytesAsync(modelPath, [1, 2, 3]);
        Directory.SetLastWriteTimeUtc(
            recentFileDirectory,
            DateTime.UtcNow - TimeSpan.FromHours(25));

        await CreateAndDisposeSnapshotAsync();

        Assert.True(Directory.Exists(recentFileDirectory));
        Assert.True(File.Exists(modelPath));
    }

    [Fact]
    public async Task SnapshotCreationRemovesSafeStaleCleanupTombstone()
    {
        using TemporaryDataRoot root = new();
        string snapshots = Path.Combine(
            root.Path,
            "host-import-snapshots");
        Directory.CreateDirectory(snapshots);
        string tombstone = Path.Combine(
            snapshots,
            int.MaxValue.ToString(CultureInfo.InvariantCulture) +
            "-cleanup-file-" +
            SnapshotToken +
            ".glb");
        await File.WriteAllBytesAsync(tombstone, [1, 2, 3]);
        File.SetLastWriteTimeUtc(
            tombstone,
            DateTime.UtcNow - TimeSpan.FromHours(25));

        await CreateAndDisposeSnapshotAsync();

        Assert.False(File.Exists(tombstone));
    }

    [Fact]
    public async Task SnapshotCreationPreservesCurrentProcessCleanupTombstone()
    {
        using TemporaryDataRoot root = new();
        string snapshots = Path.Combine(
            root.Path,
            "host-import-snapshots");
        Directory.CreateDirectory(snapshots);
        string tombstone = Path.Combine(
            snapshots,
            Environment.ProcessId.ToString(
                CultureInfo.InvariantCulture) +
            "-cleanup-file-" +
            SnapshotToken +
            ".glb");
        await File.WriteAllBytesAsync(tombstone, [1, 2, 3]);
        File.SetLastWriteTimeUtc(
            tombstone,
            DateTime.UtcNow - TimeSpan.FromHours(25));

        await CreateAndDisposeSnapshotAsync();

        Assert.True(File.Exists(tombstone));
    }

    [Fact]
    public async Task SnapshotCreationMutatesAtMostSixteenStaleCandidates()
    {
        using TemporaryDataRoot root = new();
        List<string> staleDirectories = new();
        for (int index = 1; index <= 17; index++)
        {
            string directory = CreateSnapshotDirectory(
                root,
                int.MaxValue.ToString(CultureInfo.InvariantCulture) +
                "-" +
                index.ToString("x32", CultureInfo.InvariantCulture));
            await File.WriteAllBytesAsync(
                Path.Combine(directory, "model.glb"),
                [1, 2, 3]);
            MakeSnapshotStale(directory);
            staleDirectories.Add(directory);
        }

        await CreateAndDisposeSnapshotAsync();

        Assert.Equal(
            1,
            staleDirectories.Count(Directory.Exists));
    }

    [Fact]
    public async Task SnapshotCreationPreservesSymlinkedModelOnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDataRoot root = new();
        string outside = Path.Combine(root.Path, "outside.glb");
        await File.WriteAllBytesAsync(outside, [1, 2, 3]);
        string directory = CreateSnapshotDirectory(
            root,
            int.MaxValue.ToString(CultureInfo.InvariantCulture) +
            "-" +
            SnapshotToken);
        string linkedModel = Path.Combine(directory, "model.glb");
        File.CreateSymbolicLink(linkedModel, outside);
        Directory.SetLastWriteTimeUtc(
            directory,
            DateTime.UtcNow - TimeSpan.FromHours(25));

        await CreateAndDisposeSnapshotAsync();

        Assert.True(Directory.Exists(directory));
        Assert.True(File.Exists(linkedModel));
        Assert.True(File.Exists(outside));
    }

    private static string CreateSnapshotDirectory(
        TemporaryDataRoot root,
        string name)
    {
        string snapshots = Path.Combine(
            root.Path,
            "host-import-snapshots");
        Directory.CreateDirectory(snapshots);
        string directory = Path.Combine(snapshots, name);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void MakeSnapshotStale(string directory)
    {
        string modelPath = Path.Combine(directory, "model.glb");
        if (File.Exists(modelPath))
        {
            File.SetLastWriteTimeUtc(
                modelPath,
                DateTime.UtcNow - TimeSpan.FromHours(25));
        }

        Directory.SetLastWriteTimeUtc(
            directory,
            DateTime.UtcNow - TimeSpan.FromHours(25));
    }

    private static async Task CreateAndDisposeSnapshotAsync()
    {
        byte[] glb = GlbContainerValidatorTests.BuildGlb(
            """{"asset":{"version":"2.0"}}""");
        using Tripo.Bridge.VerifiedGlbSnapshot snapshot =
            await Tripo.Bridge.VerifiedGlbSnapshot.CreateAsync(
                Prepared(glb),
                CancellationToken.None);
    }

    private static Tripo.Bridge.PreparedGlbArtifact Prepared(byte[] glb)
    {
        string hash = Convert.ToHexString(SHA256.HashData(glb))
            .ToLowerInvariant();
        return new Tripo.Bridge.PreparedGlbArtifact(
            new string('a', 64),
            "model.glb",
            new Tripo.Bridge.StagedBundleEntry(
                "model.glb",
                hash,
                glb.LongLength),
            glb);
    }
}
