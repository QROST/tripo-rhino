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

    private sealed record BuiltBundle(
        string BundleId,
        IReadOnlyList<Tripo.Bridge.StagedBundleEntry> Entries,
        string Directory);
}
