namespace Tripo.Rhino;

internal static class RhinoUiThread
{
    public static void Invoke(Action operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (!global::Rhino.RhinoApp.InvokeRequired)
        {
            operation();
            return;
        }

        global::Rhino.RhinoApp.InvokeOnUiThread(operation);
    }

    public static Task<T> InvokeAsync<T>(
        Func<T> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (!global::Rhino.RhinoApp.InvokeRequired)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(operation());
        }

        TaskCompletionSource<T> completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = cancellationToken.Register(
            () => completion.TrySetCanceled(cancellationToken));
        completion.Task.ContinueWith(
            _ => registration.Dispose(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        global::Rhino.RhinoApp.InvokeOnUiThread(
            new Action(() =>
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
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
            }));
        return completion.Task;
    }
}
