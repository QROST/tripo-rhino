using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Tripo.Bridge.Tests;

public sealed class StagedArtifactLoaderTests
{
    private const string ObjContent =
        "v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n";

    [Fact]
    public async Task LoadPreparedObjAsyncVerifiesAndParsesSingleObjBundle()
    {
        using TemporaryDataRoot dataRoot = new();
        BuiltBundle bundle = BuildBundle(("model.obj", Bytes(ObjContent)));
        Tripo.Bridge.ImportMeshRequest request = CreateRequest(
            bundle,
            objEntry: "model.obj",
            mtlEntry: null,
            applyMaterials: false);

        Tripo.Bridge.PreparedMesh mesh =
            await Tripo.Bridge.StagedArtifactLoader.LoadPreparedObjAsync(
                request,
                CancellationToken.None);

        Assert.Equal(3, mesh.VerticesInMeters.Count);
        Assert.Single(mesh.Triangles);
        Assert.Empty(mesh.Materials);
    }

    [Fact]
    public async Task StageOnlyRequestLoadsWithoutImportMetadata()
    {
        using TemporaryDataRoot dataRoot = new();
        BuiltBundle bundle = BuildBundle(("model.obj", Bytes(ObjContent)));
        Tripo.Bridge.StagedMeshLoadRequest request = new(
            bundle.BundleId,
            "model.obj",
            MtlEntry: null,
            Entries: bundle.Entries,
            SourceUnit: "meters",
            UpAxis: "Z",
            Handedness: "right",
            ApplyMaterials: false);

        Tripo.Bridge.PreparedMesh mesh =
            await Tripo.Bridge.StagedArtifactLoader.LoadPreparedObjAsync(
                request,
                CancellationToken.None);

        Assert.Equal(3, mesh.VerticesInMeters.Count);
        Assert.Single(mesh.Triangles);
    }

    [Fact]
    public async Task LoadPreparedObjAsyncParsesObjAndMtlBundleWithTexture()
    {
        using TemporaryDataRoot dataRoot = new();
        const string obj =
            "v 0 0 0\nv 1 0 0\nv 0 1 0\nvt 0 0\nvt 1 0\nvt 0 1\nusemtl mat\nf 1/1 2/2 3/3\n";
        const string mtl = "newmtl mat\nKd 1 0 0\nmap_Kd tex.png\n";
        BuiltBundle bundle = BuildBundle(
            ("model.obj", Bytes(obj)),
            ("model.mtl", Bytes(mtl)),
            ("tex.png", Bytes("PNG-BYTES")));
        Tripo.Bridge.ImportMeshRequest request = CreateRequest(
            bundle,
            objEntry: "model.obj",
            mtlEntry: "model.mtl",
            applyMaterials: true);

        Tripo.Bridge.PreparedMesh mesh =
            await Tripo.Bridge.StagedArtifactLoader.LoadPreparedObjAsync(
                request,
                CancellationToken.None);

        Assert.Equal(3, mesh.Uvs.Count);
        Tripo.Bridge.PreparedMaterial material = Assert.Single(mesh.Materials);
        Assert.Equal("mat", material.Name);
        Assert.Equal(unchecked((int)0xFFFF0000), material.DiffuseArgb);
        Assert.Equal(
            System.IO.Path.GetFullPath(
                System.IO.Path.Combine(bundle.Directory, "tex.png")),
            material.DiffuseTextureAbsolutePath);
    }

    [Fact]
    public async Task LoadPreparedObjAsyncRejectsMissingEntry()
    {
        using TemporaryDataRoot dataRoot = new();
        BuiltBundle bundle = BuildBundle(("model.obj", Bytes(ObjContent)));
        File.Delete(System.IO.Path.Combine(bundle.Directory, "model.obj"));

        Tripo.Bridge.BridgeCallException exception =
            await Assert.ThrowsAsync<Tripo.Bridge.BridgeCallException>(
                () => Tripo.Bridge.StagedArtifactLoader.LoadPreparedObjAsync(
                    CreateRequest(bundle, "model.obj", null, false),
                    CancellationToken.None));

        Assert.Equal("artifact_missing", exception.Code);
    }

