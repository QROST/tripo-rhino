using System.Globalization;
using Eto.Forms;
using GH_IO.Serialization;
using Grasshopper.Kernel;
using Tripo.Rhino.Grasshopper.Runtime;

namespace Tripo.Rhino.Grasshopper.Components;

public sealed class TripoImageTaskComponent : TripoCanvasComponent
{
    private readonly object _stateGate = new();
    private ImageInput? _input;
    private Tripo.Bridge.StagedImageTransfer? _transfer;
    private string? _operationId;
    private string? _requestFingerprint;
    private string? _taskId;
    private string _status = "idle";
    private int _progress;
    private decimal? _credits;
    private string _message =
        "Set scalar inputs, then choose Create image task from the component menu.";
    private bool _busy;
    private bool _dispatchAttempted;

    public TripoImageTaskComponent()
        : base(
            "Tripo Image Task",
            "Tripo Image",
            "Explicitly choose one local PNG/JPEG and create a recoverable Tripo " +
            "image-to-model task. The source path is never serialized by this component.")
    {
    }

    public override Guid ComponentGuid =>
        new("07b3a3d6-f74d-45b8-955b-76ebaf0d7aae");

    protected override System.Drawing.Bitmap? Icon => null;

    protected override void RegisterInputParams(
        GH_InputParamManager pManager)
    {
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
            "Durable local paid-operation UUID.",
            GH_ParamAccess.item);
        pManager.AddTextParameter(
            "Image SHA-256",
            "SHA",
            "Content identity of the staged image; no path or image bytes.",
            GH_ParamAccess.item);
        pManager.AddTextParameter(
            "Message",
            "Msg",
            "Local component status; never contains the image path, file token, or API key.",
            GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
        int faceLimit = 10_000;
        bool withMaterials = false;
        bool valid =
            HasSingleItemInputs(0, 1) &&
            DA.GetData(0, ref faceLimit) &&
            DA.GetData(1, ref withMaterials);
        if (valid && faceLimit is < 500 or > 200_000)
        {
            AddRuntimeMessage(
                GH_RuntimeMessageLevel.Error,
                "Face Limit must be 500–200000.");
            valid = false;
        }

        lock (_stateGate)
        {
            _input = valid
                ? new ImageInput(faceLimit, withMaterials)
                : null;
            string? currentFingerprint =
                _input is not null && _transfer is not null
                    ? CreateFingerprint(_input, _transfer)
                    : null;
            bool stale =
                currentFingerprint is not null &&
                _requestFingerprint is not null &&
                !string.Equals(
                    currentFingerprint,
                    _requestFingerprint,
                    StringComparison.Ordinal);
            DA.SetData(0, _taskId);
            DA.SetData(1, stale ? "stale_inputs" : _status);
            DA.SetData(2, _progress);
            DA.SetData(
                3,
                _credits is null ? null : (double)_credits.Value);
            DA.SetData(4, _operationId);
            DA.SetData(5, _transfer?.Sha256);
            DA.SetData(
                6,
                stale
                    ? "Face/material inputs changed after this image operation " +
                      "was prepared. Restore them; never reuse the UUID for a " +
                      "different paid request."
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
            retry = _operationId is not null && _transfer is not null;
        }

        Menu_AppendItem(
            menu,
            retry
                ? "Retry same image operation…"
                : "Choose image and create task…",
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
            if (_transfer is not null)
            {
                writer.SetString("TransferId", _transfer.TransferId);
                writer.SetString("ImageSha256", _transfer.Sha256);
                writer.SetString(
                    "ImageByteLength",
                    _transfer.ByteLength.ToString(
                        CultureInfo.InvariantCulture));
                writer.SetString("ImageMediaType", _transfer.MediaType);
            }

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
            // Deserialized state can request recovery only. It cannot authorize
            // creation when the original paid-operation journal is missing.
            _dispatchAttempted =
                _operationId is not null ||
                serializedDispatchAttempted != 0;
            string? transferId = GetOptionalString(reader, "TransferId");
            string? sha256 = GetOptionalString(reader, "ImageSha256");
            string? byteLength = GetOptionalString(reader, "ImageByteLength");
            string? mediaType = GetOptionalString(reader, "ImageMediaType");
            _transfer =
                transferId is not null &&
                sha256 is not null &&
                long.TryParse(
                    byteLength,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out long parsedLength) &&
                mediaType is not null
                    ? new Tripo.Bridge.StagedImageTransfer(
                        transferId,
                        sha256,
                        parsedLength,
                        mediaType)
                    : null;
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
        Tripo.Bridge.StagedImageTransfer? newlyStagedTransfer = null;
        bool handedToSidecar = false;
        try
        {
            ImageInput input;
            Tripo.Bridge.StagedImageTransfer? existingTransfer;
            string? existingOperationId;
            string? existingRequestFingerprint;
            string? existingTaskId;
            bool existingDispatchAttempted;
            lock (_stateGate)
            {
                input = _input ??
                    throw new InvalidOperationException(
                        "Provide valid scalar inputs before creating a task.");
                existingTransfer = _transfer;
                existingOperationId = _operationId;
                existingRequestFingerprint = _requestFingerprint;
                existingTaskId = _taskId;
                existingDispatchAttempted = _dispatchAttempted;
            }

            binding = CaptureBinding();
            bool hasRecoveredIdentity =
                existingOperationId is not null ||
                existingTransfer is not null ||
                existingRequestFingerprint is not null ||
                existingTaskId is not null ||
                existingDispatchAttempted;
            bool completeIdentity =
                existingOperationId is not null &&
                existingTransfer is not null &&
                existingRequestFingerprint is not null;
            if (hasRecoveredIdentity && !completeIdentity)
            {
                throw new InvalidOperationException(
                    "The saved image operation identity is incomplete. It cannot " +
                    "be reused for another image; restore the original definition " +
                    "and local data or reconcile the UUID manually.");
            }

            string operationId =
                existingOperationId ?? Guid.NewGuid().ToString("D");
            bool retry = existingDispatchAttempted;
            if (retry && !completeIdentity)
            {
                throw new InvalidOperationException(
                    "A recovered image retry requires its complete original " +
                    "transfer identity.");
            }

            if (existingTransfer is not null)
            {
                Tripo.Bridge.ImageTransferStore.ValidateDescriptor(
                    existingTransfer);
            }
            string? selectedPath = null;
            if (!completeIdentity)
            {
                selectedPath = PickImagePath(binding);
                if (selectedPath is null)
                {
                    return;
                }
            }

            if (!ConfirmPaidAction(
                    binding,
                    completeIdentity
                        ? "Retry Tripo image operation"
                        : "Create Tripo image task",
                    operationId,
                    completeIdentity
                        ? "This retries the existing durable UUID and staged " +
                          "content identity. The journal will replay or fail closed."
                        : "The selected image will be uploaded to Tripo and may " +
                          "contain embedded metadata such as EXIF."))
            {
                return;
            }

            Tripo.Bridge.StagedImageTransfer transfer =
                existingTransfer ??
                await Tripo.Bridge.ImageTransferStore.StageAsync(
                        selectedPath!,
                        LifetimeToken)
                    .ConfigureAwait(false);
            if (existingTransfer is null)
            {
                newlyStagedTransfer = transfer;
            }
            string requestFingerprint = CreateFingerprint(input, transfer);
            lock (_stateGate)
            {
                if (_requestFingerprint is not null &&
                    !string.Equals(
                        _requestFingerprint,
                        requestFingerprint,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The image or scalar inputs changed after this operation " +
                        "ID was prepared. Never reuse the UUID for new content.");
                }

                _transfer = transfer;
                _operationId = operationId;
                _requestFingerprint = requestFingerprint;
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
                string? currentFingerprint =
                    _input is null
                        ? null
                        : CreateFingerprint(_input, transfer);
                if (!string.Equals(
                        currentFingerprint,
                        requestFingerprint,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        _requestFingerprint,
                        requestFingerprint,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The image component inputs changed before paid dispatch. " +
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
            handedToSidecar = true;
            Tripo.Bridge.HostControlImageTaskCreationReceipt receipt;
            try
            {
                receipt =
                    await client.CreateImageTaskAsync(
                            new Tripo.Bridge.HostControlCreateImageTaskRequest(
                                transfer,
                                input.FaceLimit,
                                input.WithMaterials,
                                context.DocumentSessionId,
                                operationId,
                                ConfirmExternalCost: true,
                                RequireExistingOperation: retry),
                            LifetimeToken)
                        .ConfigureAwait(false);
            }
            catch (Tripo.Bridge.HostControlCallException exception)
                when (string.Equals(
                    exception.Code,
                    Tripo.Bridge.HostControlConstants.CredentialRejectedError,
                    StringComparison.Ordinal))
            {
                // This code is returned only after the sidecar durably records
                // that no remote task was created. Delete this component's
                // recovery hint before releasing its local operation identity.
                SaveGenerationRecovery(
                    context,
                    operationId,
                    dispatchAttempted: false,
                    taskId: null,
                    model: string.Empty);
                lock (_stateGate)
                {
                    _transfer = null;
                    _operationId = null;
                    _requestFingerprint = null;
                    _dispatchAttempted = false;
                    _status = "credential_rejected";
                    _message =
                        "Tripo rejected the API key before creating this image " +
                        "task. Update the key, then choose the image again.";
                }
                Tripo.Bridge.ImageTransferStore.TryDelete(transfer);
                return;
            }
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
                    "Task ID is durable. The source path and file token were not " +
                    "stored in the Grasshopper document.";
            }
        }
        catch (OperationCanceledException) when (LifetimeToken.IsCancellationRequested)
        {
            lock (_stateGate)
            {
                _message =
                    "Stopped waiting locally. This does not cancel a remote upload " +
                    "or Tripo task.";
            }
        }
        catch (Exception exception)
        {
            lock (_stateGate)
            {
                _status = "error";
                _message = SafeImageMessage(exception);
            }
        }
        finally
        {
            if (newlyStagedTransfer is not null && !handedToSidecar)
            {
                Tripo.Bridge.ImageTransferStore.TryDelete(
                    newlyStagedTransfer);
                lock (_stateGate)
                {
                    if (Equals(_transfer, newlyStagedTransfer))
                    {
                        _transfer = null;
                        _operationId = null;
                        _requestFingerprint = null;
                        _dispatchAttempted = false;
                    }
                }
            }

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
                _message = status.ErrorMessage is null
                    ? "Status refreshed explicitly."
                    : "Tripo reported an error for this image task. Provider " +
                      "details are withheld from canvas output.";
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
                _message = SafeImageMessage(exception);
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

    private string? PickImagePath(CanvasBinding binding)
    {
        GH_Document? document = OnPingDocument();
        if (!binding.Matches(document))
        {
            throw new InvalidOperationException(
                "The document binding changed before image selection.");
        }

        global::Rhino.RhinoDoc rhinoDocument = document!.RhinoDocument;
        OpenFileDialog dialog = new()
        {
            CheckFileExists = true,
            MultiSelect = false,
            Title = "Choose a PNG or JPEG for Tripo",
            Filters =
            {
                new FileFilter(
                    "PNG or JPEG",
                    "*.png",
                    "*.jpg",
                    "*.jpeg"),
            },
        };
        return dialog.ShowDialog(
                global::Rhino.UI.RhinoEtoApp.MainWindowForDocument(
                    rhinoDocument)) == DialogResult.Ok
            ? dialog.FileName
            : null;
    }

    private static string CreateFingerprint(
        ImageInput input,
        Tripo.Bridge.StagedImageTransfer transfer) =>
        Fingerprint(
            transfer.Sha256,
            transfer.ByteLength.ToString(CultureInfo.InvariantCulture),
            transfer.MediaType,
            input.FaceLimit.ToString(CultureInfo.InvariantCulture),
            input.WithMaterials ? "1" : "0");

    private static string SafeImageMessage(Exception exception) =>
        exception switch
        {
            Tripo.Bridge.BridgeCallException or InvalidOperationException =>
                BoundMessage(exception),
            _ =>
                "The image operation failed. Potentially sensitive provider or " +
                "transport details are withheld; preserve the Operation ID and " +
                "inspect the local journal.",
        };

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

    private sealed record ImageInput(
        int FaceLimit,
        bool WithMaterials);
}
