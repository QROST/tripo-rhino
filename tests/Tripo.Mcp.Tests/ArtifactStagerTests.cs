using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Tripo.Mcp.Tests;

public sealed class ArtifactStagerTests
{
    private static readonly Uri GlbUri = new("https://cdn.example.test/model.glb");
    private static readonly Uri ModelUri = new("https://cdn.example.test/model.obj");
    private const string Obj = "v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n";
    private const string Mtl = "newmtl mat\nKd 1 0 0\nmap_Kd wood.png\n";
    private const string Png = "PNG-BYTES";

    [Fact]
    public async Task StageGlbAsyncStagesMinimalValidGlbDeterministically()
    {
        using TemporaryDataRoot dataRoot = new();
        byte[] glb = CreateGlb("""{"asset":{"version":"2.0"}}""");
        DelegateHttpMessageHandler handler = new((request, _) =>
        {
            Assert.Null(request.Headers.Authorization);
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(glb),
                });
        });
        Tripo.Mcp.ArtifactStager stager = new(new HttpClient(handler));

        Tripo.Bridge.StagedGlbArtifact first =
            await stager.StageGlbAsync(GlbUri, CancellationToken.None);
        Tripo.Bridge.StagedGlbArtifact second =
            await stager.StageGlbAsync(GlbUri, CancellationToken.None);

        string expectedHash =
            Convert.ToHexString(SHA256.HashData(glb)).ToLowerInvariant();
        Assert.Equal(ExpectedGlbArtifactId(glb), first.ArtifactId);
        Assert.Equal(first.ArtifactId, second.ArtifactId);
        Assert.Equal(first.RootDirectory, second.RootDirectory);
        Assert.Equal("model.glb", first.GlbEntry);
        Assert.Equal("model.glb", first.Entry.RelativePath);
        Assert.Equal(expectedHash, first.Entry.Sha256);
        Assert.Equal(glb.Length, first.Entry.ByteLength);
        Assert.Equal(
            glb,
            await File.ReadAllBytesAsync(
                Path.Combine(first.RootDirectory, first.GlbEntry)));
        Assert.True(File.Exists(Path.Combine(first.RootDirectory, "manifest.json")));
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task StageGlbAsyncProducesManifestAcceptedByHostLoader()
    {
        using TemporaryDataRoot dataRoot = new();
        byte[] glb = CreateGlb("""{"asset":{"version":"2.0"}}""");
        Tripo.Mcp.ArtifactStager stager = new(
            new HttpClient(GlbHandler(glb)));

        Tripo.Bridge.StagedGlbArtifact staged =
            await stager.StageGlbAsync(GlbUri, CancellationToken.None);
        Tripo.Bridge.ImportGlbRequest request = new(
            Guid.NewGuid().ToString("D"),
            staged.ArtifactId,
            staged.GlbEntry,
            staged.Entry,
            "Stager-loader integration",
            Guid.NewGuid().ToString("D"),
            ApplyMaterials: true);

        Tripo.Bridge.PreparedGlbArtifact prepared =
            await Tripo.Bridge.StagedArtifactLoader.LoadPreparedGlbAsync(
                request,
                CancellationToken.None);

        Assert.Equal(glb, prepared.VerifiedContent.ToArray());
    }

    [Fact]
    public async Task StageGlbAsyncRejectsCorruptDeclaredLength()
    {
        using TemporaryDataRoot dataRoot = new();
        byte[] glb = CreateGlb("""{"asset":{"version":"2.0"}}""");
        BinaryPrimitives.WriteUInt32LittleEndian(
            glb.AsSpan(8, sizeof(uint)),
            checked((uint)glb.Length + 4));
        DelegateHttpMessageHandler handler = GlbHandler(glb);
        Tripo.Mcp.ArtifactStager stager = new(new HttpClient(handler));

        Tripo.Mcp.TripoApiException exception =
            await Assert.ThrowsAsync<Tripo.Mcp.TripoApiException>(
                () => stager.StageGlbAsync(GlbUri, CancellationToken.None));

        Assert.Contains("invalid", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StageGlbAsyncRejectsEmbeddedUriReference()
    {
        using TemporaryDataRoot dataRoot = new();
        byte[] glb = CreateGlb(
            """
            {"asset":{"version":"2.0"},"buffers":[{"byteLength":0,"uri":"data:application/octet-stream;base64,"}]}
            """);
        DelegateHttpMessageHandler handler = GlbHandler(glb);
        Tripo.Mcp.ArtifactStager stager = new(new HttpClient(handler));

        Tripo.Mcp.TripoApiException exception =
            await Assert.ThrowsAsync<Tripo.Mcp.TripoApiException>(
                () => stager.StageGlbAsync(GlbUri, CancellationToken.None));

        Assert.Contains("invalid", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StageGlbAsyncRejectsNonHttpsUriBeforeNetworkCall()
    {
        using TemporaryDataRoot dataRoot = new();
        DelegateHttpMessageHandler handler = GlbHandler(
            CreateGlb("""{"asset":{"version":"2.0"}}"""));
        Tripo.Mcp.ArtifactStager stager = new(new HttpClient(handler));

        await Assert.ThrowsAsync<Tripo.Mcp.TripoApiException>(
            () => stager.StageGlbAsync(
                new Uri("http://cdn.example.test/model.glb"),
                CancellationToken.None));

        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task StageGlbAsyncConcurrentIdenticalContentConverges()
    {
        using TemporaryDataRoot dataRoot = new();
        byte[] glb = CreateGlb("""{"asset":{"version":"2.0"}}""");
        TaskCompletionSource release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int arrivals = 0;
        DelegateHttpMessageHandler handler = new(async (_, _) =>
        {
            if (Interlocked.Increment(ref arrivals) == 2)
            {
                release.TrySetResult();
            }

            await release.Task;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(glb),
            };
        });
        HttpClient httpClient = new(handler);
        Tripo.Mcp.ArtifactStager first = new(httpClient);
        Tripo.Mcp.ArtifactStager second = new(httpClient);

        Tripo.Bridge.StagedGlbArtifact[] results = await Task.WhenAll(
            first.StageGlbAsync(GlbUri, CancellationToken.None),
            second.StageGlbAsync(GlbUri, CancellationToken.None));

        Assert.Equal(results[0].ArtifactId, results[1].ArtifactId);
        Assert.Equal(results[0].RootDirectory, results[1].RootDirectory);
        Assert.Equal(
            glb,
            await File.ReadAllBytesAsync(
                Path.Combine(results[0].RootDirectory, "model.glb")));
        Assert.True(File.Exists(
            Path.Combine(results[0].RootDirectory, "manifest.json")));
    }

    [Fact]
    public async Task StageGlbAsyncRehashesCompletedArtifactBeforeReuse()
    {
        using TemporaryDataRoot dataRoot = new();
        byte[] glb = CreateGlb("""{"asset":{"version":"2.0"}}""");
        DelegateHttpMessageHandler handler = GlbHandler(glb);
        Tripo.Mcp.ArtifactStager stager = new(new HttpClient(handler));
        Tripo.Bridge.StagedGlbArtifact staged =
            await stager.StageGlbAsync(GlbUri, CancellationToken.None);
        byte[] tampered = [.. glb];
        tampered[^1] ^= 1;
        await File.WriteAllBytesAsync(
            Path.Combine(staged.RootDirectory, staged.GlbEntry),
            tampered);

        Tripo.Mcp.TripoApiException exception =
            await Assert.ThrowsAsync<Tripo.Mcp.TripoApiException>(
                () => stager.StageGlbAsync(GlbUri, CancellationToken.None));

        Assert.Contains(
            "collision",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StageGlbAsyncRejectsChangedCompletionManifest()
    {
        using TemporaryDataRoot dataRoot = new();
        byte[] glb = CreateGlb("""{"asset":{"version":"2.0"}}""");
        DelegateHttpMessageHandler handler = GlbHandler(glb);
        Tripo.Mcp.ArtifactStager stager = new(new HttpClient(handler));
        Tripo.Bridge.StagedGlbArtifact staged =
            await stager.StageGlbAsync(GlbUri, CancellationToken.None);
        await File.WriteAllTextAsync(
            Path.Combine(staged.RootDirectory, "manifest.json"),
            "{}");

        Tripo.Mcp.TripoApiException exception =
            await Assert.ThrowsAsync<Tripo.Mcp.TripoApiException>(
                () => stager.StageGlbAsync(GlbUri, CancellationToken.None));

        Assert.Contains(
            "collision",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StageGlbAsyncRejectsSymlinkedCompletionManifest()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDataRoot dataRoot = new();
        byte[] glb = CreateGlb("""{"asset":{"version":"2.0"}}""");
        Tripo.Mcp.ArtifactStager stager =
            new(new HttpClient(GlbHandler(glb)));
        Tripo.Bridge.StagedGlbArtifact staged =
            await stager.StageGlbAsync(GlbUri, CancellationToken.None);
        string manifest = Path.Combine(staged.RootDirectory, "manifest.json");
        File.Delete(manifest);
        string target = Path.Combine(dataRoot.Path, "outside-manifest");
        await File.WriteAllTextAsync(target, "{}");
        File.CreateSymbolicLink(manifest, target);

        await Assert.ThrowsAsync<Tripo.Mcp.TripoApiException>(
            () => stager.StageGlbAsync(GlbUri, CancellationToken.None));

        Assert.Equal("{}", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task StageGlbAsyncRejectsSymlinkedConfiguredRootBeforeNetwork()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDataRoot dataRoot = new();
        string realRoot = Path.Combine(dataRoot.Path, "real-root");
        string linkedRoot = Path.Combine(dataRoot.Path, "linked-root");
        Directory.CreateDirectory(realRoot);
        Directory.CreateSymbolicLink(linkedRoot, realRoot);
        Environment.SetEnvironmentVariable(
            Tripo.Bridge.BridgePaths.LocalDataEnvironmentVariable,
            linkedRoot);
        DelegateHttpMessageHandler handler = GlbHandler(
            CreateGlb("""{"asset":{"version":"2.0"}}"""));
        Tripo.Mcp.ArtifactStager stager = new(new HttpClient(handler));

        try
        {
            Tripo.Mcp.TripoApiException exception =
                await Assert.ThrowsAsync<Tripo.Mcp.TripoApiException>(
                    () => stager.StageGlbAsync(
                        GlbUri,
                        CancellationToken.None));

            Assert.Contains(
                "collision",
                exception.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, handler.CallCount);
            Assert.Empty(Directory.EnumerateFileSystemEntries(realRoot));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                Tripo.Bridge.BridgePaths.LocalDataEnvironmentVariable,
                dataRoot.Path);
        }
    }

    [Fact]
    public async Task StageGlbAsyncRejectsSymlinkedRootAncestorBeforeNetwork()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDataRoot dataRoot = new();
        string realParent = Path.Combine(dataRoot.Path, "real-parent");
        string linkedParent = Path.Combine(dataRoot.Path, "linked-parent");
        Directory.CreateDirectory(realParent);
        Directory.CreateSymbolicLink(linkedParent, realParent);
        Environment.SetEnvironmentVariable(
            Tripo.Bridge.BridgePaths.LocalDataEnvironmentVariable,
            Path.Combine(linkedParent, "configured-root"));
        DelegateHttpMessageHandler handler = GlbHandler(
            CreateGlb("""{"asset":{"version":"2.0"}}"""));
        Tripo.Mcp.ArtifactStager stager = new(new HttpClient(handler));

        try
        {
            await Assert.ThrowsAsync<Tripo.Mcp.TripoApiException>(
                () => stager.StageGlbAsync(
                    GlbUri,
                    CancellationToken.None));

            Assert.Equal(0, handler.CallCount);
            Assert.False(Directory.Exists(
                Path.Combine(realParent, "configured-root")));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                Tripo.Bridge.BridgePaths.LocalDataEnvironmentVariable,
                dataRoot.Path);
        }
    }

    [Fact]
    public async Task StageGlbAsyncRejectsSymlinkedArtifactWithoutWritingTarget()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDataRoot dataRoot = new();
        byte[] glb = CreateGlb("""{"asset":{"version":"2.0"}}""");
        string staging = Tripo.Bridge.BridgePaths.GetStagingDirectory();
        string target = Path.Combine(dataRoot.Path, "outside-target");
        Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(
            Path.Combine(staging, ExpectedGlbArtifactId(glb)),
            target);
        Tripo.Mcp.ArtifactStager stager =
            new(new HttpClient(GlbHandler(glb)));

        Tripo.Mcp.TripoApiException exception =
            await Assert.ThrowsAsync<Tripo.Mcp.TripoApiException>(
                () => stager.StageGlbAsync(
                    GlbUri,
                    CancellationToken.None));

        Assert.Contains(
            "collision",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
    }

    [Fact]
    public async Task StageBundleAsyncDoesNotForwardAuthorization()
    {
        using TemporaryDataRoot dataRoot = new();
        DelegateHttpMessageHandler handler = new((request, _) =>
        {
            Assert.Null(request.Headers.Authorization);
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Encoding.UTF8.GetBytes(Obj)),
                });
        });
        Tripo.Mcp.ArtifactStager stager = new(new HttpClient(handler));

        Tripo.Bridge.StagedBundle bundle =
            await stager.StageBundleAsync(ModelUri, CancellationToken.None);

        Assert.Equal("model.obj", bundle.ObjEntry);
        Assert.Null(bundle.MtlEntry);
        Assert.True(File.Exists(Path.Combine(bundle.RootDirectory, "model.obj")));
        Assert.True(File.Exists(Path.Combine(bundle.RootDirectory, "manifest.json")));
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task StageBundleAsyncRejectsRedirectToLocalhost()
    {
        using TemporaryDataRoot dataRoot = new();
        DelegateHttpMessageHandler handler = new((_, _) =>
        {
            HttpResponseMessage response = new(HttpStatusCode.Found);
            response.Headers.Location = new Uri("https://localhost/private.obj");
            return Task.FromResult(response);
        });
        Tripo.Mcp.ArtifactStager stager = new(new HttpClient(handler));

        await Assert.ThrowsAsync<Tripo.Mcp.TripoApiException>(
            () => stager.StageBundleAsync(ModelUri, CancellationToken.None));

        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task StageBundleAsyncAcceptsZipWithExactlyOneObj()
    {
        using TemporaryDataRoot dataRoot = new();
        byte[] archive = CreateArchive(("asset/model.obj", Obj), ("notes.txt", "ok"));
        DelegateHttpMessageHandler handler = ArchiveHandler(archive);
        Tripo.Mcp.ArtifactStager stager = new(new HttpClient(handler));

        Tripo.Bridge.StagedBundle bundle =
            await stager.StageBundleAsync(ModelUri, CancellationToken.None);

        Assert.Equal("asset/model.obj", bundle.ObjEntry);
        Tripo.Bridge.StagedBundleEntry entry = Assert.Single(bundle.Entries);
        Assert.Equal(Encoding.UTF8.GetByteCount(Obj), entry.ByteLength);
    }

    [Fact]
    public async Task StageBundleAsyncStagesZipBundleWithMtlAndTexture()
    {
        using TemporaryDataRoot dataRoot = new();
        byte[] archive = CreateArchive(
            ("asset/model.obj", Obj),
            ("asset/model.mtl", Mtl),
            ("asset/wood.png", Png),
            ("asset/readme.md", "ignored"));
        DelegateHttpMessageHandler handler = ArchiveHandler(archive);
        Tripo.Mcp.ArtifactStager stager = new(new HttpClient(handler));

        Tripo.Bridge.StagedBundle bundle =
            await stager.StageBundleAsync(ModelUri, CancellationToken.None);

        Assert.Equal("asset/model.obj", bundle.ObjEntry);
        Assert.Equal("asset/model.mtl", bundle.MtlEntry);
        Assert.Equal(3, bundle.Entries.Count);
        Assert.Equal(
            bundle.Entries.OrderBy(e => e.RelativePath, StringComparer.Ordinal),
            bundle.Entries);
        foreach (Tripo.Bridge.StagedBundleEntry entry in bundle.Entries)
        {
            Assert.True(
                File.Exists(Path.Combine(bundle.RootDirectory, entry.RelativePath)));
        }

        Assert.True(File.Exists(Path.Combine(bundle.RootDirectory, "manifest.json")));
        string expectedBundleId = ExpectedBundleId(
            ("asset/model.obj", Encoding.UTF8.GetBytes(Obj)),
            ("asset/model.mtl", Encoding.UTF8.GetBytes(Mtl)),
            ("asset/wood.png", Encoding.UTF8.GetBytes(Png)));
        Assert.Equal(expectedBundleId, bundle.BundleId);
        Assert.EndsWith(
            Path.Combine("staging", expectedBundleId),
            bundle.RootDirectory,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../escape.png")]
    [InlineData("/absolute/escape.png")]
    [InlineData("nested\\..\\..\\escape.png")]
    public async Task StageBundleAsyncRejectsZipSlipEntryPaths(string maliciousName)
    {
        using TemporaryDataRoot dataRoot = new();
        byte[] archive = CreateArchive(
            ("model.obj", Obj),
            (maliciousName, Png));
        DelegateHttpMessageHandler handler = ArchiveHandler(archive);
        Tripo.Mcp.ArtifactStager stager = new(new HttpClient(handler));

        await Assert.ThrowsAsync<Tripo.Mcp.TripoApiException>(
            () => stager.StageBundleAsync(ModelUri, CancellationToken.None));
    }

    [Fact]
    public async Task StageBundleAsyncRejectsCaseOnlyDuplicateEntryPaths()
    {
        using TemporaryDataRoot dataRoot = new();
        byte[] archive = CreateArchive(
            ("model.obj", Obj),
            ("texture.png", Png),
            ("Texture.png", Png));
        DelegateHttpMessageHandler handler = ArchiveHandler(archive);
        Tripo.Mcp.ArtifactStager stager = new(new HttpClient(handler));

        Tripo.Mcp.TripoApiException exception =
            await Assert.ThrowsAsync<Tripo.Mcp.TripoApiException>(
                () => stager.StageBundleAsync(ModelUri, CancellationToken.None));

        Assert.Contains("duplicate", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StageBundleAsyncRejectsZipWithoutExactlyOneObj()
    {
        using TemporaryDataRoot dataRoot = new();
        byte[] archive = CreateArchive(
            ("first.obj", Obj),
            ("second.obj", Obj));
        DelegateHttpMessageHandler handler = ArchiveHandler(archive);
        Tripo.Mcp.ArtifactStager stager = new(new HttpClient(handler));

        Tripo.Mcp.TripoApiException exception =
            await Assert.ThrowsAsync<Tripo.Mcp.TripoApiException>(
                () => stager.StageBundleAsync(ModelUri, CancellationToken.None));

        Assert.Contains("exactly one OBJ", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StageBundleAsyncRejectsTooManyBundleFiles()
    {
        using TemporaryDataRoot dataRoot = new();
        List<(string Name, string Content)> entries = [("model.obj", Obj)];
        for (int index = 0; index < Tripo.Bridge.BridgeConstants.MaximumBundleFiles; index++)
        {
            entries.Add(($"texture_{index}.png", Png));
        }

        byte[] archive = CreateArchive([.. entries]);
        DelegateHttpMessageHandler handler = ArchiveHandler(archive);
        Tripo.Mcp.ArtifactStager stager = new(new HttpClient(handler));

        Tripo.Mcp.TripoApiException exception =
            await Assert.ThrowsAsync<Tripo.Mcp.TripoApiException>(
                () => stager.StageBundleAsync(ModelUri, CancellationToken.None));

        Assert.Contains("too many", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StageBundleAsyncRejectsAggregateBundleSizeOverflow()
    {
        using TemporaryDataRoot dataRoot = new();
        long entrySize =
            (Tripo.Bridge.BridgeConstants.MaximumBundleBytes / 3) + (1 * 1024 * 1024);
        byte[] archive = CreateZeroEntryArchive(
            ("model.obj", entrySize),
            ("first.png", entrySize),
            ("second.png", entrySize));
        DelegateHttpMessageHandler handler = ArchiveHandler(archive);
        Tripo.Mcp.ArtifactStager stager = new(new HttpClient(handler));

        Tripo.Mcp.TripoApiException exception =
            await Assert.ThrowsAsync<Tripo.Mcp.TripoApiException>(
                () => stager.StageBundleAsync(ModelUri, CancellationToken.None));

        Assert.Contains("aggregate", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StageBundleAsyncBoundsAStalledResponseBody()
    {
        using TemporaryDataRoot dataRoot = new();
        DelegateHttpMessageHandler handler = new((_, _) =>
            Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new HangingReadStream()),
                }));
        Tripo.Mcp.ArtifactStager stager = new(
            new HttpClient(handler),
            TimeSpan.FromMilliseconds(50));

        Tripo.Mcp.TripoApiException exception =
            await Assert.ThrowsAsync<Tripo.Mcp.TripoApiException>(
                () => stager.StageBundleAsync(ModelUri, CancellationToken.None));

        Assert.Contains("timed out", exception.Message);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task StageBundleAsyncConcurrentIdenticalContentConverges()
    {
        using TemporaryDataRoot dataRoot = new();
        TaskCompletionSource release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int arrivals = 0;
        DelegateHttpMessageHandler handler = new(async (_, _) =>
        {
            if (Interlocked.Increment(ref arrivals) == 2)
            {
                release.TrySetResult();
            }

            await release.Task;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(Obj)),
            };
        });
        HttpClient httpClient = new(handler);
        Tripo.Mcp.ArtifactStager first = new(httpClient);
        Tripo.Mcp.ArtifactStager second = new(httpClient);

        Tripo.Bridge.StagedBundle[] results = await Task.WhenAll(
            first.StageBundleAsync(ModelUri, CancellationToken.None),
            second.StageBundleAsync(ModelUri, CancellationToken.None));

        Assert.Equal(results[0].BundleId, results[1].BundleId);
        Assert.True(File.Exists(Path.Combine(results[0].RootDirectory, "model.obj")));
    }

    [Fact]
    public async Task StageBundleAsyncReusesAPreExistingCompleteBundle()
    {
        using TemporaryDataRoot dataRoot = new();
        DelegateHttpMessageHandler handler = new((_, _) =>
            Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Encoding.UTF8.GetBytes(Obj)),
                }));
        Tripo.Mcp.ArtifactStager stager = new(new HttpClient(handler));

        Tripo.Bridge.StagedBundle first =
            await stager.StageBundleAsync(ModelUri, CancellationToken.None);
        Tripo.Bridge.StagedBundle second =
            await stager.StageBundleAsync(ModelUri, CancellationToken.None);

        Assert.Equal(first.BundleId, second.BundleId);
        Assert.Equal(first.RootDirectory, second.RootDirectory);
        Assert.Equal(2, handler.CallCount);
        Assert.True(File.Exists(Path.Combine(second.RootDirectory, "model.obj")));
    }

    [Fact]
    public async Task StageBundleAsyncRejectsContentAddressedCollision()
    {
        using TemporaryDataRoot dataRoot = new();
        byte[] downloadedBytes = Encoding.UTF8.GetBytes(Obj);
        string bundleId = ExpectedBundleId(("model.obj", downloadedBytes));
        string stagingDirectory = Tripo.Bridge.BridgePaths.GetStagingDirectory();
        string collidingBundle = Path.Combine(stagingDirectory, bundleId);
        Directory.CreateDirectory(collidingBundle);
        await File.WriteAllBytesAsync(
            Path.Combine(collidingBundle, "model.obj"),
            Encoding.UTF8.GetBytes("different content that collides\n"));
        await File.WriteAllTextAsync(
            Path.Combine(collidingBundle, "manifest.json"),
            "{}");
        DelegateHttpMessageHandler handler = new((_, _) =>
            Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(downloadedBytes),
                }));
        Tripo.Mcp.ArtifactStager stager = new(new HttpClient(handler));

        Tripo.Mcp.TripoApiException exception =
            await Assert.ThrowsAsync<Tripo.Mcp.TripoApiException>(
                () => stager.StageBundleAsync(ModelUri, CancellationToken.None));

        Assert.Contains("collision", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StageBundleAsyncTreatsMismatchedZipSignaturePairAsPlainFile()
    {
        using TemporaryDataRoot dataRoot = new();
        byte[] mismatchedSignature = [(byte)'P', (byte)'K', 3, 6];
        byte[] payload = [.. mismatchedSignature, .. Encoding.UTF8.GetBytes(Obj)];
        DelegateHttpMessageHandler handler = new((_, _) =>
            Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(payload),
                }));
        Tripo.Mcp.ArtifactStager stager = new(new HttpClient(handler));

        Tripo.Bridge.StagedBundle bundle =
            await stager.StageBundleAsync(ModelUri, CancellationToken.None);

        Assert.Equal("model.obj", bundle.ObjEntry);
        Assert.Equal(
            payload,
            await File.ReadAllBytesAsync(
                Path.Combine(bundle.RootDirectory, "model.obj")));
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("192.168.1.2", false)]
    [InlineData("8.8.8.8", true)]
    [InlineData("::1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("2002:c0a8:0101::1", false)]
    [InlineData("2001:0000:4136:e378::1", false)]
    [InlineData("2001:4860:4860::8888", true)]
    public void PublicAddressPolicyIsFailClosed(string value, bool expected)
    {
        Assert.Equal(
            expected,
            Tripo.Mcp.PublicNetworkConnector.IsPublicAddress(
                System.Net.IPAddress.Parse(value)));
    }

    [Fact]
    public void ProductionHandlerPinsPublicConnectionsAndDisablesProxyState()
    {
        using SocketsHttpHandler handler =
            Tripo.Mcp.TripoMcpApplication.CreatePublicNetworkHandler();

        Assert.NotNull(handler.ConnectCallback);
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.False(handler.UseProxy);
    }

    private static DelegateHttpMessageHandler ArchiveHandler(byte[] archive) =>
        new((_, _) =>
            Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(archive),
                }));

    private static DelegateHttpMessageHandler GlbHandler(byte[] glb) =>
        new((_, _) =>
            Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(glb),
                }));

    private static string ExpectedGlbArtifactId(byte[] glb)
    {
        string sha256 =
            Convert.ToHexString(SHA256.HashData(glb)).ToLowerInvariant();
        string descriptor = string.Concat(
            sha256,
            "\n",
            glb.LongLength.ToString(CultureInfo.InvariantCulture),
            "\n");
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(descriptor)))
            .ToLowerInvariant();
    }

    private static string ExpectedBundleId(
        params (string RelativePath, byte[] Content)[] files)
    {
        IEnumerable<Tripo.Bridge.StagedBundleEntry> entries = files
            .Select(file => new Tripo.Bridge.StagedBundleEntry(
                file.RelativePath,
                Convert.ToHexString(SHA256.HashData(file.Content)).ToLowerInvariant(),
                file.Content.Length))
            .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal);
        StringBuilder manifest = new();
        foreach (Tripo.Bridge.StagedBundleEntry entry in entries)
        {
            manifest
                .Append(entry.RelativePath).Append('\n')
                .Append(entry.Sha256).Append('\n')
                .Append(entry.ByteLength).Append('\n');
        }

        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(manifest.ToString())))
            .ToLowerInvariant();
    }

    private static byte[] CreateArchive(
        params (string Name, string Content)[] entries)
    {
        using MemoryStream buffer = new();
        using (ZipArchive archive = new(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, string content) in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(name);
                using StreamWriter writer = new(
                    entry.Open(),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    leaveOpen: false);
                writer.Write(content);
            }
        }

        return buffer.ToArray();
    }

    private static byte[] CreateZeroEntryArchive(
        params (string Name, long Size)[] entries)
    {
        using MemoryStream buffer = new();
        using (ZipArchive archive = new(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            byte[] zeros = new byte[64 * 1024];
            foreach ((string name, long size) in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(
                    name,
                    CompressionLevel.Fastest);
                using Stream stream = entry.Open();
                long remaining = size;
                while (remaining > 0)
                {
                    int chunk = (int)Math.Min(remaining, zeros.Length);
                    stream.Write(zeros, 0, chunk);
                    remaining -= chunk;
                }
            }
        }

        return buffer.ToArray();
    }

    private static byte[] CreateGlb(string json)
    {
        byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
        int paddedJsonLength = checked((jsonBytes.Length + 3) & ~3);
        int totalLength = checked(12 + 8 + paddedJsonLength);
        byte[] glb = new byte[totalLength];
        BinaryPrimitives.WriteUInt32LittleEndian(
            glb.AsSpan(0, sizeof(uint)),
            0x46546c67);
        BinaryPrimitives.WriteUInt32LittleEndian(
            glb.AsSpan(4, sizeof(uint)),
            2);
        BinaryPrimitives.WriteUInt32LittleEndian(
            glb.AsSpan(8, sizeof(uint)),
            checked((uint)totalLength));
        BinaryPrimitives.WriteUInt32LittleEndian(
            glb.AsSpan(12, sizeof(uint)),
            checked((uint)paddedJsonLength));
        BinaryPrimitives.WriteUInt32LittleEndian(
            glb.AsSpan(16, sizeof(uint)),
            0x4e4f534a);
        glb.AsSpan(20, paddedJsonLength).Fill((byte)' ');
        jsonBytes.CopyTo(glb, 20);
        return glb;
    }
}