    [Fact]
    public async Task LoadPreparedObjAsyncRejectsHashMismatch()
    {
        using TemporaryDataRoot dataRoot = new();
        byte[] original = Bytes(ObjContent);
        BuiltBundle bundle = BuildBundle(("model.obj", original));
        byte[] tampered = (byte[])original.Clone();
        tampered[0] ^= 0xFF;
        await File.WriteAllBytesAsync(
            System.IO.Path.Combine(bundle.Directory, "model.obj"),
            tampered);

        Tripo.Bridge.BridgeCallException exception =
            await Assert.ThrowsAsync<Tripo.Bridge.BridgeCallException>(
                () => Tripo.Bridge.StagedArtifactLoader.LoadPreparedObjAsync(
                    CreateRequest(bundle, "model.obj", null, false),
                    CancellationToken.None));

        Assert.Equal("artifact_hash_mismatch", exception.Code);
    }

    [Fact]
    public async Task LoadPreparedObjAsyncRejectsMissingManifest()
    {
        using TemporaryDataRoot dataRoot = new();
        BuiltBundle bundle = BuildBundle(("model.obj", Bytes(ObjContent)));
        File.Delete(System.IO.Path.Combine(bundle.Directory, "manifest.json"));

        Tripo.Bridge.BridgeCallException exception =
            await Assert.ThrowsAsync<Tripo.Bridge.BridgeCallException>(
                () => Tripo.Bridge.StagedArtifactLoader.LoadPreparedObjAsync(
                    CreateRequest(bundle, "model.obj", null, false),
                    CancellationToken.None));

        Assert.Equal("bundle_invalid", exception.Code);
    }

    [Fact]
    public async Task LoadPreparedObjAsyncRejectsMtlEntryWithoutMtlExtension()
    {
        using TemporaryDataRoot dataRoot = new();
        BuiltBundle bundle = BuildBundle(
            ("model.obj", Bytes(ObjContent)),
            ("palette.png", Bytes("PNG-BYTES")));
        Tripo.Bridge.ImportMeshRequest request = CreateRequest(
            bundle,
            objEntry: "model.obj",
            mtlEntry: "palette.png",
            applyMaterials: false);

        Tripo.Bridge.BridgeCallException exception =
            await Assert.ThrowsAsync<Tripo.Bridge.BridgeCallException>(
                () => Tripo.Bridge.StagedArtifactLoader.LoadPreparedObjAsync(
                    request,
                    CancellationToken.None));

        Assert.Equal("bundle_invalid", exception.Code);
    }

    [Fact]
    public async Task LoadPreparedObjAsyncRejectsNonCanonicalIdempotencyUuid()
    {
        using TemporaryDataRoot dataRoot = new();
        BuiltBundle bundle = BuildBundle(("model.obj", Bytes(ObjContent)));
        Tripo.Bridge.ImportMeshRequest request =
            CreateRequest(bundle, "model.obj", null, false) with
            {
                IdempotencyKey =
                    "abcdefab-cdef-abcd-efab-cdefabcdefab".ToUpperInvariant(),
            };

        Tripo.Bridge.BridgeCallException exception =
            await Assert.ThrowsAsync<Tripo.Bridge.BridgeCallException>(
                () => Tripo.Bridge.StagedArtifactLoader.LoadPreparedObjAsync(
                    request,
                    CancellationToken.None));

        Assert.Equal("idempotency_key_invalid", exception.Code);
    }

    [Fact]
    public async Task LoadPreparedGlbAsyncReturnsVerifiedImmutableSnapshot()
    {
        using TemporaryDataRoot dataRoot = new();
        byte[] glb = GlbContainerValidatorTests.BuildGlb(
            """
            {"asset":{"version":"2.0"},"buffers":[{"byteLength":4}]}
            """,
            [1, 2, 3, 4]);
        BuiltBundle bundle = BuildGlbArtifact(glb);
        Tripo.Bridge.ImportGlbRequest request =
            CreateGlbRequest(bundle, "model.glb");

        Tripo.Bridge.PreparedGlbArtifact prepared =
            await Tripo.Bridge.StagedArtifactLoader.LoadPreparedGlbAsync(
                request,
                CancellationToken.None);

        Assert.Equal(bundle.BundleId, prepared.ArtifactId);
        Assert.Equal("model.glb", prepared.GlbEntry);
        Assert.Equal(request.Entry, prepared.Entry);
        Assert.Equal(glb, prepared.VerifiedContent.ToArray());

        byte[] replacement = GlbContainerValidatorTests.BuildGlb(
            """{"asset":{"version":"2.0"},"scene":0,"scenes":[{}]}""");
        await File.WriteAllBytesAsync(
            System.IO.Path.Combine(bundle.Directory, "model.glb"),
            replacement);

        Assert.Equal(glb, prepared.VerifiedContent.ToArray());
    }

