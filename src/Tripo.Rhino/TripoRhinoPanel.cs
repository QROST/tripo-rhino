using System.Runtime.InteropServices;
using Eto.Forms;
using Rhino.UI;

namespace Tripo.Rhino;

[Guid("717B15B4-C4F1-45E7-BE8E-C601440201C0")]
public sealed class TripoRhinoPanel : Panel, IPanel
{
    private const string ApiKeyInstructions =
        "Create a Tripo v3 API key at:\n" +
        "https://platform.tripo3d.ai/api-keys\n\n" +
        "The key is sent only to the local Tripo sidecar. It is never saved " +
        "in this .3dm document.\n\n" +
        "Replacing the effective key can make same-UUID recovery fail closed " +
        "for unfinished UI or MCP paid operations. Reconcile them first.";

    private readonly uint _documentSerialNumber;
    private readonly TripoRhinoPlugin _plugin;
    private Tripo.HostUi.TripoPanelSession _session;
    private readonly Tripo.HostUi.CoalescingUiRenderQueue<PanelRenderFrame>
        _renderQueue;
    private readonly Tripo.HostUi.GenerationStatusPoller
        _generationStatusPoller;
    private readonly CancellationTokenSource _panelLifetime = new();
    private readonly TextArea _prompt = new()
    {
        Height = 84,
        Text = "A buildable architectural pavilion",
        Wrap = true,
    };
    private readonly NumericStepper _faceLimit = new()
    {
        DecimalPlaces = 0,
        Increment = 500,
        MaxValue = 200_000,
        MinValue = 500,
        Value = 20_000,
    };
    private readonly CheckBox _withMaterials = new()
    {
        Checked = true,
        Text = "Generate materials",
        ToolTip =
            "Request generated materials from Tripo. This is the recommended " +
            "setting for direct GLB import.",
    };
    private readonly TextBox _name = new()
    {
        Text = "Tripo Model",
    };
    private readonly DropDown _importSource = new()
    {
        DataStore = new[]
        {
            "Direct GLB (recommended)",
            "OBJ compatibility",
        },
        SelectedIndex = 0,
    };
    private readonly Label _createGuidance = StatusLabel();
    private readonly Label _importGuidance = StatusLabel();
    private readonly DropDown _importMode = new()
    {
        DataStore = new[] { "native", "mesh", "instance" },
        SelectedIndex = 0,
    };
    private readonly CheckBox _applyMaterials = new()
    {
        Text = "Apply diffuse materials (OBJ fallback)",
        ToolTip =
            "Apply baked OBJ/MTL diffuse materials when available. Direct GLB " +
            "imports generated materials when they are available.",
    };
    private readonly Label _documentStatus = StatusLabel();
    private readonly TextBox _documentSession = OperationStatusBox();
    private readonly Label _credentialStatus = StatusLabel();
    private readonly Label _recoveryHeader = StatusLabel();
    private readonly TextArea _recoveryStatus = new()
    {
        Height = 108,
        ReadOnly = true,
        Wrap = true,
    };
    private readonly TextBox _generationOperation = OperationStatusBox();
    private readonly TextBox _generationTaskId = OperationStatusBox();
    private readonly Label _generationTask = StatusLabel();
    private readonly TextArea _generationDiagnostic = DiagnosticBox();
    private readonly ProgressBar _generationProgress = WorkflowProgressBar();
    private readonly TextBox _conversionOperation = OperationStatusBox();
    private readonly TextBox _conversionTaskId = OperationStatusBox();
    private readonly Label _conversionTask = StatusLabel();
    private readonly TextArea _conversionDiagnostic = DiagnosticBox();
    private readonly ProgressBar _conversionProgress = WorkflowProgressBar();
    private readonly TextBox _importOperation = OperationStatusBox();
    private readonly TextBox _importCreatedObject = OperationStatusBox();
    private readonly TextBox _importTransaction = OperationStatusBox();
    private readonly Label _resultStatus = StatusLabel();
    private readonly Expander _settingsExpander = new();
    private readonly Expander _recoveryExpander = new();
    private readonly Expander _advancedExpander = new();
    private readonly Expander _detailsExpander = new();
    private readonly Button _connect = new() { Text = "Connect / Refresh" };
    private readonly Button _apiKey = new() { Text = "API key…" };
    private readonly Button _clearApiKey =
        new() { Text = "Remove saved key…" };
    private readonly Button _checkRecovery = new()
    {
        Text = "Refresh recovery status",
        ToolTip =
            "Read local operation status without sending a paid request.",
    };
    private readonly Button _reviewRecovery = new()
    {
        Text = "Review recovery…",
        ToolTip =
            "Inspect recovered operations, review the risk, and unlock Tripo.",
    };
    private readonly Button _createInRhino =
        new() { Text = "Create in Rhino" };
    private readonly Button _directGlbWaitAction = new()
    {
        Text = "Stop waiting",
        Visible = false,
    };
    private readonly Button _generate = new() { Text = "Generate" };
    private readonly Button _refreshGeneration = new()
    {
        Text = "Refresh generation",
        ToolTip =
            "Updates automatically every 2 seconds while queued or running. " +
            "Click for an immediate refresh.",
    };
    private readonly Button _convert = new() { Text = "Convert to OBJ" };
    private readonly Button _refreshConversion = new()
    {
        Text = "Refresh conversion",
    };
    private readonly Button _import = new() { Text = "Import into Rhino" };
    private readonly Button _reset = new() { Text = "New workflow" };
    private readonly RadioButtonList _inputMode = new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = new Eto.Drawing.Size(16, 0),
        Items = { "Text", "Image" },
        SelectedIndex = 0,
    };
    private readonly StackLayout _promptBlock;
    private readonly Button _pickImage = new()
    {
        Text = "Choose image\u2026",
        ToolTip =
            "Select a PNG or JPEG on disk. The file is copied into private " +
            "staging and then uploaded to Tripo; the original is not retained.",
    };
    private readonly ImageView _imagePreview = new()
    {
        Size = new Eto.Drawing.Size(96, 96),
    };
    private readonly Label _imageName = StatusLabel();
    private readonly Button _clearImage = new()
    {
        Text = "Remove image",
        Visible = false,
    };
    private readonly StackLayout _imageBlock;
    private readonly StackLayout _generationDiagnosticBlock;
    private readonly StackLayout _conversionDiagnosticBlock;
    private readonly StackLayout _importReceiptDetails;
    private string? _recoveryInspection;
    private string? _recoveryInspectionToken;
    private string? _displayedPreparedOperationId;
    private string _lastValidObjectName =
        Tripo.HostUi.RhinoPanelUserSettings.DefaultObjectName;
    private Tripo.HostUi.DirectGlbAutoImportIntent?
        _directGlbAutoImportIntent;
    private Tripo.HostUi.DirectGlbCreateUiStage _directGlbCreateUiStage =
        Tripo.HostUi.DirectGlbCreateUiStage.Inactive;
    private bool _imageMode;
    private Tripo.Bridge.StagedImageTransfer? _stagedImage;
    private string? _stagedImageName;
    private string? _stagedImageSourcePath;
    private bool _recoveryWasBlocked;
    private bool _recoveryReviewInProgress;
    private bool _createInRhinoStarting;
    private bool _closing;
    private long _sessionGeneration = 1;

    public TripoRhinoPanel(uint documentSerialNumber)
    {
        _documentSerialNumber = documentSerialNumber;
        _plugin = TripoRhinoPlugin.Instance;
        _session = _plugin.CreatePanelSession();
        _renderQueue = new(
            callback => Application.Instance.AsyncInvoke(callback),
            ApplyRenderFrame,
            ShowError);
        _generationStatusPoller = new(
            TimeSpan.FromSeconds(2),
            RefreshGenerationStatusAutomaticallyAsync,
            ReportAutomaticGenerationRefreshFailure);
        ApplyUserSettings(Tripo.HostUi.RhinoPanelUserSettings.Load());
        _promptBlock = FieldBlock("Describe the model", _prompt);
        _imageBlock = new StackLayout
        {
            AlignLabels = false,
            Spacing = 6,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                _pickImage,
                new StackLayout
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Items =
                    {
                        _imagePreview,
                        new StackLayout
                        {
                            Spacing = 4,
                            HorizontalContentAlignment =
                                HorizontalAlignment.Stretch,
                            Items =
                            {
                                _imageName,
                                _clearImage,
                            },
                        },
                    },
                },
            },
        };
        _imageBlock.Visible = false;
        _generationDiagnosticBlock = FieldBlock(
            "Generation diagnostic",
            _generationDiagnostic);
        _conversionDiagnosticBlock = FieldBlock(
            "Conversion diagnostic",
            _conversionDiagnostic);
        _importReceiptDetails = new StackLayout
        {
            AlignLabels = false,
            Spacing = 7,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                FieldBlock(
                    "Rhino object ID",
                    _importCreatedObject),
                FieldBlock(
                    "Import transaction",
                    _importTransaction),
            },
        };
        _session.StateChanged += OnStateChanged;
        _session.RecoveryChanged += OnRecoveryChanged;

        Content = new Scrollable
        {
            Border = BorderType.None,
            ExpandContentHeight = true,
            ExpandContentWidth = true,
            Content = BuildContent(),
        };
        _connect.Click += OnConnect;
        _apiKey.Click += OnApiKey;
        _clearApiKey.Click += OnClearApiKey;
        _checkRecovery.Click += OnCheckRecovery;
        _reviewRecovery.Click += OnReviewRecovery;
        _createInRhino.Click += OnCreateInRhino;
        _directGlbWaitAction.Click += OnDirectGlbWaitAction;
        _generate.Click += OnGenerate;
        _refreshGeneration.Click += OnRefreshGeneration;
        _convert.Click += OnConvert;
        _refreshConversion.Click += OnRefreshConversion;
        _import.Click += OnImport;
        _reset.Click += OnReset;
        _prompt.TextChanged += OnFormInputChanged;
        _name.TextChanged += OnObjectNameChanged;
        _importSource.SelectedIndexChanged += OnFormInputChanged;
        _inputMode.SelectedIndexChanged += OnInputModeChanged;
        _pickImage.Click += OnPickImage;
        _clearImage.Click += OnClearImage;
        _faceLimit.ValueChanged += OnSettingsChanged;
        _withMaterials.CheckedChanged += OnSettingsChanged;
        Load += OnLoaded;
        KeyDown += OnPanelKeyDown;
        ApplyControls(_session.State, _session.Recovery);
    }

    public void PanelShown(uint documentSerialNumber, ShowPanelReason reason)
    {
        if (_session.Recovery.HasBlock)
        {
            _session.RefreshRecovery();
        }

        if (!_session.State.Connected && !_session.State.Busy)
        {
            _ = ConnectSafelyAsync();
        }
    }

    public void PanelHidden(uint documentSerialNumber, ShowPanelReason reason)
    {
        // Hiding a Rhino panel is not a workflow cancellation.
    }

    public void PanelClosing(uint documentSerialNumber, bool onCloseDocument)
    {
        if (onCloseDocument)
        {
            _ = DisposeSessionAsync();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Windows can reuse a PerDoc panel instance after an ordinary tab
            // close, while macOS may actually dispose an inspector instance.
            // Rhino's real control-disposal signal is the common teardown seam.
            _ = DisposeSessionAsync();
        }

        base.Dispose(disposing);
    }

    private StackLayout BuildContent()
    {
        _recoveryExpander.Header = _recoveryHeader;
        _recoveryExpander.Expanded = false;
        _recoveryExpander.Content = new StackLayout
        {
            AlignLabels = false,
            Padding = new Eto.Drawing.Padding(8, 6, 0, 2),
            Spacing = 6,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                _recoveryStatus,
                ActionColumn(_reviewRecovery, _checkRecovery),
            },
        };

        _detailsExpander.Header = LeftLabel("Workflow details");
        _detailsExpander.Expanded = false;
        _detailsExpander.Content = new StackLayout
        {
            AlignLabels = false,
            Padding = new Eto.Drawing.Padding(8, 6, 0, 2),
            Spacing = 7,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                FieldBlock("Document session", _documentSession),
                FieldBlock(
                    "Generation operation ID",
                    _generationOperation),
                FieldBlock("Generation task ID", _generationTaskId),
                _generationDiagnosticBlock,
                FieldBlock(
                    "Conversion operation ID",
                    _conversionOperation),
                FieldBlock("Conversion task ID", _conversionTaskId),
                _conversionDiagnosticBlock,
                FieldBlock("Import operation ID", _importOperation),
                _importReceiptDetails,
            },
        };

        _settingsExpander.Header = LeftLabel("Settings");
        _settingsExpander.Expanded = false;
        _settingsExpander.Content = new StackLayout
        {
            AlignLabels = false,
            Padding = new Eto.Drawing.Padding(8, 6, 0, 2),
            Spacing = 7,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                FieldBlock(
                    "Face limit",
                    _faceLimit,
                    stretchControl: false),
                _withMaterials,
                FieldBlock("Object name", _name),
                ActionColumn(_apiKey, _clearApiKey),
            },
        };

        _advancedExpander.Header = LeftLabel("Advanced");
        _advancedExpander.Expanded = false;
        _advancedExpander.Content = new StackLayout
        {
            AlignLabels = false,
            Padding = new Eto.Drawing.Padding(8, 6, 0, 2),
            Spacing = 8,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                ActionColumn(_connect),
                Section(
                    "Manual generation",
                    ActionColumn(_generate, _refreshGeneration)),
                Section(
                    "Optional OBJ compatibility",
                    _conversionTask,
                    _conversionProgress,
                    ActionColumn(_convert, _refreshConversion)),
                Section(
                    "Manual import",
                    FieldBlock(
                        "Import source",
                        _importSource,
                        stretchControl: false),
                    _importGuidance,
                    FieldBlock(
                        "Import mode",
                        _importMode,
                        stretchControl: false),
                    _applyMaterials,
                    ActionColumn(_import)),
                _detailsExpander,
            },
        };

        _createInRhino.Height = 40;
        return new StackLayout
        {
            AlignLabels = false,
            Padding = 12,
            Spacing = 10,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                Stretch(
                    new StackLayout
                    {
                        AlignLabels = false,
                        Spacing = 5,
                        HorizontalContentAlignment =
                            HorizontalAlignment.Stretch,
                        Items =
                        {
                            Stretch(
                                new Label
                                {
                                    Text = "Tripo 3D",
                                    Font = Eto.Drawing.SystemFonts.Bold(),
                                    TextAlignment = TextAlignment.Left,
                                }),
                            Stretch(_documentStatus),
                            Stretch(_credentialStatus),
                        },
                    }),
                Stretch(_recoveryExpander),
                FieldBlock("Input mode", _inputMode),
                _promptBlock,
                _imageBlock,
                Stretch(_createInRhino),
                Stretch(_createGuidance),
                Stretch(_resultStatus),
                Stretch(_generationTask),
                Stretch(_generationProgress),
                Stretch(ActionColumn(_directGlbWaitAction, _reset)),
                Stretch(_settingsExpander),
                Stretch(_advancedExpander),
                new StackLayoutItem(new Panel(), expand: true),
            },
        };
    }

    private static GroupBox Section(string title, params Control[] controls)
    {
        StackLayout content = new()
        {
            AlignLabels = false,
            Padding = new Eto.Drawing.Padding(8, 6),
            Spacing = 7,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        foreach (Control control in controls)
        {
            content.Items.Add(Stretch(control));
        }

        return new GroupBox
        {
            Text = title,
            Content = content,
        };
    }

    private static StackLayout FieldBlock(
        string label,
        Control control,
        bool stretchControl = true) =>
        new()
        {
            AlignLabels = false,
            Spacing = 3,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                Stretch(LeftLabel(label)),
                new StackLayoutItem(
                    control,
                    stretchControl
                        ? HorizontalAlignment.Stretch
                        : HorizontalAlignment.Left),
            },
        };

    private static StackLayoutItem Stretch(Control control) =>
        new(control, HorizontalAlignment.Stretch);

    private static StackLayout ActionColumn(params Button[] buttons)
    {
        StackLayout layout = new()
        {
            Spacing = 4,
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };
        foreach (Button button in buttons)
        {
            layout.Items.Add(
                new StackLayoutItem(
                    button,
                    HorizontalAlignment.Left));
        }

        return layout;
    }

    private static Label LeftLabel(string text) =>
        new()
        {
            Text = text,
            TextAlignment = TextAlignment.Left,
            Wrap = WrapMode.Word,
        };

    private static Label StatusLabel() => LeftLabel(string.Empty);

    private static ProgressBar WorkflowProgressBar() =>
        new()
        {
            MaxValue = 100,
            MinValue = 0,
            Visible = false,
        };

    private static TextArea DiagnosticBox() =>
        new()
        {
            Height = 60,
            ReadOnly = true,
            Wrap = true,
        };

    private static TableLayout RightAlignedActions(params Button[] buttons)
    {
        TableRow row = new()
        {
            Cells =
            {
                new TableCell(new Panel(), scaleWidth: true),
            },
        };
        foreach (Button button in buttons)
        {
            row.Cells.Add(button);
        }

        return new TableLayout
        {
            Spacing = new Eto.Drawing.Size(8, 4),
            Rows = { row },
        };
    }

    private async void OnLoaded(object? sender, EventArgs args) =>
        await ConnectSafelyAsync();

    private void OnFormInputChanged(object? sender, EventArgs args) =>
        RequestRender();

    private void OnSettingsChanged(object? sender, EventArgs args)
    {
        PersistUserSettings();
        RequestRender();
    }

    private void OnInputModeChanged(object? sender, EventArgs args)
    {
        bool image = _inputMode.SelectedIndex == 1;
        if (_imageMode == image)
        {
            return;
        }

        _imageMode = image;
        _promptBlock.Visible = !image;
        _imageBlock.Visible = image;
        RequestRender();
    }

    private async void OnPickImage(object? sender, EventArgs args)
    {
        try
        {
            EnsurePanelDocumentIsActive();
            string? path = PickImagePath();
            if (path is null)
            {
                return;
            }

            await StageSelectedImageAsync(path);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private void OnClearImage(object? sender, EventArgs args)
    {
        if (_stagedImage is null)
        {
            return;
        }

        _stagedImage = null;
        _stagedImageName = null;
        _stagedImageSourcePath = null;
        _imagePreview.Image = null;
        _imageName.Text = string.Empty;
        RequestRender();
    }

    private void OnPanelKeyDown(object? sender, KeyEventArgs args)
    {
        if (!_imageMode || args.Handled)
        {
            return;
        }

        bool paste =
            args.Key == Keys.V &&
            (args.Modifiers & Application.Instance.CommonModifier) != Keys.None;
        if (!paste)
        {
            return;
        }

        args.Handled = true;
        _ = StageClipboardImageAsync();
    }

    private async Task StageClipboardImageAsync()
    {
        try
        {
            EnsurePanelDocumentIsActive();
            Eto.Drawing.Image? data = Clipboard.Instance.Image;
            if (data is null)
            {
                return;
            }

            string path = Path.Combine(
                Path.GetTempPath(),
                $"tripo-clipboard-{Guid.NewGuid():N}.png");
            using (Eto.Drawing.Bitmap bitmap =
                data is Eto.Drawing.Bitmap existing
                    ? existing
                    : new Eto.Drawing.Bitmap(data))
            {
                await using FileStream stream = new(
                    path,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    64 * 1024,
                    useAsync: true);
                using Eto.Drawing.Bitmap png = bitmap;
                png.Save(stream, Eto.Drawing.ImageFormat.Png);
            }

            await StageSelectedImageAsync(path, isClipboardTemp: true);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async Task StageSelectedImageAsync(
        string path,
        bool isClipboardTemp = false)
    {
        Tripo.Bridge.StagedImageTransfer transfer =
            await Tripo.Bridge.ImageTransferStore.StageAsync(
                    path,
                    _panelLifetime.Token)
                .ConfigureAwait(true);
        _stagedImage = transfer;
        _stagedImageName = Path.GetFileName(path);
        _stagedImageSourcePath = isClipboardTemp ? null : path;
        try
        {
            _imagePreview.Image = new Eto.Drawing.Bitmap(path);
        }
        catch
        {
            // A non-fatal preview failure must not block staging. The label
            // still identifies the file; the user can proceed or clear it.
            _imagePreview.Image = null;
        }

        _imageName.Text = _stagedImageName;
        RequestRender();
    }

    private string? PickImagePath()
    {
        EnsurePanelDocumentIsActive();
        global::Rhino.RhinoDoc rhinoDocument = global::Rhino.RhinoDoc.ActiveDoc!;
        Eto.Forms.OpenFileDialog dialog = new()
        {
            CheckFileExists = true,
            MultiSelect = false,
            Title = "Choose a PNG or JPEG for Tripo",
            Filters =
            {
                new Eto.Forms.FileFilter(
                    "PNG or JPEG",
                    "*.png",
                    "*.jpg",
                    "*.jpeg"),
            },
        };
        return dialog.ShowDialog(
                global::Rhino.UI.RhinoEtoApp.MainWindowForDocument(
                    rhinoDocument)) == Eto.Forms.DialogResult.Ok
            ? dialog.FileName
            : null;
    }

    private void OnObjectNameChanged(object? sender, EventArgs args)
    {
        if (Tripo.HostUi.RhinoPanelUserSettings.TryNormalizeObjectName(
                _name.Text,
                out string normalized))
        {
            _lastValidObjectName = normalized;
            PersistUserSettings();
        }

        RequestRender();
    }

    private async void OnConnect(object? sender, EventArgs args) =>
        await ConnectSafelyAsync();

    private async Task ConnectSafelyAsync()
    {
        if (_closing || _session.State.Busy)
        {
            return;
        }

        try
        {
            EnsureNoAutomaticDirectGlbCreateMutation("reconnect the panel");
            EnsurePanelDocumentIsActive();
            await _session.ConnectAsync(_panelLifetime.Token);
            if (_closing)
            {
                return;
            }

            if (_session.State.CredentialStatus?.HasApiKey == false &&
                !_session.Recovery.HasBlock)
            {
                await PromptForCurrentWorkflowApiKeyAsync();
            }
        }
        catch (OperationCanceledException) when (_closing)
        {
        }
        catch (ObjectDisposedException) when (_closing)
        {
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async void OnApiKey(object? sender, EventArgs args)
    {
        try
        {
            EnsureDirectGlbCredentialMutationAllowed();
            if (!_session.State.Connected)
            {
                await _session.ConnectAsync(_panelLifetime.Token);
            }

            if (_closing)
            {
                return;
            }

            if (_session.Recovery.HasBlock &&
                !await ReviewAndUnlockRecoveryAsync())
            {
                return;
            }

            if (!_closing)
            {
                await PromptForCurrentWorkflowApiKeyAsync();
            }
        }
        catch (OperationCanceledException) when (_closing)
        {
        }
        catch (ObjectDisposedException) when (_closing)
        {
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async void OnClearApiKey(object? sender, EventArgs args)
    {
        try
        {
            EnsureNoAutomaticDirectGlbCreateMutation(
                "remove the saved API key");
            if (_closing ||
                MessageBox.Show(
                    this,
                    "Remove the Tripo API key saved for this user? This also " +
                    "clears any session-only key. A TRIPO_API_KEY environment " +
                    "override, if configured outside Rhino, remains effective." +
                    "\n\nThis does not change the .3dm document or cancel a " +
                    "remote Tripo task.",
                    "Remove saved Tripo API key?",
                    MessageBoxButtons.YesNo,
                    MessageBoxType.Warning,
                    MessageBoxDefaultButton.No) != DialogResult.Yes)
            {
                return;
            }

            await _session.ClearApiKeyAsync(_panelLifetime.Token);
        }
        catch (OperationCanceledException) when (_closing)
        {
        }
        catch (ObjectDisposedException) when (_closing)
        {
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async Task PromptForCurrentWorkflowApiKeyAsync()
    {
        if (_closing)
        {
            return;
        }

        Tripo.HostUi.TripoPanelSession ownerSession = _session;
        long ownerGeneration = _sessionGeneration;
        Tripo.HostUi.TripoApiKeyPromptPolicy policy =
            Tripo.HostUi.TripoApiKeyPromptPolicy.Create(
                ownerSession.State);
        ApiKeySubmission? submission =
            await RhinoUiThread.InvokeAsync(
                    () => CollectApiKeySubmission(policy),
                    _panelLifetime.Token)
                .ConfigureAwait(false);
        if (submission is null)
        {
            return;
        }

        if (_closing || !ReferenceEquals(ownerSession, _session))
        {
            submission.Clear();
            return;
        }

        string secret = submission.TakeSecret();
        try
        {
            await ownerSession.SetApiKeyAsync(
                    secret,
                    submission.Persist,
                    _panelLifetime.Token)
                .ConfigureAwait(false);
            if (!_closing &&
                ownerGeneration == _sessionGeneration &&
                ReferenceEquals(ownerSession, _session) &&
                _directGlbAutoImportIntent is not null &&
                (_directGlbCreateUiStage is
                    Tripo.HostUi.DirectGlbCreateUiStage
                        .WaitingForGeneration or
                    Tripo.HostUi.DirectGlbCreateUiStage.WaitingPaused))
            {
                string? pendingTaskId =
                    Tripo.HostUi.DirectGlbGenerationPollingPolicy
                        .GetPendingTaskId(
                            ownerSession.State,
                            ownerSession.Recovery,
                            _directGlbAutoImportIntent);
                _generationStatusPoller.Resume(pendingTaskId);

                QueueDirectGlbAutoImportContinuation(
                    ownerSession,
                    ownerGeneration);
            }
        }
        finally
        {
            secret = string.Empty;
            submission.Clear();
        }
    }

    private ApiKeySubmission? CollectApiKeySubmission(
        Tripo.HostUi.TripoApiKeyPromptPolicy policy)
    {
        if (_closing)
        {
            return null;
        }

        if (policy.RecoveryMode &&
            !ConfirmRecoveryApiKey(policy))
        {
            return null;
        }

        PasswordBox password = new()
        {
            MaxLength = 2048,
            ToolTip = "Paste the Tripo v3 API key.",
        };
        CheckBox persist = new()
        {
            Checked = policy.PersistAllowed,
            Enabled = policy.PersistAllowed,
            Text = "Save in this user's OS credential store",
            ToolTip = policy.RecoveryMode
                ? "Recovery credentials remain session-only until the " +
                  "account-bound workflow is reconciled and explicitly reset."
                : null,
        };
        CheckBox confirmReplacement = new()
        {
            Text =
                "I want this new key to be used for future paid requests.",
            Visible = policy.RequiresReplacementConfirmation,
        };
        Button save = new()
        {
            Enabled = false,
            Text = policy.RecoveryMode
                ? "Use recovery key"
                : policy.Replacing
                    ? "Replace API key"
                    : "Save API key",
        };
        Button cancel = new() { Text = "Cancel" };
        StackLayout content = new()
        {
            Padding = 12,
            Spacing = 8,
        };
        content.Items.Add(
            new Label
            {
                Text = ApiKeyInstructions,
                Wrap = WrapMode.Word,
            });
        if (policy.RecoveryMode)
        {
            content.Items.Add(
                new Label
                {
                    Text = policy.ExactOriginalKeyRequired
                        ? "Recovery mode: this key is session-only. " +
                          "Restore the exact original key for the unresolved " +
                          "paid UUID."
                        : "Recovery mode: this key is session-only. Use a key " +
                          "for the same Tripo account as the unfinished workflow.",
                    Wrap = WrapMode.Word,
                });
        }

        content.Items.Add(LeftLabel("API key"));
        content.Items.Add(password);
        content.Items.Add(persist);
        content.Items.Add(confirmReplacement);
        content.Items.Add(RightAlignedActions(save, cancel));
        Dialog<bool> dialog = new()
        {
            Title = policy.Replacing
                ? "Replace Tripo API key"
                : "Set Tripo API key",
            ClientSize = new Eto.Drawing.Size(
                520,
                policy.Replacing ? 410 : 370),
            Resizable = true,
            Content = content,
        };
        void UpdateSaveEnabled() =>
            save.Enabled =
                !string.IsNullOrWhiteSpace(password.Text) &&
                (!policy.RequiresReplacementConfirmation ||
                 confirmReplacement.Checked == true);
        password.TextChanged += (_, _) => UpdateSaveEnabled();
        confirmReplacement.CheckedChanged +=
            (_, _) => UpdateSaveEnabled();
        save.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        if (!policy.RequiresReplacementConfirmation)
        {
            dialog.DefaultButton = save;
        }

        dialog.AbortButton = cancel;
        dialog.Shown += (_, _) => password.Focus();

        try
        {
            if (!dialog.ShowModal(this))
            {
                return null;
            }

            if (_closing)
            {
                return null;
            }

            string secret = password.Text;
            bool shouldPersist = persist.Checked == true;
            password.Text = string.Empty;
            return new ApiKeySubmission(secret, shouldPersist);
        }
        finally
        {
            password.Text = string.Empty;
            dialog.Dispose();
        }
    }

    private bool ConfirmRecoveryApiKey(
        Tripo.HostUi.TripoApiKeyPromptPolicy policy)
    {
        return MessageBox.Show(
            this,
            (policy.ExactOriginalKeyRequired
                ? "A paid dispatch has no durable task ID. Restore the exact " +
                  "original API key; the journal fingerprint is bound to that key."
                : "An accepted remote task or unresolved import still needs " +
                  "access. Use a key for the same Tripo account.") +
            "\n\nWorkflow operation ID:\n" +
            (policy.WorkflowOperationId ?? "Unavailable") +
            "\n\nThe recovery key remains session-only.",
            "Restore workflow access with an API key?",
            MessageBoxButtons.YesNo,
            MessageBoxType.Warning,
            MessageBoxDefaultButton.No) == DialogResult.Yes;
    }

    private async void OnCheckRecovery(object? sender, EventArgs args)
    {
        if (_recoveryReviewInProgress || _closing)
        {
            return;
        }

        try
        {
            EnsureNoAutomaticDirectGlbCreateMutation(
                "inspect recovery records");
            _recoveryReviewInProgress = true;
            RequestRender();
            Tripo.HostUi.TripoPanelRecoveryLoadResult recovery =
                _session.RefreshRecovery();
            if (recovery.Hints.Count == 0)
            {
                _recoveryInspection = null;
                _recoveryInspectionToken = null;
                RequestRender();
                return;
            }

            if (!_session.State.Connected)
            {
                await _session.ConnectAsync(_panelLifetime.Token);
            }

            if (_closing)
            {
                return;
            }

            Tripo.HostUi.TripoPanelRecoveryReviewSnapshot snapshot =
                await _session.CreateRecoveryReviewSnapshotAsync(
                    _panelLifetime.Token);
            if (_closing)
            {
                return;
            }

            _recoveryInspection =
                Tripo.HostUi.TripoPanelRecoveryReviewFormatter.Format(
                    snapshot);
            _recoveryInspectionToken = snapshot.RecoveryToken;
            RequestRender();
        }
        catch (OperationCanceledException) when (_closing)
        {
        }
        catch (ObjectDisposedException) when (_closing)
        {
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
        finally
        {
            _recoveryReviewInProgress = false;
            RequestRender();
        }
    }

    private async void OnReviewRecovery(object? sender, EventArgs args)
    {
        try
        {
            EnsureNoAutomaticDirectGlbCreateMutation(
                "review recovery records");
            await ReviewAndUnlockRecoveryAsync();
        }
        catch (OperationCanceledException) when (_closing)
        {
        }
        catch (ObjectDisposedException) when (_closing)
        {
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async Task<bool> ReviewAndUnlockRecoveryAsync()
    {
        if (_recoveryReviewInProgress || _closing)
        {
            return false;
        }

        _recoveryReviewInProgress = true;
        RequestRender();
        try
        {
            Tripo.HostUi.TripoPanelRecoveryLoadResult recovery =
                _session.RefreshRecovery();
            if (!recovery.HasBlock)
            {
                return true;
            }

            if (recovery.Issues.Count > 0)
            {
                List<string> issues = [];
                foreach (Tripo.HostUi.TripoPanelRecoveryIssue issue in
                         recovery.Issues)
                {
                    issues.Add(
                        $"{issue.FileName}: {issue.Code}. {issue.Message}");
                }

                MessageBox.Show(
                    this,
                    "Tripo cannot safely read these recovery records, so it " +
                    "will not delete or archive them automatically:\n\n" +
                    string.Join(Environment.NewLine, issues) +
                    "\n\nInspect or move the named files aside manually, " +
                    "then choose Refresh recovery status.",
                    "Recovery needs manual attention",
                    MessageBoxType.Warning);
                return false;
            }

            if (_session.State.HasWorkflowState)
            {
                if (!ConfirmReloadForRecovery())
                {
                    return false;
                }

                await ReloadPanelSessionForRecoveryAsync();
                if (_closing)
                {
                    return false;
                }

                recovery = _session.RefreshRecovery();
                if (recovery.Issues.Count > 0)
                {
                    throw new InvalidOperationException(
                        "Reloaded recovery contains a record that needs manual " +
                        "attention. Repair it, then refresh recovery status.");
                }
            }

            if (!_session.State.Connected)
            {
                await _session.ConnectAsync(_panelLifetime.Token);
            }

            if (_closing)
            {
                return false;
            }

            Tripo.HostUi.TripoPanelRecoveryReviewSnapshot snapshot =
                await _session.CreateRecoveryReviewSnapshotAsync(
                    _panelLifetime.Token);
            if (_closing)
            {
                return false;
            }

            string details =
                Tripo.HostUi.TripoPanelRecoveryReviewFormatter.Format(
                    snapshot);
            _recoveryInspection = details;
            _recoveryInspectionToken = snapshot.RecoveryToken;
            RequestRender();
            if (snapshot.HasOperationInProgress)
            {
                MessageBox.Show(
                    this,
                    "A recovered paid operation is still active. Tripo will " +
                    "keep recovery blocked. Wait for it to finish, then choose " +
                    "Refresh recovery status and review again.",
                    "Tripo operation still active",
                    MessageBoxType.Information);
                return false;
            }

            if (_closing)
            {
                return false;
            }

            if (!ShowRecoveryReviewDialog(details))
            {
                return false;
            }

            if (_closing)
            {
                return false;
            }

            try
            {
                await _session.UnlockRecoveredOperationsAsync(
                    userConfirmed: true,
                    snapshot,
                    _panelLifetime.Token);
            }
            catch
            {
                _recoveryInspection = null;
                _recoveryInspectionToken = null;
                throw;
            }

            _recoveryInspection = null;
            _recoveryInspectionToken = null;
            RequestRender();
            return !_session.Recovery.HasBlock;
        }
        finally
        {
            _recoveryReviewInProgress = false;
            RequestRender();
        }
    }

    private bool ConfirmReloadForRecovery() =>
        MessageBox.Show(
            this,
            "This panel also contains current workflow state.\n\n" +
            "Reloading the panel session sends no Tripo request. It preserves " +
            "every dispatched operation ID as recovery evidence and clears " +
            "only setup that was never sent. You can then review all recovered " +
            "work together.\n\nReload and continue?",
            "Reload Tripo recovery?",
            MessageBoxButtons.YesNo,
            MessageBoxType.Warning,
            MessageBoxDefaultButton.No) == DialogResult.Yes;

    private async Task ReloadPanelSessionForRecoveryAsync()
    {
        Tripo.HostUi.TripoPanelSession previous = _session;
        _directGlbAutoImportIntent = null;
        _directGlbCreateUiStage =
            Tripo.HostUi.DirectGlbCreateUiStage.Inactive;
        _generationStatusPoller.Stop();
        _renderQueue.CancelPending();
        previous.StateChanged -= OnStateChanged;
        previous.RecoveryChanged -= OnRecoveryChanged;
        await _plugin.ReleasePanelSessionAsync(previous);
        if (_closing)
        {
            return;
        }

        Tripo.HostUi.TripoPanelSession replacement =
            _plugin.CreatePanelSession();
        _session = replacement;
        _sessionGeneration++;
        replacement.StateChanged += OnStateChanged;
        replacement.RecoveryChanged += OnRecoveryChanged;
        _recoveryInspection = null;
        _recoveryInspectionToken = null;
        _displayedPreparedOperationId = null;
        RequestRender();
    }

    private bool ShowRecoveryReviewDialog(string details)
    {
        TextArea evidence = new()
        {
            Height = 190,
            ReadOnly = true,
            Text = details,
            Wrap = true,
        };
        CheckBox confirmed = new()
        {
            Text =
                "I reviewed every operation above, checked Tripo task and " +
                "billing history where local status was missing or ambiguous, " +
                "and checked any Rhino import in the original document.",
        };
        Button unlock = new()
        {
            Enabled = false,
            Text = "Archive reviewed notices and continue",
            ToolTip =
                "Archive only the local recovery notice and unlock Tripo.",
        };
        Button cancel = new() { Text = "Keep blocked" };
        StackLayout content = new()
        {
            Padding = 12,
            Spacing = 9,
            Items =
            {
                new Label
                {
                    Text =
                        "A previous paid request may have reached Tripo " +
                        "before its result was saved. Review the evidence " +
                        "below to avoid an accidental duplicate charge.",
                    Wrap = WrapMode.Word,
                },
                evidence,
                confirmed,
                new Label
                {
                    Text =
                        "Continuing archives only this local notice. It " +
                        "does not retry, cancel, refund, or delete a remote " +
                        "Tripo task.",
                    Wrap = WrapMode.Word,
                },
                RightAlignedActions(unlock, cancel),
            },
        };
        Dialog<bool> dialog = new()
        {
            Title = "Review previous Tripo work",
            ClientSize = new Eto.Drawing.Size(600, 500),
            Resizable = true,
            Content = content,
        };
        confirmed.CheckedChanged +=
            (_, _) => unlock.Enabled = confirmed.Checked == true;
        unlock.Click += (_, _) =>
        {
            if (confirmed.Checked == true)
            {
                dialog.Close(true);
            }
        };
        cancel.Click += (_, _) => dialog.Close(false);
        dialog.AbortButton = cancel;
        dialog.Shown += (_, _) => confirmed.Focus();
        try
        {
            return dialog.ShowModal(this);
        }
        finally
        {
            confirmed.Checked = false;
            evidence.Text = string.Empty;
            dialog.Dispose();
        }
    }

    private async void OnCreateInRhino(object? sender, EventArgs args)
    {
        if (_closing ||
            _createInRhinoStarting ||
            _directGlbAutoImportIntent is not null ||
            _directGlbCreateUiStage !=
                Tripo.HostUi.DirectGlbCreateUiStage.Inactive)
        {
            return;
        }

        _createInRhinoStarting = true;
        _directGlbCreateUiStage =
            Tripo.HostUi.DirectGlbCreateUiStage.Preflighting;
        RequestRender();
        Tripo.HostUi.TripoPanelSession ownerSession = _session;
        long ownerGeneration = _sessionGeneration;
        Tripo.HostUi.PreparedTextGeneration? prepared = null;
        Tripo.HostUi.DirectGlbAutoImportIntent? intent = null;
        try
        {
            EnsurePanelDocumentIsActive();
            await ownerSession.ConnectAsync(_panelLifetime.Token);
            if (_closing ||
                ownerGeneration != _sessionGeneration ||
                !ReferenceEquals(ownerSession, _session))
            {
                return;
            }

            EnsurePanelDocumentIsActive();
            Tripo.HostUi.TripoPanelState state = ownerSession.State;
            Tripo.HostUi.TripoPanelPresentation presentation =
                CreatePresentation(state, ownerSession.Recovery);
            if (!presentation.CanStartDirectGlbCreate)
            {
                throw new InvalidOperationException(
                    presentation.CreateInRhinoHelp);
            }

            if (!Tripo.HostUi.RhinoPanelUserSettings.TryNormalizeObjectName(
                    _name.Text,
                    out string normalizedObjectName))
            {
                throw new InvalidOperationException(
                    "The Rhino object name must contain 1 to 128 characters.");
            }

            prepared = ownerSession.PrepareGeneration(
                    _prompt.Text,
                    checked((int)_faceLimit.Value),
                    _withMaterials.Checked == true);
            PersistUserSettings();
            RequestRender();
            if (!ConfirmDirectGlbCreate(
                    prepared.OperationId,
                    state.Context?.DocumentTitle ?? "the active document",
                    normalizedObjectName))
            {
                if (!_closing &&
                    ownerGeneration == _sessionGeneration &&
                    ReferenceEquals(ownerSession, _session) &&
                    !ownerSession.State.GenerationDispatchAttempted)
                {
                    ownerSession.ResetWorkflow();
                    _directGlbCreateUiStage =
                        Tripo.HostUi.DirectGlbCreateUiStage.Inactive;
                    RequestRender();
                }

                return;
            }

            EnsurePanelDocumentIsActive();
            await ownerSession.ConnectAsync(_panelLifetime.Token);
            if (_closing ||
                ownerGeneration != _sessionGeneration ||
                !ReferenceEquals(ownerSession, _session))
            {
                return;
            }

            EnsurePanelDocumentIsActive();
            EnsureDirectGlbFirstDispatchAvailable(
                ownerSession.State,
                ownerSession.Recovery,
                prepared);
            intent = new Tripo.HostUi.DirectGlbAutoImportIntent(
                ownerGeneration,
                prepared.OperationId,
                prepared.DocumentSessionId,
                normalizedObjectName);
            _directGlbAutoImportIntent = intent;
            _directGlbCreateUiStage =
                Tripo.HostUi.DirectGlbCreateUiStage.WaitingForGeneration;
            RequestRender();
            await ownerSession
                .DispatchPreparedGenerationRequiringCapabilityAsync(
                userConfirmedExternalCost: true,
                requiredHostCapability:
                    Tripo.Bridge.BridgeConstants.ImportGlbMethod,
                requiredSidecarCapability:
                    Tripo.Bridge.HostControlConstants
                        .ImportGenerationGlbMethod,
                cancellationToken: _panelLifetime.Token);
            if (!_closing &&
                ownerGeneration == _sessionGeneration &&
                ReferenceEquals(ownerSession, _session))
            {
                RequestRender(
                    ownerSession,
                    ownerSession.State,
                    ownerSession.Recovery);
                QueueDirectGlbAutoImportContinuation(
                    ownerSession,
                    ownerGeneration);
            }
        }
        catch (OperationCanceledException) when (_closing)
        {
        }
        catch (ObjectDisposedException) when (
            _closing ||
            ownerGeneration != _sessionGeneration ||
            !ReferenceEquals(ownerSession, _session))
        {
        }
        catch (Exception exception)
        {
            if (ownerGeneration == _sessionGeneration &&
                ReferenceEquals(ownerSession, _session))
            {
                Tripo.HostUi.TripoPanelState failedState =
                    ownerSession.State;
                bool matchingUnsentGeneration =
                    prepared is not null &&
                    failedState.PreparedGeneration is { } failedPrepared &&
                    string.Equals(
                        failedPrepared.OperationId,
                        prepared.OperationId,
                        StringComparison.Ordinal) &&
                    !failedState.GenerationDispatchAttempted &&
                    !failedState.HasDurableGenerationTask;
                if (matchingUnsentGeneration)
                {
                    if (intent is not null &&
                        ReferenceEquals(
                            _directGlbAutoImportIntent,
                            intent))
                    {
                        _directGlbAutoImportIntent = null;
                    }

                    _directGlbCreateUiStage =
                        Tripo.HostUi.DirectGlbCreateUiStage.Inactive;
                    TryResetUnsentGeneration(
                        ownerSession,
                        prepared!.OperationId);
                    RequestRender();
                }
                else if (intent is not null &&
                         ReferenceEquals(
                             _directGlbAutoImportIntent,
                             intent) &&
                         failedState.PreparedGeneration is null)
                {
                    _directGlbAutoImportIntent = null;
                    _directGlbCreateUiStage =
                        Tripo.HostUi.DirectGlbCreateUiStage.Inactive;
                    RequestRender();
                }
            }

            ShowError(exception);
        }
        finally
        {
            _createInRhinoStarting = false;
            if (intent is null &&
                _directGlbCreateUiStage ==
                    Tripo.HostUi.DirectGlbCreateUiStage.Preflighting)
            {
                _directGlbCreateUiStage =
                    Tripo.HostUi.DirectGlbCreateUiStage.Inactive;
            }

            if (!_closing &&
                ownerGeneration == _sessionGeneration &&
                ReferenceEquals(ownerSession, _session))
            {
                RequestRender();
            }
        }
    }

    private void OnDirectGlbWaitAction(object? sender, EventArgs args)
    {
        try
        {
            if (_closing || _session.State.Busy)
            {
                throw new InvalidOperationException(
                    "Wait for the current panel operation to finish before " +
                    "changing automatic waiting.");
            }

            if (_session.Recovery.HasBlock)
            {
                throw new InvalidOperationException(
                    "Review recovered operation IDs before changing automatic " +
                    "waiting.");
            }

            Tripo.HostUi.DirectGlbAutoImportIntent intent =
                _directGlbAutoImportIntent ??
                throw new InvalidOperationException(
                    "There is no active one-click generation task to pause.");
            Tripo.HostUi.TripoPanelState state = _session.State;
            PanelRenderFrame frame = new(
                _session,
                _sessionGeneration,
                state,
                _session.Recovery);
            if (_directGlbCreateUiStage ==
                Tripo.HostUi.DirectGlbCreateUiStage.WaitingForGeneration)
            {
                if (!intent.TryStopWaiting(_sessionGeneration, state))
                {
                    if (DirectGlbWaitIntentIsIrrecoverable(intent, frame))
                    {
                        FinishDirectGlbIntentForReview(
                            intent,
                            Tripo.HostUi.DirectGlbCreateUiStage.Refused);
                        throw new InvalidOperationException(
                            "Automatic waiting stopped because the durable " +
                            "generation evidence no longer matches this " +
                            "workflow.");
                    }

                    QueueDirectGlbAutoImportContinuation(
                        _session,
                        _sessionGeneration);
                    RequestRender();
                    return;
                }

                if (!IntentOwnsState(intent, frame))
                {
                    FinishDirectGlbIntentForReview(
                        intent,
                        Tripo.HostUi.DirectGlbCreateUiStage.Refused);
                    throw new InvalidOperationException(
                        "Automatic waiting stopped because the panel identity " +
                        "changed while it was being paused.");
                }

                _directGlbCreateUiStage =
                    Tripo.HostUi.DirectGlbCreateUiStage.WaitingPaused;
                _generationStatusPoller.Stop();
            }
            else if (_directGlbCreateUiStage ==
                     Tripo.HostUi.DirectGlbCreateUiStage.WaitingPaused)
            {
                if (state.HasCredentialRefreshFailure)
                {
                    throw new InvalidOperationException(
                        "Restore a same-account session-only API key before " +
                        "resuming automatic waiting.");
                }

                if (!intent.TryResumeWaiting(_sessionGeneration, state))
                {
                    if (DirectGlbWaitIntentIsIrrecoverable(intent, frame))
                    {
                        FinishDirectGlbIntentForReview(
                            intent,
                            Tripo.HostUi.DirectGlbCreateUiStage.Refused);
                        throw new InvalidOperationException(
                            "Automatic waiting could not resume because the " +
                            "durable generation evidence no longer matches this " +
                            "workflow.");
                    }

                    QueueDirectGlbAutoImportContinuation(
                        _session,
                        _sessionGeneration);
                    RequestRender();
                    return;
                }

                if (!IntentOwnsState(intent, frame))
                {
                    FinishDirectGlbIntentForReview(
                        intent,
                        Tripo.HostUi.DirectGlbCreateUiStage.Refused);
                    throw new InvalidOperationException(
                        "Automatic waiting stopped because the panel identity " +
                        "changed while it was being resumed.");
                }

                _directGlbCreateUiStage =
                    Tripo.HostUi.DirectGlbCreateUiStage.WaitingForGeneration;
                _generationStatusPoller.Resume(
                    Tripo.HostUi.DirectGlbGenerationPollingPolicy
                        .GetPendingTaskId(
                            state,
                            _session.Recovery,
                            intent));
                QueueDirectGlbAutoImportContinuation(
                    _session,
                    _sessionGeneration);
            }
            else
            {
                throw new InvalidOperationException(
                    "Automatic waiting can change only while the confirmed " +
                    "generation task is waiting.");
            }

            RequestRender();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async void OnGenerate(object? sender, EventArgs args)
    {
        try
        {
            EnsureNoAutomaticDirectGlbCreateMutation(
                "start or retry generation");
            EnsurePanelDocumentIsActive();
            if (_imageMode)
            {
                await GenerateFromImageAsync();
            }
            else
            {
                await GenerateFromTextAsync();
            }
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async Task GenerateFromTextAsync()
    {
        bool retry = _session.State.GenerationRetryAllowed;
        Tripo.HostUi.PreparedTextGeneration prepared =
            _session.State.PreparedGeneration ??
            _session.PrepareGeneration(
                _prompt.Text,
                checked((int)_faceLimit.Value),
                _withMaterials.Checked == true);
        PersistUserSettings();
        RequestRender();
        if (!ConfirmPaidDispatch(
                retry
                    ? "Retry same generation operation?"
                    : "Create Tripo generation task?",
                prepared.OperationId,
                retry))
        {
            return;
        }

        await _session.DispatchPreparedGenerationAsync(
            userConfirmedExternalCost: true);
    }

    private async Task GenerateFromImageAsync()
    {
        if (_stagedImage is null)
        {
            throw new InvalidOperationException(
                "Choose an image before generating from it.");
        }

        bool retry = _session.State.GenerationRetryAllowed;
        Tripo.HostUi.PreparedImageGeneration prepared =
            _session.State.PreparedImageGeneration ??
            _session.PrepareImageGeneration(
                _stagedImage,
                checked((int)_faceLimit.Value),
                _withMaterials.Checked == true);
        PersistUserSettings();
        RequestRender();
        if (!ConfirmPaidDispatch(
                retry
                    ? "Retry same image generation operation?"
                    : "Create Tripo image generation task?",
                prepared.OperationId,
                retry))
        {
            return;
        }

        await _session.DispatchPreparedImageGenerationAsync(
            userConfirmedExternalCost: true);
    }

    private async void OnRefreshGeneration(object? sender, EventArgs args)
    {
        Tripo.HostUi.TripoPanelSession ownerSession = _session;
        long ownerGeneration = _sessionGeneration;
        try
        {
            EnsureDirectGlbGenerationRefreshAllowed();
            await ownerSession.RefreshGenerationStatusAsync(
                _panelLifetime.Token);
            if (!_closing &&
                ownerGeneration == _sessionGeneration &&
                ReferenceEquals(ownerSession, _session))
            {
                _generationStatusPoller.Resume(
                    Tripo.HostUi.DirectGlbGenerationPollingPolicy
                        .GetPendingTaskId(
                            ownerSession.State,
                            ownerSession.Recovery,
                            _directGlbAutoImportIntent));

                QueueDirectGlbAutoImportContinuation(
                    ownerSession,
                    ownerGeneration);
            }
        }
        catch (OperationCanceledException) when (_closing)
        {
        }
        catch (ObjectDisposedException) when (_closing)
        {
        }
        catch (Exception exception)
        {
            bool ownsCurrentSession =
                !_closing &&
                ownerGeneration == _sessionGeneration &&
                ReferenceEquals(ownerSession, _session);
            if (ownsCurrentSession &&
                _directGlbAutoImportIntent is not null)
            {
                if (!ownerSession.State.HasDurableGenerationTask)
                {
                    _directGlbAutoImportIntent = null;
                    _directGlbCreateUiStage =
                        Tripo.HostUi.DirectGlbCreateUiStage.Inactive;
                    RequestRender();
                }
            }

            ShowError(exception);
            if (ownsCurrentSession &&
                _directGlbAutoImportIntent is not null &&
                ownerSession.State.HasDurableGenerationTask)
            {
                QueueDirectGlbAutoImportContinuation(
                    ownerSession,
                    ownerGeneration);
            }
        }
    }

    private async Task RefreshGenerationStatusAutomaticallyAsync(
        string expectedTaskId,
        CancellationToken cancellationToken)
    {
        Tripo.HostUi.TripoPanelSession ownerSession = _session;
        long ownerGeneration = _sessionGeneration;
        if (_closing ||
            ownerSession.State.Busy ||
            _directGlbAutoImportIntent?.Phase ==
                Tripo.HostUi.DirectGlbAutoImportPhase.Stopped)
        {
            return;
        }

        string? pendingTaskId =
            Tripo.HostUi.DirectGlbGenerationPollingPolicy.GetPendingTaskId(
                ownerSession.State,
                ownerSession.Recovery,
                _directGlbAutoImportIntent);
        if (!string.Equals(
                pendingTaskId,
                expectedTaskId,
                StringComparison.Ordinal))
        {
            return;
        }

        await ownerSession.RefreshGenerationStatusAsync(cancellationToken);
        if (_closing ||
            ownerGeneration != _sessionGeneration ||
            !ReferenceEquals(ownerSession, _session))
        {
            return;
        }

        QueueDirectGlbAutoImportContinuation(
            ownerSession,
            ownerGeneration);
    }

    private void ReportAutomaticGenerationRefreshFailure(
        string expectedTaskId,
        Exception exception)
    {
        if (_closing)
        {
            return;
        }

        Tripo.HostUi.TripoPanelSession ownerSession = _session;
        long ownerGeneration = _sessionGeneration;
        Tripo.HostUi.TripoPanelState state = ownerSession.State;
        string? pendingTaskId =
            Tripo.HostUi.DirectGlbGenerationPollingPolicy.GetPendingTaskId(
                state,
                ownerSession.Recovery,
                _directGlbAutoImportIntent);
        Tripo.HostUi.DirectGlbAutoImportIntent? intent =
            _directGlbAutoImportIntent;
        if (intent?.Phase ==
            Tripo.HostUi.DirectGlbAutoImportPhase.Stopped)
        {
            return;
        }

        if (intent is not null && ownerSession.Recovery.HasBlock)
        {
            QueueDirectGlbIntentReviewTransition(
                ownerSession,
                ownerGeneration,
                intent,
                Tripo.HostUi.DirectGlbCreateUiStage.RecoveryBlocked);
            return;
        }

        bool automaticIntentOwnsTask =
            intent is not null &&
            intent.TryBindDurableTask(ownerGeneration, state) &&
            string.Equals(
                intent.TaskId,
                expectedTaskId,
                StringComparison.Ordinal);
        if (intent is not null &&
            DirectGlbWaitIntentIsIrrecoverable(
                intent,
                new PanelRenderFrame(
                    ownerSession,
                    ownerGeneration,
                    state,
                    ownerSession.Recovery)))
        {
            QueueDirectGlbIntentReviewTransition(
                ownerSession,
                ownerGeneration,
                intent,
                Tripo.HostUi.DirectGlbCreateUiStage.Refused);
            return;
        }

        if (!string.Equals(
                pendingTaskId,
                expectedTaskId,
                StringComparison.Ordinal) &&
            !automaticIntentOwnsTask)
        {
            return;
        }

        ShowError(
            new InvalidOperationException(
                "Automatic generation refresh stopped. Click Refresh " +
                $"generation to retry.\n\n{exception.Message}",
                exception));
        if (automaticIntentOwnsTask)
        {
            QueueDirectGlbAutoImportContinuation(
                ownerSession,
                ownerGeneration);
        }
    }

    private async void OnConvert(object? sender, EventArgs args)
    {
        try
        {
            EnsureNoAutomaticDirectGlbCreateMutation(
                "use OBJ compatibility");
            EnsurePanelDocumentIsActive();
            bool retry = _session.State.ConversionRetryAllowed;
            Tripo.HostUi.PreparedObjConversion prepared =
                _session.State.PreparedConversion ??
                _session.PrepareConversion(
                    checked((int)_faceLimit.Value),
                    _withMaterials.Checked == true);
            RequestRender();
            if (!ConfirmPaidDispatch(
                    retry
                        ? "Retry same OBJ conversion operation?"
                        : "Create Tripo OBJ conversion task?",
                    prepared.OperationId,
                    retry))
            {
                return;
            }

            await _session.DispatchPreparedConversionAsync(
                userConfirmedExternalCost: true);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async void OnRefreshConversion(object? sender, EventArgs args)
    {
        try
        {
            EnsureNoAutomaticDirectGlbCreateMutation(
                "refresh OBJ conversion");
            await _session.RefreshConversionStatusAsync();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async void OnImport(object? sender, EventArgs args)
    {
        try
        {
            EnsureNoAutomaticDirectGlbCreateMutation(
                "start a manual import");
            EnsurePanelDocumentIsActive();
            if (_session.State.PreparedImport is null)
            {
                _ = IsDirectGlbSelected()
                    ? _session.PrepareGlbImport(_name.Text)
                    : _session.PrepareImport(
                        _name.Text,
                        _importMode.SelectedValue?.ToString() ?? "native",
                        _applyMaterials.Checked == true);
                PersistUserSettings();
            }

            RequestRender();
            await _session.ImportPreparedAsync();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private void OnReset(object? sender, EventArgs args)
    {
        try
        {
            EnsureNoAutomaticDirectGlbCreateMutation("reset the workflow");
            _session.ResetWorkflow();
            _directGlbAutoImportIntent = null;
            _directGlbCreateUiStage =
                Tripo.HostUi.DirectGlbCreateUiStage.Inactive;
            _stagedImage = null;
            _stagedImageName = null;
            _stagedImageSourcePath = null;
            _imagePreview.Image = null;
            _imageName.Text = string.Empty;
            _imageMode = false;
            _inputMode.SelectedIndex = 0;
            _promptBlock.Visible = true;
            _imageBlock.Visible = false;
            _generationStatusPoller.Stop();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private bool IsAutomaticDirectGlbCreateActive =>
        _createInRhinoStarting ||
        _directGlbAutoImportIntent is not null ||
        _directGlbCreateUiStage is
            Tripo.HostUi.DirectGlbCreateUiStage.Preflighting or
            Tripo.HostUi.DirectGlbCreateUiStage.WaitingForGeneration or
            Tripo.HostUi.DirectGlbCreateUiStage.WaitingPaused or
            Tripo.HostUi.DirectGlbCreateUiStage.Importing;

    private void EnsureNoAutomaticDirectGlbCreateMutation(string action)
    {
        if (IsAutomaticDirectGlbCreateActive)
        {
            throw new InvalidOperationException(
                "The confirmed one-click direct GLB workflow is active and " +
                $"cannot {action}. Refresh its generation status or wait for " +
                "its durable outcome instead.");
        }
    }

    private void EnsureDirectGlbCredentialMutationAllowed()
    {
        if (!IsAutomaticDirectGlbCreateActive)
        {
            return;
        }

        Tripo.HostUi.TripoPanelState state = _session.State;
        Tripo.HostUi.TripoPanelRecoveryLoadResult recovery =
            _session.Recovery;
        bool environmentOverride =
            state.CredentialStatus is
            {
                HasApiKey: true,
                Source: "environment",
            };
        if (_directGlbAutoImportIntent is not null &&
            (_directGlbCreateUiStage is
                Tripo.HostUi.DirectGlbCreateUiStage.WaitingForGeneration or
                Tripo.HostUi.DirectGlbCreateUiStage.WaitingPaused) &&
            state.Connected &&
            !state.Busy &&
            state.HasDurableGenerationTask &&
            state.HasCredentialRefreshFailure &&
            !environmentOverride &&
            !recovery.HasBlock)
        {
            return;
        }

        throw new InvalidOperationException(
            "The API key cannot change while the one-click direct GLB " +
            "workflow is active. If generation refresh fails after a durable " +
            "task exists, restore a same-account session-only key from the " +
            "enabled API-key action.");
    }

    private void EnsureDirectGlbGenerationRefreshAllowed()
    {
        if (!IsAutomaticDirectGlbCreateActive)
        {
            return;
        }

        if (_directGlbAutoImportIntent is not null &&
            (_directGlbCreateUiStage is
                Tripo.HostUi.DirectGlbCreateUiStage.WaitingForGeneration or
                Tripo.HostUi.DirectGlbCreateUiStage.WaitingPaused))
        {
            return;
        }

        throw new InvalidOperationException(
            "Generation refresh is available for the one-click workflow only " +
            "while it is waiting for or has paused the confirmed task.");
    }

    private bool ConfirmPaidDispatch(
        string title,
        string operationId,
        bool retry) =>
        MessageBox.Show(
            this,
            (retry
                ? "This retries the existing durable operation ID; it does not " +
                  "create a replacement ID. The sidecar journal will replay a " +
                  "receipt or fail closed. If no paid request was accepted " +
                  "previously, this retry can consume Tripo credits.\n\n"
                : "This action can consume Tripo credits.\n\n") +
            $"Durable operation ID:\n{operationId}\n\n" +
            "Keep this ID if the response is lost.",
            title,
            MessageBoxButtons.YesNo,
            MessageBoxType.Warning,
            MessageBoxDefaultButton.No) == DialogResult.Yes;

    private bool ConfirmDirectGlbCreate(
        string operationId,
        string documentTitle,
        string objectName)
    {
        Tripo.HostUi.DirectGlbCreateConfirmation confirmation =
            Tripo.HostUi.DirectGlbCreateConfirmation.Create(
                operationId,
                documentTitle,
                objectName);
        if (!confirmation.DefaultToNo)
        {
            throw new InvalidOperationException(
                "Direct GLB creation confirmation must fail closed.");
        }

        return MessageBox.Show(
            this,
            confirmation.Message,
            confirmation.Title,
            MessageBoxButtons.YesNo,
            MessageBoxType.Warning,
            MessageBoxDefaultButton.No) == DialogResult.Yes;
    }

    private void EnsureDirectGlbFirstDispatchAvailable(
        Tripo.HostUi.TripoPanelState state,
        Tripo.HostUi.TripoPanelRecoveryLoadResult recovery,
        Tripo.HostUi.PreparedTextGeneration prepared)
    {
        string? blockingReason =
            Tripo.HostUi.DirectGlbFirstDispatchGuard.GetBlockingReason(
                state,
                recovery,
                prepared,
                IsDirectGlbSelected());
        if (blockingReason is not null)
        {
            throw new InvalidOperationException(blockingReason);
        }
    }

    private static void TryResetUnsentGeneration(
        Tripo.HostUi.TripoPanelSession ownerSession,
        string operationId)
    {
        try
        {
            Tripo.HostUi.TripoPanelState state = ownerSession.State;
            if (state.PreparedGeneration is { } prepared &&
                string.Equals(
                    prepared.OperationId,
                    operationId,
                    StringComparison.Ordinal) &&
                !state.GenerationDispatchAttempted &&
                !state.HasDurableGenerationTask &&
                !state.HasUnresolvedDispatch)
            {
                ownerSession.ResetWorkflow();
            }
        }
        catch
        {
            // Preserve the original failure and any recovery evidence.
        }
    }

    private void OnStateChanged(
        object? sender,
        Tripo.HostUi.TripoPanelState state)
    {
        if (_closing ||
            sender is not Tripo.HostUi.TripoPanelSession ownerSession ||
            !ReferenceEquals(ownerSession, _session))
        {
            return;
        }

        RequestRender(ownerSession, state, ownerSession.Recovery);
    }

    private void OnRecoveryChanged(
        object? sender,
        Tripo.HostUi.TripoPanelRecoveryLoadResult recovery)
    {
        if (_closing ||
            sender is not Tripo.HostUi.TripoPanelSession ownerSession ||
            !ReferenceEquals(ownerSession, _session))
        {
            return;
        }

        RequestRender(ownerSession, ownerSession.State, recovery);
    }

    private void RequestRender()
    {
        Tripo.HostUi.TripoPanelSession ownerSession = _session;
        RequestRender(
            ownerSession,
            ownerSession.State,
            ownerSession.Recovery);
    }

    private void RequestRender(
        Tripo.HostUi.TripoPanelSession ownerSession,
        Tripo.HostUi.TripoPanelState state,
        Tripo.HostUi.TripoPanelRecoveryLoadResult recovery)
    {
        if (_closing)
        {
            return;
        }

        _renderQueue.Request(
            new PanelRenderFrame(
                ownerSession,
                _sessionGeneration,
                state,
                recovery));
    }

    private void ApplyRenderFrame(PanelRenderFrame frame)
    {
        if (_closing ||
            frame.SessionGeneration != _sessionGeneration ||
            !ReferenceEquals(frame.OwnerSession, _session))
        {
            return;
        }

        if (_recoveryInspectionToken is not null &&
            !string.Equals(
                _recoveryInspectionToken,
                frame.Recovery.PresentationToken,
                StringComparison.Ordinal))
        {
            _recoveryInspection = null;
            _recoveryInspectionToken = null;
        }

        ApplyControls(frame.State, frame.Recovery);
    }

    private void ApplyControls(
        Tripo.HostUi.TripoPanelState state,
        Tripo.HostUi.TripoPanelRecoveryLoadResult recovery)
    {
        if (_closing)
        {
            return;
        }

        Tripo.HostUi.TripoPanelPresentation presentation =
            CreatePresentation(state, recovery);

        _documentStatus.Text = presentation.DocumentStatus;
        _documentSession.Text = presentation.DocumentSessionId;
        _credentialStatus.Text = presentation.CredentialStatus;
        _recoveryHeader.Text = presentation.RecoveryHeader;
        _recoveryStatus.Text = presentation.RecoveryDetails;
        _recoveryExpander.Visible = presentation.RecoveryHasBlock;
        if (presentation.RecoveryHasBlock)
        {
            _recoveryExpander.Expanded = true;
        }
        else if (_recoveryWasBlocked)
        {
            _recoveryExpander.Expanded = false;
        }

        _recoveryWasBlocked = presentation.RecoveryHasBlock;
        if (!IsDirectGlbSelected() ||
            _directGlbCreateUiStage is
                Tripo.HostUi.DirectGlbCreateUiStage
                    .TerminalWithoutImport or
                Tripo.HostUi.DirectGlbCreateUiStage.Refused or
                Tripo.HostUi.DirectGlbCreateUiStage.ImportFailed or
                Tripo.HostUi.DirectGlbCreateUiStage.ImportRetryRequired or
                Tripo.HostUi.DirectGlbCreateUiStage.ManualReviewRequired)
        {
            _advancedExpander.Expanded = true;
        }

        if (!string.Equals(
                presentation.LatestPreparedOperationId,
                _displayedPreparedOperationId,
                StringComparison.Ordinal))
        {
            _detailsExpander.Expanded =
                presentation.LatestPreparedOperationId is not null;
            _displayedPreparedOperationId =
                presentation.LatestPreparedOperationId;
        }

        _generationOperation.Text = presentation.GenerationOperationId;
        _generationTaskId.Text = presentation.GenerationTaskId;
        _generationTask.Text = presentation.GenerationStatus;
        _generationTask.Visible = presentation.WorkflowStatusVisible;
        _generationDiagnostic.Text =
            presentation.GenerationDiagnostic;
        _generationDiagnosticBlock.Visible =
            presentation.GenerationDiagnosticVisible;
        SetProgress(
            _generationProgress,
            presentation.GenerationProgress);
        _conversionOperation.Text = presentation.ConversionOperationId;
        _conversionTaskId.Text = presentation.ConversionTaskId;
        _conversionTask.Text = presentation.ConversionStatus;
        _conversionDiagnostic.Text =
            presentation.ConversionDiagnostic;
        _conversionDiagnosticBlock.Visible =
            presentation.ConversionDiagnosticVisible;
        SetProgress(
            _conversionProgress,
            presentation.ConversionProgress);
        _importOperation.Text = presentation.ImportOperationId;
        _importCreatedObject.Text = presentation.ImportCreatedObjectId;
        _importTransaction.Text =
            presentation.ImportTransactionStatus;
        _importReceiptDetails.Visible =
            presentation.ImportReceiptDetailsVisible;
        _resultStatus.Text = presentation.ResultStatus;
        _resultStatus.Visible = presentation.ResultVisible;

        _connect.Enabled =
            presentation.ConnectEnabled &&
            !_recoveryReviewInProgress;
        _apiKey.Text = presentation.ApiKeyText;
        _apiKey.ToolTip = presentation.ApiKeyHelp;
        _apiKey.Enabled =
            presentation.ApiKeyEnabled &&
            !_recoveryReviewInProgress;
        _clearApiKey.ToolTip = presentation.ClearApiKeyHelp;
        _clearApiKey.Enabled =
            presentation.ClearApiKeyEnabled &&
            !_recoveryReviewInProgress;
        _reviewRecovery.Text = presentation.RecoveryActionText;
        _reviewRecovery.Enabled =
            presentation.ReviewRecoveryEnabled &&
            !_recoveryReviewInProgress;
        _checkRecovery.Enabled =
            presentation.CheckRecoveryEnabled &&
            !_recoveryReviewInProgress;
        _createInRhino.Enabled =
            presentation.CreateInRhinoEnabled &&
            !_createInRhinoStarting;
        _createInRhino.Text =
            presentation.CreateInRhinoText;
        _createInRhino.ToolTip =
            presentation.CreateInRhinoHelp;
        _createGuidance.Text =
            presentation.CreateInRhinoHelp;
        _createGuidance.Visible =
            presentation.CreateInRhinoGuidanceVisible;
        _directGlbWaitAction.Visible =
            presentation.DirectGlbWaitActionVisible;
        _directGlbWaitAction.Enabled =
            presentation.DirectGlbWaitActionEnabled;
        _directGlbWaitAction.Text =
            presentation.DirectGlbWaitActionText;
        _directGlbWaitAction.ToolTip =
            presentation.DirectGlbWaitActionHelp;
        _generate.Enabled = presentation.GenerateEnabled;
        _generate.Text = presentation.GenerateText;
        _refreshGeneration.Enabled =
            presentation.RefreshGenerationEnabled;
        _convert.Enabled = presentation.ConvertEnabled;
        _convert.Text = presentation.ConvertText;
        _refreshConversion.Enabled =
            presentation.RefreshConversionEnabled;
        _import.Enabled = presentation.ImportEnabled;
        _import.Text = presentation.ImportText;
        _importSource.Enabled = presentation.ImportSourceEnabled;
        _importGuidance.Text = presentation.ImportGuidance;
        _reset.Enabled = presentation.ResetEnabled;
        _reset.Visible = presentation.ResetVisible;
        _prompt.Enabled = presentation.PromptEnabled;
        _faceLimit.Enabled = presentation.FaceLimitEnabled;
        _withMaterials.Enabled = presentation.WithMaterialsEnabled;
        _name.Enabled = presentation.NameEnabled;
        _importMode.Enabled = presentation.ImportModeEnabled;
        _applyMaterials.Enabled = presentation.ApplyMaterialsEnabled;
        // Keep the input-mode selector authoritative for prompt/image block
        // visibility during normal editing; presentation only flips the enabled
        // state of the image controls so they lock once a generation is prepared.
        _promptBlock.Visible = !presentation.ImageMode;
        _imageBlock.Visible = presentation.ImageMode;
        _pickImage.Enabled = presentation.PickImageEnabled;
        _clearImage.Visible = presentation.ClearImageVisible;
        if (presentation.ImageName is not null)
        {
            _imageName.Text = presentation.ImageName;
        }
        string? pendingGenerationTaskId =
            Tripo.HostUi.DirectGlbGenerationPollingPolicy.GetPendingTaskId(
                state,
                recovery,
                _directGlbAutoImportIntent);
        _generationStatusPoller.Reconcile(pendingGenerationTaskId);
    }

    private Tripo.HostUi.TripoPanelPresentation CreatePresentation(
        Tripo.HostUi.TripoPanelState state,
        Tripo.HostUi.TripoPanelRecoveryLoadResult recovery) =>
        Tripo.HostUi.TripoPanelPresentation.Create(
            state,
            recovery,
            string.Equals(
                _recoveryInspectionToken,
                recovery.PresentationToken,
                StringComparison.Ordinal)
                ? _recoveryInspection
                : null,
            _prompt.Text,
            _name.Text,
            IsDirectGlbSelected() ? "glb" : "obj",
            _directGlbCreateUiStage,
            imageMode: _imageMode,
            hasImage: _stagedImage is not null,
            imageName: _stagedImageName);

    private void QueueDirectGlbAutoImportContinuation(
        Tripo.HostUi.TripoPanelSession ownerSession,
        long ownerGeneration)
    {
        if (_closing)
        {
            return;
        }

        Application.Instance.AsyncInvoke(
            () =>
            {
                Tripo.HostUi.DirectGlbAutoImportIntent? intent =
                    _directGlbAutoImportIntent;
                if (_closing ||
                    intent is null ||
                    ownerGeneration != _sessionGeneration ||
                    !ReferenceEquals(ownerSession, _session))
                {
                    return;
                }

                PanelRenderFrame frame = new(
                    ownerSession,
                    ownerGeneration,
                    ownerSession.State,
                    ownerSession.Recovery);
                if (!IntentOwnsState(intent, frame))
                {
                    FinishDirectGlbIntentForReview(
                        intent,
                        Tripo.HostUi.DirectGlbCreateUiStage.Refused);
                    return;
                }

                TryContinueDirectGlbAutoImport(frame, intent);
            });
    }

    private void TryContinueDirectGlbAutoImport(
        PanelRenderFrame frame,
        Tripo.HostUi.DirectGlbAutoImportIntent intent)
    {
        if (_closing ||
            !ReferenceEquals(_directGlbAutoImportIntent, intent))
        {
            return;
        }

        if (frame.Recovery.HasBlock)
        {
            FinishDirectGlbIntentForReview(
                intent,
                Tripo.HostUi.DirectGlbCreateUiStage.RecoveryBlocked);
            return;
        }

        if (frame.State.Busy ||
            frame.State.HasCredentialRefreshFailure)
        {
            return;
        }

        Tripo.HostUi.DirectGlbAutoImportDecision decision =
            intent.ObserveState(frame.SessionGeneration, frame.State);
        switch (decision)
        {
            case Tripo.HostUi.DirectGlbAutoImportDecision.BeginImport:
                _directGlbCreateUiStage =
                    Tripo.HostUi.DirectGlbCreateUiStage.Importing;
                _ = ImportDirectGlbFromIntentAsync(
                    frame.OwnerSession,
                    frame.SessionGeneration,
                    intent);
                break;
            case Tripo.HostUi.DirectGlbAutoImportDecision.Stopped:
                _directGlbCreateUiStage =
                    Tripo.HostUi.DirectGlbCreateUiStage.WaitingPaused;
                _generationStatusPoller.Stop();
                RequestRender();
                break;
            case Tripo.HostUi.DirectGlbAutoImportDecision
                .TerminalWithoutImport:
                _directGlbAutoImportIntent = null;
                _directGlbCreateUiStage =
                    Tripo.HostUi.DirectGlbCreateUiStage
                        .TerminalWithoutImport;
                RequestRender();
                break;
            case Tripo.HostUi.DirectGlbAutoImportDecision.Refused:
                _directGlbAutoImportIntent = null;
                _directGlbCreateUiStage =
                    Tripo.HostUi.DirectGlbCreateUiStage.Refused;
                RequestRender();
                ShowError(
                    new InvalidOperationException(
                        "Automatic direct GLB import was refused because the " +
                        "generation evidence did not match this workflow. " +
                        "Nothing was imported; review Workflow details."));
                break;
        }
    }

    private async Task ImportDirectGlbFromIntentAsync(
        Tripo.HostUi.TripoPanelSession ownerSession,
        long ownerGeneration,
        Tripo.HostUi.DirectGlbAutoImportIntent intent)
    {
        bool imported = false;
        bool deferred = false;
        bool recoveryBlocked = false;
        try
        {
            if (_closing ||
                ownerGeneration != _sessionGeneration ||
                !ReferenceEquals(ownerSession, _session) ||
                !ReferenceEquals(_directGlbAutoImportIntent, intent))
            {
                return;
            }

            EnsurePanelDocumentIsActive();
            PanelRenderFrame current = new(
                ownerSession,
                ownerGeneration,
                ownerSession.State,
                ownerSession.Recovery);
            if (current.Recovery.HasBlock)
            {
                recoveryBlocked = true;
                throw new InvalidOperationException(
                    "Automatic direct GLB import stopped before Rhino " +
                    "mutation because recovery evidence requires review.");
            }

            if (!IntentOwnsState(intent, current) ||
                current.State.PreparedImport is not null)
            {
                throw new InvalidOperationException(
                    "Automatic direct GLB import stopped because the current " +
                    "panel state no longer matches the confirmed workflow.");
            }

            if (current.State.Busy &&
                intent.TryDeferImport(
                    ownerGeneration,
                    current.State))
            {
                deferred = true;
                return;
            }

            try
            {
                ownerSession.PrepareGlbImport(intent.ObjectName);
            }
            catch (InvalidOperationException)
                when (ownerSession.State.Busy &&
                      ownerSession.State.PreparedImport is null &&
                      intent.TryDeferImport(
                          ownerGeneration,
                          ownerSession.State))
            {
                deferred = true;
                return;
            }

            RequestRender(
                ownerSession,
                ownerSession.State,
                ownerSession.Recovery);
            await ownerSession.ImportPreparedAsync(_panelLifetime.Token);
            imported = true;
        }
        catch (OperationCanceledException) when (_closing)
        {
        }
        catch (ObjectDisposedException) when (
            _closing ||
            ownerGeneration != _sessionGeneration ||
            !ReferenceEquals(ownerSession, _session))
        {
        }
        catch (Exception exception)
        {
            recoveryBlocked =
                recoveryBlocked ||
                (ownerSession.Recovery.HasBlock &&
                 !ownerSession.State.ImportDispatchAttempted &&
                 ownerSession.State.ImportReceipt is null);
            ShowError(exception);
        }
        finally
        {
            if (!_closing &&
                ownerGeneration == _sessionGeneration &&
                ReferenceEquals(ownerSession, _session) &&
                ReferenceEquals(_directGlbAutoImportIntent, intent))
            {
                if (deferred)
                {
                    _directGlbCreateUiStage =
                        Tripo.HostUi.DirectGlbCreateUiStage
                            .WaitingForGeneration;
                }
                else
                {
                    Tripo.HostUi.TripoPanelState finalState =
                        ownerSession.State;
                    _ = intent.TryFinishImport(
                        ownerGeneration,
                        finalState);
                    _directGlbAutoImportIntent = null;
                    if (imported)
                    {
                        _directGlbCreateUiStage =
                            Tripo.HostUi.DirectGlbCreateUiStage.Completed;
                    }
                    else if (recoveryBlocked &&
                             !finalState.ImportDispatchAttempted &&
                             finalState.ImportReceipt is null)
                    {
                        _directGlbCreateUiStage =
                            Tripo.HostUi.DirectGlbCreateUiStage
                                .RecoveryBlocked;
                    }
                    else if (finalState.ImportRequiresManualReview)
                    {
                        _directGlbCreateUiStage =
                            Tripo.HostUi.DirectGlbCreateUiStage
                                .ManualReviewRequired;
                    }
                    else if (finalState.ImportDispatchAttempted &&
                             finalState.ImportReceipt is null)
                    {
                        _directGlbCreateUiStage =
                            Tripo.HostUi.DirectGlbCreateUiStage
                                .ImportRetryRequired;
                    }
                    else
                    {
                        _directGlbCreateUiStage =
                            Tripo.HostUi.DirectGlbCreateUiStage.ImportFailed;
                    }
                }

                RequestRender();
            }
        }
    }

    private static bool IntentOwnsState(
        Tripo.HostUi.DirectGlbAutoImportIntent intent,
        PanelRenderFrame frame) =>
        frame.SessionGeneration == intent.SessionGeneration &&
        frame.State.Connected &&
        frame.State.Context is { } context &&
        frame.State.PreparedGeneration is { } prepared &&
        string.Equals(
            context.DocumentSessionId,
            intent.DocumentSessionId,
            StringComparison.Ordinal) &&
        string.Equals(
            prepared.DocumentSessionId,
            intent.DocumentSessionId,
            StringComparison.Ordinal) &&
        string.Equals(
            prepared.OperationId,
            intent.GenerationOperationId,
            StringComparison.Ordinal);

    private static bool DirectGlbWaitIntentIsIrrecoverable(
        Tripo.HostUi.DirectGlbAutoImportIntent intent,
        PanelRenderFrame frame) =>
        intent.Phase == Tripo.HostUi.DirectGlbAutoImportPhase.Finished ||
        !frame.State.HasDurableGenerationTask ||
        !IntentOwnsState(intent, frame);

    private void FinishDirectGlbIntentForReview(
        Tripo.HostUi.DirectGlbAutoImportIntent intent,
        Tripo.HostUi.DirectGlbCreateUiStage stage)
    {
        if (!ReferenceEquals(_directGlbAutoImportIntent, intent))
        {
            return;
        }

        _directGlbAutoImportIntent = null;
        _directGlbCreateUiStage = stage;
        _generationStatusPoller.Stop();
        RequestRender();
    }

    private void QueueDirectGlbIntentReviewTransition(
        Tripo.HostUi.TripoPanelSession ownerSession,
        long ownerGeneration,
        Tripo.HostUi.DirectGlbAutoImportIntent intent,
        Tripo.HostUi.DirectGlbCreateUiStage stage)
    {
        if (_closing)
        {
            return;
        }

        Application.Instance.AsyncInvoke(
            () =>
            {
                if (_closing ||
                    ownerGeneration != _sessionGeneration ||
                    !ReferenceEquals(ownerSession, _session) ||
                    !ReferenceEquals(_directGlbAutoImportIntent, intent))
                {
                    return;
                }

                FinishDirectGlbIntentForReview(intent, stage);
            });
    }

    private bool IsDirectGlbSelected() =>
        _importSource.SelectedIndex != 1;

    private void ApplyUserSettings(
        Tripo.HostUi.RhinoPanelUserSettings settings)
    {
        _faceLimit.Value = settings.FaceLimit;
        _withMaterials.Checked = settings.WithMaterials;
        _name.Text = settings.ObjectName;
        _lastValidObjectName = settings.ObjectName;

        // Direct GLB is intentionally the fresh-panel default. Choosing the
        // compatibility route never makes a later panel spend conversion
        // credits by surprise.
        _importSource.SelectedIndex = 0;
    }

    private void PersistUserSettings()
    {
        try
        {
            new Tripo.HostUi.RhinoPanelUserSettings(
                checked((int)_faceLimit.Value),
                _withMaterials.Checked == true,
                _lastValidObjectName).Save();
        }
        catch
        {
            // Preferences are best-effort and never block a workflow.
        }
    }

    private static void SetProgress(ProgressBar control, int? progress)
    {
        control.Visible = progress.HasValue;
        if (progress.HasValue)
        {
            control.Value = Math.Clamp(progress.Value, 0, 100);
        }
    }

    private void EnsurePanelDocumentIsActive()
    {
        global::Rhino.RhinoDoc? active = global::Rhino.RhinoDoc.ActiveDoc;
        if (active is null ||
            active.RuntimeSerialNumber != _documentSerialNumber)
        {
            throw new InvalidOperationException(
                "Activate the Rhino document that owns this Tripo panel before " +
                "starting or retrying a workflow stage.");
        }
    }

    private void ShowError(Exception exception)
    {
        string message = BoundMessage(exception.Message);
        RhinoUiThread.Invoke(
            () =>
            {
                if (!_closing)
                {
                    MessageBox.Show(
                        this,
                        message,
                        "Tripo",
                        MessageBoxType.Error);
                }
            });
    }

    private async Task DisposeSessionAsync()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        _directGlbAutoImportIntent = null;
        _directGlbCreateUiStage =
            Tripo.HostUi.DirectGlbCreateUiStage.Inactive;
        _generationStatusPoller.Dispose();
        _renderQueue.Dispose();
        _panelLifetime.Cancel();
        _session.StateChanged -= OnStateChanged;
        _session.RecoveryChanged -= OnRecoveryChanged;
        await _plugin.ReleasePanelSessionAsync(_session);
        _panelLifetime.Dispose();
    }

    private static string BoundMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "The Tripo operation failed.";
        }

        string trimmed = message.Trim();
        return trimmed.Length <= 512 ? trimmed : trimmed[..512];
    }

    private static TextBox OperationStatusBox() =>
        new()
        {
            ReadOnly = true,
        };

    private sealed class PanelRenderFrame
    {
        public PanelRenderFrame(
            Tripo.HostUi.TripoPanelSession ownerSession,
            long sessionGeneration,
            Tripo.HostUi.TripoPanelState state,
            Tripo.HostUi.TripoPanelRecoveryLoadResult recovery)
        {
            OwnerSession = ownerSession;
            SessionGeneration = sessionGeneration;
            State = state;
            Recovery = recovery;
        }

        public Tripo.HostUi.TripoPanelSession OwnerSession { get; }

        public long SessionGeneration { get; }

        public Tripo.HostUi.TripoPanelState State { get; }

        public Tripo.HostUi.TripoPanelRecoveryLoadResult Recovery { get; }
    }

    private sealed class ApiKeySubmission
    {
        private string _secret;

        public ApiKeySubmission(string secret, bool persist)
        {
            _secret = secret;
            Persist = persist;
        }

        public bool Persist { get; }

        public string TakeSecret()
        {
            string secret = _secret;
            _secret = string.Empty;
            return secret;
        }

        public void Clear()
        {
            _secret = string.Empty;
        }

        public override string ToString() => nameof(ApiKeySubmission);
    }
}
