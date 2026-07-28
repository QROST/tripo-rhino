using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tripo.Bridge;

public static partial class StagedArtifactLoader
{
    private const string ManifestFileName = "manifest.json";

    public static async Task<PreparedGlbArtifact> LoadPreparedGlbAsync(
        ImportGlbRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateGlbRequest(request);

        string stagingRoot = Path.GetFullPath(BridgePaths.GetStagingDirectory());
        EnsureSafeDirectory(stagingRoot, "The staging root");
        string artifactDirectory = Path.GetFullPath(
            Path.Combine(stagingRoot, request.ArtifactId));
        if (!IsContained(artifactDirectory, stagingRoot))
        {
            throw new BridgeCallException(
                "artifact_invalid",
                "The staged GLB artifact resolved outside the staging directory.");
        }

        EnsureSafeDirectory(artifactDirectory, "The staged GLB artifact directory");
        string manifestPath = Path.Combine(artifactDirectory, ManifestFileName);
        EnsureSafeRegularFile(
            manifestPath,
            "The staged GLB artifact manifest",
            maximumLength: 64 * 1024);
        await ValidateGlbManifestAsync(
                manifestPath,
                request,
                cancellationToken)
            .ConfigureAwait(false);

        string entryPath = Path.GetFullPath(
            Path.Combine(artifactDirectory, request.GlbEntry));
        string artifactPrefix =
            artifactDirectory.EndsWith(Path.DirectorySeparatorChar)
                ? artifactDirectory
                : artifactDirectory + Path.DirectorySeparatorChar;
        if (!entryPath.StartsWith(artifactPrefix, StringComparison.Ordinal))
        {
            throw new BridgeCallException(
                "artifact_invalid",
                "The staged GLB entry resolved outside its artifact directory.");
        }

        EnsureSafeEntryDirectories(artifactDirectory, request.GlbEntry);
        EnsureSafeRegularFile(
            entryPath,
            "The staged GLB entry",
            BridgeConstants.MaximumArtifactBytes);
        byte[] content = await ReadVerifiedEntryAsync(
                entryPath,
                request.Entry,
                cancellationToken)
            .ConfigureAwait(false);
        _ = GlbContainerValidator.Validate(content);
        return new PreparedGlbArtifact(
            request.ArtifactId,
            request.GlbEntry,
            request.Entry,
            content);
    }

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

    private static void ValidateGlbRequest(ImportGlbRequest request)
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

        if (!HexHashRegex().IsMatch(request.ArtifactId))
        {
            throw new BridgeCallException(
                "artifact_invalid",
                "artifactId must be 64 lowercase hexadecimal characters.");
        }

        if (request.Entry is null ||
            !IsValidRelativePath(request.GlbEntry) ||
            !request.GlbEntry.EndsWith(
                ".glb",
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                request.Entry.RelativePath,
                request.GlbEntry,
                StringComparison.Ordinal))
        {
            throw new BridgeCallException(
                "artifact_invalid",
                "glbEntry must exactly match one staged .glb entry.");
        }

        if (!HexHashRegex().IsMatch(request.Entry.Sha256) ||
            request.Entry.ByteLength <= 0 ||
            request.Entry.ByteLength >
                BridgeConstants.MaximumGlbArtifactBytes)
        {
            throw new BridgeCallException(
                "artifact_invalid",
                "The staged GLB entry hash or byte length is invalid.");
        }