    [Fact]
    public async Task LoadPreparedGlbAsyncRejectsHashMismatch()
    {
        using TemporaryDataRoot dataRoot = new();
        byte[] glb = GlbContainerValidatorTests.BuildGlb(
            """{"asset":{"version":"2.0"}}""");
        BuiltBundle bundle = BuildGlbArtifact(glb);
        byte[] tampered = (byte[])glb.Clone();
        tampered[^1] ^= 1;
        await File.WriteAllBytesAsync(
            System.IO.Path.Combine(bundle.Directory, "model.glb"),
            tampered);

        Tripo.Bridge.BridgeCallException exception =
            await Assert.ThrowsAsync<Tripo.Bridge.BridgeCallException>(
                () => Tripo.Bridge.StagedArtifactLoader.LoadPreparedGlbAsync(
                    CreateGlbRequest(bundle, "model.glb"),
                    CancellationToken.None));

        Assert.Equal("artifact_hash_mismatch", exception.Code);
    }

    [Fact]
    public async Task LoadPreparedGlbAsyncRejectsInvalidContainerAfterHashVerification()
    {
        using TemporaryDataRoot dataRoot = new();
        byte[] notGlb = Bytes("not a GLB container");
        BuiltBundle bundle = BuildGlbArtifact(notGlb);

        Tripo.Bridge.BridgeCallException exception =
            await Assert.ThrowsAsync<Tripo.Bridge.BridgeCallException>(
                () => Tripo.Bridge.StagedArtifactLoader.LoadPreparedGlbAsync(
                    CreateGlbRequest(bundle, "model.glb"),
                    CancellationToken.None));

        Assert.Equal("glb_invalid", exception.Code);
    }

    [Fact]
    public async Task LoadPreparedGlbAsyncRejectsMissingManifest()
    {
        using TemporaryDataRoot dataRoot = new();
        BuiltBundle bundle = BuildGlbArtifact(
            GlbContainerValidatorTests.BuildGlb(
                """{"asset":{"version":"2.0"}}"""));
        File.Delete(System.IO.Path.Combine(bundle.Directory, "manifest.json"));

        Tripo.Bridge.BridgeCallException exception =
            await Assert.ThrowsAsync<Tripo.Bridge.BridgeCallException>(
                () => Tripo.Bridge.StagedArtifactLoader.LoadPreparedGlbAsync(
                    CreateGlbRequest(bundle, "model.glb"),
                    CancellationToken.None));

        Assert.Equal("artifact_missing", exception.Code);
    }

    [Fact]
    public async Task LoadPreparedGlbAsyncRejectsMismatchedManifest()
    {
        using TemporaryDataRoot dataRoot = new();
        BuiltBundle bundle = BuildGlbArtifact(
            GlbContainerValidatorTests.BuildGlb(
                """{"asset":{"version":"2.0"}}"""));
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(bundle.Directory, "manifest.json"),
            "{}");

        Tripo.Bridge.BridgeCallException exception =
            await Assert.ThrowsAsync<Tripo.Bridge.BridgeCallException>(
                () => Tripo.Bridge.StagedArtifactLoader.LoadPreparedGlbAsync(
                    CreateGlbRequest(bundle, "model.glb"),
                    CancellationToken.None));

