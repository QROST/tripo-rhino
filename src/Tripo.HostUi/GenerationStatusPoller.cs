namespace Tripo.HostUi;

internal sealed class GenerationStatusPoller : IDisposable
{
    private readonly object _gate = new();
    private readonly TimeSpan _interval;
    private readonly Func<string, CancellationToken, Task> _refresh;
    private readonly Action<string, Exception> _reportFailure;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;
    private string? _activeTaskId;
    private string? _failedTaskId;
    private bool _disposed;

    public GenerationStatusPoller(
        TimeSpan interval,
        Func<string, CancellationToken, Task> refresh,
        Action<string, Exception> reportFailure)
        : this(
            interval,
            refresh,
            reportFailure,
            static (delay, cancellationToken) =>
                Task.Delay(delay, cancellationToken))
    {
    }

    internal GenerationStatusPoller(
        TimeSpan interval,
        Func<string, CancellationToken, Task> refresh,
        Action<string, Exception> reportFailure,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(interval),
                "The polling interval must be positive.");
        }

        _interval = interval;
        _refresh =
            refresh ?? throw new ArgumentNullException(nameof(refresh));
        _reportFailure =
            reportFailure ??
            throw new ArgumentNullException(nameof(reportFailure));
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
    }

    public static string? GetPendingTaskId(
        TripoPanelState state,
        TripoPanelRecoveryLoadResult recovery)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(recovery);
        if (!state.Connected || recovery.HasBlock)
        {
            return null;
        }

        string? taskId = state.GenerationReceipt?.TaskId;
        if (string.IsNullOrWhiteSpace(taskId) &&
            state.GenerationOperationStatus?.TaskIdDurable == true)
        {
            taskId = state.GenerationOperationStatus.CreatedTaskId;
        }

        if (string.IsNullOrWhiteSpace(taskId))
        {
            return null;
        }

        taskId = taskId.Trim();
        Tripo.Bridge.HostControlTaskStatusReceipt? status =
            state.GenerationStatus;
        if (status is null)
        {
            return taskId;
        }

        if (!string.Equals(
                status.TaskId,
                taskId,
                StringComparison.Ordinal))
        {
            return null;
        }

        string normalized = status.Status.Trim();
        return string.Equals(
                   normalized,
                   "queued",
                   StringComparison.OrdinalIgnoreCase) ||
               string.Equals(
                   normalized,
                   "running",
                   StringComparison.OrdinalIgnoreCase)
            ? taskId
            : null;
    }

    public void Reconcile(string? pendingTaskId) =>
        Reconcile(pendingTaskId, resumeAfterFailure: false);

    public void Resume(string? pendingTaskId) =>
        Reconcile(pendingTaskId, resumeAfterFailure: true);

    public void Stop() => Reconcile(null, resumeAfterFailure: false);

    public void Dispose()
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _activeTaskId = null;
            _failedTaskId = null;
            cancellation = _runCancellation;
            _runCancellation = null;
            _runTask = null;
        }

        CancelSafely(cancellation);
    }

    private void Reconcile(
        string? pendingTaskId,
        bool resumeAfterFailure)
    {
        string? taskId = string.IsNullOrWhiteSpace(pendingTaskId)
            ? null
            : pendingTaskId.Trim();
        CancellationTokenSource? cancellation = null;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (taskId is null)
            {
                cancellation = _runCancellation;
                _activeTaskId = null;
                _failedTaskId = null;
                _runCancellation = null;
                _runTask = null;
            }
            else if (string.Equals(
                         taskId,
                         _activeTaskId,
                         StringComparison.Ordinal))
            {
                if (_runTask is { IsCompleted: false })
                {
                    return;
                }

                if (!resumeAfterFailure &&
                    string.Equals(
                        taskId,
                        _failedTaskId,
                        StringComparison.Ordinal))
                {
                    return;
                }

                _failedTaskId = null;
                StartRun(taskId);
            }
            else
            {
                cancellation = _runCancellation;
                _activeTaskId = taskId;
                _failedTaskId = null;
                StartRun(taskId);
            }
        }

        CancelSafely(cancellation);
    }

    private void StartRun(string taskId)
    {
        CancellationTokenSource cancellation = new();
        _runCancellation = cancellation;
        _runTask = Task.Run(
            () => RunAsync(taskId, cancellation));
    }

    private async Task RunAsync(
        string taskId,
        CancellationTokenSource cancellation)
    {
        bool failed = false;
        try
        {
            while (true)
            {
                await _delay(_interval, cancellation.Token)
                    .ConfigureAwait(false);
                cancellation.Token.ThrowIfCancellationRequested();
                await _refresh(taskId, cancellation.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
            when (cancellation.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
            when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            failed = true;
            try
            {
                _reportFailure(taskId, exception);
            }
            catch
            {
                // A failure reporter must not terminate the Rhino host process.
            }
        }
        finally
        {
            CompleteRun(taskId, cancellation, failed);
        }
    }

    private void CompleteRun(
        string taskId,
        CancellationTokenSource cancellation,
        bool failed)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_runCancellation, cancellation))
            {
                _runCancellation = null;
                _runTask = null;
                if (failed &&
                    string.Equals(
                        taskId,
                        _activeTaskId,
                        StringComparison.Ordinal))
                {
                    _failedTaskId = taskId;
                }
            }
        }

        cancellation.Dispose();
    }

    private static void CancelSafely(
        CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
