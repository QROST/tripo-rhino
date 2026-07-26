using System.Security.Cryptography;
using System.Text;
using Eto.Forms;
using Grasshopper.Kernel;

namespace Tripo.Rhino.Grasshopper.Runtime;

public abstract class TripoCanvasComponent : GH_Component
{
    private Tripo.HostUi.TripoPanelRecoveryStore? _recoveryStore;
    private int _actionInProgress;
    private int _removed;

    protected TripoCanvasComponent(
        string name,
        string nickname,
        string description)
        : base(name, nickname, description, "Tripo", "Generate")
    {
    }

    // A paid dispatch is allowed to finish after canvas removal so its durable
    // journal can capture the remote receipt. UI publication is still rejected
    // by ScheduleCanvasUpdate when the originating document is gone.
    protected static CancellationToken LifetimeToken => CancellationToken.None;

    protected bool IsRemoved => Volatile.Read(ref _removed) != 0;

    protected void MarkRemoved()
    {
        Volatile.Write(ref _removed, 1);
    }

    protected bool TryBeginAction()
    {
        if (Volatile.Read(ref _removed) != 0 ||
            Interlocked.CompareExchange(
                ref _actionInProgress,
                1,
                0) != 0)
        {
            return false;
        }

        if (Volatile.Read(ref _removed) != 0)
        {
            EndAction();
            return false;
        }

        return true;
    }

    protected void EndAction()
    {
        Volatile.Write(ref _actionInProgress, 0);
        if (Volatile.Read(ref _removed) != 0)
        {
            DisposeRecoveryStore();
        }
    }

    protected CanvasBinding CaptureBinding() =>
        CanvasBinding.Capture(
            OnPingDocument() ??
            throw new InvalidOperationException(
                "This component is not attached to a Grasshopper document."));

    protected void EnsurePaidActionStillBound(CanvasBinding binding)
    {
        if (IsRemoved || !binding.Matches(OnPingDocument()))
        {
            throw new InvalidOperationException(
                "The originating Grasshopper/Rhino document or component " +
                "changed before paid dispatch. No new request was sent.");
        }
    }

    protected static async Task<(
        Tripo.Bridge.IHostControlClient Client,
        Tripo.Bridge.HostContextReceipt Context)> ConnectBoundAsync(
        CanvasBinding binding,
        CancellationToken cancellationToken)
    {
        Tripo.Bridge.IHostSidecarConnector connector =
            global::Tripo.Rhino.TripoRhinoPlugin.GetSidecarConnector();
        Tripo.Bridge.IHostControlClient client =
            await connector.EnsureConnectedAsync(cancellationToken)
                .ConfigureAwait(false);
        Tripo.Bridge.HostContextReceipt context =
            await client.GetHostContextAsync(cancellationToken)
                .ConfigureAwait(false);
        if (!string.Equals(context.Host, "rhino", StringComparison.Ordinal) ||
            context.ProcessId != Environment.ProcessId ||
            !string.Equals(
                context.DocumentSessionId,
                binding.DocumentSessionId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The sidecar is not bound to this component's exact Rhino document " +
                "session. Activate the associated Rhino document and retry.");
        }

        return (client, context);
    }

    protected IDisposable AcquireCredentialWorkflowLease() =>
        RecoveryStore.AcquireCredentialWorkflowLease();

    protected void EnsureNoConflictingRecovery(string operationId)
    {
        Tripo.HostUi.TripoPanelRecoveryLoadResult recovery =
            RecoveryStore.LoadCredentialMutationBlocks();
        bool hasDifferentOperation =
            recovery.Hints.Any(loaded =>
                EnumeratePaidOperationIds(loaded.Hint).Any(id =>
                    !string.Equals(
                        id,
                        operationId,
                        StringComparison.Ordinal)));
        if (recovery.Issues.Count > 0 || hasDifferentOperation)
        {
            throw new InvalidOperationException(
                "Another recovered Tripo UI operation requires reconciliation. " +
                "Open the Tripo panel, check the displayed operation IDs, and " +
                "acknowledge them before starting this canvas dispatch.");
        }
    }

    protected void SaveGenerationRecovery(
        Tripo.Bridge.HostContextReceipt context,
        string operationId,
        bool dispatchAttempted,
        string? taskId,
        string model)
    {
        Tripo.Bridge.HostControlTextTaskCreationReceipt? receipt =
            taskId is null
                ? null
                : new Tripo.Bridge.HostControlTextTaskCreationReceipt(
                    operationId,
                    taskId,
                    model);
        Tripo.HostUi.TripoPanelState state =
            Tripo.HostUi.TripoPanelState.Initial with
            {
                Connected = true,
                Context = context,
                PreparedGeneration = new Tripo.HostUi.PreparedTextGeneration(
                    "[grasshopper operation]",
                    500,
                    false,
                    context.DocumentSessionId,
                    operationId),
                GenerationDispatchAttempted = dispatchAttempted,
                GenerationReceipt = receipt,
            };
        RecoveryStore.Save(state);
    }

