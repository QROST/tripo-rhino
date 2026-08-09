using Xunit;

namespace Tripo.Bridge.Tests;

public sealed class ImageTransferStoreTests
{
    [Fact]
    public void PixelDimensionsReadPngJpegAndAllWebpLayoutsWithoutDecoding()
    {
        AssertDimensions(PngHeader(4096, 3072), "image/png", 4096, 3072);
        AssertDimensions(JpegHeader(1200, 800), "image/jpeg", 1200, 800);
        AssertDimensions(WebpExtendedHeader(640, 480), "image/webp", 640, 480);
        AssertDimensions(WebpLosslessHeader(321, 123), "image/webp", 321, 123);
        AssertDimensions(WebpLossyHeader(1920, 1080), "image/webp", 1920, 1080);
    }

    [Fact]
    public void PixelDimensionsFailClosedWithoutChangingStreamPosition()
    {
        using MemoryStream malformed = new([0x89, (byte)'P']);
        malformed.Position = 1;

        Assert.False(
            Tripo.Bridge.ImagePixelDimensions.TryRead(
                malformed,
                "image/png",
                out _));
        Assert.Equal(1, malformed.Position);
        Assert.False(
            Tripo.Bridge.ImagePixelDimensions.TryRead(
                new MemoryStream(PngHeader(10, 10)),
                "image/gif",
                out _));
    }

    [Fact]
    public void PixelDimensionsExposeOverflowSafePixelCountForPreviewGate()
    {
        using MemoryStream stream = new(PngHeader(16_384, 16_384));

        Assert.True(
            Tripo.Bridge.ImagePixelDimensions.TryRead(
                stream,
                "image/png",
                out Tripo.Bridge.ImagePixelDimensions dimensions));
        Assert.Equal(268_435_456L, dimensions.PixelCount);
    }

    [Fact]
    public void PixelDimensionsRejectWrappedWebpChunkLength()
    {
        byte[] bytes = WebpContainer("JUNK", 8);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(16, 4),
            uint.MaxValue);
        using MemoryStream stream = new(bytes);

