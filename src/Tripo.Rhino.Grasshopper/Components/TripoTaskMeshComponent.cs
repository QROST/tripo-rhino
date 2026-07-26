using System.Globalization;
using GH_IO.Serialization;
using Grasshopper.Kernel;
using Rhino.Geometry;
using Tripo.Rhino.Grasshopper.Runtime;

namespace Tripo.Rhino.Grasshopper.Components;

public sealed class TripoTaskMeshComponent : TripoCanvasComponent
{
    private readonly object _stateGate = new();
    private MeshInput? _input;
    private string? _operationId;
    private string? _requestFingerprint;
    private string? _conversionTaskId;
    private string _status = "idle";
    private int _progress;
    private decimal? _credits;
    private Mesh? _mesh;
    private IReadOnlyList<string> _materialNames = [];
    private string _message =
        "Connect a successful text/image task, then create an OBJ conversion " +
        "from the component menu.";
    private bool _busy;
    private bool _dispatchAttempted;

    public TripoTaskMeshComponent()
        : base(
            "Tripo Task to Mesh",
            "Tripo Mesh",
            "Explicitly create a recoverable OBJ conversion, then stage and load " +
            "it as a Grasshopper Mesh without adding objects to the Rhino document.")
    {
    }

    public override Guid ComponentGuid =>
        new("411b4bdd-3675-4f24-bd9b-f11a47c95168");

    protected override System.Drawing.Bitmap? Icon => null;

    protected override void RegisterInputParams(
        GH_InputParamManager pManager)
    {
        pManager.AddTextParameter(
            "Source Task ID",
            "Task",
            "Successful Tripo text-to-model or image-to-model task ID.",
            GH_ParamAccess.item);
        pManager.AddIntegerParameter(
            "Face Limit",
            "F",
            "OBJ conversion face limit from 500 through 200000.",
            GH_ParamAccess.item,
            10_000);
        pManager.AddBooleanParameter(
            "With Materials",
            "M",
            "Request baked OBJ/MTL output and retain UV/material metadata.",
            GH_ParamAccess.item,
            false);
    }

