using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Vantage.App.Shell;
using Vantage.App.ViewModels;
using Vantage.App.Views;
using Vantage.Infrastructure;
using Vantage.Infrastructure.Acceptance;
using Vantage.Infrastructure.Logging;
using Vantage.Infrastructure.Persistence;
using Vantage.Infrastructure.Processes;
using Vantage.Infrastructure.Settings;
using Serilog.Core;

// WinForms brings System.Drawing into scope for the tray icon; here the WPF types are meant.
using Color = System.Windows.Media.Color;

namespace Vantage.App;

/// <summary>
/// The composition root. Everything is wired by hand: the product deliberately ships without a
/// dependency-injection host, and the object graph is small enough to read in one place.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// Asks the application to decide whether another dashboard already owns this session, report
    /// it as an exit code, and stop without building any UI.
    /// </summary>
    private const string SingleInstanceProbeArgument = "--single-instance-probe";

    /// <summary>
    /// Which session the probe decides about. Honoured only alongside the probe, so no argument
    /// can talk an ordinary launch into running beside a dashboard that is already up.
    /// <para>
    /// It exists because the suite has to ask the shipped executable what it decided, and the
    /// product's own session belongs to the dashboard the owner is running. Without this the tests
    /// could only be run on a machine with the dashboard closed, and a failure there would be
    /// indistinguishable from the regression they exist to catch.
    /// </para>
    /// </summary>
    private const string InstanceNameArgument = "--instance-name";

    private const int ExitCodeAlreadyRunning = 2;

    /// <summary>Something the run was supposed to leave alone did not survive it.</summary>
    private const int ExitCodeAcceptanceViolated = 3;

    /// <summary>
    /// Nothing changed, but something could not be read — a private or deleted repository, an
    /// unreadable file. The run is clean as far as it saw, and says how far that was.
    /// </summary>
    private const int ExitCodeObservationIncomplete = 4;

    private SingleInstanceGuard? _instance;
    private Logger? _logger;
    private AppPaths _paths = null!;
    private SettingsStore _settingsStore = null!;
    private DashboardSettings _settings = null!;
    private DashboardCache _cache = null!;
    private BoundedProcessRunner _processRunner = null!;
    private ThemeManager _themes = null!;
    private AppearanceWatcher? _appearance;
    private DashboardViewModel _viewModel = null!;
    private DashboardWindow _window = null!;
    private TrayIcon _tray = null!;
    private ShellController _shell = null!;
    private StartupRegistration _startup = null!;
    private WorkflowWatcher? _watcher;
    private GlobalHotkey? _hotkey;
    private GlobalHotkey? _focusHotkey;
    private DispatcherTimer? _periodicRefresh;
    private SettingsWindow? _settingsWindow;
    private ShellJournal? _journal;
    private DispatcherTimer? _journalSample;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Before the instance guard: an acceptance run observes the same roots the running
        // dashboard does, and has no business displacing it or being displaced by it.
        if (Argument(e.Args, "--acceptance") is { } acceptanceDirectory)
        {
            _ = RunAcceptanceAsync(acceptanceDirectory, Argument(e.Args, "--stamp") ?? "unstamped");
            return;
        }

        // One overlay, one indexer: a second instance would compete for the same cache.
        var probing = Array.Exists(e.Args, a => a.Equals(SingleInstanceProbeArgument, StringComparison.OrdinalIgnoreCase));

        _instance = new SingleInstanceGuard(
            (probing ? Argument(e.Args, InstanceNameArgument) : null) ?? SingleInstanceGuard.DashboardSession);

        if (probing)
        {
            Shutdown(_instance.IsOnlyInstance ? 0 : ExitCodeAlreadyRunning);
            return;
        }

        if (!_instance.IsOnlyInstance)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // Development-only: runs the real UI against a state directory of its own. Visual evidence
        // has to be captured from sanitized fixtures rather than the owner's private workspaces,
        // and pointing the shipped settings file at fixtures to do that would leave the owner's
        // own dashboard rewritten afterwards.
        _paths = new AppPaths(Argument(e.Args, "--state"));
        _paths.EnsureCreated();
        _logger = LogSetup.Create(_paths);

        _settingsStore = new SettingsStore(_paths);
        var loaded = _settingsStore.Load();
        _settings = loaded.Settings;
        foreach (var diagnostic in loaded.Diagnostics)
        {
            _logger.Warning("{Code}: {Message}", diagnostic.Code, diagnostic.Message);
        }

        _cache = DashboardCache.Open(_paths.CacheFile);
        _processRunner = new BoundedProcessRunner(
            _settings.MaxConcurrentExternalProcesses,
            TimeSpan.FromSeconds(_settings.ExternalProcessTimeoutSeconds));

        _themes = new ThemeManager(this);
        _themes.Apply(_settings.Ui.Theme);

        _appearance = new AppearanceWatcher();
        _appearance.Changed += OnWindowsAppearanceChanged;

        _viewModel = new DashboardViewModel(_settings, _settingsStore, _cache, _processRunner);
        _window = new DashboardWindow(_viewModel, _settings, SaveSettings);
        _window.SettingsRequested += ShowSettings;

        _startup = new StartupRegistration();
        _tray = new TrayIcon();
        _shell = new ShellController(_window, _tray, _viewModel, _settings, _startup, SaveSettings);
        _shell.SettingsRequested += ShowSettings;
        _shell.LogsRequested += OpenLogsFolder;
        _shell.ExitRequested += Shutdown;

        DispatcherUnhandledException += OnUnhandledException;

        _shell.Start();

        var handle = WindowInterop.HandleOf(_window);
        _hotkey = new GlobalHotkey(handle, _shell.ToggleClickThrough, GlobalHotkey.ClickThroughId);
        _hotkey.Bind(_settings.Ui.ClickThroughHotkey);

        // The only way the dashboard ever takes the keyboard, and only if the owner binds it.
        _focusHotkey = new GlobalHotkey(handle, _window.FocusForKeyboard, GlobalHotkey.FocusId);
        _focusHotkey.Bind(_settings.Ui.FocusHotkey);

        _watcher = new WorkflowWatcher(_settings.Roots, () => Dispatcher.BeginInvoke(RequestRefresh));

        _periodicRefresh = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMinutes(5),
        };
        _periodicRefresh.Tick += (_, _) => RequestRefresh();
        _periodicRefresh.Start();

        RequestRefresh();

        if (Argument(e.Args, "--shell-journal") is { } journalPath)
        {
            StartJournal(journalPath);
        }

        var captureIndex = Array.FindIndex(e.Args, a => a.Equals("--capture", StringComparison.OrdinalIgnoreCase));
        if (captureIndex >= 0 && captureIndex + 1 < e.Args.Length)
        {
            _ = CaptureFramesAsync(e.Args[captureIndex + 1]);
        }
    }

    private static string? Argument(string[] args, string name)
    {
        var index = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    /// <summary>
    /// Development-only: reads the owner's real configuration, scans the real roots read-only, and
    /// writes the sanitized evidence that nothing it observed was changed. No UI is built.
    /// <para>
    /// The dashboard's own state for this run lives under the output directory rather than under
    /// <c>%LOCALAPPDATA%</c>, so an acceptance run leaves the owner's settings, cache, and logs as
    /// untouched as it leaves their workspaces. The roots and registry intent it reads are the real
    /// ones; only the writing end is redirected.
    /// </para>
    /// </summary>
    private async Task RunAcceptanceAsync(string outputDirectory, string commit)
    {
        var exitCode = 0;

        try
        {
            var settings = new SettingsStore(new AppPaths()).Load().Settings;

            // Checked before the directory is created, not after: creating it is already the first
            // change to a workspace this run is supposed to leave alone.
            if (ReadOnlyAcceptanceRun.RejectOutputInsideARoot(outputDirectory, settings.Roots) is { } rejection)
            {
                await Console.Error.WriteLineAsync(rejection);
                Shutdown(ExitCodeAcceptanceViolated);
                return;
            }

            Directory.CreateDirectory(outputDirectory);

            var run = new ReadOnlyAcceptanceRun(
                settings,
                new AppPaths(Path.Combine(outputDirectory, "state")),
                commit);

            var report = await run.ExecuteAsync(CancellationToken.None);

            await File.WriteAllTextAsync(
                Path.Combine(outputDirectory, "acceptance-report.json"),
                ReadOnlyAcceptanceRun.Serialize(report));

            // Written beside the report and never committed: the report says a workspace moved
            // without saying whose, which is unusable exactly when it matters. Only the projects
            // with something to answer for are named, and only here.
            if (run.AffectedProjects.Count > 0)
            {
                await File.WriteAllLinesAsync(
                    Path.Combine(outputDirectory, "affected-projects.local.txt"),
                    [
                        "# Local only. Names the projects behind the identifiers in the report.",
                        "# Do not commit this file.",
                        .. run.AffectedProjects.Select(pair => $"{pair.Key}  {pair.Value}"),
                    ]);
            }

            exitCode = report.NothingWasChanged
                ? report.ObservationWasComplete ? 0 : ExitCodeObservationIncomplete
                : ExitCodeAcceptanceViolated;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await Console.Error.WriteLineAsync($"The acceptance run could not complete: {ex.Message}");
            exitCode = ExitCodeAcceptanceViolated;
        }

        Shutdown(exitCode);
    }

    /// <summary>
    /// Development-only: waits for the first refresh, writes runtime frames of the built
    /// application in each layout, then exits. Never reached in ordinary use.
    /// </summary>
    private async Task CaptureFramesAsync(string directory)
    {
        await _viewModel.RefreshCommand.ExecutionTask!.ConfigureAwait(true);
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);

        var backdrop = new LinearGradientBrush(
            Color.FromRgb(0x24, 0x28, 0x2E),
            Color.FromRgb(0x3A, 0x33, 0x2C),
            45);

        void Frame(string name, DashboardViewMode mode, double width)
        {
            _viewModel.Mode = mode;
            _window.Width = width;
            _window.UpdateLayout();
            FrameCapture.Save(_window, Path.Combine(directory, name), backdrop);
        }

        Frame("ribbon.png", DashboardViewMode.Ribbon, width: 380);
        Frame("compact.png", DashboardViewMode.Compact, width: 380);
        Frame("expanded.png", DashboardViewMode.Expanded, width: 720);
        Frame("narrow.png", DashboardViewMode.Compact, width: 320);

        // The conflicts region, reached the way the owner reaches it rather than by scrolling the
        // pane here: the aggregate badge's own command runs, which selects the project, opens the
        // expanded view and asks the region to be brought into view. What the frame shows is
        // therefore the result of the navigation, not a pane arranged for the photograph.
        if (_viewModel.AllProjects.FirstOrDefault(p => p.HasConflicts) is { } disagreeing)
        {
            _viewModel.Mode = DashboardViewMode.Expanded;
            _window.Width = 720;
            disagreeing.InspectConflictsCommand.Execute(null);
            _window.UpdateLayout();

            // The bring-into-view runs on the dispatcher after layout has settled, exactly as it
            // does for the owner; the second pass lets that scroll take effect.
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            _window.UpdateLayout();

            FrameCapture.Save(_window, Path.Combine(directory, "conflicts.png"), backdrop);
        }

        _window.ExitForReal();
        Shutdown();
    }

    /// <summary>
    /// Development-only: starts recording what the running window is, so a change made to Windows
    /// while the dashboard is up can be checked against the window that was already open rather
    /// than against a fresh one. Reached only by <c>--shell-journal</c>.
    /// <para>
    /// Three things are watched. Appearance changes are recorded after the theme has been
    /// re-applied, because the question is what the window became, not that it was told. Visibility
    /// is recorded because that is how a real click on the tray icon shows up from inside the
    /// process. The timer is the control: a line that keeps appearing with an unchanged palette is
    /// what makes a line with a changed one mean something.
    /// </para>
    /// </summary>
    private void StartJournal(string path)
    {
        _journal = new ShellJournal(this, _window, _viewModel, _settings, path);
        _journal.Record("startup");

        _window.IsVisibleChanged += (_, _) => _journal?.Record("visibility-changed");

        _journalSample = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(5),
        };
        _journalSample.Tick += (_, _) => _journal?.Record("sample");
        _journalSample.Start();
    }

    private void RequestRefresh() => _shell.RequestRefresh();

    private void ShowSettings()
    {
        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            return;
        }

        var viewModel = new SettingsViewModel(_settings, _settingsStore, OnSettingsApplied, _startup);
        _settingsWindow = new SettingsWindow(viewModel);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    private void OnSettingsApplied()
    {
        _themes.Apply(_settings.Ui.Theme);
        _viewModel.NotifyAppearanceChanged();
        _hotkey?.Bind(_settings.Ui.ClickThroughHotkey);
        _focusHotkey?.Bind(_settings.Ui.FocusHotkey);

        _watcher?.Dispose();
        _watcher = new WorkflowWatcher(_settings.Roots, () => Dispatcher.BeginInvoke(RequestRefresh));

        RequestRefresh();
    }

    /// <summary>
    /// Windows moved: re-resolve the palette and let the running window restyle itself. This is
    /// what makes the System theme actually follow Windows rather than only follow a restart.
    /// </summary>
    private void OnWindowsAppearanceChanged() => Dispatcher.BeginInvoke(() =>
    {
        _themes.Apply(_settings.Ui.Theme);
        _viewModel.NotifyAppearanceChanged();
        _journal?.Record("appearance-changed");
    });

    private void OpenLogsFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_paths.LogDirectory) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _logger?.Warning(ex, "Could not open the logs folder.");
        }
    }

    private void SaveSettings()
    {
        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.Warning(ex, "Could not save settings.");
        }
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Nothing leaves this machine: the failure is logged locally and the dashboard stays up.
        _logger?.Error(e.Exception, "Unhandled dispatcher exception.");
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Anything the session left outstanding — where the window came to rest, registry intent a
        // refresh produced — is written before the process goes away. The tray's Exit has already
        // flushed the window's own geometry by the time it gets here; this covers the ways out that
        // do not go through it, such as Windows ending the session.
        if (_settings is not null && _settings.HasUnsavedChanges)
        {
            SaveSettings();
        }

        _journalSample?.Stop();
        _journal?.Record("exit");
        _journal?.Dispose();
        _appearance?.Dispose();
        _viewModel?.CancelRefresh();
        _periodicRefresh?.Stop();
        _watcher?.Dispose();
        _hotkey?.Dispose();
        _focusHotkey?.Dispose();
        _shell?.Dispose();
        _tray?.Dispose();
        _processRunner?.Dispose();
        _cache?.Dispose();
        _logger?.Dispose();
        _instance?.Dispose();

        base.OnExit(e);
    }
}
