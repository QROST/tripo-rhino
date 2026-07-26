using System.Globalization;
using GH_IO.Serialization;
using Grasshopper.Kernel;
using Tripo.Rhino.Grasshopper.Runtime;

namespace Tripo.Rhino.Grasshopper.Components;

public sealed class TripoTextTaskComponent : TripoCanvasComponent
{
    private readonly object _stateGate = new();
    private TextInput? _input;
    private string? _operationId;
    private string? _requestFingerprint;
    private string? _taskId;
    private string _status = "idle";
    private int _progress;
    private decimal? _credits;
    private string _message =
        "Set scalar inputs, then use the component menu to create a task.";
    private bool _busy;
    private bool _dispatchAttempted;

    public TripoTextTaskComponent()
        : base(
            "Tripo Text Task",
            "Tripo Text",
            "Explicitly create and inspect one recoverable Tripo text-to-model task. " +
            "Canvas recompute never dispatches a paid request.")
    {
    }

    public override Guid ComponentGuid =>
        new("21a61b1e-5147-48e8-bc69-e9a7e7cc6144");

    protected override System.Drawing.Bitmap? Icon => null;

    protected override void RegisterInputParams(
        GH_InputParamManager pManager)
    {
        pManager.AddTextParameter(
            "Prompt",
            "P",
            "Text prompt containing 1 to 1024 characters.",
            GH_ParamAccess.item,
            "A small timber pavilion");
        pManager.AddIntegerParameter(
            "Face Limit",
            "F",
            "Requested face limit from 500 through 200000.",
            GH_ParamAccess.item,
            10_000);
        pManager.AddBooleanParameter(
            "With Materials",
            "M",
            "Request textured PBR generation. Conversion remains a separate paid stage.",
            GH_ParamAccess.item,
            false);
    }