    protected override void RegisterOutputParams(
        GH_OutputParamManager pManager)
    {
        pManager.AddMeshParameter(
            "Mesh",
            "M",
            "Grasshopper mesh in the associated Rhino document units. No Rhino " +
            "document object or Undo record is created.",
            GH_ParamAccess.item);
        pManager.AddTextParameter(
            "Conversion Task ID",
            "Convert",
            "Durable Tripo OBJ conversion task ID.",
            GH_ParamAccess.item);
        pManager.AddTextParameter(
            "Status",
            "S",
            "Last explicitly refreshed conversion status.",
            GH_ParamAccess.item);
        pManager.AddIntegerParameter(
            "Progress",
            "%",
            "Last explicitly refreshed conversion progress.",
            GH_ParamAccess.item);
        pManager.AddNumberParameter(
            "Credits",
            "C",
            "Conversion credits reported by Tripo, when available.",
            GH_ParamAccess.item);
        pManager.AddTextParameter(
            "Operation ID",
            "Op",
            "Durable local OBJ-conversion operation UUID.",
            GH_ParamAccess.item);
        pManager.AddTextParameter(
            "Material Names",
            "Materials",
            "Validated MTL material names. A Grasshopper Mesh does not apply PBR " +
            "document materials automatically.",
            GH_ParamAccess.list);
        pManager.AddTextParameter(
            "Message",
            "Msg",
            "Local component status.",
            GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
        string sourceTaskId = string.Empty;
        int faceLimit = 10_000;
        bool withMaterials = false;
        bool valid =
            HasSingleItemInputs(0, 1, 2) &&
            DA.GetData(0, ref sourceTaskId) &&
            DA.GetData(1, ref faceLimit) &&
            DA.GetData(2, ref withMaterials);
        if (valid &&
            (string.IsNullOrWhiteSpace(sourceTaskId) ||
             !sourceTaskId.StartsWith("task_", StringComparison.Ordinal) ||
             faceLimit is < 500 or > 200_000))
        {
            AddRuntimeMessage(
                GH_RuntimeMessageLevel.Error,
                "Source Task ID must begin with task_ and Face Limit must be " +
                "500–200000.");
            valid = false;
        }

        lock (_stateGate)
        {
            _input = valid
                ? new MeshInput(
                    sourceTaskId,
                    faceLimit,
                    withMaterials,
                    Fingerprint(
                        sourceTaskId,
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
            DA.SetData(
                0,
                stale || _mesh is null
                    ? null
                    : _mesh.DuplicateMesh());
            DA.SetData(1, _conversionTaskId);
            DA.SetData(2, stale ? "stale_inputs" : _status);
            DA.SetData(3, _progress);
            DA.SetData(
                4,
                _credits is null ? null : (double)_credits.Value);
            DA.SetData(5, _operationId);
            DA.SetDataList(
                6,
                stale
                    ? Array.Empty<string>()
                    : _materialNames);
            DA.SetData(
                7,
                stale
                    ? "Inputs changed after this conversion operation was " +
                      "prepared. The prior IDs remain visible, but its mesh is " +
                      "withheld from the new input state."
                    : _message);
        }
    }

    protected override void AppendAdditionalComponentMenuItems(
        System.Windows.Forms.ToolStripDropDown menu)
    {
        base.AppendAdditionalComponentMenuItems(menu);
        bool canConvert;
        bool canRefresh;
        bool retry;
        lock (_stateGate)
        {
            canConvert =
                !_busy &&
                _input is not null &&
                _conversionTaskId is null;
            canRefresh =
                !_busy &&
                _conversionTaskId is not null;
            retry = _operationId is not null;
        }

        Menu_AppendItem(
            menu,
            retry
                ? "Retry same OBJ conversion…"
                : "Create OBJ conversion…",
            OnCreateConversion,
            canConvert,
            false);
        Menu_AppendItem(
            menu,
            "Refresh conversion / load mesh",
            OnRefreshAndLoad,
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
            SetOptionalString(writer, "ConversionTaskId", _conversionTaskId);
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
            _conversionTaskId =
                GetOptionalString(reader, "ConversionTaskId");
            int serializedDispatchAttempted =
                reader.ItemExists("DispatchAttempted")
                    ? reader.GetInt32("DispatchAttempted")
                    : 0;
            // A recovered UUID always requires its original journal. A serialized
            // false value cannot downgrade recovery into a fresh paid conversion.
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
                "Recovered conversion IDs. Use Refresh / load; file loading " +
                "never calls Tripo.";
            _busy = false;
            _mesh = null;
            _materialNames = [];
        }

        return base.Read(reader);
    }

    public override void RemovedFromDocument(GH_Document document)
    {
        MarkRemoved();
        lock (_stateGate)
        {
            _mesh?.Dispose();
            _mesh = null;
        }

        base.RemovedFromDocument(document);
    }

    private async void OnCreateConversion(object? sender, EventArgs args)
    {
        if (!TryBeginAction())
        {
            return;
        }

        CanvasBinding? binding = null;
        try
        {
            MeshInput input;
            string operationId;
            bool retry;
            lock (_stateGate)
            {
                input = _input ??
                    throw new InvalidOperationException(
                        "Provide one valid source task and scalar input set.");
                bool hasRecoveredIdentity =
                    _operationId is not null ||
                    _requestFingerprint is not null ||
                    _conversionTaskId is not null ||
                    _dispatchAttempted;
                bool completeIdentity =
                    _operationId is not null &&
                    _requestFingerprint is not null;
                if (hasRecoveredIdentity && !completeIdentity)
                {
                    throw new InvalidOperationException(
                        "The saved conversion operation identity is incomplete. " +
                        "Restore the original definition/local data or reconcile it.");
                }

                retry = _dispatchAttempted;
                if (completeIdentity &&
                    !string.Equals(
                        _requestFingerprint,
                        input.Fingerprint,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The source task or conversion settings changed after " +
                        "this UUID was prepared. Restore them; never reuse the UUID.");
                }

                operationId = _operationId ?? Guid.NewGuid().ToString("D");
            }

            binding = CaptureBinding();
            if (!ConfirmPaidAction(
                    binding,
                    retry
                        ? "Retry Tripo OBJ conversion"
                        : "Create Tripo OBJ conversion",
                    operationId,
                    retry
                        ? "This retries the existing durable conversion UUID. " +
                          "The journal will replay or fail closed."
                        : "This creates a separate OBJ conversion task for the " +
                          "source generation task."))
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
                        "The conversion inputs changed before paid dispatch. " +
                        "No new request was sent.");
                }
            }
            SaveConversionRecovery(
                context,
                input.SourceTaskId,
                operationId,
                dispatchAttempted: true,
                conversionTaskId: null);
            lock (_stateGate)
            {
                _dispatchAttempted = true;
            }
            Tripo.Bridge.HostControlObjConversionCreationReceipt receipt =
                await client.CreateObjConversionAsync(
                        new Tripo.Bridge.HostControlCreateObjConversionRequest(
                            input.SourceTaskId,
                            input.FaceLimit,
                            input.WithMaterials,
                            context.DocumentSessionId,
                            operationId,
                            ConfirmExternalCost: true,
                            RequireExistingOperation: retry),
                        LifetimeToken)
                    .ConfigureAwait(false);
            SaveConversionRecovery(
                context,
                input.SourceTaskId,
                receipt.OperationId,
                dispatchAttempted: true,
                receipt.ConversionTaskId);
            lock (_stateGate)
            {
                _conversionTaskId = receipt.ConversionTaskId;
                _status = "created";
                _message =
                    "Conversion task ID is durable. Use Refresh conversion / " +
                    "load mesh; no automatic polling is performed.";
            }
        }
        catch (OperationCanceledException) when (LifetimeToken.IsCancellationRequested)
        {
            lock (_stateGate)
            {
                _message =
                    "Stopped waiting locally. This does not cancel a remote conversion.";
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

    private async void OnRefreshAndLoad(object? sender, EventArgs args)
    {
        if (!TryBeginAction())
        {
            return;
        }

        CanvasBinding? binding = null;
        try
        {
            string conversionTaskId;
            MeshInput input;
            lock (_stateGate)
            {
                conversionTaskId = _conversionTaskId ??
                    throw new InvalidOperationException(
                        "Create an OBJ conversion before loading a mesh.");
                input = _input ??
                    throw new InvalidOperationException(
                        "Restore the conversion component inputs before refreshing.");
                if (!string.Equals(
                        input.Fingerprint,
                        _requestFingerprint,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The component inputs no longer match this conversion UUID.");
                }

                _busy = true;
                _message = "Refreshing the existing conversion task…";
            }

            binding = CaptureBinding();
            ScheduleCanvasUpdate(binding);
            (Tripo.Bridge.IHostControlClient client,
                Tripo.Bridge.HostContextReceipt context) =
                await ConnectBoundAsync(binding, LifetimeToken)
                    .ConfigureAwait(false);
            Tripo.Bridge.HostControlTaskStatusReceipt status =
                await client.GetTaskStatusAsync(
                        conversionTaskId,
                        LifetimeToken)
                    .ConfigureAwait(false);
            lock (_stateGate)
            {
                _status = status.Status;
                _progress = status.Progress;
                _credits = status.CreditsConsumed;
                _message = status.ErrorMessage ??
                    "Conversion status refreshed explicitly.";
            }

            if (!string.Equals(
                    status.Status,
                    "success",
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Tripo.Bridge.HostControlObjTaskStageReceipt staged =
                await client.StageObjTaskAsync(
                        new Tripo.Bridge.HostControlStageObjTaskRequest(
                            conversionTaskId,
                            context.DocumentSessionId,
                            IncludeMaterials: input.WithMaterials),
                        LifetimeToken)
                    .ConfigureAwait(false);
            Tripo.Bridge.PreparedMesh prepared =
                await Tripo.Bridge.StagedArtifactLoader.LoadPreparedObjAsync(
                        staged.Mesh,
                        LifetimeToken)
                    .ConfigureAwait(false);
            Mesh projected = await ProjectOnUiThreadAsync(
                    binding,
                    prepared,
                    LifetimeToken)
                .ConfigureAwait(false);
            lock (_stateGate)
            {
                if (IsRemoved ||
                    _input is null ||
                    !string.Equals(
                        _input.Fingerprint,
                        input.Fingerprint,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        _requestFingerprint,
                        input.Fingerprint,
                        StringComparison.Ordinal))
                {
                    projected.Dispose();
                    return;
                }

                _mesh?.Dispose();
                _mesh = projected;
                _materialNames =
                    prepared.Materials
                        .Select(material => material.Name)
                        .ToArray();
                _credits = staged.ConversionCreditsConsumed ?? _credits;
                _status = "success";
                _progress = 100;
                _message =
                    $"Loaded {projected.Vertices.Count} vertices and " +
                    $"{projected.Faces.Count} triangles as a Grasshopper value. " +
                    "No Rhino document object or Undo record was created.";
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

    private Task<Mesh> ProjectOnUiThreadAsync(
        CanvasBinding binding,
        Tripo.Bridge.PreparedMesh prepared,
        CancellationToken cancellationToken)
    {
        TaskCompletionSource<Mesh> completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration =
            cancellationToken.Register(
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
                    if (!binding.Matches(OnPingDocument()))
                    {
                        throw new InvalidOperationException(
                            "The Grasshopper/Rhino document binding changed " +
                            "before mesh publication.");
                    }

                    completion.TrySetResult(
                        RhinoMeshProjector.Project(
                            prepared,
                            binding.RhinoUnitSystem));
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

    private sealed record MeshInput(
        string SourceTaskId,
        int FaceLimit,
        bool WithMaterials,
        string Fingerprint);
}
