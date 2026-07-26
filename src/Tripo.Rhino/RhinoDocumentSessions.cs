using System.Collections.Concurrent;

namespace Tripo.Rhino;

internal sealed class RhinoDocumentSessions
{
    private readonly ConcurrentDictionary<uint, string> _sessions = new();

    public string GetOrCreate(global::Rhino.RhinoDoc document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return _sessions.GetOrAdd(
            document.RuntimeSerialNumber,
            _ => Guid.NewGuid().ToString("D"));
    }

    public void Forget(uint documentRuntimeSerialNumber) =>
        _sessions.TryRemove(documentRuntimeSerialNumber, out _);
}
