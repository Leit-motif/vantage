using System.Windows.Forms;
using MattWorkflowDashboard.App.Shell;
using MattWorkflowDashboard.App.ViewModels;
using MattWorkflowDashboard.App.Views;
using MattWorkflowDashboard.Infrastructure.Settings;

namespace MattWorkflowDashboard.Tests.TestSupport;

/// <summary>
/// The dashboard's shell as the owner actually runs it: a real window with a real HWND, the real
/// notification-area icon, and the same wiring the composition root installs between them. Tests
/// built on this drive user-visible commands and read the result back from Windows.
/// </summary>
public sealed class RunningShell : IDisposable
{
    private readonly DashboardWindow _window;
    private readonly TrayIcon _tray;
    private readonly ShellController _controller;
    private bool _disposed;

    private RunningShell(DashboardWindow window, TrayIcon tray, ShellController controller)
    {
        _window = window;
        _tray = tray;
        _controller = controller;
    }

    /// <summary>How many times the shell asked for its settings to be persisted.</summary>
    public int SettingsSaves { get; private set; }

    public int ExitRequests { get; private set; }

    public int SettingsWindowRequests { get; private set; }

    public int LogsRequests { get; private set; }

    public DashboardWindow Window => _window;

    public TrayIcon Tray => _tray;

    public ShellController Controller => _controller;

    /// <summary>The real window handle Windows knows this shell by.</summary>
    public nint Handle => WpfTestHost.Run(() => WindowInterop.HandleOf(_window));

    public static RunningShell Start(
        DashboardViewModel viewModel,
        DashboardSettings settings,
        StartupRegistration startup)
    {
        RunningShell shell = null!;

        WpfTestHost.Run(() =>
        {
            var window = new DashboardWindow(viewModel, settings, () => shell.SettingsSaves++);
            var tray = new TrayIcon();
            var controller = new ShellController(window, tray, viewModel, settings, startup, () => shell.SettingsSaves++);

            shell = new RunningShell(window, tray, controller);

            controller.SettingsRequested += () => shell.SettingsWindowRequests++;
            controller.LogsRequested += () => shell.LogsRequests++;
            controller.ExitRequested += () => shell.ExitRequests++;

            controller.Start();
        });

        Pump();
        return shell;
    }

    /// <summary>Invokes a notification-area command exactly as the owner's click would.</summary>
    public void ClickTray(string text)
    {
        WpfTestHost.Run(() =>
        {
            var item = TrayItem(text)
                ?? throw new InvalidOperationException($"The tray has no '{text}' command.");
            item.PerformClick();
        });

        Pump();
    }

    public string TrayLabelStartingWith(string prefix) => WpfTestHost.Run(() =>
        TrayItems()
            .Select(i => i.Text ?? string.Empty)
            .FirstOrDefault(t => t.StartsWith(prefix, StringComparison.Ordinal))
        ?? string.Empty);

    public bool TrayItemIsChecked(string text) => WpfTestHost.Run(() => TrayItem(text)?.Checked ?? false);

    public IReadOnlyList<string> TrayCommands() => WpfTestHost.Run(() =>
        (IReadOnlyList<string>)TrayItems().Select(i => i.Text ?? string.Empty).ToList());

    /// <summary>Lets queued shell work — showing, restyling, restoring geometry — actually run.</summary>
    public static void Pump() => WpfTestHost.Drain();

    private ToolStripMenuItem? TrayItem(string text) =>
        TrayItems().FirstOrDefault(i => string.Equals(i.Text, text, StringComparison.Ordinal));

    private IEnumerable<ToolStripMenuItem> TrayItems() =>
        _tray.Menu.Items.OfType<ToolStripMenuItem>();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        WpfTestHost.Run(() =>
        {
            _controller.Dispose();
            _window.ExitForReal();
            _tray.Dispose();
        });
    }
}
