namespace Tripo.Bridge;

internal static class OnceStartedOperation
{
    public static Task<T> DispatchAsync<T>(
        Func<T> operation,
        Action<Action> dispatch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(dispatch);
        cancellationToken.ThrowIfCancellationRequested();

        TaskCompletionSource<T> completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int state = 0; // 0 queued, 1 running, 2 terminal/canceled
        CancellationTokenRegistration registration = cancellationToken.Register(
            () =>
            {
                if (Interlocked.CompareExchange(ref state, 2, 0) == 0)
                {
                    completion.TrySetCanceled(cancellationToken);
                }
            });
        _ = completion.Task.ContinueWith(
            _ => registration.Dispose(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        try
        {
            dispatch(
                () =>
                {
                    if (Interlocked.CompareExchange(ref state, 1, 0) != 0)
                    {
                        return;
                    }

                    try
                    {
                        completion.TrySetResult(operation());
                    }
                    catch (OperationCanceledException)
                        when (cancellationToken.IsCancellationRequested)
                    {
                        completion.TrySetCanceled(cancellationToken);
                    }
                    catch (Exception exception)
                    {
                        completion.TrySetException(exception);
                    }
                    finally
                    {
                        Interlocked.Exchange(ref state, 2);
                    }
                });
        }
        catch (Exception exception)
        {
            if (Interlocked.CompareExchange(ref state, 2, 0) == 0)
            {
                completion.TrySetException(exception);
            }
        }

        return completion.Task;
    }
}