        Assert.Equal("artifact_invalid", exception.Code);
    }

    [Fact]
    public async Task LoadPreparedGlbAsyncRejectsArtifactIdNotBoundToEntry()
    {
        using TemporaryDataRoot dataRoot = new();
        BuiltBundle bundle = BuildGlbArtifact(
            GlbContainerValidatorTests.BuildGlb(
                """{"asset":{"version":"2.0"}}"""));
        Tripo.Bridge.ImportGlbRequest request =
            CreateGlbRequest(bundle, "model.glb") with
            {
                ArtifactId = new string('a', 64),
            };

        Tripo.Bridge.BridgeCallException exception =
            await Assert.ThrowsAsync<Tripo.Bridge.BridgeCallException>(
                () => Tripo.Bridge.StagedArtifactLoader.LoadPreparedGlbAsync(
                    request,
                    CancellationToken.None));

        Assert.Equal("artifact_invalid", exception.Code);
    }

    [Fact]
    public async Task LoadPreparedGlbAsyncRejectsSymlinkedEntryOnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDataRoot dataRoot = new();
        byte[] glb = GlbContainerValidatorTests.BuildGlb(
            """{"asset":{"version":"2.0"}}""");
        BuiltBundle bundle = BuildGlbArtifact(glb);
        string entryPath = System.IO.Path.Combine(bundle.Directory, "model.glb");
        string targetPath = System.IO.Path.Combine(dataRoot.Path, "outside.glb");
        await File.WriteAllBytesAsync(targetPath, glb);
        File.Delete(entryPath);
        File.CreateSymbolicLink(entryPath, targetPath);

        Tripo.Bridge.BridgeCallException exception =
            await Assert.ThrowsAsync<Tripo.Bridge.BridgeCallException>(
                () => Tripo.Bridge.StagedArtifactLoader.LoadPreparedGlbAsync(
                    CreateGlbRequest(bundle, "model.glb"),
                    CancellationToken.None));

        Assert.Equal("artifact_invalid", exception.Code);
    }

    private static Tripo.Bridge.ImportMeshRequest CreateRequest(
        BuiltBundle bundle,
        string objEntry,
        string? mtlEntry,
        bool applyMaterials) =>
        new(
            Guid.NewGuid().ToString("D"),
            bundle.BundleId,
            objEntry,
            mtlEntry,
            bundle.Entries,
            "meters",
            "Z",
            "right",
            "Test mesh",
            Guid.NewGuid().ToString("D"),
            "mesh",
            applyMaterials);

    private static Tripo.Bridge.ImportGlbRequest CreateGlbRequest(
        BuiltBundle bundle,
        string glbEntry)
    {
        Tripo.Bridge.StagedBundleEntry entry = bundle.Entries.Single(
            candidate => string.Equals(
                candidate.RelativePath,
                glbEntry,
                StringComparison.Ordinal));
        return new Tripo.Bridge.ImportGlbRequest(
            Guid.NewGuid().ToString("D"),
            bundle.BundleId,
            glbEntry,
            entry,
            "Test GLB",
            Guid.NewGuid().ToString("D"),
            ApplyMaterials: true);
    }

    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);

    private static BuiltBundle BuildBundle(
        params (string RelativePath, byte[] Content)[] files)
    {
        List<Tripo.Bridge.StagedBundleEntry> entries = files
            .Select(file => new Tripo.Bridge.StagedBundleEntry(
                file.RelativePath,
                Convert.ToHexString(SHA256.HashData(file.Content)).ToLowerInvariant(),
                file.Content.Length))
            .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)
            .ToList();

        StringBuilder manifest = new();
        foreach (Tripo.Bridge.StagedBundleEntry entry in entries)
        {
            manifest.Append(entry.RelativePath).Append('\n')
                .Append(entry.Sha256).Append('\n')
                .Append(entry.ByteLength).Append('\n');
        }

        string bundleId = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(manifest.ToString())))
            .ToLowerInvariant();
        string bundleDirectory = System.IO.Path.Combine(
            Tripo.Bridge.BridgePaths.GetStagingDirectory(),
            bundleId);
        Directory.CreateDirectory(bundleDirectory);
        foreach ((string relativePath, byte[] content) in files)
        {
            string fullPath = System.IO.Path.Combine(bundleDirectory, relativePath);
            string? parent = System.IO.Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            File.WriteAllBytes(fullPath, content);
        }

        File.WriteAllText(
            System.IO.Path.Combine(bundleDirectory, "manifest.json"),
            manifest.ToString());
        return new BuiltBundle(bundleId, entries, bundleDirectory);
    }

    private static BuiltBundle BuildGlbArtifact(byte[] content)
    {
        Tripo.Bridge.StagedBundleEntry entry = new(
            "model.glb",
            Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
            content.Length);
        string descriptor =
            entry.Sha256 + "\n" +
            entry.ByteLength.ToString(
                System.Globalization.CultureInfo.InvariantCulture) + "\n";
        string artifactId = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(descriptor)))
            .ToLowerInvariant();
        string artifactDirectory = System.IO.Path.Combine(
            Tripo.Bridge.BridgePaths.GetStagingDirectory(),
            artifactId);
        Directory.CreateDirectory(artifactDirectory);
        File.WriteAllBytes(
            System.IO.Path.Combine(artifactDirectory, entry.RelativePath),
            content);
        File.WriteAllText(
            System.IO.Path.Combine(artifactDirectory, "manifest.json"),
            System.Text.Json.JsonSerializer.Serialize(
                new
                {
                    artifactId,
                    glbEntry = entry.RelativePath,
                    entry,
                },
                Tripo.Bridge.BridgeJson.Options));
        return new BuiltBundle(artifactId, [entry], artifactDirectory);
    }

    private sealed record BuiltBundle(
        string BundleId,
        IReadOnlyList<Tripo.Bridge.StagedBundleEntry> Entries,
        string Directory);
}
