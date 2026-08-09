using Xunit;

namespace Tripo.Bridge.Tests;

public sealed class ImageTransferStoreTests
{
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
}
