using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using MattWorkflowDashboard.App.Shell;
using MattWorkflowDashboard.App.ViewModels;
using MattWorkflowDashboard.App.Views;
using MattWorkflowDashboard.Infrastructure;
using MattWorkflowDashboard.Infrastructure.Logging;
using MattWorkflowDashboard.Infrastructure.Persistence;
using MattWorkflowDashboard.Infrastructure.Processes;
using MattWorkflowDashboard.Infrastructure.Settings;
using Serilog.Core;

// WinForms brings System.Drawing into scope for the tray icon; here the WPF types are meant.
using Color = System.Windows.Media.Color;

namespace MattWorkflowDashboard.App;

/// <summary>
/// The composition root. Everything is wired by hand: the product deliberately ships without a
/// dependency-injection host, and the object graph is small enough to read in one place.
/// </summary>
public partial class App : Application
{
    private const string InstanceMutexName = @"Local\MattWorkflowDashboard.SingleInstance";

    private Mutex? _instanceMutex;
    private Logger? _logger;
    private AppPaths _paths = null!;
    private SettingsStore _settingsStore = null!;
    private DashboardSettings _settings = null!;
    private DashboardCache _cache = null!;
    private BoundedProcessRunner _processRunner = null!;
    private ThemeManager _themes = null!;
    private DashboardViewModel _viewModel = null!;
    private DashboardWindow _window = null!;
    private TrayIcon _tray = null!;
    private WorkflowWatcher? _watcher;
    private GlobalHotkey? _hotkey;
    private DispatcherTimer? _periodicRefresh;
    private SettingsWindow? _settingsWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        // One overlay, one indexer: a second instance would compete for the same cache.
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);

        _paths = new AppPaths();
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
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;

        _viewModel = new DashboardViewModel(_settings, _settingsStore, _cache, _processRunner);
        _window = new DashboardWindow(_viewModel, _settings, SaveSettings);
        _window.SettingsRequested += ShowSettings;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        _tray = new TrayIcon();
        WireTray();

        DispatcherUnhandledException += OnUnhandledException;

        _window.ShowWithoutStealingFocus();
        UpdateTrayState();

        _hotkey = new GlobalHotkey(WindowInterop.HandleOf(_window), ToggleClickThrough);
        _hotkey.Bind(_settings.Ui.ClickThroughHotkey);

        _watcher = new WorkflowWatcher(_settings.Roots, () => Dispatcher.BeginInvoke(RequestRefresh));

        _periodicRefresh = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMinutes(5),
        };
        _periodicRefresh.Tick += (_, _) => RequestRefresh();
        _periodicRefresh.Start();

        RequestRefresh();

        var captureIndex = Array.FindIndex(e.Args, a => a.Equals("--capture", StringComparison.OrdinalIgnoreCase));
        if (captureIndex >= 0 && captureIndex + 1 < e.Args.Length)
        {
            _ = CaptureFramesAsync(e.Args[captureIndex + 1]);
        }
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

        void Frame(string name, bool expanded, double width)
        {
            _viewModel.IsExpanded = expanded;
            _window.Width = width;
            _window.UpdateLayout();
            FrameCapture.Save(_window, Path.Combine(directory, name), backdrop);
        }

        Frame("compact.png", expanded: false, width: 380);
        Frame("expanded.png", expanded: true, width: 720);
        Frame("narrow.png", expanded: false, width: 320);

        _window.ExitForReal();
        Shutdown();
    }

    private void WireTray()
    {
        _tray.ShowHideRequested += () =>
        {
            if (_window.IsVisible)
            {
                _window.Hide();
            }
            else
            {
                _window.ShowWithoutStealingFocus();
            }

            UpdateTrayState();
        };

        _tray.CompactExpandRequested += () =>
        {
            _viewModel.IsExpanded = !_viewModel.IsExpanded;
            _window.ApplyModeWidth();
            UpdateTrayState();
        };

        _tray.ClickThroughToggled += ToggleClickThrough;
        _tray.RefreshRequested += RequestRefresh;
        _tray.SettingsRequested += ShowSettings;
        _tray.LogsRequested += OpenLogsFolder;

        _tray.LaunchAtSignInToggled += () =>
        {
            var enabled = !StartupRegistration.IsEnabled();
            StartupRegistration.Set(enabled, Environment.ProcessPath ?? string.Empty);
            _settings.LaunchAtSignIn = enabled;
            SaveSettings();
            UpdateTrayState();
        };

        _tray.ExitRequested += () =>
        {
            _window.ExitForReal();
            Shutdown();
        };
    }

    /// <summary>
    /// Click-through is always recoverable: the tray owns the same toggle, so the overlay can
    /// never make itself impossible to get back.
    /// </summary>
    private void ToggleClickThrough()
    {
        _window.SetClickThrough(!_settings.Ui.ClickThrough);
        UpdateTrayState();
    }

    private void RequestRefresh() => _ = _viewModel.RefreshCommand.ExecuteAsync(null);

    private void ShowSettings()
    {
        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            return;
        }

        var viewModel = new SettingsViewModel(_settings, _settingsStore, OnSettingsApplied);
        _settingsWindow = new SettingsWindow(viewModel);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    private void OnSettingsApplied()
    {
        _themes.Apply(_settings.Ui.Theme);
        _hotkey?.Bind(_settings.Ui.ClickThroughHotkey);

        _watcher?.Dispose();
        _watcher = new WorkflowWatcher(_settings.Roots, () => Dispatcher.BeginInvoke(RequestRefresh));

        RequestRefresh();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DashboardViewModel.IsExpanded))
        {
            _window.ApplyModeWidth();
            UpdateTrayState();
        }

        if (e.PropertyName == nameof(DashboardViewModel.StatusLine))
        {
            _tray.UpdateTooltip(_viewModel.StatusLine);
        }
    }

    private void OnSystemParametersChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast))
        {
            Dispatcher.BeginInvoke(() => _themes.Apply(_settings.Ui.Theme));
        }
    }

    private void UpdateTrayState() =>
        _tray.UpdateState(_window.IsVisible, _viewModel.IsExpanded, _settings.Ui.ClickThrough, StartupRegistration.IsEnabled());

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
        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        _viewModel?.CancelRefresh();
        _periodicRefresh?.Stop();
        _watcher?.Dispose();
        _hotkey?.Dispose();
        _tray?.Dispose();
        _processRunner?.Dispose();
        _cache?.Dispose();
        _logger?.Dispose();
        _instanceMutex?.Dispose();

        base.OnExit(e);
    }
}
