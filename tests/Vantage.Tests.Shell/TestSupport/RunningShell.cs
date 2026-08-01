using System.Windows.Forms;
using Vantage.App.Shell;
using Vantage.App.ViewModels;
using Vantage.App.Views;
using Vantage.Infrastructure.Settings;

namespace Vantage.Tests.TestSupport;

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
    private int _deactivations;
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

    /// <summary>
    /// How many times Windows has taken the keyboard away from the shell. This is the honest
    /// signal that the machine was in use during a test that needed the dashboard in front:
    /// asking who holds the foreground answers only for the instant it is asked, and by then the
    /// shell has already reacted to having lost it — the no-activate refusal it restores on
    /// deactivation is exactly what such a test goes on to assert about.
    /// </summary>
    public int Deactivations => Volatile.Read(ref _deactivations);

    public DashboardWindow Window => _window;

    public TrayIcon Tray => _tray;

    public ShellController Controller => _controller;

    /// <summary>The real window handle Windows knows this shell by.</summary>
    public nint Handle => WpfTestHost.Run(() => WindowInterop.HandleOf(_window));

    /// <param name="save">
    /// What the shell's request to persist settings actually does. Counted either way; supply a
    /// real <see cref="SettingsStore"/> write when the test is about what survives the session,
    /// because only a completed save records which revision it captured.
    /// </param>
    public static RunningShell Start(
        DashboardViewModel viewModel,
        DashboardSettings settings,
        StartupRegistration startup,
        Action? save = null)
    {
        RunningShell shell = null!;

        WpfTestHost.Run(() =>
        {
            void Save()
            {
                shell.SettingsSaves++;
                save?.Invoke();
            }

            var window = new DashboardWindow(viewModel, settings, Save);
            var tray = new TrayIcon();
            var controller = new ShellController(window, tray, viewModel, settings, startup, Save);

            shell = new RunningShell(window, tray, controller);

            window.Deactivated += (_, _) => Interlocked.Increment(ref shell._deactivations);

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

    /// <summary>
    /// Moves the running window, as the owner's drag does. The drag itself cannot be performed
    /// here — the suite's windows are on a desktop with no pointer, see <see cref="IsolatedDesktop"/>
    /// — but what the shell answers is the same thing either way: the window's own
    /// <c>LocationChanged</c>, raised once per step of the movement.
    /// </summary>
    public void MoveTo(double left, double top)
    {
        WpfTestHost.Run(() =>
        {
            _window.Left = left;
            _window.Top = top;
        });

        Pump();
    }

    /// <summary>Lets queued shell work — showing, restyling, restoring geometry — actually run.</summary>
    public static void Pump() => WpfTestHost.Drain();

    /// <summary>
    /// Keeps the shell running for a while. Not everything it does is immediate: the geometry
    /// write deliberately waits for a move to stop, so a test that read straight after the move
    /// would be reading the pause rather than the result.
    /// </summary>
    public static void PumpFor(TimeSpan duration)
    {
        var deadline = Environment.TickCount64 + (long)duration.TotalMilliseconds;

        while (Environment.TickCount64 < deadline)
        {
            Thread.Sleep(20);
            Pump();
        }
    }

    /// <summary>Keeps the shell running until something it does on its own has happened.</summary>
    public static void PumpUntil(Func<bool> done, TimeSpan within)
    {
        var deadline = Environment.TickCount64 + (long)within.TotalMilliseconds;

        while (!done() && Environment.TickCount64 < deadline)
        {
            Thread.Sleep(20);
            Pump();
        }
    }

    /// <summary>
    /// Waits out any geometry write the shell already had outstanding, so a test about the next
    /// one starts from a shell with nothing in flight rather than in the middle of it.
    /// </summary>
    public void LetGeometrySettle() => PumpFor(DashboardWindow.GeometrySettleDelay * 3);

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