        Assert.False(
            Tripo.Bridge.ImagePixelDimensions.TryRead(
                stream,
                "image/webp",
                out _));
    }

    private static readonly byte[] PngBytes =
    [
        0x89,
        (byte)'P',
        (byte)'N',
        (byte)'G',
        0x0d,
        0x0a,
        0x1a,
        0x0a,
        0x00,
        0x01,
    ];

    [Fact]
    public async Task StageAndOpenVerifiedAsyncRoundTripsOpaquePng()
    {
        using TemporaryDataRoot dataRoot = new();
        string source = Path.Combine(dataRoot.Path, "source.png");
        await File.WriteAllBytesAsync(source, PngBytes);

        Tripo.Bridge.StagedImageTransfer transfer =
            await Tripo.Bridge.ImageTransferStore.StageAsync(
                source,
                CancellationToken.None);

        Assert.Equal("image/png", transfer.MediaType);
        Assert.Equal(PngBytes.Length, transfer.ByteLength);
        Assert.DoesNotContain("source", transfer.TransferId);
        await using Stream verified =
            await Tripo.Bridge.ImageTransferStore.OpenVerifiedAsync(
                transfer,
                CancellationToken.None);
        using MemoryStream copy = new();
        await verified.CopyToAsync(copy);
        Assert.Equal(PngBytes, copy.ToArray());
    }

    [Fact]
    public async Task VerifiedOpenReturnsAnImmutableUploadSnapshot()
    {
        using TemporaryDataRoot dataRoot = new();
        string source = Path.Combine(dataRoot.Path, "source.png");
        await File.WriteAllBytesAsync(source, PngBytes);
        Tripo.Bridge.StagedImageTransfer transfer =
            await Tripo.Bridge.ImageTransferStore.StageAsync(
                source,
                CancellationToken.None);

        await using Stream verified =
            await Tripo.Bridge.ImageTransferStore.OpenVerifiedAsync(
                transfer,
                CancellationToken.None);
        string stagedPath = Path.Combine(
            Tripo.Bridge.BridgePaths.GetImageTransferDirectory(),
            transfer.TransferId + ".image");
        byte[] replacement = (byte[])PngBytes.Clone();
        replacement[^1] ^= 0xff;
        await File.WriteAllBytesAsync(stagedPath, replacement);
        using MemoryStream copy = new();
        await verified.CopyToAsync(copy);

        Assert.Equal(PngBytes, copy.ToArray());
    }

    [Fact]
    public void ImageTransferDirectoryRejectsSymbolicLinksBeforeChangingTheTarget()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDataRoot dataRoot = new();
        string target = Path.Combine(dataRoot.Path, "external-target");
        Directory.CreateDirectory(target);
        File.SetUnixFileMode(
            target,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute |
            UnixFileMode.GroupRead |
            UnixFileMode.GroupExecute);
        UnixFileMode originalMode = File.GetUnixFileMode(target);
        Directory.CreateSymbolicLink(
            Path.Combine(dataRoot.Path, "image-transfers"),
            target);

        Assert.Throws<InvalidOperationException>(
            Tripo.Bridge.BridgePaths.GetImageTransferDirectory);
        Assert.Equal(originalMode, File.GetUnixFileMode(target));
    }

    [Fact]
    public async Task StageAsyncRejectsExtensionSpoof()
    {
        using TemporaryDataRoot dataRoot = new();
        string source = Path.Combine(dataRoot.Path, "source.jpg");
        await File.WriteAllBytesAsync(source, PngBytes);

        Tripo.Bridge.BridgeCallException exception =
            await Assert.ThrowsAsync<Tripo.Bridge.BridgeCallException>(
                () => Tripo.Bridge.ImageTransferStore.StageAsync(
                    source,
                    CancellationToken.None));

        Assert.Equal("image_type_invalid", exception.Code);
        Assert.DoesNotContain(source, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StageAsyncRejectsASourceSymbolicLink()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDataRoot dataRoot = new();
        string target = Path.Combine(dataRoot.Path, "target.png");
        string sourceLink = Path.Combine(dataRoot.Path, "source.png");
        await File.WriteAllBytesAsync(target, PngBytes);
        File.CreateSymbolicLink(sourceLink, target);

        Tripo.Bridge.BridgeCallException exception =
            await Assert.ThrowsAsync<Tripo.Bridge.BridgeCallException>(
                () => Tripo.Bridge.ImageTransferStore.StageAsync(
                    sourceLink,
                    CancellationToken.None));

        Assert.Equal("image_path_invalid", exception.Code);
        Assert.DoesNotContain(
            sourceLink,
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenVerifiedAsyncRejectsTamperedTransfer()
    {
        using TemporaryDataRoot dataRoot = new();
        string source = Path.Combine(dataRoot.Path, "source.png");
        await File.WriteAllBytesAsync(source, PngBytes);
        Tripo.Bridge.StagedImageTransfer transfer =
            await Tripo.Bridge.ImageTransferStore.StageAsync(
                source,
                CancellationToken.None);
        string stagedPath = Path.Combine(
            Tripo.Bridge.BridgePaths.GetImageTransferDirectory(),
            transfer.TransferId + ".image");
        byte[] tampered = (byte[])PngBytes.Clone();
        tampered[^1] ^= 0xff;
        await File.WriteAllBytesAsync(stagedPath, tampered);

        Tripo.Bridge.BridgeCallException exception =
            await Assert.ThrowsAsync<Tripo.Bridge.BridgeCallException>(
                () => Tripo.Bridge.ImageTransferStore.OpenVerifiedAsync(
                    transfer,
                    CancellationToken.None));

        Assert.Equal("image_transfer_mismatch", exception.Code);
    }

    [Fact]
    public async Task OpenVerifiedAsyncRejectsLengthDescriptorMismatch()
    {
        using TemporaryDataRoot dataRoot = new();
        string source = Path.Combine(dataRoot.Path, "source.png");
        await File.WriteAllBytesAsync(source, PngBytes);
        Tripo.Bridge.StagedImageTransfer transfer =
            await Tripo.Bridge.ImageTransferStore.StageAsync(
                source,
                CancellationToken.None);

        Tripo.Bridge.BridgeCallException exception =
            await Assert.ThrowsAsync<Tripo.Bridge.BridgeCallException>(
                () => Tripo.Bridge.ImageTransferStore.OpenVerifiedAsync(
                    transfer with { ByteLength = transfer.ByteLength + 1 },
                    CancellationToken.None));

        Assert.Equal("image_transfer_mismatch", exception.Code);
    }

    [Fact]
    public async Task StagedSymbolicLinkIsNeitherOpenedNorDeletedThrough()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDataRoot dataRoot = new();
        string source = Path.Combine(dataRoot.Path, "source.png");
        await File.WriteAllBytesAsync(source, PngBytes);
        Tripo.Bridge.StagedImageTransfer transfer =
            await Tripo.Bridge.ImageTransferStore.StageAsync(
                source,
                CancellationToken.None);
        string stagedPath = Path.Combine(
            Tripo.Bridge.BridgePaths.GetImageTransferDirectory(),
            transfer.TransferId + ".image");
        File.Delete(stagedPath);
        string replacementTarget =
            Path.Combine(dataRoot.Path, "replacement.png");
        await File.WriteAllBytesAsync(replacementTarget, PngBytes);
        File.CreateSymbolicLink(stagedPath, replacementTarget);

        Tripo.Bridge.BridgeCallException exception =
            await Assert.ThrowsAsync<Tripo.Bridge.BridgeCallException>(
                () => Tripo.Bridge.ImageTransferStore.OpenVerifiedAsync(
                    transfer,
                    CancellationToken.None));
        Tripo.Bridge.ImageTransferStore.TryDelete(transfer);

        Assert.Equal("image_path_invalid", exception.Code);
        Assert.True(File.Exists(replacementTarget));
    }

    [Fact]
    public async Task UnixImageTransferPathsArePrivate()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDataRoot dataRoot = new();
        string source = Path.Combine(dataRoot.Path, "source.png");
        await File.WriteAllBytesAsync(source, PngBytes);
        Tripo.Bridge.StagedImageTransfer transfer =
            await Tripo.Bridge.ImageTransferStore.StageAsync(
                source,
                CancellationToken.None);
        string directory =
            Tripo.Bridge.BridgePaths.GetImageTransferDirectory();
        string stagedPath = Path.Combine(
            directory,
            transfer.TransferId + ".image");

        Assert.Equal(
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute,
            File.GetUnixFileMode(directory));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(stagedPath));
    }

    [Fact]
    public async Task StageAsyncRejectsFileAboveDocumentedUploadLimit()
    {
        using TemporaryDataRoot dataRoot = new();
        string source = Path.Combine(dataRoot.Path, "source.png");
        await using (FileStream stream = new(
            source,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None))
        {
            await stream.WriteAsync(PngBytes);
            stream.SetLength(
                Tripo.Bridge.BridgeConstants.MaximumImageTransferBytes + 1);
        }

        Tripo.Bridge.BridgeCallException exception =
            await Assert.ThrowsAsync<Tripo.Bridge.BridgeCallException>(
                () => Tripo.Bridge.ImageTransferStore.StageAsync(
                    source,
                    CancellationToken.None));

        Assert.Equal("image_size_invalid", exception.Code);
    }

    // WebP: "RIFF" + 4-byte little-endian size + "WEBP" + VP8 chunk payload.
    private static readonly byte[] WebpBytes =
    [
        (byte)'R', (byte)'I', (byte)'F', (byte)'F',
        0x0a, 0x00, 0x00, 0x00,
        (byte)'W', (byte)'E', (byte)'B', (byte)'P',
        (byte)'V', (byte)'P', (byte)'8', (byte)' ',
    ];

    [Fact]
    public async Task StageAndOpenVerifiedAsyncRoundTripsOpaqueWebp()
    {
        using TemporaryDataRoot dataRoot = new();
        string source = Path.Combine(dataRoot.Path, "source.webp");
        await File.WriteAllBytesAsync(source, WebpBytes);

        Tripo.Bridge.StagedImageTransfer transfer =
            await Tripo.Bridge.ImageTransferStore.StageAsync(
                source,
                CancellationToken.None);

        Assert.Equal("image/webp", transfer.MediaType);
        Assert.Equal(WebpBytes.Length, transfer.ByteLength);
        await using Stream verified =
            await Tripo.Bridge.ImageTransferStore.OpenVerifiedAsync(
                transfer,
                CancellationToken.None);
        using MemoryStream copy = new();
        await verified.CopyToAsync(copy);
        Assert.Equal(WebpBytes, copy.ToArray());
    }

    [Fact]
    public async Task StageAsyncRejectsWebpExtensionSpoof()
    {
        using TemporaryDataRoot dataRoot = new();
        // PNG bytes under a .webp name: extension/signature must not match.
        string source = Path.Combine(dataRoot.Path, "source.webp");
        await File.WriteAllBytesAsync(source, PngBytes);

        Tripo.Bridge.BridgeCallException exception =
            await Assert.ThrowsAsync<Tripo.Bridge.BridgeCallException>(
                () => Tripo.Bridge.ImageTransferStore.StageAsync(
                    source,
                    CancellationToken.None));

        Assert.Equal("image_type_invalid", exception.Code);
    }

    [Fact]
    public async Task StageAsyncRejectsUnsupportedExtension()
    {
        using TemporaryDataRoot dataRoot = new();
        // Valid GIF magic under a .gif name: GIF is not supported by Tripo v3.
        byte[] gifBytes = { (byte)'G', (byte)'I', (byte)'F', (byte)'8' };
        string source = Path.Combine(dataRoot.Path, "source.gif");
        await File.WriteAllBytesAsync(source, gifBytes);

        Tripo.Bridge.BridgeCallException exception =
            await Assert.ThrowsAsync<Tripo.Bridge.BridgeCallException>(
                () => Tripo.Bridge.ImageTransferStore.StageAsync(
                    source,
                    CancellationToken.None));

        Assert.Equal("image_type_invalid", exception.Code);
    }

    private static void AssertDimensions(
        byte[] bytes,
        string mediaType,
        int width,
        int height)
    {
        using MemoryStream stream = new(bytes);
        Assert.True(
            Tripo.Bridge.ImagePixelDimensions.TryRead(
                stream,
                mediaType,
                out Tripo.Bridge.ImagePixelDimensions dimensions));
        Assert.Equal(width, dimensions.Width);
        Assert.Equal(height, dimensions.Height);
    }

    private static byte[] PngHeader(int width, int height)
    {
        byte[] bytes = new byte[24];
        PngBytes.AsSpan(0, 8).CopyTo(bytes);
        bytes[11] = 13;
        "IHDR"u8.CopyTo(bytes.AsSpan(12));
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(
            bytes.AsSpan(16, 4),
            width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(
            bytes.AsSpan(20, 4),
            height);
        return bytes;
    }

    private static byte[] JpegHeader(int width, int height) =>
    [
        0xff, 0xd8,
        0xff, 0xc0,
        0x00, 0x11,
        0x08,
        (byte)(height >> 8), (byte)height,
        (byte)(width >> 8), (byte)width,
        0x03, 0x01, 0x11, 0x00, 0x02, 0x11, 0x00, 0x03, 0x11, 0x00,
    ];

    private static byte[] WebpExtendedHeader(int width, int height)
    {
        byte[] bytes = WebpContainer("VP8X", 10);
        WriteUInt24LittleEndian(bytes.AsSpan(24, 3), width - 1);
        WriteUInt24LittleEndian(bytes.AsSpan(27, 3), height - 1);
        return bytes;
    }

    private static byte[] WebpLosslessHeader(int width, int height)
    {
        byte[] bytes = WebpContainer("VP8L", 5);
        int widthMinusOne = width - 1;
        int heightMinusOne = height - 1;
        bytes[20] = 0x2f;
        bytes[21] = (byte)widthMinusOne;
        bytes[22] = (byte)(
            (widthMinusOne >> 8) |
            ((heightMinusOne & 0x03) << 6));
        bytes[23] = (byte)(heightMinusOne >> 2);
        bytes[24] = (byte)(heightMinusOne >> 10);
        return bytes;
    }

    private static byte[] WebpLossyHeader(int width, int height)
    {
        byte[] bytes = WebpContainer("VP8 ", 10);
        bytes[23] = 0x9d;
        bytes[24] = 0x01;
        bytes[25] = 0x2a;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(
            bytes.AsSpan(26, 2),
            checked((ushort)width));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(
            bytes.AsSpan(28, 2),
            checked((ushort)height));
        return bytes;
    }

    private static byte[] WebpContainer(string chunkKind, int chunkLength)
    {
        int paddedLength = chunkLength + (chunkLength & 1);
        byte[] bytes = new byte[20 + paddedLength];
        "RIFF"u8.CopyTo(bytes);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(4, 4),
            checked((uint)(bytes.Length - 8)));
        "WEBP"u8.CopyTo(bytes.AsSpan(8));
        System.Text.Encoding.ASCII.GetBytes(chunkKind).CopyTo(bytes, 12);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(16, 4),
            checked((uint)chunkLength));
        return bytes;
    }

    private static void WriteUInt24LittleEndian(
        Span<byte> destination,
        int value)
    {
        destination[0] = (byte)value;
        destination[1] = (byte)(value >> 8);
        destination[2] = (byte)(value >> 16);
    }
}
