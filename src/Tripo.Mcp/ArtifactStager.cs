using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Tripo.Mcp;

public interface IArtifactStager
{
    Task<Tripo.Bridge.StagedGlbArtifact> StageGlbAsync(
        Uri modelUri,
        CancellationToken cancellationToken);

    Task<Tripo.Bridge.StagedBundle> StageBundleAsync(
        Uri modelUri,
        CancellationToken cancellationToken);
}

public sealed class ArtifactStager : IArtifactStager
{
    private const int MaximumRedirects = 3;
    private const string GlbFileName = "model.glb";
    private const string ManifestFileName = "manifest.json";
    private const string CollisionMessage =
        "A content-addressed staging collision was detected.";
    private static readonly TimeSpan DefaultDownloadTimeout = TimeSpan.FromMinutes(5);
    private static readonly string[] TextureExtensions = [".png", ".jpg", ".jpeg"];
    private readonly HttpClient _httpClient;
    private readonly TimeSpan _downloadTimeout;

    public ArtifactStager(HttpClient httpClient)
        : this(httpClient, DefaultDownloadTimeout)
    {
    }

    internal ArtifactStager(HttpClient httpClient, TimeSpan downloadTimeout)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            downloadTimeout,
            TimeSpan.Zero);
        _downloadTimeout = downloadTimeout;
    }

    public async Task<Tripo.Bridge.StagedGlbArtifact> StageGlbAsync(
        Uri modelUri,
        CancellationToken cancellationToken)
    {
        ValidateRemoteUri(modelUri);
        string stagingDirectory = GetSafeStagingDirectory();
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(12))
            .ToLowerInvariant();
        string downloadedPath = Path.Combine(stagingDirectory, token + ".download");

        try
        {
            await DownloadAsync(modelUri, downloadedPath, cancellationToken)
                .ConfigureAwait(false);
            (string sha256, long byteLength) = await ValidateAndDescribeGlbAsync(
                    downloadedPath,
                    cancellationToken)
                .ConfigureAwait(false);
            Tripo.Bridge.StagedBundleEntry entry = new(
                GlbFileName,
                sha256,
                byteLength);
            string artifactId = ComputeGlbArtifactId(entry);
            string artifactDirectory = Path.Combine(stagingDirectory, artifactId);
            string manifestPath = Path.Combine(
                artifactDirectory,
                ManifestFileName);
            string manifestJson = JsonSerializer.Serialize(new GlbManifest(
                artifactId,
                entry.RelativePath,
                entry),
                Tripo.Bridge.BridgeJson.Options);

            if (File.Exists(manifestPath))
            {
                await VerifyExistingGlbArtifactAsync(
                        artifactDirectory,
                        manifestPath,
                        entry,
                        manifestJson,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await WriteGlbArtifactAsync(
                        artifactDirectory,
                        manifestPath,
                        token,
                        downloadedPath,
                        entry,
                        manifestJson,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return new Tripo.Bridge.StagedGlbArtifact(
                artifactId,
                GlbFileName,
                entry,
                Path.GetFullPath(artifactDirectory));
        }
        finally
        {
            Tripo.Bridge.BridgePaths.TryDelete(downloadedPath);
        }
    }

    public async Task<Tripo.Bridge.StagedBundle> StageBundleAsync(
        Uri modelUri,
        CancellationToken cancellationToken)
    {
        ValidateRemoteUri(modelUri);
        string stagingDirectory = GetSafeStagingDirectory();
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(12))
            .ToLowerInvariant();
        string downloadedPath = Path.Combine(stagingDirectory, token + ".download");
        string workDirectory = Path.Combine(stagingDirectory, token + ".work");

        try
        {
            await DownloadAsync(modelUri, downloadedPath, cancellationToken)
                .ConfigureAwait(false);
            List<EntryDraft> drafts =
                await IsZipAsync(downloadedPath, cancellationToken).ConfigureAwait(false)
                    ? await ExtractBundleAsync(
                            downloadedPath,
                            workDirectory,
                            cancellationToken)
                        .ConfigureAwait(false)
                    : await StageSingleObjAsync(downloadedPath, cancellationToken)
                        .ConfigureAwait(false);

            drafts.Sort(
                (left, right) => string.CompareOrdinal(
                    left.RelativePath,
                    right.RelativePath));
            List<Tripo.Bridge.StagedBundleEntry> entries = drafts
                .Select(draft => new Tripo.Bridge.StagedBundleEntry(
                    draft.RelativePath,
                    draft.Sha256,
                    draft.ByteLength))
                .ToList();
            string objEntry = drafts
                .Single(draft => HasExtension(draft.RelativePath, ".obj"))
                .RelativePath;
            string? mtlEntry = drafts
                .SingleOrDefault(draft => HasExtension(draft.RelativePath, ".mtl"))
                ?.RelativePath;
            string bundleId = ComputeBundleId(entries);
            string bundleDirectory = Path.Combine(stagingDirectory, bundleId);
            string manifestPath = Path.Combine(bundleDirectory, ManifestFileName);

            if (File.Exists(manifestPath))
            {
                await VerifyExistingBundleAsync(
                        bundleDirectory,
                        entries,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await WriteBundleAsync(
                        bundleDirectory,
                        manifestPath,
                        token,
                        drafts,
                        entries,
                        objEntry,
                        mtlEntry,
                        bundleId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return new Tripo.Bridge.StagedBundle(
                bundleId,
                objEntry,
                mtlEntry,
                entries,
                Path.GetFullPath(bundleDirectory));
        }
        finally
        {
            Tripo.Bridge.BridgePaths.TryDelete(downloadedPath);
            TryDeleteDirectory(workDirectory);
        }
    }

    private static async Task<(string Sha256, long ByteLength)>
        ValidateAndDescribeGlbAsync(
            string downloadedPath,
            CancellationToken cancellationToken)
    {
        FileInfo downloaded = new(downloadedPath);
        downloaded.Refresh();
        if (!downloaded.Exists ||
            downloaded.Length <= 0 ||
            downloaded.Length >
                Tripo.Bridge.BridgeConstants.MaximumGlbArtifactBytes)
        {
            throw new TripoApiException(
                "The downloaded GLB was empty or exceeded the direct-import size limit.");
        }

        byte[] content = await File.ReadAllBytesAsync(
                downloadedPath,
                cancellationToken)
            .ConfigureAwait(false);
        try
        {
            Tripo.Bridge.GlbContainerValidator.Validate(content);
        }
        catch (Tripo.Bridge.BridgeCallException exception)
        {
            throw new TripoApiException(
                "The downloaded GLB container is invalid.",
                innerException: exception);
        }

        return (
            Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
            content.LongLength);
    }

    private static async Task<List<EntryDraft>> StageSingleObjAsync(
        string downloadedPath,
        CancellationToken cancellationToken)
    {
        await using FileStream content = OpenRead(downloadedPath);
        if (content.Length <= 0 ||
            content.Length > Tripo.Bridge.BridgeConstants.MaximumArtifactBytes)
        {
            throw new TripoApiException(
                "The downloaded OBJ was empty or exceeded the local size limit.");
        }

        long byteLength = content.Length;
        byte[] digest = await SHA256.HashDataAsync(content, cancellationToken)
            .ConfigureAwait(false);
        string sha256 = Convert.ToHexString(digest).ToLowerInvariant();
        return [new EntryDraft("model.obj", downloadedPath, sha256, byteLength)];
    }

    private static async Task<List<EntryDraft>> ExtractBundleAsync(
        string archivePath,
        string workDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(workDirectory);
        await using FileStream archiveStream = OpenRead(archivePath);
        using ZipArchive archive = new(
            archiveStream,
            ZipArchiveMode.Read,
            leaveOpen: false);

        List<(ZipArchiveEntry Entry, string RelativePath)> candidates = [];
        HashSet<string> keptPaths = new(StringComparer.Ordinal);
        HashSet<string> keptPathsIgnoreCase = new(StringComparer.OrdinalIgnoreCase);
        int objCount = 0;
        int mtlCount = 0;
        long declaredBytes = 0;

        // First pass: validate archive metadata (paths, counts, declared sizes)
        // before writing any bytes, so a malicious or oversized archive is rejected
        // without extraction.
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name) ||
                entry.FullName.EndsWith('/'))
            {
                continue;
            }

            string? extension = ClassifyExtension(entry.FullName);
            if (extension is null)
            {
                continue;
            }

            string? relativePath = NormalizeBundlePath(entry.FullName);
            if (relativePath is null)
            {
                throw new TripoApiException(
                    "The converted archive contains an unsafe entry path.");
            }

            // macOS and Windows staging directories are case-insensitive, so two
            // entries differing only by case would collide when PlaceEntry writes
            // them and one would silently overwrite the other. Reject the pair.
            if (!keptPaths.Add(relativePath) ||
                !keptPathsIgnoreCase.Add(relativePath))
            {
                throw new TripoApiException(
                    "The converted archive contains duplicate entry paths.");
            }

            if (candidates.Count >= Tripo.Bridge.BridgeConstants.MaximumBundleFiles)
            {
                throw new TripoApiException(
                    "The converted archive contains too many bundle files.");
            }

            if (entry.Length > Tripo.Bridge.BridgeConstants.MaximumArtifactBytes)
            {
                throw new TripoApiException(
                    "A converted archive entry exceeded the local size limit.");
            }

            declaredBytes += entry.Length;
            if (declaredBytes > Tripo.Bridge.BridgeConstants.MaximumBundleBytes)
            {
                throw new TripoApiException(
                    "The converted archive exceeded the aggregate bundle size limit.");
            }

            candidates.Add((entry, relativePath));
            if (extension == ".obj")
            {
                objCount++;
            }
            else if (extension == ".mtl")
            {
                mtlCount++;
            }
        }

        if (objCount != 1)
        {
            throw new TripoApiException(
                "The converted archive must contain exactly one OBJ file.");
        }

        if (mtlCount > 1)
        {
            throw new TripoApiException(
                "The converted archive must contain at most one MTL file.");
        }

        // Second pass: extract each validated entry, hashing and re-bounding the
        // actual bytes as defense against archive metadata that under-reports sizes.
        List<EntryDraft> drafts = new(candidates.Count);
        long aggregateBytes = 0;
        int tempIndex = 0;
        foreach ((ZipArchiveEntry entry, string relativePath) in candidates)
        {
            string tempPath = Path.Combine(workDirectory, "entry_" + tempIndex++);
            await using Stream source = entry.Open();
            (string sha256, long byteLength) = await CopyHashBoundedAsync(
                    source,
                    tempPath,
                    cancellationToken)
                .ConfigureAwait(false);
            aggregateBytes += byteLength;
            if (aggregateBytes > Tripo.Bridge.BridgeConstants.MaximumBundleBytes)
            {
                throw new TripoApiException(
                    "The converted archive exceeded the aggregate bundle size limit.");
            }

            drafts.Add(new EntryDraft(relativePath, tempPath, sha256, byteLength));
        }

        return drafts;
    }

    private async Task DownloadAsync(
        Uri initialUri,
        string outputPath,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource downloadDeadline =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        downloadDeadline.CancelAfter(_downloadTimeout);
        try
        {
            await DownloadWithRedirectsAsync(
                    initialUri,
                    outputPath,
                    downloadDeadline.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw new TripoApiException(
                "The signed model download timed out.",
                innerException: exception);
        }
        catch (IOException exception)
        {
            throw new TripoApiException(
                "The signed model download could not be completed.",
                innerException: exception);
        }
        catch (HttpRequestException exception)
        {
            throw new TripoApiException(
                "The signed model download could not be completed.",
                innerException: exception);
        }
    }

    private static async Task WriteGlbArtifactAsync(
        string artifactDirectory,
        string manifestPath,
        string token,
        string downloadedPath,
        Tripo.Bridge.StagedBundleEntry entry,
        string manifestJson,
        CancellationToken cancellationToken)
    {
        EnsurePrivateStagingDirectory(artifactDirectory);
        PlaceEntry(
            downloadedPath,
            Path.Combine(artifactDirectory, entry.RelativePath),
            artifactDirectory);
        await VerifyExistingBundleAsync(
                artifactDirectory,
                [entry],
                cancellationToken)
            .ConfigureAwait(false);

        string manifestTemp = Path.Combine(
            artifactDirectory,
            ManifestFileName + "." + token + ".tmp");
        try
        {
            await WritePrivateTextCreateNewAsync(
                    manifestTemp,
                    manifestJson,
                    cancellationToken)
                .ConfigureAwait(false);
            try
            {
                File.Move(manifestTemp, manifestPath);
            }
            catch (IOException) when (File.Exists(manifestPath))
            {
                // A concurrent writer published the completion marker. The
                // exact manifest and payload are verified below.
            }

            await VerifyExistingGlbArtifactAsync(
                    artifactDirectory,
                    manifestPath,
                    entry,
                    manifestJson,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            Tripo.Bridge.BridgePaths.TryDelete(manifestTemp);
        }
    }

    private async Task DownloadWithRedirectsAsync(
        Uri initialUri,
        string outputPath,
        CancellationToken cancellationToken)
    {
        Uri currentUri = initialUri;
        for (int redirect = 0; redirect <= MaximumRedirects; redirect++)
        {
            ValidateRemoteUri(currentUri);
            using HttpRequestMessage request = new(HttpMethod.Get, currentUri);
            using HttpResponseMessage response =
                await SendDownloadRequestAsync(request, cancellationToken)
                    .ConfigureAwait(false);

            if (IsRedirect(response.StatusCode))
            {
                if (redirect == MaximumRedirects || response.Headers.Location is null)
                {
                    throw new TripoApiException(
                        "The signed model download exceeded the redirect limit.",
                        response.StatusCode);
                }

                currentUri = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(currentUri, response.Headers.Location);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new TripoApiException(
                    $"The signed model download returned HTTP {(int)response.StatusCode}.",
                    response.StatusCode);
            }

            if (response.Content.Headers.ContentLength >
                Tripo.Bridge.BridgeConstants.MaximumArtifactBytes)
            {
                throw new TripoApiException(
                    "The signed model download exceeded the local size limit.",
                    response.StatusCode);
            }

            await using Stream source = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            await using FileStream destination = new(
                outputPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await CopyBoundedAsync(
                    source,
                    destination,
                    Tripo.Bridge.BridgeConstants.MaximumArtifactBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        throw new InvalidOperationException("The redirect loop terminated unexpectedly.");
    }

    private async Task<HttpResponseMessage> SendDownloadRequestAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new TripoApiException(
                "The signed model download could not be completed.",
                innerException: exception);
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw new TripoApiException(
                "The signed model download timed out.",
                innerException: exception);
        }
    }

    private static async Task WriteBundleAsync(
        string bundleDirectory,
        string manifestPath,
        string token,
        IReadOnlyList<EntryDraft> drafts,
        IReadOnlyList<Tripo.Bridge.StagedBundleEntry> entries,
        string objEntry,
        string? mtlEntry,
        string bundleId,
        CancellationToken cancellationToken)
    {
        EnsurePrivateStagingDirectory(bundleDirectory);
        foreach (EntryDraft draft in drafts)
        {
            PlaceEntry(
                draft.TempPath,
                Path.Combine(bundleDirectory, draft.RelativePath),
                bundleDirectory);
        }

        await VerifyExistingBundleAsync(
                bundleDirectory,
                entries,
                cancellationToken)
            .ConfigureAwait(false);
        string manifestJson = JsonSerializer.Serialize(new BundleManifest(
            bundleId,
            objEntry,
            mtlEntry,
            entries));
        string manifestTemp = Path.Combine(
            bundleDirectory,
            ManifestFileName + "." + token + ".tmp");
        try
        {
            await WritePrivateTextCreateNewAsync(
                    manifestTemp,
                    manifestJson,
                    cancellationToken)
                .ConfigureAwait(false);
            try
            {
                File.Move(manifestTemp, manifestPath);
            }
            catch (IOException) when (File.Exists(manifestPath))
            {
                await VerifyExistingBundleAsync(
                        bundleDirectory,
                        entries,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            Tripo.Bridge.BridgePaths.TryDelete(manifestTemp);
        }
    }

    private static void PlaceEntry(
        string tempPath,
        string destinationPath,
        string artifactDirectory)
    {
        EnsureSafeDestinationParent(artifactDirectory, destinationPath);
        EnsureNotReparsePoint(destinationPath);

        try
        {
            File.Move(tempPath, destinationPath);
        }
        catch (IOException) when (File.Exists(destinationPath))
        {
            // A concurrent identical writer may have won. Never overwrite it;
            // the exact handle rehash below decides whether it is reusable.
        }

        EnsureSafeRegularFile(destinationPath);
        Tripo.Bridge.BridgePaths.SetPrivateFileMode(destinationPath);
    }

    private static async Task VerifyExistingBundleAsync(
        string bundleDirectory,
        IReadOnlyList<Tripo.Bridge.StagedBundleEntry> entries,
        CancellationToken cancellationToken)
    {
        string fullBundleDirectory = Path.GetFullPath(bundleDirectory);
        EnsurePrivateStagingDirectory(fullBundleDirectory);
        foreach (Tripo.Bridge.StagedBundleEntry entry in entries)
        {
            string path = Path.Combine(fullBundleDirectory, entry.RelativePath);
            EnsureSafeDestinationParent(fullBundleDirectory, path);
            EnsureSafeRegularFile(path);

            await using FileStream existing = OpenRead(path);
            if (existing.Length != entry.ByteLength)
            {
                throw new TripoApiException(CollisionMessage);
            }

            byte[] digest = await SHA256.HashDataAsync(existing, cancellationToken)
                .ConfigureAwait(false);
            string actualHash = Convert.ToHexString(digest).ToLowerInvariant();
            if (!string.Equals(actualHash, entry.Sha256, StringComparison.Ordinal))
            {
                throw new TripoApiException(CollisionMessage);
            }
        }
    }

    private static async Task VerifyExistingGlbArtifactAsync(
        string artifactDirectory,
        string manifestPath,
        Tripo.Bridge.StagedBundleEntry entry,
        string expectedManifest,
        CancellationToken cancellationToken)
    {
        await VerifyExistingBundleAsync(
                artifactDirectory,
                [entry],
                cancellationToken)
            .ConfigureAwait(false);
        EnsureSafeDestinationParent(artifactDirectory, manifestPath);
        EnsureSafeRegularFile(manifestPath);
        byte[] expected = Encoding.UTF8.GetBytes(expectedManifest);
        await using FileStream stream = OpenRead(manifestPath);
        if (stream.Length != expected.LongLength ||
            stream.Length > 64 * 1024)
        {
            throw new TripoApiException(CollisionMessage);
        }

        byte[] actual = new byte[expected.Length];
        await stream.ReadExactlyAsync(actual, cancellationToken)
            .ConfigureAwait(false);
        if (!actual.AsSpan().SequenceEqual(expected))
        {
            throw new TripoApiException(CollisionMessage);
        }
    }

    private static void EnsurePrivateStagingDirectory(string path)
    {
        try
        {
            Tripo.Bridge.BridgePaths.EnsurePrivateNonReparseDirectory(path);
        }
        catch (InvalidOperationException exception)
        {
            throw new TripoApiException(
                CollisionMessage,
                innerException: exception);
        }
    }

    private static string GetSafeStagingDirectory()
    {
        try
        {
            return Tripo.Bridge.BridgePaths.GetStagingDirectory();
        }
        catch (InvalidOperationException exception)
        {
            throw new TripoApiException(
                CollisionMessage,
                innerException: exception);
        }
    }

    private static void EnsureSafeDestinationParent(
        string artifactDirectory,
        string destinationPath)
    {
        string root = Path.GetFullPath(artifactDirectory);
        string destination = Path.GetFullPath(destinationPath);
        string prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new TripoApiException(CollisionMessage);
        }

        EnsurePrivateStagingDirectory(root);
        string? parent = Path.GetDirectoryName(destination);
        if (parent is null)
        {
            throw new TripoApiException(CollisionMessage);
        }

        string relative = Path.GetRelativePath(root, parent);
        string current = root;
        if (!string.Equals(relative, ".", StringComparison.Ordinal))
        {
            foreach (string segment in relative.Split(
                         Path.DirectorySeparatorChar,
                         StringSplitOptions.RemoveEmptyEntries))
            {
                if (segment is "." or "..")
                {
                    throw new TripoApiException(CollisionMessage);
                }

                current = Path.Combine(current, segment);
                EnsurePrivateStagingDirectory(current);
            }
        }
    }

    private static void EnsureNotReparsePoint(string path)
    {
        FileInfo info = new(path);
        info.Refresh();
        if (info.LinkTarget is not null ||
            info.Exists &&
            (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new TripoApiException(CollisionMessage);
        }
    }

    private static void EnsureSafeRegularFile(string path)
    {
        FileInfo info = new(path);
        info.Refresh();
        if (!info.Exists ||
            info.LinkTarget is not null ||
            (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new TripoApiException(CollisionMessage);
        }
    }

    private static async Task WritePrivateTextCreateNewAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await using StreamWriter writer = new(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            4096,
            leaveOpen: true);
        await writer.WriteAsync(content.AsMemory(), cancellationToken)
            .ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
        Tripo.Bridge.BridgePaths.SetPrivateFileMode(path);
    }

    private static string ComputeBundleId(
        IReadOnlyList<Tripo.Bridge.StagedBundleEntry> entries)
    {
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

    private static string ComputeGlbArtifactId(
        Tripo.Bridge.StagedBundleEntry entry)
    {
        string descriptor = string.Concat(
            entry.Sha256,
            "\n",
            entry.ByteLength.ToString(CultureInfo.InvariantCulture),
            "\n");
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(descriptor)))
            .ToLowerInvariant();
    }

    private static async Task<(string Sha256, long ByteLength)> CopyHashBoundedAsync(
        Stream source,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        using IncrementalHash hash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        long total = 0;
        await using (FileStream destination = new(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            while (true)
            {
                int read = await source.ReadAsync(buffer, cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
                if (total > Tripo.Bridge.BridgeConstants.MaximumArtifactBytes)
                {
                    throw new TripoApiException(
                        "A converted archive entry exceeded the local size limit.");
                }

                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        if (total <= 0)
        {
            throw new TripoApiException(
                "A converted archive entry was empty.");
        }

        return (
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
            total);
    }

    private static async Task<bool> IsZipAsync(
        string path,
        CancellationToken cancellationToken)
    {
        byte[] signature = new byte[4];
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        int read = await stream.ReadAsync(signature, cancellationToken).ConfigureAwait(false);
        return read == signature.Length &&
               signature[0] == (byte)'P' &&
               signature[1] == (byte)'K' &&
               ((signature[2] == 3 && signature[3] == 4) ||
                (signature[2] == 5 && signature[3] == 6) ||
                (signature[2] == 7 && signature[3] == 8));
    }

    private static async Task CopyBoundedAsync(
        Stream source,
        Stream destination,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            total += read;
            if (total > maximumBytes)
            {
                throw new TripoApiException(
                    "The downloaded artifact exceeded the local size limit.");
            }

            await destination.WriteAsync(
                    buffer.AsMemory(0, read),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static string? ClassifyExtension(string name)
    {
        string extension = Path.GetExtension(name).ToLowerInvariant();
        if (extension is ".obj" or ".mtl")
        {
            return extension;
        }

        return Array.IndexOf(TextureExtensions, extension) >= 0 ? extension : null;
    }

    private static bool HasExtension(string name, string extension) =>
        string.Equals(
            Path.GetExtension(name),
            extension,
            StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeBundlePath(string rawName)
    {
        if (string.IsNullOrEmpty(rawName))
        {
            return null;
        }

        string forward = rawName.Replace('\\', '/');
        if (forward.Length == 0 || forward.Length > 128 || forward.StartsWith('/'))
        {
            return null;
        }

        foreach (string segment in forward.Split('/'))
        {
            if (segment.Length == 0 ||
                segment == "." ||
                segment == ".." ||
                segment.Contains(':', StringComparison.Ordinal))
            {
                return null;
            }
        }

        return forward;
    }

    private static FileStream OpenRead(string path) =>
        new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Cleanup is best effort.
        }
        catch (UnauthorizedAccessException)
        {
            // Cleanup is best effort.
        }
    }

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently or
            HttpStatusCode.Found or
            HttpStatusCode.SeeOther or
            HttpStatusCode.TemporaryRedirect or
            HttpStatusCode.PermanentRedirect;

    private static void ValidateRemoteUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new TripoApiException(
                "Signed model downloads must use an absolute HTTPS URL without user information.");
        }

        if (IPAddress.TryParse(uri.Host, out _) ||
            string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.IndexOf('.') < 0)
        {
            throw new TripoApiException(
                "The signed model download host is not allowed.");
        }
    }

    private sealed record EntryDraft(
        string RelativePath,
        string TempPath,
        string Sha256,
        long ByteLength);

    private sealed record BundleManifest(
        string BundleId,
        string ObjEntry,
        string? MtlEntry,
        IReadOnlyList<Tripo.Bridge.StagedBundleEntry> Entries);

    private sealed record GlbManifest(
        string ArtifactId,
        string GlbEntry,
        Tripo.Bridge.StagedBundleEntry Entry);
}
