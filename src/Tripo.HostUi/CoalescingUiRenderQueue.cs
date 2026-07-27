namespace Tripo.HostUi;

internal sealed class CoalescingUiRenderQueue<T> : IDisposable
    where T : class
{
    private readonly object _gate = new();
    private readonly Action<Action> _enqueueOnUi;
    private readonly Action<T> _render;
    private readonly Action<Exception> _reportFailure;
    private T? _nextFrame;
    private T? _trailingFrame;
    private bool _callbackPosted;
    private bool _draining;
    private bool _hasNextFrame;
    private bool _hasTrailingFrame;
    private bool _nextFrameIsLeading;
    private bool _disposed;

    public CoalescingUiRenderQueue(
        Action<Action> enqueueOnUi,
        Action<T> render,
        Action<Exception> reportFailure)
    {
        _enqueueOnUi =
            enqueueOnUi ?? throw new ArgumentNullException(nameof(enqueueOnUi));
        _render = render ?? throw new ArgumentNullException(nameof(render));
        _reportFailure =
            reportFailure ?? throw new ArgumentNullException(
                nameof(reportFailure));
    }

    public bool Request(T frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        bool post;
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            if (!_callbackPosted)
            {
                _nextFrame = frame;
                _hasNextFrame = true;
                _nextFrameIsLeading = true;
                _callbackPosted = true;
                post = true;
            }
            else if (_draining)
            {
                _trailingFrame = frame;
                _hasTrailingFrame = true;
                post = false;
            }
            else if (!_hasNextFrame)
            {
                _nextFrame = frame;
                _hasNextFrame = true;
                _nextFrameIsLeading = true;
                post = false;
            }
            else if (_nextFrameIsLeading)
            {
                _trailingFrame = frame;
                _hasTrailingFrame = true;
                post = false;
            }
            else
            {
                _nextFrame = frame;
                post = false;
            }
        }

        if (post)
        {
            return EnqueueDrain();
        }

        return true;
    }

    public void CancelPending()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            ClearFrames();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _callbackPosted = false;
            ClearFrames();
        }
    }

    private void Drain()
    {
        T? frame;
        lock (_gate)
        {
            if (_disposed)
            {
                _callbackPosted = false;
                ClearFrames();
                return;
            }

            if (!_hasNextFrame)
            {
                _callbackPosted = false;
                return;
            }

            frame = _nextFrame;
            _nextFrame = null;
            _hasNextFrame = false;
            _nextFrameIsLeading = false;
            _draining = true;
        }

        Exception? renderFailure = null;
        try
        {
            _render(frame!);
        }
        catch (Exception exception)
        {
            renderFailure = exception;
        }
        finally
        {
            bool post;
            lock (_gate)
            {
                _draining = false;
                if (_disposed)
                {
                    _callbackPosted = false;
                    ClearFrames();
                    post = false;
                }
                else if (_hasTrailingFrame)
                {
                    _nextFrame = _trailingFrame;
                    _trailingFrame = null;
                    _hasNextFrame = true;
                    _hasTrailingFrame = false;
                    _nextFrameIsLeading = false;
                    post = true;
                }
                else
                {
                    _callbackPosted = false;
                    post = false;
                }
            }

            if (post)
            {
                EnqueueDrain();
            }
        }

        if (renderFailure is not null)
        {
            ReportFailure(renderFailure);
        }
    }

    private bool EnqueueDrain()
    {
        try
        {
            _enqueueOnUi(Drain);
            return true;
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                _callbackPosted = false;
                _draining = false;
            }

            ReportFailure(exception);
            return false;
        }
    }

    private void ReportFailure(Exception exception)
    {
        try
        {
            _reportFailure(exception);
        }
        catch
        {
            // A UI dispatch or render failure must never escape the queued
            // callback and terminate the host process.
        }
    }

    private void ClearFrames()
    {
        _nextFrame = null;
        _trailingFrame = null;
        _hasNextFrame = false;
        _hasTrailingFrame = false;
        _nextFrameIsLeading = false;
    }
}
