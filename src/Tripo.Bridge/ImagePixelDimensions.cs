using System.Buffers.Binary;

namespace Tripo.Bridge;

public readonly record struct ImagePixelDimensions(int Width, int Height)
{
    private static readonly byte[] PngSignature =
    [
        0x89, (byte)'P', (byte)'N', (byte)'G',
        0x0d, 0x0a, 0x1a, 0x0a,
    ];
    private static readonly byte[] Vp8FrameSignature = [0x9d, 0x01, 0x2a];

    public long PixelCount => (long)Width * Height;

    public static bool TryRead(
        Stream stream,
        string mediaType,
        out ImagePixelDimensions dimensions)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ArgumentException(
                "The image stream must be readable and seekable.",
                nameof(stream));
        }

        long originalPosition = stream.Position;
        try
        {
            stream.Position = 0;
            return mediaType switch
            {
                "image/png" => TryReadPng(stream, out dimensions),
                "image/jpeg" => TryReadJpeg(stream, out dimensions),
                "image/webp" => TryReadWebp(stream, out dimensions),
                _ => Fail(out dimensions),
            };
        }
        catch (EndOfStreamException)
        {
            dimensions = default;
            return false;
        }
        catch (IOException)
        {
            dimensions = default;
            return false;
        }
        finally
        {
            stream.Position = originalPosition;
        }
    }

    private static bool TryReadPng(
        Stream stream,
        out ImagePixelDimensions dimensions)
    {
        Span<byte> header = stackalloc byte[24];
        if (!TryReadExactly(stream, header) ||
            !header[..8].SequenceEqual(PngSignature) ||
            BinaryPrimitives.ReadUInt32BigEndian(header.Slice(8, 4)) != 13 ||
            !header.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            return Fail(out dimensions);
        }

        int width = BinaryPrimitives.ReadInt32BigEndian(header.Slice(16, 4));
        int height = BinaryPrimitives.ReadInt32BigEndian(header.Slice(20, 4));
        return Success(width, height, out dimensions);
    }

    private static bool TryReadJpeg(
        Stream stream,
        out ImagePixelDimensions dimensions)
    {
        if (ReadByte(stream) != 0xff || ReadByte(stream) != 0xd8)
        {
            return Fail(out dimensions);
        }

        Span<byte> lengthBytes = stackalloc byte[2];
        Span<byte> frame = stackalloc byte[5];
        while (stream.Position < stream.Length)
        {
            int prefix;
            do
            {
                prefix = ReadByte(stream);
            }
            while (prefix != 0xff && stream.Position < stream.Length);

            if (prefix != 0xff)
            {
                return Fail(out dimensions);
            }

            int marker;
            do
            {
                marker = ReadByte(stream);
            }
            while (marker == 0xff);

            if (marker is 0x00 or 0x01 or 0xd8 or 0xd9 ||
                marker is >= 0xd0 and <= 0xd7)
            {
                continue;
            }

            if (!TryReadExactly(stream, lengthBytes))
            {
                return Fail(out dimensions);
            }

            int segmentLength =
                BinaryPrimitives.ReadUInt16BigEndian(lengthBytes);
            if (segmentLength < 2 ||
                segmentLength - 2 > stream.Length - stream.Position)
            {
                return Fail(out dimensions);
            }

            if (IsStartOfFrame(marker))
            {
                if (segmentLength < 7 || !TryReadExactly(stream, frame))
                {
                    return Fail(out dimensions);
                }

                int height =
                    BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(1, 2));
                int width =
                    BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(3, 2));
                return Success(width, height, out dimensions);
            }

            stream.Position += segmentLength - 2;
        }

        return Fail(out dimensions);
    }

    private static bool TryReadWebp(
        Stream stream,
        out ImagePixelDimensions dimensions)
    {
        Span<byte> container = stackalloc byte[12];
        if (!TryReadExactly(stream, container) ||
            !container[..4].SequenceEqual("RIFF"u8) ||
            !container.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return Fail(out dimensions);
        }

        Span<byte> chunkHeader = stackalloc byte[8];
        Span<byte> payload = stackalloc byte[10];
        while (stream.Position + 8 <= stream.Length)
        {
            if (!TryReadExactly(stream, chunkHeader))
            {
                return Fail(out dimensions);
            }

            uint chunkLength =
                BinaryPrimitives.ReadUInt32LittleEndian(
                    chunkHeader.Slice(4, 4));
            long paddedLength =
                (long)chunkLength + (chunkLength & 1u);
            if (paddedLength > stream.Length - stream.Position)
            {
                return Fail(out dimensions);
            }

            ReadOnlySpan<byte> kind = chunkHeader[..4];
            if (kind.SequenceEqual("VP8X"u8) && chunkLength >= 10)
            {
                if (!TryReadExactly(stream, payload))
                {
                    return Fail(out dimensions);
                }

                int width = 1 + ReadUInt24LittleEndian(payload.Slice(4, 3));
                int height = 1 + ReadUInt24LittleEndian(payload.Slice(7, 3));
                return Success(width, height, out dimensions);
            }

            if (kind.SequenceEqual("VP8L"u8) && chunkLength >= 5)
            {
                Span<byte> losslessPayload = payload[..5];
                if (!TryReadExactly(stream, losslessPayload) ||
                    losslessPayload[0] != 0x2f)
                {
                    return Fail(out dimensions);
                }

                int width = 1 + losslessPayload[1] +
                    ((losslessPayload[2] & 0x3f) << 8);
                int height = 1 +
                    (losslessPayload[2] >> 6) +
                    (losslessPayload[3] << 2) +
                    ((losslessPayload[4] & 0x0f) << 10);
                return Success(width, height, out dimensions);
            }

            if (kind.SequenceEqual("VP8 "u8) && chunkLength >= 10)
            {
                if (!TryReadExactly(stream, payload) ||
                    !payload.Slice(3, 3).SequenceEqual(
                        Vp8FrameSignature))
                {
                    return Fail(out dimensions);
                }

                int width =
                    BinaryPrimitives.ReadUInt16LittleEndian(
                        payload.Slice(6, 2)) & 0x3fff;
                int height =
                    BinaryPrimitives.ReadUInt16LittleEndian(
                        payload.Slice(8, 2)) & 0x3fff;
                return Success(width, height, out dimensions);
            }

            stream.Position += paddedLength;
        }

        return Fail(out dimensions);
    }

    private static bool IsStartOfFrame(int marker) =>
        marker is >= 0xc0 and <= 0xc3 or
            >= 0xc5 and <= 0xc7 or
            >= 0xc9 and <= 0xcb or
            >= 0xcd and <= 0xcf;

    private static int ReadUInt24LittleEndian(ReadOnlySpan<byte> value) =>
        value[0] | value[1] << 8 | value[2] << 16;

    private static int ReadByte(Stream stream)
    {
        int value = stream.ReadByte();
        if (value < 0)
        {
            throw new EndOfStreamException();
        }

        return value;
    }

    private static bool TryReadExactly(Stream stream, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer[total..]);
            if (read == 0)
            {
                return false;
            }

            total += read;
        }

        return true;
    }

    private static bool Success(
        int width,
        int height,
        out ImagePixelDimensions dimensions)
    {
        if (width <= 0 || height <= 0)
        {
            return Fail(out dimensions);
        }

        dimensions = new ImagePixelDimensions(width, height);
        return true;
    }

    private static bool Fail(out ImagePixelDimensions dimensions)
    {
        dimensions = default;
        return false;
    }
}
