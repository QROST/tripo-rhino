namespace Tripo.Bridge;

internal sealed class BoundedReadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _expectedBytes;
    private long _bytesRead;

    public BoundedReadStream(Stream inner, long expectedBytes)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        if (expectedBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedBytes));
        }

        _expectedBytes = expectedBytes;
    }

    public long BytesRead => _bytesRead;

    public override bool CanRead => _inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _bytesRead;
        set => throw new NotSupportedException();
    }

    public override void Flush() => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        int boundedCount = GetBoundedCount(count);
        int read = _inner.Read(buffer, offset, boundedCount);
        RecordRead(read);
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        int boundedCount = GetBoundedCount(buffer.Length);
        int read = _inner.Read(buffer[..boundedCount]);
        RecordRead(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        int boundedCount = GetBoundedCount(buffer.Length);
        int read = await _inner.ReadAsync(
                buffer[..boundedCount],
                cancellationToken)
            .ConfigureAwait(false);
        RecordRead(read);
        return read;
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        int boundedCount = GetBoundedCount(count);
        return ReadAsync(
                buffer.AsMemory(offset, boundedCount),
                cancellationToken)
            .AsTask();
    }

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();

    public override void SetLength(long value) =>
        throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    private int GetBoundedCount(int requestedCount)
    {
        if (requestedCount == 0)
        {
            return 0;
        }

        long remaining = _expectedBytes - _bytesRead;
        long probeCount = Math.Min(remaining + 1, requestedCount);
        return checked((int)Math.Max(1, probeCount));
    }

    private void RecordRead(int read)
    {
        _bytesRead += read;
        if (_bytesRead > _expectedBytes)
        {
            throw new BridgeCallException(
                "artifact_length_mismatch",
                "The OBJ stream contained more bytes than declared.");
        }
    }
}
