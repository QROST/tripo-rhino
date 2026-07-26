namespace Tripo.Mcp;

internal sealed class NoOpCredentialWorkflowExecutionGate :
    Tripo.Bridge.ICredentialWorkflowExecutionGate
{
    public static NoOpCredentialWorkflowExecutionGate Instance { get; } =
        new();

    private NoOpCredentialWorkflowExecutionGate()
    {
    }

    public IDisposable Acquire() => NoOpLease.Instance;

    private sealed class NoOpLease : IDisposable
    {
        public static NoOpLease Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
