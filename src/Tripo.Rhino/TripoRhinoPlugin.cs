using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using Rhino.PlugIns;

namespace Tripo.Rhino;

[Guid("626D164C-A15C-45DE-B8A1-0718C81305DE")]
public sealed class TripoRhinoPlugin : PlugIn, IDisposable
{
    private static TripoRhinoPlugin? _instance;

    private RhinoDocumentSessions? _documentSessions;
    private RhinoBridgeDispatcher? _dispatcher;
    private Tripo.Bridge.NamedPipeBridgeServer? _bridgeServer;
    private Tripo.Bridge.HostSidecarProcessManager? _sidecarManager;
    private readonly object _componentDisposalGate = new();
    private readonly ConcurrentDictionary<
        Tripo.HostUi.TripoPanelSession,
        byte> _panelSessions = new();
    private Task? _componentDisposal;

    internal static TripoRhinoPlugin Instance =>
        _instance ??
        throw new InvalidOperationException(
            "The Tripo Rhino plug-in is not loaded.");

    public static Tripo.Bridge.IHostSidecarConnector GetSidecarConnector() =>
        Instance._sidecarManager ??
        throw new InvalidOperationException(
            "The Rhino sidecar manager is unavailable.");

    public static string GetDocumentSessionId(
        global::Rhino.RhinoDoc document) =>
        (Instance._documentSessions ??
         throw new InvalidOperationException(
             "The Rhino document-session registry is unavailable."))
        .GetOrCreate(
            document ??
            throw new ArgumentNullException(nameof(document)));

    public override PlugInLoadTime LoadTime => PlugInLoadTime.AtStartup;

    protected override LoadReturnCode OnLoad(ref string errorMessage)
    {
        try
        {
            _documentSessions = new RhinoDocumentSessions();
            _dispatcher = new RhinoBridgeDispatcher(_documentSessions);
            _bridgeServer = new Tripo.Bridge.NamedPipeBridgeServer(
                "rhino",
                global::Rhino.RhinoApp.Version.ToString(),
                [
                    Tripo.Bridge.BridgeConstants.ContextMethod,
                    Tripo.Bridge.BridgeConstants.ImportMeshMethod,
                    Tripo.Bridge.BridgeConstants.ImportGlbMethod,
                ],
                _dispatcher);
            using CancellationTokenSource startupTimeout = new(TimeSpan.FromSeconds(10));
            _bridgeServer.StartAsync(startupTimeout.Token).GetAwaiter().GetResult();
            string pluginDirectory = Path.GetDirectoryName(
                Assembly.GetExecutingAssembly().Location)
                ?? throw new InvalidOperationException(
                    "The Rhino plug-in directory is unavailable.");
            _sidecarManager = new Tripo.Bridge.HostSidecarProcessManager(
                "rhino",
                Environment.ProcessId,
                pluginDirectory,
                "Tripo.Rhino.Mcp");
            _instance = this;
            global::Rhino.UI.Panels.RegisterPanel(
                this,
                typeof(TripoRhinoPanel),
                "Tripo",
                icon: null!,
                global::Rhino.UI.PanelType.PerDoc);
            global::Rhino.RhinoDoc.CloseDocument += OnDocumentClosed;
            global::Rhino.RhinoApp.Closing += OnRhinoClosing;
            global::Rhino.RhinoApp.WriteLine(
                $"[Tripo] Rhino bridge and Eto panel ready for PID " +
                $"{Environment.ProcessId}.");
            return LoadReturnCode.Success;
        }
        catch (Exception exception)
        {
            errorMessage = "The Tripo MCP bridge could not start: " + exception.Message;
            Interlocked.CompareExchange(ref _instance, null, this);
            DisposeComponents();
            return LoadReturnCode.ErrorShowDialog;
        }
    }

    protected override void OnShutdown()
    {
        Dispose();
        base.OnShutdown();
    }

    public void Dispose()
    {
        Interlocked.CompareExchange(ref _instance, null, this);
        global::Rhino.RhinoDoc.CloseDocument -= OnDocumentClosed;
        global::Rhino.RhinoApp.Closing -= OnRhinoClosing;
        DisposeComponents();
        GC.SuppressFinalize(this);
    }

    private void OnDocumentClosed(object? sender, global::Rhino.DocumentEventArgs args) =>
        _documentSessions?.Forget(args.DocumentSerialNumber);

    private void OnRhinoClosing(object? sender, EventArgs args)
    {
        Dispose();
    }