        string expectedArtifactId = ComputeSingleEntryArtifactId(request.Entry);
        if (!string.Equals(
                request.ArtifactId,
                expectedArtifactId,
                StringComparison.Ordinal))
        {
            throw new BridgeCallException(
                "artifact_invalid",
                "artifactId does not match the staged GLB entry manifest.");
        }
    }

    private static string ComputeSingleEntryArtifactId(StagedBundleEntry entry)
    {
        string manifest =
            entry.Sha256 + "\n" +
            entry.ByteLength.ToString(CultureInfo.InvariantCulture) + "\n";
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(manifest)))
            .ToLowerInvariant();
    }

    private static async Task<byte[]> ReadVerifiedEntryAsync(
        string entryPath,
        StagedBundleEntry entry,
        CancellationToken cancellationToken)
    {
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
                "The staged GLB entry byte length does not match its descriptor.");
        }

        byte[] content = new byte[checked((int)entry.ByteLength)];
        await stream.ReadExactlyAsync(content, cancellationToken)
            .ConfigureAwait(false);
        byte[] trailing = new byte[1];
        if (await stream.ReadAsync(trailing, cancellationToken).ConfigureAwait(false) != 0)
        {
            throw new BridgeCallException(
                "artifact_hash_mismatch",
                "The staged GLB entry changed while it was being verified.");
        }

        string actualHash =
            Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        if (!string.Equals(actualHash, entry.Sha256, StringComparison.Ordinal))
        {
            throw new BridgeCallException(
                "artifact_hash_mismatch",
                "The staged GLB entry SHA-256 does not match its descriptor.");
        }

        return content;
    }

    private static async Task ValidateGlbManifestAsync(
        string manifestPath,
        ImportGlbRequest request,
        CancellationToken cancellationToken)
    {
        byte[] payload = await File.ReadAllBytesAsync(
                manifestPath,
                cancellationToken)
            .ConfigureAwait(false);
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("The GLB artifact manifest must be an object.");
            }

            HashSet<string> expectedProperties =
            [
                "artifactId",
                "glbEntry",
                "entry",
            ];
            HashSet<string> seenProperties = new(StringComparer.Ordinal);
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (!expectedProperties.Contains(property.Name) ||
                    !seenProperties.Add(property.Name))
                {
                    throw new JsonException(
                        "The GLB artifact manifest properties are invalid.");
                }
            }

            if (seenProperties.Count != expectedProperties.Count)
            {
                throw new JsonException(
                    "The GLB artifact manifest is incomplete.");
            }

            GlbArtifactManifest manifest =
                root.Deserialize<GlbArtifactManifest>(BridgeJson.Options)
                ?? throw new JsonException("The GLB artifact manifest is null.");
            if (!string.Equals(
                    manifest.ArtifactId,
                    request.ArtifactId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    manifest.GlbEntry,
                    request.GlbEntry,
                    StringComparison.Ordinal) ||
                !Equals(manifest.Entry, request.Entry))
            {
                throw new JsonException(
                    "The GLB artifact manifest does not match the request.");
            }
        }
        catch (JsonException exception)
        {
            throw new BridgeCallException(
                "artifact_invalid",
                "The staged GLB artifact manifest is invalid.",
                exception);
        }
    }

    private static void EnsureSafeEntryDirectories(
        string artifactDirectory,
        string relativePath)
    {
        string[] segments = relativePath.Split('/', '\\');
        string current = artifactDirectory;
        for (int index = 0; index < segments.Length - 1; index++)
        {
            current = Path.Combine(current, segments[index]);
            EnsureSafeDirectory(current, "A staged GLB entry directory");
        }
    }

    private static void EnsureSafeDirectory(string path, string subject)
    {
        DirectoryInfo directory = new(path);
        directory.Refresh();
        if (!directory.Exists)
        {
            throw new BridgeCallException(
                "artifact_missing",
                $"{subject} is missing.");
        }

        if (IsLinkOrReparsePoint(directory))
        {
            throw new BridgeCallException(
                "artifact_invalid",
                $"{subject} must not be a symbolic link or reparse point.");
        }
    }

    private static void EnsureSafeRegularFile(
        string path,
        string subject,
        long maximumLength)
    {
        FileInfo file = new(path);
        file.Refresh();
        if (!file.Exists)
        {
            throw new BridgeCallException(
                "artifact_missing",
                $"{subject} is missing.");
        }

        if (IsLinkOrReparsePoint(file))
        {
            throw new BridgeCallException(
                "artifact_invalid",
                $"{subject} must not be a symbolic link or reparse point.");
        }

        if (file.Length <= 0 || file.Length > maximumLength)
        {
            throw new BridgeCallException(
                "artifact_invalid",
                $"{subject} has an invalid byte length.");
        }
    }

    private static bool IsLinkOrReparsePoint(FileSystemInfo info) =>
        info.LinkTarget is not null ||
        (info.Attributes & FileAttributes.ReparsePoint) != 0;

    private sealed record GlbArtifactManifest(
        string ArtifactId,
        string GlbEntry,
        StagedBundleEntry Entry);

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
