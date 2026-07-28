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

        return Tripo.Bridge.OnceStartedOperation.DispatchAsync(
            operation,
            callback => global::Rhino.RhinoApp.InvokeOnUiThread(
                new Action(callback)),
            cancellationToken);
    }
}
