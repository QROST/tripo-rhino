using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Tripo.Bridge;

public static partial class StagedArtifactLoader
{
    private const string ManifestFileName = "manifest.json";

    public static async Task<PreparedMesh> LoadPreparedObjAsync(
        ImportMeshRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateImportMetadata(request);
        return await LoadPreparedObjAsync(
                new StagedMeshLoadRequest(
                    request.BundleId,
                    request.ObjEntry,
                    request.MtlEntry,
                    request.Entries,
                    request.SourceUnit,
                    request.UpAxis,
                    request.Handedness,
                    request.ApplyMaterials),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public static async Task<PreparedMesh> LoadPreparedObjAsync(
        StagedMeshLoadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateStagedRequest(request);

        string stagingRoot = Path.GetFullPath(BridgePaths.GetStagingDirectory());
        string bundleDirectory = Path.GetFullPath(
            Path.Combine(stagingRoot, request.BundleId));
        if (!IsContained(bundleDirectory, stagingRoot))
        {
            throw new BridgeCallException(
                "bundle_invalid",
                "The staged bundle resolved outside the staging directory.");
        }

        string manifestPath = Path.Combine(bundleDirectory, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new BridgeCallException(
                "bundle_invalid",
                "The staged bundle is missing its manifest completion marker.");
        }

        string bundlePrefix = bundleDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? bundleDirectory
            : bundleDirectory + Path.DirectorySeparatorChar;
        foreach (StagedBundleEntry entry in request.Entries)
        {
            await VerifyEntryAsync(
                    bundleDirectory,
                    bundlePrefix,
                    entry,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        StagedBundleEntry objEntry = FindEntry(request.Entries, request.ObjEntry);
        ParsedObjMesh parsed = await ParseObjAsync(
                Path.Combine(bundleDirectory, objEntry.RelativePath),
                objEntry.ByteLength,
                cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<ObjMaterial> materials = [];
        if (request.ApplyMaterials && request.MtlEntry is not null)
        {
            StagedBundleEntry mtlEntry = FindEntry(request.Entries, request.MtlEntry);
            materials = await ParseMtlAsync(
                    Path.Combine(bundleDirectory, mtlEntry.RelativePath),
                    mtlEntry.ByteLength,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return MeshPreparation.Prepare(
            parsed,
            request.SourceUnit,
            request.UpAxis,
            request.Handedness,
            bundleDirectory,
            materials,
            request.Entries,
            request.ApplyMaterials);
    }

    private static async Task VerifyEntryAsync(
        string bundleDirectory,
        string bundlePrefix,
        StagedBundleEntry entry,
        CancellationToken cancellationToken)
    {
        string entryPath = Path.GetFullPath(
            Path.Combine(bundleDirectory, entry.RelativePath));
        if (!entryPath.StartsWith(bundlePrefix, StringComparison.Ordinal))
        {
            throw new BridgeCallException(
                "bundle_invalid",
                "A staged bundle entry resolved outside the bundle directory.");
        }

        if (!File.Exists(entryPath))
        {
            throw new BridgeCallException(
                "artifact_missing",
                "A staged bundle entry is missing from the bundle directory.");
        }

        await using FileStream stream = new(
            entryPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != entry.ByteLength)
        {
            throw new BridgeCallException(
                "artifact_hash_mismatch",
                "A staged bundle entry byte length does not match the manifest.");
        }

        byte[] digest = await SHA256.HashDataAsync(stream, cancellationToken)
            .ConfigureAwait(false);
        string actualHash = Convert.ToHexString(digest).ToLowerInvariant();
        if (!string.Equals(actualHash, entry.Sha256, StringComparison.Ordinal))
        {
            throw new BridgeCallException(
                "artifact_hash_mismatch",
                "A staged bundle entry SHA-256 does not match the manifest.");
        }
    }

    private static async Task<ParsedObjMesh> ParseObjAsync(
        string path,
        long byteLength,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ObjParser.ParseAsync(
                stream,
                byteLength,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<ObjMaterial>> ParseMtlAsync(
        string path,
        long byteLength,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await MtlParser.ParseAsync(
                stream,
                byteLength,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private static StagedBundleEntry FindEntry(
        IReadOnlyList<StagedBundleEntry> entries,
        string relativePath)
    {
        foreach (StagedBundleEntry entry in entries)
        {
            if (string.Equals(entry.RelativePath, relativePath, StringComparison.Ordinal))
            {
                return entry;
            }
        }

        throw new BridgeCallException(
            "bundle_invalid",
            "A referenced bundle entry is not present in the manifest entries.");
    }

    private static void ValidateStagedRequest(StagedMeshLoadRequest request)
    {
        if (!HexHashRegex().IsMatch(request.BundleId))
        {
            throw new BridgeCallException(
                "bundle_invalid",
                "bundleId must be 64 lowercase hexadecimal characters.");
        }

        if (request.Entries is null ||
            request.Entries.Count < 1 ||
            request.Entries.Count > BridgeConstants.MaximumBundleFiles)
        {
            throw new BridgeCallException(
                "bundle_invalid",
                $"A bundle must contain 1 to {BridgeConstants.MaximumBundleFiles} entries.");
        }

        long aggregateBytes = 0;
        foreach (StagedBundleEntry entry in request.Entries)
        {
            if (!IsValidRelativePath(entry.RelativePath))
            {
                throw new BridgeCallException(
                    "bundle_invalid",
                    "A bundle entry has an invalid relative path.");
            }

            if (!HexHashRegex().IsMatch(entry.Sha256))
            {
                throw new BridgeCallException(
                    "bundle_invalid",
                    "A bundle entry sha256 must be 64 lowercase hexadecimal characters.");
            }

            if (entry.ByteLength <= 0 ||
                entry.ByteLength > BridgeConstants.MaximumArtifactBytes)
            {
                throw new BridgeCallException(
                    "bundle_invalid",
                    "A bundle entry byte length is outside the allowed range.");
            }

            aggregateBytes += entry.ByteLength;
        }

        if (aggregateBytes > BridgeConstants.MaximumBundleBytes)
        {
            throw new BridgeCallException(
                "bundle_invalid",
                "The bundle aggregate byte length exceeds the allowed range.");
        }

        if (!IsValidRelativePath(request.ObjEntry) ||
            !request.ObjEntry.EndsWith(".obj", StringComparison.OrdinalIgnoreCase) ||
            !ContainsEntry(request.Entries, request.ObjEntry))
        {
            throw new BridgeCallException(
                "bundle_invalid",
                "objEntry must be a bundle entry ending in .obj.");
        }

        if (request.MtlEntry is not null &&
            (!IsValidRelativePath(request.MtlEntry) ||
             !request.MtlEntry.EndsWith(".mtl", StringComparison.OrdinalIgnoreCase) ||
             !ContainsEntry(request.Entries, request.MtlEntry)))
        {
            throw new BridgeCallException(
                "bundle_invalid",
                "mtlEntry must be null or a bundle entry ending in .mtl.");
        }

    }

    private static void ValidateImportMetadata(ImportMeshRequest request)
    {
        if (!IsCanonicalGuid(request.DocumentSessionId))
        {
            throw new BridgeCallException(
                "document_session_invalid",
                "documentSessionId must be a canonical lowercase D-format UUID.");
        }

        if (!IsCanonicalGuid(request.IdempotencyKey))
        {
            throw new BridgeCallException(
                "idempotency_key_invalid",
                "idempotencyKey must be a canonical lowercase D-format UUID.");
        }

        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 128)
        {
            throw new BridgeCallException(
                "name_invalid",
                "The imported object name must contain 1 to 128 characters.");
        }
    }

    private static bool ContainsEntry(
        IReadOnlyList<StagedBundleEntry> entries,
        string relativePath)
    {
        foreach (StagedBundleEntry entry in entries)
        {
            if (string.Equals(entry.RelativePath, relativePath, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsValidRelativePath(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128)
        {
            return false;
        }

        if (value.StartsWith('/') || value.StartsWith('\\'))
        {
            return false;
        }

        string[] segments = value.Split('/', '\\');
        foreach (string segment in segments)
        {
            if (segment.Length == 0 ||
                segment == "." ||
                segment == ".." ||
                segment.Contains(':', StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsContained(string candidate, string root)
    {
        string prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.Ordinal);
    }

    private static bool IsCanonicalGuid(string value) =>
        Guid.TryParseExact(value, "D", out Guid parsed) &&
        string.Equals(parsed.ToString("D"), value, StringComparison.Ordinal);

    [GeneratedRegex("^[a-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex HexHashRegex();
}