    protected override void RegisterOutputParams(
        GH_OutputParamManager pManager)
    {
        pManager.AddTextParameter(
            "Task ID",
            "Task",
            "Durable Tripo generation task ID.",
            GH_ParamAccess.item);
        pManager.AddTextParameter(
            "Status",
            "S",
            "Last explicitly refreshed task status.",
            GH_ParamAccess.item);
        pManager.AddIntegerParameter(
            "Progress",
            "%",
            "Last explicitly refreshed progress.",
            GH_ParamAccess.item);
        pManager.AddNumberParameter(
            "Credits",
            "C",
            "Credits reported by Tripo, when available.",
            GH_ParamAccess.item);
        pManager.AddTextParameter(
            "Operation ID",
            "Op",
            "Durable local paid-operation UUID. Preserve it for recovery.",
            GH_ParamAccess.item);
        pManager.AddTextParameter(
            "Message",
            "Msg",
            "Local component status; never contains the API key.",
            GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
        string prompt = string.Empty;
        int faceLimit = 10_000;
        bool withMaterials = false;
        bool valid =
            HasSingleItemInputs(0, 1, 2) &&
            DA.GetData(0, ref prompt) &&
            DA.GetData(1, ref faceLimit) &&
            DA.GetData(2, ref withMaterials);
        if (valid &&
            (string.IsNullOrWhiteSpace(prompt) ||
             prompt.Length > 1024 ||
             faceLimit is < 500 or > 200_000))
        {
            AddRuntimeMessage(
                GH_RuntimeMessageLevel.Error,
                "Prompt must contain 1–1024 characters and Face Limit must be " +
                "500–200000.");
            valid = false;
        }

        lock (_stateGate)
        {
            _input = valid
                ? new TextInput(
                    prompt,
                    faceLimit,
                    withMaterials,
                    Fingerprint(
                        prompt,
                        faceLimit.ToString(CultureInfo.InvariantCulture),
                        withMaterials ? "1" : "0"))
                : null;
            bool stale =
                _input is not null &&
                _requestFingerprint is not null &&
                !string.Equals(
                    _input.Fingerprint,
                    _requestFingerprint,
                    StringComparison.Ordinal);
            DA.SetData(0, _taskId);
            DA.SetData(1, stale ? "stale_inputs" : _status);
            DA.SetData(2, _progress);
            DA.SetData(
                3,
                _credits is null ? null : (double)_credits.Value);
            DA.SetData(4, _operationId);
            DA.SetData(
                5,
                stale
                    ? "Inputs changed after this operation was prepared. The " +
                      "existing task ID is preserved, but create a new component " +
                      "for a different paid request."
                    : _message);
        }
    }

    protected override void AppendAdditionalComponentMenuItems(
        System.Windows.Forms.ToolStripDropDown menu)
    {
        base.AppendAdditionalComponentMenuItems(menu);
        bool canCreate;
        bool canRefresh;
        bool retry;
        lock (_stateGate)
        {
            canCreate = !_busy && _input is not null && _taskId is null;
            canRefresh = !_busy && _taskId is not null;
            retry = _operationId is not null;
        }

        Menu_AppendItem(
            menu,
            retry
                ? "Retry same text operation…"
                : "Create text task…",
            OnCreateTask,
            canCreate,
            false);
        Menu_AppendItem(
            menu,
            "Refresh task status",
            OnRefreshStatus,
            canRefresh,
            false);
        AppendSharedMenuItems(menu);
    }

    public override bool Write(GH_IWriter writer)
    {
        lock (_stateGate)
        {
            SetOptionalString(writer, "OperationId", _operationId);
            SetOptionalString(writer, "RequestFingerprint", _requestFingerprint);
            SetOptionalString(writer, "TaskId", _taskId);
            writer.SetInt32(
                "DispatchAttempted",
                _dispatchAttempted ? 1 : 0);
            writer.SetString("Status", _status);
            writer.SetInt32("Progress", _progress);
            if (_credits is not null)
            {
                writer.SetDouble("Credits", (double)_credits.Value);
            }

        }

        return base.Write(writer);
    }

    public override bool Read(GH_IReader reader)
    {
        lock (_stateGate)
        {
            _operationId = GetOptionalString(reader, "OperationId");
            _requestFingerprint =
                GetOptionalString(reader, "RequestFingerprint");
            _taskId = GetOptionalString(reader, "TaskId");
            int serializedDispatchAttempted =
                reader.ItemExists("DispatchAttempted")
                    ? reader.GetInt32("DispatchAttempted")
                    : 0;
            // A saved definition is never authority for a fresh paid dispatch.
            // Any recovered operation identity must require its original journal,
            // even if a copied/tampered file claims dispatch was not attempted.
            _dispatchAttempted =
                _operationId is not null ||
                serializedDispatchAttempted != 0;
            _status = reader.ItemExists("Status")
                ? reader.GetString("Status")
                : "idle";
            _progress = reader.ItemExists("Progress")
                ? reader.GetInt32("Progress")
                : 0;
            _credits = reader.ItemExists("Credits")
                ? (decimal)reader.GetDouble("Credits")
                : null;
            _message =
                "Recovered local IDs. Use Refresh; loading never calls Tripo.";
            _busy = false;
        }

        return base.Read(reader);
    }

    private async void OnCreateTask(object? sender, EventArgs args)
    {
        if (!TryBeginAction())
        {
            return;
        }

        CanvasBinding? binding = null;
        try
        {
            TextInput input;
            string operationId;
            bool retry;
            lock (_stateGate)
            {
                input = _input ??
                    throw new InvalidOperationException(
                        "Provide one valid scalar input set before creating a task.");
                bool hasRecoveredIdentity =
                    _operationId is not null ||
                    _requestFingerprint is not null ||
                    _taskId is not null ||
                    _dispatchAttempted;
                bool completeIdentity =
                    _operationId is not null &&
                    _requestFingerprint is not null;
                if (hasRecoveredIdentity && !completeIdentity)
                {
                    throw new InvalidOperationException(
                        "The saved text operation identity is incomplete. Restore " +
                        "the original definition/local data or reconcile it manually.");
                }

                retry = _dispatchAttempted;
                if (completeIdentity &&
                    !string.Equals(
                        _requestFingerprint,
                        input.Fingerprint,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The inputs changed after this operation ID was prepared. " +
                        "Restore the original inputs; never reuse the UUID for a " +
                        "different paid request.");
                }

                operationId = _operationId ?? Guid.NewGuid().ToString("D");
            }

            binding = CaptureBinding();
            if (!ConfirmPaidAction(
                    binding,
                    retry
                        ? "Retry Tripo text operation"
                        : "Create Tripo text task",
                    operationId,
                    retry
                        ? "This retries the existing durable UUID. The journal " +
                          "will replay a receipt or fail closed."
                        : "This creates one text-to-model generation task."))
            {
                return;
            }

            lock (_stateGate)
            {
                _operationId = operationId;
                _requestFingerprint = input.Fingerprint;
                _busy = true;
                _status = "dispatching";
                _message = "Connecting to the Rhino sidecar…";
            }
            ScheduleCanvasUpdate(binding);

            (Tripo.Bridge.IHostControlClient client,
                Tripo.Bridge.HostContextReceipt context) =
                await ConnectBoundAsync(binding, LifetimeToken)
                    .ConfigureAwait(false);
            using IDisposable lease = AcquireCredentialWorkflowLease();
            EnsureNoConflictingRecovery(operationId);
            EnsurePaidActionStillBound(binding);
            lock (_stateGate)
            {
                if (_input is null ||
                    !string.Equals(
                        _input.Fingerprint,
                        input.Fingerprint,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        _requestFingerprint,
                        input.Fingerprint,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The component inputs changed before paid dispatch. " +
                        "No new request was sent.");
                }
            }
            SaveGenerationRecovery(
                context,
                operationId,
                dispatchAttempted: true,
                taskId: null,
                model: string.Empty);
            lock (_stateGate)
            {
                _dispatchAttempted = true;
            }
            Tripo.Bridge.HostControlTextTaskCreationReceipt receipt =
                await client.CreateTextTaskAsync(
                        new Tripo.Bridge.HostControlCreateTextTaskRequest(
                            input.Prompt,
                            input.FaceLimit,
                            input.WithMaterials,
                            context.DocumentSessionId,
                            operationId,
                            ConfirmExternalCost: true,
                            RequireExistingOperation: retry),
                        LifetimeToken)
                    .ConfigureAwait(false);
            SaveGenerationRecovery(
                context,
                receipt.OperationId,
                dispatchAttempted: true,
                receipt.TaskId,
                receipt.Model);
            lock (_stateGate)
            {
                _taskId = receipt.TaskId;
                _status = "created";
                _message =
                    "Task ID is durable. Use Refresh task status; no automatic " +
                    "polling is performed.";
            }
        }
        catch (OperationCanceledException) when (LifetimeToken.IsCancellationRequested)
        {
            lock (_stateGate)
            {
                _message =
                    "Stopped waiting locally. This does not cancel a remote Tripo task.";
            }
        }
        catch (Exception exception)
        {
            lock (_stateGate)
            {
                _status = "error";
                _message = BoundMessage(exception);
            }
        }
        finally
        {
            lock (_stateGate)
            {
                _busy = false;
            }
            try
            {
                ScheduleCanvasUpdate(binding);
            }
            finally
            {
                EndAction();
            }
        }
    }

    private async void OnRefreshStatus(object? sender, EventArgs args)
    {
        if (!TryBeginAction())
        {
            return;
        }

        CanvasBinding? binding = null;
        try
        {
            string taskId;
            lock (_stateGate)
            {
                taskId = _taskId ??
                    throw new InvalidOperationException(
                        "Create a task before refreshing status.");
                _busy = true;
                _message = "Refreshing the existing task…";
            }
            binding = CaptureBinding();
            ScheduleCanvasUpdate(binding);
            (Tripo.Bridge.IHostControlClient client, _) =
                await ConnectBoundAsync(binding, LifetimeToken)
                    .ConfigureAwait(false);
            Tripo.Bridge.HostControlTaskStatusReceipt status =
                await client.GetTaskStatusAsync(taskId, LifetimeToken)
                    .ConfigureAwait(false);
            lock (_stateGate)
            {
                _status = status.Status;
                _progress = status.Progress;
                _credits = status.CreditsConsumed;
                _message = status.ErrorMessage ??
                    "Status refreshed explicitly.";
            }
        }
        catch (OperationCanceledException) when (LifetimeToken.IsCancellationRequested)
        {
            lock (_stateGate)
            {
                _message =
                    "Stopped waiting locally. This does not cancel the remote task.";
            }
        }
        catch (Exception exception)
        {
            lock (_stateGate)
            {
                _status = "error";
                _message = BoundMessage(exception);
            }
        }
        finally
        {
            lock (_stateGate)
            {
                _busy = false;
            }
            try
            {
                ScheduleCanvasUpdate(binding);
            }
            finally
            {
                EndAction();
            }
        }
    }

    private static void SetOptionalString(
        GH_IWriter writer,
        string name,
        string? value)
    {
        if (value is not null)
        {
            writer.SetString(name, value);
        }
    }

    private static string? GetOptionalString(
        GH_IReader reader,
        string name) =>
        reader.ItemExists(name)
            ? reader.GetString(name)
            : null;

    private sealed record TextInput(
        string Prompt,
        int FaceLimit,
        bool WithMaterials,
        string Fingerprint);
}