    protected void SaveConversionRecovery(
        Tripo.Bridge.HostContextReceipt context,
        string sourceTaskId,
        string operationId,
        bool dispatchAttempted,
        string? conversionTaskId)
    {
        Tripo.Bridge.HostControlObjConversionCreationReceipt? receipt =
            conversionTaskId is null
                ? null
                : new Tripo.Bridge.HostControlObjConversionCreationReceipt(
                    operationId,
                    sourceTaskId,
                    conversionTaskId,
                    "OBJ");
        Tripo.HostUi.TripoPanelState state =
            Tripo.HostUi.TripoPanelState.Initial with
            {
                Connected = true,
                Context = context,
                PreparedConversion = new Tripo.HostUi.PreparedObjConversion(
                    sourceTaskId,
                    500,
                    false,
                    context.DocumentSessionId,
                    operationId),
                ConversionDispatchAttempted = dispatchAttempted,
                ConversionReceipt = receipt,
            };
        RecoveryStore.Save(state);
    }

    protected bool ConfirmPaidAction(
        CanvasBinding binding,
        string title,
        string operationId,
        string description)
    {
        GH_Document? document = OnPingDocument();
        if (!binding.Matches(document))
        {
            throw new InvalidOperationException(
                "The Grasshopper/Rhino document binding changed before confirmation.");
        }

        global::Rhino.RhinoDoc rhinoDocument = document!.RhinoDocument;
        return MessageBox.Show(
            global::Rhino.UI.RhinoEtoApp.MainWindowForDocument(
                rhinoDocument),
            description +
            "\n\nThis action can consume Tripo credits.\n\n" +
            $"Durable operation ID:\n{operationId}\n\n" +
            "Keep this ID if the response is lost. Recompute does not resend it.",
            title,
            MessageBoxButtons.YesNo,
            MessageBoxType.Warning,
            MessageBoxDefaultButton.No) == DialogResult.Yes;
    }

    protected void ScheduleCanvasUpdate(
        CanvasBinding? binding = null,
        Action? beforeSolution = null)
    {
        global::Rhino.RhinoApp.InvokeOnUiThread(
            new Action(() =>
            {
                GH_Document? document = OnPingDocument();
                if (document is null ||
                    (binding is not null && !binding.Matches(document)))
                {
                    return;
                }

                beforeSolution?.Invoke();
                document.ScheduleSolution(
                    1,
                    _ => ExpireSolution(recompute: false));
            }));
    }

    protected static string Fingerprint(params string[] parts)
    {
        StringBuilder material = new();
        foreach (string part in parts)
        {
            material
                .Append(part.Length)
                .Append(':')
                .Append(part);
        }

        return Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(material.ToString())))
            .ToLowerInvariant();
    }

    protected bool HasSingleItemInputs(params int[] indexes)
    {
        foreach (int index in indexes)
        {
            if (Params.Input[index].VolatileDataCount > 1)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "Tripo v1 paid components accept exactly one item per input. " +
                    "List/tree batching is refused.");
                return false;
            }
        }

        return true;
    }

    protected static string BoundMessage(Exception exception)
    {
        string message = exception.Message.Trim();
        return message.Length <= 512
            ? message
            : message[..512];
    }

    protected static void AppendSharedMenuItems(
        System.Windows.Forms.ToolStripDropDown menu)
    {
        Menu_AppendSeparator(menu);
        Menu_AppendItem(
            menu,
            "Open Tripo panel / API key…",
            (_, _) => global::Rhino.RhinoApp.RunScript(
                "_TripoPanel",
                echo: false));
    }

    public override void RemovedFromDocument(GH_Document document)
    {
        MarkRemoved();
        if (Volatile.Read(ref _actionInProgress) == 0)
        {
            DisposeRecoveryStore();
        }

        base.RemovedFromDocument(document);
    }

    private void DisposeRecoveryStore() =>
        Interlocked.Exchange(ref _recoveryStore, null)?.Dispose();

    private static IEnumerable<string> EnumeratePaidOperationIds(
        Tripo.HostUi.TripoPanelRecoveryHint hint)
    {
        if (hint.Generation is not null)
        {
            yield return hint.Generation.OperationId;
        }

        if (hint.Conversion is not null)
        {
            yield return hint.Conversion.OperationId;
        }
    }

    private Tripo.HostUi.TripoPanelRecoveryStore RecoveryStore =>
        _recoveryStore ??=
            new Tripo.HostUi.TripoPanelRecoveryStore("rhino");
}
