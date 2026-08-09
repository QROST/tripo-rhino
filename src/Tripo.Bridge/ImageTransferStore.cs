using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Tripo.Bridge;

public static partial class ImageTransferStore
{
    private const string TransferExtension = ".image";
    private const string PngMediaType = "image/png";
    private const string JpegMediaType = "image/jpeg";
    private const string WebpMediaType = "image/webp";
    private static readonly byte[] PngSignature =
    [
        0x89,
        (byte)'P',
        (byte)'N',
        (byte)'G',
        0x0d,
        0x0a,
        0x1a,
        0x0a,
    ];

    // WebP files start with "RIFF" + 4-byte little-endian size + "WEBP". The
    // size field is variable, so only the RIFF and WEBP markers are matched.
    private static readonly byte[] RiffMarker = [(byte)'R', (byte)'I', (byte)'F', (byte)'F'];
    private static readonly byte[] WebpMarker = [(byte)'W', (byte)'E', (byte)'B', (byte)'P'];
    private const int WebpHeaderLength = 12;

    public static async Task<StagedImageTransfer> StageAsync(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        try
        {
            return await StageCoreAsync(sourcePath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (BridgeCallException)
        {
            throw;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is IOException or
                  UnauthorizedAccessException or
                  System.Security.SecurityException or
                  NotSupportedException)
        {
            throw new BridgeCallException(
                "image_stage_failed",
                "The selected image could not be copied into private staging. " +
                "The source path was not retained.",
                exception);
        }
    }

    private static async Task<StagedImageTransfer> StageCoreAsync(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        ValidateSourcePath(sourcePath);
        FileInfo sourceInfo = new(sourcePath);
        RejectLinkOrReparsePoint(sourceInfo, "The selected image");
        string extension = Path.GetExtension(sourceInfo.Name).ToLowerInvariant();
        if (extension is not ".png" and not ".jpg" and not ".jpeg" and not ".webp")
        {
            throw new BridgeCallException(
                "image_type_invalid",
                "The selected image must use a .png, .jpg, .jpeg, or .webp extension.");
        }

        await using FileStream source = new(
            sourceInfo.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        ValidateLength(source.Length);

        string directory = BridgePaths.GetImageTransferDirectory();
        RejectDirectoryLinkOrReparsePoint(directory);
        string transferId = Guid.NewGuid().ToString("D");
        string finalPath = GetTransferPath(directory, transferId);
        string temporaryPath = finalPath + "." +
            Convert.ToHexString(RandomNumberGenerator.GetBytes(8)) +
            ".tmp";
        try
        {
            (string sha256, long byteLength, string mediaType) =
                await CopyAndInspectAsync(
                        source,
                        temporaryPath,
                        cancellationToken)
                    .ConfigureAwait(false);
            EnsureExtensionMatches(extension, mediaType);
            BridgePaths.SetPrivateFileMode(temporaryPath);
            File.Move(temporaryPath, finalPath);
            BridgePaths.SetPrivateFileMode(finalPath);
            return new StagedImageTransfer(
                transferId,
                sha256,
                byteLength,
                mediaType);
        }
        finally
        {
            BridgePaths.TryDelete(temporaryPath);
        }
    }

    public static async Task<Stream> OpenVerifiedAsync(
        StagedImageTransfer transfer,
        CancellationToken cancellationToken)
    {
        ValidateDescriptor(transfer);
        string directory = BridgePaths.GetImageTransferDirectory();
        RejectDirectoryLinkOrReparsePoint(directory);
        string path = GetTransferPath(directory, transfer.TransferId);
        FileInfo info = new(path);
        RejectLinkOrReparsePoint(info, "The staged image transfer");

        FileStream stream;
        try
        {
            stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (FileNotFoundException exception)
        {
            throw new BridgeCallException(
                "image_transfer_missing",
                "The staged image transfer no longer exists.",
                exception);
        }

        await using (stream)
        {
            if (stream.Length != transfer.ByteLength)
            {
                throw new BridgeCallException(
                    "image_transfer_mismatch",
                    "The staged image transfer length does not match its descriptor.");
            }

            byte[] snapshot = GC.AllocateUninitializedArray<byte>(
                checked((int)stream.Length));
            int total = 0;
            while (total < snapshot.Length)
            {
                int read = await stream.ReadAsync(
                        snapshot.AsMemory(total),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            if (total != snapshot.Length)
            {
                throw new BridgeCallException(
                    "image_transfer_mismatch",
                    "The staged image transfer changed while it was read.");
            }

            string mediaType = DetectMediaType(snapshot);
            if (!string.Equals(
                    mediaType,
                    transfer.MediaType,
                    StringComparison.Ordinal))
            {
                throw new BridgeCallException(
                    "image_transfer_mismatch",
                    "The staged image transfer type does not match its descriptor.");
            }

            byte[] digest = SHA256.HashData(snapshot);
            string sha256 = Convert.ToHexString(digest).ToLowerInvariant();
            if (!string.Equals(
                    sha256,
                    transfer.Sha256,
                    StringComparison.Ordinal))
            {
                throw new BridgeCallException(
                    "image_transfer_mismatch",
                    "The staged image transfer hash does not match its descriptor.");
            }

            return new MemoryStream(snapshot, writable: false);
        }
    }

    public static void TryDelete(StagedImageTransfer transfer)
    {
        ValidateDescriptor(transfer);
        try
        {
            string directory = BridgePaths.GetImageTransferDirectory();
            RejectDirectoryLinkOrReparsePoint(directory);
            string path = GetTransferPath(directory, transfer.TransferId);
            FileInfo info = new(path);
            if (!info.Exists ||
                info.LinkTarget is not null ||
                (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return;
            }

            BridgePaths.TryDelete(path);
        }
        catch (IOException)
        {
            // Best-effort cleanup after a durable checkpoint.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup after a durable checkpoint.
        }
        catch (InvalidOperationException)
        {
            // A replaced transfer root is not safe to traverse during cleanup.
        }
        catch (System.Security.SecurityException)
        {
            // Best-effort cleanup must not weaken a completed safety checkpoint.
        }
        catch (NotSupportedException)
        {
            // Link metadata may be unavailable on an unsupported filesystem.
        }
    }

    public static void ValidateDescriptor(StagedImageTransfer transfer)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        if (!Guid.TryParseExact(transfer.TransferId, "D", out Guid parsed) ||
            !string.Equals(
                parsed.ToString("D"),
                transfer.TransferId,
                StringComparison.Ordinal))
        {
            throw new BridgeCallException(
                "image_transfer_invalid",
                "transferId must be a canonical lowercase D-format UUID.");
        }

        if (!HexHashRegex().IsMatch(transfer.Sha256))
        {
            throw new BridgeCallException(
                "image_transfer_invalid",
                "sha256 must be 64 lowercase hexadecimal characters.");
        }

        ValidateLength(transfer.ByteLength);
        if (transfer.MediaType is not PngMediaType
            and not JpegMediaType
            and not WebpMediaType)
        {
            throw new BridgeCallException(
                "image_transfer_invalid",
                "mediaType must be image/png, image/jpeg, or image/webp.");
        }
    }

    private static void ValidateSourcePath(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) ||
            !Path.IsPathFullyQualified(sourcePath))
        {
            throw new BridgeCallException(
                "image_path_invalid",
                "The selected image path must be absolute.");
        }

        if (!File.Exists(sourcePath))
        {
            throw new BridgeCallException(
                "image_path_invalid",
                "The selected image does not exist.");
        }
    }

    private static void ValidateLength(long length)
    {
        if (length <= 0 || length > BridgeConstants.MaximumImageTransferBytes)
        {
            throw new BridgeCallException(
                "image_size_invalid",
                $"The selected image must contain 1 to " +
                $"{BridgeConstants.MaximumImageTransferBytes} bytes.");
        }
    }

    private static async Task<(
        string Sha256,
        long ByteLength,
        string MediaType)> CopyAndInspectAsync(
        Stream source,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        using IncrementalHash hash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        // WebP needs the largest header (RIFF + size + WEBP = 12 bytes); the
        // same buffer covers PNG (8) and JPEG (3) signature checks.
        byte[] prefix = new byte[WebpHeaderLength];
        int prefixLength = 0;
        long total = 0;
        await using (FileStream destination = CreatePrivateFile(destinationPath))
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
                ValidateLength(total);
                int prefixCopy = Math.Min(
                    read,
                    prefix.Length - prefixLength);
                if (prefixCopy > 0)
                {
                    buffer.AsSpan(0, prefixCopy).CopyTo(
                        prefix.AsSpan(prefixLength));
                    prefixLength += prefixCopy;
                }

                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(
                        buffer.AsMemory(0, read),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            destination.Flush(flushToDisk: true);
        }

        ValidateLength(total);
        string mediaType = DetectMediaType(prefix.AsSpan(0, prefixLength));
        return (
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
            total,
            mediaType);
    }

    private static FileStream CreatePrivateFile(string path)
    {
        FileStreamOptions options = new()
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 64 * 1024,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode =
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite;
        }

        return new FileStream(path, options);
    }

    private static string DetectMediaType(ReadOnlySpan<byte> prefix)
    {
        if (prefix.Length >= PngSignature.Length &&
            prefix[..PngSignature.Length].SequenceEqual(PngSignature))
        {
            return PngMediaType;
        }

        if (prefix.Length >= 3 &&
            prefix[0] == 0xff &&
            prefix[1] == 0xd8 &&
            prefix[2] == 0xff)
        {
            return JpegMediaType;
        }

        // WebP: "RIFF" + 4-byte little-endian size + "WEBP".
        if (prefix.Length >= WebpHeaderLength &&
            prefix[..RiffMarker.Length].SequenceEqual(RiffMarker) &&
            prefix[(WebpHeaderLength - WebpMarker.Length)..WebpHeaderLength]
                .SequenceEqual(WebpMarker))
        {
            return WebpMediaType;
        }

        throw new BridgeCallException(
            "image_type_invalid",
            "The selected file is not a supported PNG, JPEG, or WebP image.");
    }

    private static void EnsureExtensionMatches(
        string extension,
        string mediaType)
    {
        bool matches =
            mediaType == PngMediaType && extension == ".png" ||
            mediaType == JpegMediaType &&
            extension is ".jpg" or ".jpeg" ||
            mediaType == WebpMediaType && extension == ".webp";
        if (!matches)
        {
            throw new BridgeCallException(
                "image_type_invalid",
                "The selected image extension does not match its file signature.");
        }
    }

    private static string GetTransferPath(
        string directory,
        string transferId) =>
        Path.Combine(directory, transferId + TransferExtension);

    private static void RejectLinkOrReparsePoint(
        FileInfo info,
        string description)
    {
        if (!info.Exists)
        {
            throw new BridgeCallException(
                "image_transfer_missing",
                $"{description} does not exist.");
        }

        if (info.LinkTarget is not null ||
            (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new BridgeCallException(
                "image_path_invalid",
                $"{description} cannot be a symbolic link or reparse point.");
        }
    }

    private static void RejectDirectoryLinkOrReparsePoint(string directory)
    {
        DirectoryInfo info = new(directory);
        if (info.LinkTarget is not null ||
            (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new BridgeCallException(
                "image_transfer_invalid",
                "The image transfer directory cannot be a symbolic link or " +
                "reparse point.");
        }
    }

    [GeneratedRegex("^[a-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex HexHashRegex();
}
