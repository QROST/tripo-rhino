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
        Text = "Generate materials",
        ToolTip = "Request generated materials from Tripo.",
    };
    private readonly TextBox _name = new()
    {
        Text = "Tripo Model",
    };
    private readonly DropDown _importMode = new()
    {
        DataStore = new[] { "native", "mesh", "instance" },
        SelectedIndex = 0,
    };
    private readonly CheckBox _applyMaterials = new()
    {
        Text = "Apply diffuse materials",
        ToolTip = "Apply baked diffuse materials when available.",
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
    private readonly Expander _recoveryExpander = new();
    private readonly Expander _detailsExpander = new();
    private readonly Button _connect = new() { Text = "Connect / Refresh" };
    private readonly Button _apiKey = new() { Text = "API key…" };
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
    private readonly Button _generate = new() { Text = "Generate" };
    private readonly Button _refreshGeneration = new()
    {
        Text = "Refresh generation",
    };
    private readonly Button _convert = new() { Text = "Convert to OBJ" };
    private readonly Button _refreshConversion = new()
    {
        Text = "Refresh conversion",
    };
    private readonly Button _import = new() { Text = "Import into Rhino" };
    private readonly Button _reset = new() { Text = "New workflow" };
    private readonly StackLayout _generationDiagnosticBlock;
    private readonly StackLayout _conversionDiagnosticBlock;
    private readonly StackLayout _importReceiptDetails;
    private string? _recoveryInspection;
    private string? _recoveryInspectionToken;
    private string? _displayedPreparedOperationId;
    private bool _recoveryWasBlocked;
    private bool _recoveryReviewInProgress;
    private bool _closing;

    public TripoRhinoPanel(uint documentSerialNumber)
    {
        _documentSerialNumber = documentSerialNumber;
        _plugin = TripoRhinoPlugin.Instance;
        _session = _plugin.CreatePanelSession();
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
            ExpandContentHeight = false,
            ExpandContentWidth = true,
            Content = BuildContent(),
        };
        _connect.Click += OnConnect;
        _apiKey.Click += OnApiKey;
        _checkRecovery.Click += OnCheckRecovery;
        _reviewRecovery.Click += OnReviewRecovery;
        _generate.Click += OnGenerate;
        _refreshGeneration.Click += OnRefreshGeneration;
        _convert.Click += OnConvert;
        _refreshConversion.Click += OnRefreshConversion;
        _import.Click += OnImport;
        _reset.Click += OnReset;
        _prompt.TextChanged += OnFormInputChanged;
        _name.TextChanged += OnFormInputChanged;
        Load += OnLoaded;
        UpdateControls(_session.State);
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

        return new StackLayout
        {
            AlignLabels = false,
            Padding = 12,
            Spacing = 10,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                new StackLayout
                {
                    AlignLabels = false,
                    Spacing = 5,
                    HorizontalContentAlignment =
                        HorizontalAlignment.Stretch,
                    Items =
                    {
                        new StackLayoutItem(
                            new Label
                            {
                                Text = "Tripo 3D",
                                Font = Eto.Drawing.SystemFonts.Bold(),
                                TextAlignment = TextAlignment.Left,
                            },
                            HorizontalAlignment.Left),
                        _documentStatus,
                        _credentialStatus,
                        _resultStatus,
                        ActionColumn(_connect, _apiKey),
                    },
                },
                _recoveryExpander,
                Section(
                    "1 · Generate model",
                    FieldBlock("Describe the model", _prompt),
                    FieldBlock(
                        "Face limit",
                        _faceLimit,
                        stretchControl: false),
                    _withMaterials,
                    _generationTask,
                    _generationProgress,
                    ActionColumn(_generate, _refreshGeneration)),
                Section(
                    "2 · Convert to OBJ",
                    _conversionTask,
                    _conversionProgress,
                    ActionColumn(_convert, _refreshConversion)),
                Section(
                    "3 · Import to Rhino",
                    FieldBlock(
                        "Object name",
                        _name),
                    FieldBlock(
                        "Import mode",
                        _importMode,
                        stretchControl: false),
                    _applyMaterials,
                    ActionColumn(_import)),
                _detailsExpander,
                ActionColumn(_reset),
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
            content.Items.Add(control);
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
                LeftLabel(label),
                new StackLayoutItem(
                    control,
                    stretchControl
                        ? HorizontalAlignment.Stretch
                        : HorizontalAlignment.Left),
            },
        };

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
        UpdateControls(_session.State);

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
            EnsurePanelDocumentIsActive();
            await _session.ConnectAsync(_panelLifetime.Token);
            if (_closing)
            {
                return;
            }

            if (_session.State.CredentialStatus?.HasApiKey == false &&
                !_session.Recovery.HasBlock)
            {
                await PromptForApiKeyAsync();
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
                await PromptForApiKeyAsync();
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

    private async Task PromptForApiKeyAsync()
    {
        if (_closing)
        {
            return;
        }

        bool replacing =
            _session.State.CredentialStatus?.HasApiKey == true;
        PasswordBox password = new()
        {
            MaxLength = 2048,
            ToolTip = "Paste the Tripo v3 API key.",
        };
        CheckBox persist = new()
        {
            Checked = true,
            Text = "Save in this user's OS credential store",
        };
        CheckBox confirmReplacement = new()
        {
            Text =
                "I want this new key to be used for future paid requests.",
            Visible = replacing,
        };
        Button save = new()
        {
            Enabled = false,
            Text = replacing ? "Replace API key" : "Save API key",
        };
        Button cancel = new() { Text = "Cancel" };
        Dialog<bool> dialog = new()
        {
            Title = replacing ? "Replace Tripo API key" : "Set Tripo API key",
            ClientSize = new Eto.Drawing.Size(520, replacing ? 410 : 370),
            Resizable = true,
            Content = new Scrollable
            {
                Border = BorderType.None,
                ExpandContentHeight = true,
                ExpandContentWidth = true,
                Content = new StackLayout
                {
                    Padding = 12,
                    Spacing = 8,
                    Items =
                    {
                        new Label
                        {
                            Text = ApiKeyInstructions,
                            Wrap = WrapMode.Word,
                        },
                        LeftLabel("API key"),
                        password,
                        persist,
                        confirmReplacement,
                        RightAlignedActions(save, cancel),
                    },
                },
            },
        };
        void UpdateSaveEnabled() =>
            save.Enabled =
                !string.IsNullOrWhiteSpace(password.Text) &&
                (!replacing || confirmReplacement.Checked == true);
        password.TextChanged += (_, _) => UpdateSaveEnabled();
        confirmReplacement.CheckedChanged +=
            (_, _) => UpdateSaveEnabled();
        save.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        if (!replacing)
        {
            dialog.DefaultButton = save;
        }

        dialog.AbortButton = cancel;
        dialog.Shown += (_, _) => password.Focus();

        string? secret = null;
        try
        {
            if (!dialog.ShowModal(this))
            {
                return;
            }

            if (_closing)
            {
                return;
            }

            secret = password.Text;
            password.Text = string.Empty;
            await _session.SetApiKeyAsync(
                secret,
                persist.Checked == true,
                _panelLifetime.Token);
        }
        finally
        {
            password.Text = string.Empty;
            secret = null;
            dialog.Dispose();
        }
    }

    private async void OnCheckRecovery(object? sender, EventArgs args)
    {
        if (_recoveryReviewInProgress || _closing)
        {
            return;
        }

        _recoveryReviewInProgress = true;
        UpdateControls(_session.State);
        try
        {
            Tripo.HostUi.TripoPanelRecoveryLoadResult recovery =
                _session.RefreshRecovery();
            if (recovery.Hints.Count == 0)
            {
                _recoveryInspection = null;
                _recoveryInspectionToken = null;
                UpdateControls(_session.State);
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
            UpdateControls(_session.State);
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
            UpdateControls(_session.State);
        }
    }

    private async void OnReviewRecovery(object? sender, EventArgs args)
    {
        try
        {
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
        UpdateControls(_session.State);
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
            UpdateControls(_session.State);
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
            UpdateControls(_session.State);
            return !_session.Recovery.HasBlock;
        }
        finally
        {
            _recoveryReviewInProgress = false;
            UpdateControls(_session.State);
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
        replacement.StateChanged += OnStateChanged;
        replacement.RecoveryChanged += OnRecoveryChanged;
        _recoveryInspection = null;
        _recoveryInspectionToken = null;
        _displayedPreparedOperationId = null;
        UpdateControls(replacement.State);
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
        Dialog<bool> dialog = new()
        {
            Title = "Review previous Tripo work",
            ClientSize = new Eto.Drawing.Size(600, 500),
            Resizable = true,
            Content = new Scrollable
            {
                Border = BorderType.None,
                ExpandContentHeight = true,
                ExpandContentWidth = true,
                Content = new StackLayout
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
                },
            },
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

    private async void OnGenerate(object? sender, EventArgs args)
    {
        try
        {
            EnsurePanelDocumentIsActive();
            bool retry = _session.State.GenerationRetryAllowed;
            Tripo.HostUi.PreparedTextGeneration prepared =
                _session.State.PreparedGeneration ??
                _session.PrepareGeneration(
                    _prompt.Text,
                    checked((int)_faceLimit.Value),
                    _withMaterials.Checked == true);
            UpdateControls(_session.State);
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
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async void OnRefreshGeneration(object? sender, EventArgs args)
    {
        try
        {
            await _session.RefreshGenerationStatusAsync();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async void OnConvert(object? sender, EventArgs args)
    {
        try
        {
            EnsurePanelDocumentIsActive();
            bool retry = _session.State.ConversionRetryAllowed;
            Tripo.HostUi.PreparedObjConversion prepared =
                _session.State.PreparedConversion ??
                _session.PrepareConversion(
                    checked((int)_faceLimit.Value),
                    _withMaterials.Checked == true);
            UpdateControls(_session.State);
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
            EnsurePanelDocumentIsActive();
            _ = _session.State.PreparedImport ??
                _session.PrepareImport(
                    _name.Text,
                    _importMode.SelectedValue?.ToString() ?? "native",
                    _applyMaterials.Checked == true);
            UpdateControls(_session.State);
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
            _session.ResetWorkflow();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
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

    private void OnStateChanged(
        object? sender,
        Tripo.HostUi.TripoPanelState state)
    {
        Application.Instance.AsyncInvoke(
            () =>
            {
                if (!ReferenceEquals(sender, _session))
                {
                    return;
                }

                UpdateControls(state);
            });
    }

    private void OnRecoveryChanged(
        object? sender,
        Tripo.HostUi.TripoPanelRecoveryLoadResult recovery)
    {
        Application.Instance.AsyncInvoke(
            () =>
            {
                if (!ReferenceEquals(sender, _session))
                {
                    return;
                }

                if (_recoveryInspectionToken is not null &&
                    !string.Equals(
                        _recoveryInspectionToken,
                        recovery.PresentationToken,
                        StringComparison.Ordinal))
                {
                    _recoveryInspection = null;
                    _recoveryInspectionToken = null;
                }

                UpdateControls(_session.State);
            });
    }

    private void UpdateControls(Tripo.HostUi.TripoPanelState state)
    {
        if (_closing)
        {
            return;
        }

        Tripo.HostUi.TripoPanelPresentation presentation =
            Tripo.HostUi.TripoPanelPresentation.Create(
                state,
                _session.Recovery,
                string.Equals(
                    _recoveryInspectionToken,
                    _session.Recovery.PresentationToken,
                    StringComparison.Ordinal)
                    ? _recoveryInspection
                    : null,
                _prompt.Text,
                _name.Text);

        _documentStatus.Text = presentation.DocumentStatus;
        _documentSession.Text = presentation.DocumentSessionId;
        _credentialStatus.Text = presentation.CredentialStatus;
        _recoveryHeader.Text = presentation.RecoveryHeader;
        _recoveryStatus.Text = presentation.RecoveryDetails;
        if (presentation.RecoveryHasBlock)
        {
            _recoveryExpander.Expanded = true;
        }
        else if (_recoveryWasBlocked)
        {
            _recoveryExpander.Expanded = false;
        }

        _recoveryWasBlocked = presentation.RecoveryHasBlock;
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
        _apiKey.ToolTip = presentation.RecoveryHasBlock
            ? "Review the previous request before setting or changing the key."
            : "Set or replace the Tripo v3 API key.";
        _apiKey.Enabled =
            presentation.ApiKeyEnabled &&
            !_recoveryReviewInProgress;
        _reviewRecovery.Text = presentation.RecoveryActionText;
        _reviewRecovery.Enabled =
            presentation.ReviewRecoveryEnabled &&
            !_recoveryReviewInProgress;
        _checkRecovery.Enabled =
            presentation.CheckRecoveryEnabled &&
            !_recoveryReviewInProgress;
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
        _reset.Enabled = presentation.ResetEnabled;
        _prompt.Enabled = presentation.PromptEnabled;
        _faceLimit.Enabled = presentation.FaceLimitEnabled;
        _withMaterials.Enabled = presentation.WithMaterialsEnabled;
        _name.Enabled = presentation.NameEnabled;
        _importMode.Enabled = presentation.ImportModeEnabled;
        _applyMaterials.Enabled = presentation.ApplyMaterialsEnabled;
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
        if (_closing)
        {
            return;
        }

        MessageBox.Show(
            this,
            BoundMessage(exception.Message),
            "Tripo",
            MessageBoxType.Error);
    }

    private async Task DisposeSessionAsync()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
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
}