    private void DisposeComponents()
    {
        Task componentDisposal;
        lock (_componentDisposalGate)
        {
            _componentDisposal ??= DetachAndDrainComponents();
            componentDisposal = _componentDisposal;
        }

        componentDisposal.GetAwaiter().GetResult();
    }

    private Task DetachAndDrainComponents()
    {
        Tripo.Bridge.NamedPipeBridgeServer? bridgeServer =
            Interlocked.Exchange(ref _bridgeServer, null);
        RhinoBridgeDispatcher? dispatcher =
            Interlocked.Exchange(ref _dispatcher, null);
        Tripo.Bridge.HostSidecarProcessManager? sidecarManager =
            Interlocked.Exchange(ref _sidecarManager, null);
        Tripo.HostUi.TripoPanelSession[] panelSessions =
            _panelSessions.Keys.ToArray();
        _panelSessions.Clear();
        Interlocked.Exchange(ref _documentSessions, null);

        if (bridgeServer is null)
        {
            return DrainDetachedComponentsAsync(
                panelSessions,
                sidecarManager,
                dispatcher);
        }

        return DrainComponentsAsync(
            panelSessions,
            sidecarManager,
            bridgeServer,
            dispatcher);
    }

    internal Tripo.HostUi.TripoPanelSession CreatePanelSession()
    {
        Tripo.HostUi.TripoPanelSession session = new(
            _sidecarManager ??
            throw new InvalidOperationException(
                "The Rhino sidecar manager is unavailable."),
            new Tripo.HostUi.TripoPanelRecoveryStore("rhino"));
        if (!_panelSessions.TryAdd(session, 0))
        {
            session.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw new InvalidOperationException(
                "The Rhino panel session could not be registered.");
        }

        return session;
    }

    internal async Task ReleasePanelSessionAsync(
        Tripo.HostUi.TripoPanelSession session)
    {
        await session.DisposeAsync().ConfigureAwait(false);
        _panelSessions.TryRemove(session, out _);
    }

    private static async Task DrainDetachedComponentsAsync(
        IReadOnlyList<Tripo.HostUi.TripoPanelSession> panelSessions,
        Tripo.Bridge.HostSidecarProcessManager? sidecarManager,
        RhinoBridgeDispatcher? dispatcher)
    {
        await DisposePanelSessionsAsync(panelSessions).ConfigureAwait(false);
        await DisposeSidecarAsync(sidecarManager).ConfigureAwait(false);
        DisposeDispatcher(dispatcher);
    }

    private static async Task DrainComponentsAsync(
        IReadOnlyList<Tripo.HostUi.TripoPanelSession> panelSessions,
        Tripo.Bridge.HostSidecarProcessManager? sidecarManager,
        Tripo.Bridge.NamedPipeBridgeServer bridgeServer,
        RhinoBridgeDispatcher? dispatcher)
    {
        await DisposePanelSessionsAsync(panelSessions).ConfigureAwait(false);
        await DisposeSidecarAsync(sidecarManager).ConfigureAwait(false);
        await DisposeBridgeAsync(bridgeServer).ConfigureAwait(false);
        DisposeDispatcher(dispatcher);
    }

    private static async Task DisposePanelSessionsAsync(
        IReadOnlyList<Tripo.HostUi.TripoPanelSession> panelSessions)
    {
        foreach (Tripo.HostUi.TripoPanelSession session in panelSessions)
        {
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Trace.TraceError(
                    "Tripo Rhino panel shutdown failed: {0}",
                    exception);
            }
        }
    }

    private static async Task DisposeSidecarAsync(
        Tripo.Bridge.HostSidecarProcessManager? sidecarManager)
    {
        if (sidecarManager is null)
        {
            return;
        }

        try
        {
            await sidecarManager.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError(
                "Tripo Rhino sidecar shutdown failed: {0}",
                exception);
        }
    }

    private static async Task DisposeBridgeAsync(
        Tripo.Bridge.NamedPipeBridgeServer bridgeServer)
    {
        try
        {
            await bridgeServer.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError(
                "Tripo Rhino bridge shutdown failed: {0}",
                exception);
        }
    }

    private static void DisposeDispatcher(RhinoBridgeDispatcher? dispatcher)
    {
        try
        {
            dispatcher?.Dispose();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError(
                "Tripo Rhino dispatcher shutdown failed: {0}",
                exception);
        }
    }
}
