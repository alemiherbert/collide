using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Collide.Presentation.ViewModels;
using Collide.Revit.Commands;
using Collide.Revit.Integration;
using Collide.Revit.UI;
using Nice3point.Revit.Toolkit.Decorators;
using Nice3point.Revit.Toolkit.External;
using Serilog;

namespace Collide.Revit.Application;

/// <summary>
/// Add-in entry point (Nice3point Toolkit <see cref="ExternalApplication"/>): registers the dockable clash pane, the
/// ribbon commands, and the document events that drive live monitoring. Event handlers only record ids and switch
/// sessions; all model access goes through external events or commands.
/// </summary>
public sealed class CollideApplication : ExternalApplication
{
    public static readonly Guid PaneGuid = new("8d4b6f0e-6c1e-4d0b-9a57-3f2a1c7e5b21");

    private static readonly List<CollideSession> SessionList = [];
    private static readonly object Gate = new();
    private RevitChangeTracker? _tracker;
    private ActiveDocumentMonitor? _active;
    private ClashPaneViewModel? _viewModel;

    internal static ILogger Log { get; private set; } = Serilog.Core.Logger.None;

    internal static CollideSession? SessionFor(Document document)
    {
        lock (Gate) return SessionList.FirstOrDefault(s => s.Document.IsValidObject && s.Document.Equals(document));
    }

    public override void OnStartup()
    {
        var logFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Collide", "logs");
        Log = new LoggerConfiguration().MinimumLevel.Information()
            .WriteTo.File(Path.Combine(logFolder, "collide-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
            .CreateLogger();

        _active = new ActiveDocumentMonitor();
        _viewModel = new ClashPaneViewModel(_active, new ActiveDocumentInteraction(_active), new WpfDispatcher(System.Windows.Threading.Dispatcher.CurrentDispatcher));
        var view = new ClashPaneView(_viewModel);
        DockablePaneProvider.Register(Application, PaneGuid, "Rebar clashes").SetConfiguration(data =>
        {
            data.FrameworkElement = view;
            data.InitialState = new DockablePaneState { DockPosition = DockPosition.Right, MinimumWidth = 340, MinimumHeight = 400 };
        });

        var panel = Application.CreateRibbonPanel("Collide");
        var assembly = typeof(CollideApplication).Assembly.Location;
        panel.AddItem(new PushButtonData("CollideShowPane", "Clash\nPane", assembly, typeof(ShowClashPaneCommand).FullName) { ToolTip = "Show the live rebar clash pane." });
        panel.AddItem(new PushButtonData("CollideFullScan", "Full\nScan", assembly, typeof(FullScanCommand).FullName) { ToolTip = "Re-read and re-check the whole monitored scope." });
        panel.AddItem(new PushButtonData("CollideResolve", "Resolve", assembly, typeof(ResolveClashesCommand).FullName)
            { ToolTip = "Plan corrections for all eligible issues; applies only what the project's resolution policy explicitly permits." });
        panel.AddItem(new PushButtonData("CollideReport", "Export\nReport", assembly, typeof(ExportClashReportCommand).FullName) { ToolTip = "Write JSON and Markdown clash reports." });

        _tracker = new RevitChangeTracker(doc => SessionFor(doc) is { } s ? s.OnChanged : null);
        Application.ControlledApplication.DocumentChanged += _tracker.OnDocumentChanged;
        Application.ControlledApplication.DocumentOpened += OnDocumentOpened;
        Application.ControlledApplication.DocumentCreated += OnDocumentCreated;
        Application.ControlledApplication.DocumentClosing += OnDocumentClosing;
        Application.ViewActivated += OnViewActivated;
    }

    public override void OnShutdown()
    {
        if (_tracker is not null) Application.ControlledApplication.DocumentChanged -= _tracker.OnDocumentChanged;
        Application.ControlledApplication.DocumentOpened -= OnDocumentOpened;
        Application.ControlledApplication.DocumentCreated -= OnDocumentCreated;
        Application.ControlledApplication.DocumentClosing -= OnDocumentClosing;
        Application.ViewActivated -= OnViewActivated;
        _viewModel?.Dispose();
        CollideSession[] sessions;
        lock (Gate)
        {
            sessions = SessionList.ToArray();
            SessionList.Clear();
        }

        foreach (var s in sessions) s.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        (Log as IDisposable)?.Dispose();
    }

    private void OnDocumentOpened(object? sender, DocumentOpenedEventArgs e) => EnsureSession(e.Document);

    private void OnDocumentCreated(object? sender, DocumentCreatedEventArgs e) => EnsureSession(e.Document);

    private void OnViewActivated(object? sender, ViewActivatedEventArgs e) => _active?.Switch(EnsureSession(e.Document));

    private void OnDocumentClosing(object? sender, DocumentClosingEventArgs e)
    {
        CollideSession? session;
        lock (Gate)
        {
            session = SessionList.FirstOrDefault(s => s.Document.Equals(e.Document));
            if (session is not null) SessionList.Remove(session);
        }

        if (session is null) return;
        if (ReferenceEquals(_active?.Session, session)) _active!.Switch(null);
        _ = session.DisposeAsync();
    }

    private static CollideSession? EnsureSession(Document document)
    {
        if (document.IsFamilyDocument || document.IsLinked) return null;
        lock (Gate)
        {
            var existing = SessionList.FirstOrDefault(s => s.Document.Equals(document));
            if (existing is not null) return existing;
            var session = CollideSession.Start(document, Log);
            SessionList.Add(session);
            return session;
        }
    }
}
