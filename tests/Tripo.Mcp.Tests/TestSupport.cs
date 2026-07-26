using System.Net;

namespace Tripo.Mcp.Tests;

internal sealed class TemporaryDataRoot : IDisposable
{
    private readonly string? _previous;

    public TemporaryDataRoot()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "tripo-mcp-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
        _previous = Environment.GetEnvironmentVariable(
            Tripo.Bridge.BridgePaths.LocalDataEnvironmentVariable);
        Environment.SetEnvironmentVariable(
            Tripo.Bridge.BridgePaths.LocalDataEnvironmentVariable,
            Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(
            Tripo.Bridge.BridgePaths.LocalDataEnvironmentVariable,
            _previous);
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

internal sealed class DelegateHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, int, Task<HttpResponseMessage>> _handler;
    private int _callCount;

    public DelegateHttpMessageHandler(
        Func<HttpRequestMessage, int, Task<HttpResponseMessage>> handler)
    {
        _handler = handler;
    }

    public int CallCount => _callCount;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        int call = Interlocked.Increment(ref _callCount);
        return _handler(request, call);
    }

    public static HttpResponseMessage Json(
        string json,
        HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(statusCode)
        {
            Content = new StringContent(
                json,
                System.Text.Encoding.UTF8,
                "application/json"),
        };
}

internal sealed class HangingReadStream : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return 0;
    }

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();

    public override void SetLength(long value) =>
        throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();
}
