using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Vantage.App.Shell;
using Vantage.App.ViewModels;
using Vantage.App.Views;
using Vantage.Infrastructure;
using Vantage.Infrastructure.Persistence;
using Vantage.Infrastructure.Settings;
using Vantage.Tests.TestSupport;

// WinForms is referenced for the tray icon; here the WPF types are meant.
using Application = System.Windows.Application;
using Border = System.Windows.Controls.Border;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using Color = System.Windows.Media.Color;
using SystemColors = System.Windows.SystemColors;

namespace Vantage.Tests.ShellSeam;

/// <summary>
/// The running Windows shell. Every claim here is made against a real window with a real HWND and
/// the real notification-area icon, driven by the commands the owner uses, and read back from
/// Windows itself — a view model that agreed to be topmost is not evidence that the overlay is.
/// </summary>
[TestClass]
public sealed class RunningShellTests
{
    /// <summary>
    /// Launch-at-sign-in is proven against a scratch key, never the real Run key: a test must not
    /// change what starts on the owner's machine.
    /// </summary>
    private const string ScratchRunKey = @"Software\Vantage\ShellSeamTests\Run";

    private WorkspaceFixture _workspace = null!;
    private DashboardCache _cache = null!;
    private DashboardSettings _settings = null!;
    private DashboardViewModel _viewModel = null!;
    private StartupRegistration _startup = null!;

    [TestInitialize]
    public void SetUp()
    {
        _workspace = new WorkspaceFixture();
        _cache = DashboardCache.Open(_workspace.CacheFile);
        _settings = new DashboardSettings { Roots = [_workspace.WorkspacesRoot] };

        var paths = new AppPaths(Path.Combine(_workspace.Root, "appdata"));
        _viewModel = new DashboardViewModel(_settings, new SettingsStore(paths), _cache, new FakeProcessRunner());
        _startup = new StartupRegistration(ScratchRunKey, "ShellSeamTest");
    }

    [TestCleanup]
    public void TearDown()
    {
        _startup.Set(false, string.Empty);
        Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(ScratchRunKey, throwOnMissingSubKey: false);
        _cache.Dispose();
        _workspace.Dispose();
    }

    private RunningShell Start() => RunningShell.Start(_viewModel, _settings, _startup);

    // ---- Topmost, focus, and the honest limits of both -------------------------------------

    [TestMethod]
    public void The_running_overlay_is_topmost_stays_out_of_the_taskbar_and_never_takes_activation()
    {
        using var shell = Start();
        var handle = shell.Handle;

        Assert.IsTrue(Win32Window.Exists(handle), "The shell must be a real window before anything else is claimed.");
        Assert.IsTrue(
            Win32Window.HasExtendedStyle(handle, Win32Window.WsExTopmost),
            "The dashboard has to stay visible over ordinary and borderless-windowed applications.");
        Assert.IsTrue(
            Win32Window.HasExtendedStyle(handle, Win32Window.WsExToolWindow),
            "A tool window keeps the overlay out of the taskbar and the Alt-Tab list.");
        Assert.IsTrue(
            Win32Window.HasExtendedStyle(handle, Win32Window.WsExNoActivate),
            "A refresh must never be able to interrupt typing or gameplay.");
    }

    [TestMethod]
    public void Showing_the_dashboard_leaves_the_keyboard_exactly_where_it_was()
    {
        using var elsewhere = TheOtherWindow.Open();

        using var shell = Start();

        Assert.AreNotEqual(shell.Handle, ActiveWindow(), "The overlay must not take the keyboard when it is shown.");
        Assert.AreEqual(
            elsewhere.Handle,
            ActiveWindow(),
            "Showing the dashboard must leave the keyboard with whatever the owner was using.");
    }

    /// <summary>
    /// The window Windows is sending the keyboard to, asked on the thread that owns the shell's
    /// windows. It has to be asked there: this is a per-thread question, and the suite's windows
    /// live on a desktop of their own — see <see cref="IsolatedDesktop"/>.
    /// </summary>
    private static nint ActiveWindow() => WpfTestHost.Run(Win32Window.Active);

    // ---- Close-to-hide versus a real exit ---------------------------------------------------

    [TestMethod]
    public void Closing_the_window_hides_it_to_the_tray_and_leaves_the_session_running()
    {
        using var shell = Start();
        var handle = shell.Handle;

        Assert.IsTrue(Win32Window.IsVisible(handle), "The shell starts on screen.");

        WpfTestHost.Run(() => shell.Window.Close());
        RunningShell.Pump();

        Assert.IsFalse(Win32Window.IsVisible(handle), "Closing must hide the dashboard.");
        Assert.IsTrue(Win32Window.Exists(handle), "Closing must not end the session — the tray still owns the dashboard.");
    }

    [TestMethod]
    public void Only_the_tray_s_Exit_really_ends_the_session()
    {
        var shell = Start();
        var handle = shell.Handle;

        shell.ClickTray("Exit");

        Assert.IsFalse(Win32Window.Exists(handle), "Exit is the one command that actually destroys the window.");
        Assert.AreEqual(1, shell.ExitRequests, "Exit must also ask the application to shut down.");

        shell.Dispose();
    }

    // ---- Click-through, and getting back from it --------------------------------------------

    [TestMethod]
    public void Click_through_is_off_until_asked_for_and_the_tray_always_takes_it_back()
    {
        using var shell = Start();
        var handle = shell.Handle;

        Assert.IsFalse(
            Win32Window.HasExtendedStyle(handle, Win32Window.WsExTransparent),
            "Click-through is off by default: a window that ignores the mouse is hard to recover.");

        shell.ClickTray("Click-through");
        Assert.IsTrue(
            Win32Window.HasExtendedStyle(handle, Win32Window.WsExTransparent),
            "The tray command has to actually stop the window taking the mouse.");
        Assert.IsTrue(shell.TrayItemIsChecked("Click-through"), "The tray has to show that it is on.");

        shell.ClickTray("Click-through");
        Assert.IsFalse(
            Win32Window.HasExtendedStyle(handle, Win32Window.WsExTransparent),
            "Recovery is the whole reason the toggle lives in the tray.");
        Assert.IsFalse(shell.TrayItemIsChecked("Click-through"));
    }

    // ---- The notification area ---------------------------------------------------------------

    [TestMethod]
    public void Every_lifecycle_command_the_owner_needs_is_reachable_from_the_notification_area()
    {
        using var shell = Start();
        var commands = shell.TrayCommands();

        foreach (var expected in new[]
                 {
                     "Hide", "Expand", "Click-through", "Refresh now", "Settings…", "Open logs folder",
                     "Launch at sign-in", "Exit",
                 })
        {
            Assert.IsTrue(
                commands.Contains(expected),
                $"The tray must offer '{expected}'. It offers: {string.Join(", ", commands)}");
        }
    }

    [TestMethod]
    public void The_tray_hides_and_restores_the_running_window()
    {
        using var elsewhere = TheOtherWindow.Open();
        using var shell = Start();
        var handle = shell.Handle;

        shell.ClickTray("Hide");
        Assert.IsFalse(Win32Window.IsVisible(handle));
        Assert.AreEqual("Show", shell.TrayLabelStartingWith("Show"), "The tray has to offer the way back.");

        shell.ClickTray("Show");
        Assert.IsTrue(Win32Window.IsVisible(handle));
        Assert.AreNotEqual(handle, ActiveWindow(), "Coming back must not steal the keyboard either.");
    }

    [TestMethod]
    public void The_tray_switches_the_running_window_between_compact_and_expanded()
    {
        _settings.Ui.Geometry.CompactWidth = 380;
        _settings.Ui.Geometry.ExpandedWidth = 720;
        _viewModel.IsExpanded = false;

        using var shell = Start();
        var handle = shell.Handle;
        var scale = WpfTestHost.Run(() => VisualTreeHelper.GetDpi(shell.Window).DpiScaleX);

        Assert.AreEqual(380 * scale, Win32Window.BoundsOf(handle).Width, 2d, "The compact shell is the width the owner chose.");

        shell.ClickTray("Expand");

        Assert.IsTrue(_viewModel.IsExpanded);
        Assert.AreEqual(720 * scale, Win32Window.BoundsOf(handle).Width, 2d, "Expanding has to resize the real window.");
        Assert.AreEqual("Full screen", shell.TrayLabelStartingWith("Full"), "The tray offers the next step of the cycle.");
    }

    /// <summary>
    /// Full screen, asked of Windows: the window has to actually fill the display it was on, stop
    /// at the taskbar, and hand the owner back the geometry they had when it lets go.
    /// </summary>
    [TestMethod]
    public void Full_screen_fills_the_work_area_and_gives_the_owner_their_own_geometry_back()
    {
        _settings.Ui.Geometry.ExpandedWidth = 720;
        _settings.Ui.Geometry.Height = 520;
        _viewModel.Mode = DashboardViewMode.Expanded;

        using var shell = Start();
        var handle = shell.Handle;
        var scale = WpfTestHost.Run(() => VisualTreeHelper.GetDpi(shell.Window).DpiScaleX);
        var before = Win32Window.BoundsOf(handle);

        var workArea = WpfTestHost.Run(() =>
            WindowInterop.WorkAreaAt(shell.Window.Left, shell.Window.Top, scale));

        shell.ClickTray("Full screen");
        RunningShell.PumpUntil(() => Win32Window.BoundsOf(handle).Width > before.Width, TimeSpan.FromSeconds(2));

        var filled = Win32Window.BoundsOf(handle);
        Assert.AreEqual(workArea.Width * scale, filled.Width, 2d, "Full screen fills the display it was already on.");
        Assert.AreEqual(workArea.Height * scale, filled.Height, 2d, "Working area, so the taskbar is still there.");

        var screen = System.Windows.Forms.Screen.FromHandle(handle).Bounds;
        Assert.IsTrue(
            filled.Height <= screen.Height,
            "A borderless window told to maximize covers the taskbar; this one must not.");

        shell.ClickTray("Compact");
        RunningShell.PumpUntil(() => Win32Window.BoundsOf(handle).Width < filled.Width, TimeSpan.FromSeconds(2));
        shell.ClickTray("Expand");
        RunningShell.PumpUntil(() => Win32Window.BoundsOf(handle).Width > 700 * scale, TimeSpan.FromSeconds(2));

        Assert.AreEqual(720 * scale, Win32Window.BoundsOf(handle).Width, 2d, "The expanded width they chose survived the trip.");
        Assert.AreEqual(720, _settings.Ui.Geometry.ExpandedWidth, 0.5d, "Full screen's own size was never written over it.");
        Assert.AreEqual(520, _settings.Ui.Geometry.Height, 0.5d);
    }

    /// <summary>
    /// The claim the ribbon exists to make, asked of Windows rather than of the view model: a mode
    /// that says it is one line but leaves a 220-unit window on the desktop has not collapsed
    /// anything. The bound is a line of body text plus the row's own padding, well under the
    /// standard view's floor and nowhere near tight enough to be measuring the font.
    /// </summary>
    [TestMethod]
    public void The_ribbon_leaves_a_window_one_line_tall_and_gives_the_owner_their_height_back()
    {
        _settings.Ui.Geometry.Height = 520;

        using var shell = Start();
        var handle = shell.Handle;
        var scale = WpfTestHost.Run(() => VisualTreeHelper.GetDpi(shell.Window).DpiScaleX);

        var standardHeight = Win32Window.BoundsOf(handle).Height;
        Assert.AreEqual(520 * scale, standardHeight, 2d, "The standard view opens at the height the owner chose.");

        shell.ClickTray("Expand");
        WpfTestHost.Run(() => _viewModel.ToggleRibbon());
        RunningShell.Pump();
        RunningShell.PumpUntil(() => Win32Window.BoundsOf(handle).Height < standardHeight / 2, TimeSpan.FromSeconds(2));

        var ribbonHeight = Win32Window.BoundsOf(handle).Height;
        Assert.IsTrue(
            ribbonHeight <= 44 * scale,
            $"The ribbon has to be one line, not a small window; it measured {ribbonHeight / scale} units.");

        WpfTestHost.Run(() => _viewModel.ToggleRibbon());
        RunningShell.Pump();
        RunningShell.PumpUntil(() => Win32Window.BoundsOf(handle).Height > ribbonHeight, TimeSpan.FromSeconds(2));

        Assert.AreEqual(
            standardHeight,
            Win32Window.BoundsOf(handle).Height,
            2d,
            "Coming back out of the ribbon must restore the height they sized the window to, not the ribbon's.");
        Assert.AreEqual(520, _settings.Ui.Geometry.Height, 0.5d, "And the ribbon's own height was never written over it.");
    }

    /// <summary>
    /// An overlay lives against an edge, and growing to the right from the right of the screen is
    /// growing off it — after which the window gets shoved sideways to fit, which the owner sees as
    /// a jump rather than as the view opening. Whichever edge it is nearest has to stay put.
    /// </summary>
    [TestMethod]
    public void Changing_the_view_grows_away_from_the_edge_the_window_is_nearest()
    {
        _settings.Ui.Geometry.CompactWidth = 380;
        _settings.Ui.Geometry.ExpandedWidth = 720;
        _viewModel.Mode = DashboardViewMode.Compact;

        using var shell = Start();
        var area = WorkArea(shell);

        // Parked against the right, where an overlay like this normally lives.
        shell.MoveTo(area.Right - 380 - 8, area.Top + 60);
        shell.LetGeometrySettle();
        var rightEdge = Edges(shell).Right;

        shell.ClickTray("Expand");
        RunningShell.PumpUntil(() => Edges(shell).Width > 700, TimeSpan.FromSeconds(2));

        Assert.AreEqual(rightEdge, Edges(shell).Right, 2d, "The edge it is against is the edge that stays.");
        Assert.IsTrue(Edges(shell).Left < rightEdge - 700, "So the room for the detail pane comes out of the other side.");

        // And dragged to the other side, it anchors to the other side — with nothing recorded and
        // nothing for the owner to set.
        shell.MoveTo(area.Left + 8, area.Top + 60);
        shell.LetGeometrySettle();
        var leftEdge = Edges(shell).Left;

        WpfTestHost.Run(() => _viewModel.Mode = DashboardViewMode.Compact);
        RunningShell.PumpUntil(() => Edges(shell).Width < 500, TimeSpan.FromSeconds(2));

        Assert.AreEqual(leftEdge, Edges(shell).Left, 2d, "Against the left, the left edge is the one held still.");
    }

    [TestMethod]
    public void Collapsing_at_the_bottom_of_the_screen_keeps_the_ribbon_at_the_bottom()
    {
        _settings.Ui.Geometry.Height = 520;
        _viewModel.Mode = DashboardViewMode.Compact;

        using var shell = Start();
        var area = WorkArea(shell);

        shell.MoveTo(area.Left + 40, area.Bottom - 520 - 8);
        shell.LetGeometrySettle();
        var bottomEdge = Edges(shell).Bottom;

        WpfTestHost.Run(() => _viewModel.ToggleRibbon());
        RunningShell.PumpUntil(() => Edges(shell).Height < 100, TimeSpan.FromSeconds(2));

        var folded = Edges(shell);
        Assert.IsTrue(folded.Height < 100, $"The window has to have actually folded down; it is {folded.Height} tall.");
        Assert.AreEqual(
            bottomEdge,
            folded.Bottom,
            2d,
            "A ribbon folded down at the bottom of the screen must not walk up it.");

        WpfTestHost.Run(() => _viewModel.ToggleRibbon());
        RunningShell.PumpUntil(() => Edges(shell).Height > 400, TimeSpan.FromSeconds(2));

        Assert.AreEqual(bottomEdge, Edges(shell).Bottom, 2d, "And coming back out unfolds upward from the same place.");
    }

    /// <summary>
    /// Folding away from full screen is the case that used to go wrong: the window filled the
    /// display, so it was against every edge and none, and the ribbon came back against the left
    /// of the screen regardless of which side the owner keeps it on.
    /// </summary>
    [TestMethod]
    public void Folding_away_from_full_screen_lands_small_against_the_edge_it_was_against()
    {
        _settings.Ui.Geometry.CompactWidth = 380;
        _settings.Ui.Geometry.ExpandedWidth = 720;
        _viewModel.Mode = DashboardViewMode.Expanded;

        using var shell = Start();
        var area = WorkArea(shell);

        // Expanded and against the right, which is where an overlay like this is kept.
        shell.MoveTo(area.Right - 720 - 8, area.Top + 60);
        shell.LetGeometrySettle();
        var rightEdge = Edges(shell).Right;

        WpfTestHost.Run(() => _viewModel.CycleViewMode());
        RunningShell.PumpUntil(() => Edges(shell).Width > 900, TimeSpan.FromSeconds(2));
        Assert.IsTrue(Edges(shell).Width > 900, "The window has to have actually filled the display.");

        WpfTestHost.Run(() => _viewModel.ToggleRibbon());
        RunningShell.PumpUntil(() => Edges(shell).Height < 100, TimeSpan.FromSeconds(2));

        var folded = Edges(shell);
        Assert.IsTrue(folded.Height < 100, $"It has to have folded down; it is {folded.Height} tall.");
        Assert.AreEqual(380, folded.Width, 2d, "The ribbon is the small view folded down, whatever it folded down from.");
        Assert.AreEqual(rightEdge, folded.Right, 2d, "And it comes back against the edge the window was against.");
    }

    /// <summary>
    /// Full screen borrows the whole display, which is near no edge in particular. Coming out of it
    /// has to answer about where the window was before it went in, or the owner gets their window
    /// back somewhere they never put it.
    /// </summary>
    [TestMethod]
    public void Leaving_full_screen_returns_to_the_edge_the_window_was_against()
    {
        _settings.Ui.Geometry.CompactWidth = 380;
        _settings.Ui.Geometry.ExpandedWidth = 720;
        _viewModel.Mode = DashboardViewMode.Expanded;

        using var shell = Start();
        var area = WorkArea(shell);

        // Expanded and against the right, which is the state the full-screen step is entered from.
        shell.MoveTo(area.Right - 720 - 8, area.Top + 60);
        shell.LetGeometrySettle();
        var rightEdge = Edges(shell).Right;

        WpfTestHost.Run(() => _viewModel.CycleViewMode());
        RunningShell.PumpUntil(() => Edges(shell).Width > 900, TimeSpan.FromSeconds(2));
        Assert.IsTrue(Edges(shell).Width > 900, "The full-screen step has to have actually happened.");

        WpfTestHost.Run(() => _viewModel.CycleViewMode());
        RunningShell.PumpUntil(() => Edges(shell).Width < 500, TimeSpan.FromSeconds(2));

        Assert.AreEqual(DashboardViewMode.Compact, _viewModel.Mode);
        Assert.AreEqual(
            rightEdge,
            Edges(shell).Right,
            2d,
            "The whole way round the cycle, the edge it was against is the edge it comes back to.");
    }

    /// <summary>The running window's own bounds, in its own layout units.</summary>
    private static Rect Edges(RunningShell shell) => WpfTestHost.Run(() =>
        new Rect(shell.Window.Left, shell.Window.Top, shell.Window.ActualWidth, shell.Window.ActualHeight));

    private static Rect WorkArea(RunningShell shell) => WpfTestHost.Run(() => WindowInterop.WorkAreaAt(
        shell.Window.Left,
        shell.Window.Top,
        VisualTreeHelper.GetDpi(shell.Window).DpiScaleX));

    [TestMethod]
    public void The_tray_refresh_command_actually_refreshes_the_dashboard()
    {
        using var shell = Start();
        var before = _viewModel.RefreshId;

        shell.ClickTray("Refresh now");
        _viewModel.RefreshCommand.ExecutionTask!.GetAwaiter().GetResult();

        Assert.AreNotEqual(before, _viewModel.RefreshId, "A refresh produces a new refresh identity.");
    }

    [TestMethod]
    public void Settings_and_logs_stay_reachable_from_the_tray_even_when_the_overlay_ignores_the_mouse()
    {
        using var shell = Start();

        shell.ClickTray("Click-through");
        shell.ClickTray("Settings…");
        shell.ClickTray("Open logs folder");

        Assert.AreEqual(1, shell.SettingsWindowRequests, "Settings must open from the tray.");
        Assert.AreEqual(1, shell.LogsRequests, "The logs folder must open from the tray.");
    }

    // ---- Geometry, displays, and DPI ----------------------------------------------------------

    [TestMethod]
    public void Geometry_saved_on_a_monitor_that_is_no_longer_there_comes_back_on_screen()
    {
        _settings.Ui.Geometry.Left = -40_000;
        _settings.Ui.Geometry.Top = -40_000;

        using var shell = Start();
        var bounds = Win32Window.BoundsOf(shell.Handle);

        Assert.IsTrue(
            System.Windows.Forms.Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(
                new System.Drawing.Rectangle(bounds.Left, bounds.Top, Math.Max(bounds.Width, 1), Math.Max(bounds.Height, 1)))),
            "A display that went away must never strand the running window off-screen.");
    }

    [TestMethod]
    public void Geometry_the_owner_chose_on_a_display_that_is_still_there_is_restored_untouched()
    {
        var area = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
        _settings.Ui.Geometry.Left = area.Left + 120;
        _settings.Ui.Geometry.Top = area.Top + 90;

        using var shell = Start();

        var (left, top) = WpfTestHost.Run(() => (shell.Window.Left, shell.Window.Top));
        Assert.AreEqual(area.Left + 120, left, 1d);
        Assert.AreEqual(area.Top + 90, top, 1d);
    }

    [TestMethod]
    public void A_position_saved_on_a_differently_scaled_display_is_read_back_in_this_window_s_units()
    {
        // A place on screen, expressed as it would have been saved by a window on a 100% display,
        // where one layout unit is one pixel.
        var area = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
        _settings.Ui.Geometry.Left = area.Left + 400;
        _settings.Ui.Geometry.Top = area.Top + 200;
        _settings.Ui.Geometry.DpiScale = 1d;

        using var shell = Start();
        var bounds = Win32Window.BoundsOf(shell.Handle);

        Assert.AreEqual(
            area.Left + 400,
            bounds.Left,
            2d,
            "The window has to come back to the same physical place, whatever this display's scale is.");
        Assert.AreEqual(area.Top + 200, bounds.Top, 2d);
    }

    [TestMethod]
    public void A_saved_position_carries_the_scale_it_was_measured_at()
    {
        using var shell = Start();
        RunningShell.Pump();

        var scale = WpfTestHost.Run(() => VisualTreeHelper.GetDpi(shell.Window).DpiScaleX);

        Assert.AreEqual(
            scale,
            _settings.Ui.Geometry.DpiScale,
            "Without the scale it was written under, a saved position cannot be read on another display.");
    }

    /// <summary>
    /// A place well inside the primary display's working area, in the running window's own layout
    /// units. Well inside deliberately: a position that had to be rescued from being off-screen
    /// would prove the recovery path rather than that the owner's own choice came back.
    /// </summary>
    private static (double Left, double Top) SomewhereOnScreen(RunningShell shell, double inset)
    {
        var scale = WpfTestHost.Run(() => VisualTreeHelper.GetDpi(shell.Window).DpiScaleX);
        var area = WindowInterop.InDeviceIndependentUnits(
            System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea,
            scale);

        return (area.Left + inset, area.Top + inset);
    }

    [TestMethod]
    public void A_drag_is_written_once_it_stops_rather_than_once_per_frame_of_the_movement()
    {
        using var shell = Start();
        shell.LetGeometrySettle();

        var (left, top) = SomewhereOnScreen(shell, 160);
        var before = shell.SettingsSaves;

        // A drag reaches the window as a run of LocationChanged, one per frame of the movement.
        for (var step = 0; step < 10; step++)
        {
            shell.MoveTo(left + step, top + step);
        }

        Assert.AreEqual(
            before,
            shell.SettingsSaves,
            "Every save rewrites the settings file and swaps it into place; a drag must not cost one per frame.");

        RunningShell.PumpUntil(
            () => shell.SettingsSaves > before,
            DashboardWindow.GeometrySettleDelay * 8);

        Assert.AreEqual(
            before + 1,
            shell.SettingsSaves,
            "Once the movement stops, the whole of it has to coalesce into exactly one save.");
        Assert.AreEqual(
            left + 9,
            _settings.Ui.Geometry.Left!.Value,
            1d,
            "And what is written is where the drag ended, not somewhere it passed through.");
        Assert.AreEqual(top + 9, _settings.Ui.Geometry.Top!.Value, 1d);
    }

    [TestMethod]
    public void Where_the_owner_left_the_window_is_where_it_opens_next_time()
    {
        var store = new SettingsStore(new AppPaths(Path.Combine(_workspace.Root, "durable-geometry")));

        var firstSession = new DashboardSettings { Roots = [_workspace.WorkspacesRoot] };
        var shell = RunningShell.Start(
            ViewModelOver(firstSession),
            firstSession,
            _startup,
            () => store.Save(firstSession));

        shell.LetGeometrySettle();
        var before = shell.SettingsSaves;

        var (left, top) = SomewhereOnScreen(shell, 180);
        shell.MoveTo(left, top);

        Assert.AreEqual(before, shell.SettingsSaves, "The move is still waiting to be written when the owner exits.");

        // The one gesture that ends a session, and the only thing this session changes: no
        // click-through, no launch-at-sign-in, nothing whose save could carry the position for it.
        shell.ClickTray("Exit");
        shell.Dispose();

        Assert.AreEqual(
            before + 1,
            shell.SettingsSaves,
            "Exiting has to write what the owner's last move left outstanding.");

        // Read back the way the next launch reads it: from the file, into a fresh settings object.
        var nextSession = store.Load().Settings;

        Assert.AreEqual(left, nextSession.Ui.Geometry.Left!.Value, 1d, "The position has to have reached the file.");
        Assert.AreEqual(top, nextSession.Ui.Geometry.Top!.Value, 1d);

        using var reopened = RunningShell.Start(ViewModelOver(nextSession), nextSession, _startup);
        var (restoredLeft, restoredTop) = WpfTestHost.Run(() => (reopened.Window.Left, reopened.Window.Top));

        Assert.AreEqual(left, restoredLeft, 1d, "The dashboard has to come back where the owner left it.");
        Assert.AreEqual(top, restoredTop, 1d);
    }

    /// <summary>A view model over a settings object of the test's own, for the sessions above.</summary>
    private DashboardViewModel ViewModelOver(DashboardSettings settings) =>
        new(
            settings,
            new SettingsStore(new AppPaths(Path.Combine(_workspace.Root, "appdata"))),
            _cache,
            new FakeProcessRunner());

    [TestMethod]
    public void The_running_window_is_laid_out_in_device_independent_units_and_scaled_by_its_monitor()
    {
        using var shell = Start();
        var handle = shell.Handle;

        var (dip, scale) = WpfTestHost.Run(() =>
            (shell.Window.ActualWidth, VisualTreeHelper.GetDpi(shell.Window).DpiScaleX));

        Assert.IsTrue(scale > 0, "The window has to report a real DPI scale.");
        Assert.AreEqual(
            dip * scale,
            Win32Window.BoundsOf(handle).Width,
            2d,
            $"Physical pixels must be the layout size scaled by the monitor's DPI (scale {scale}).");
    }

    // ---- Theme transitions on the running window ----------------------------------------------

    [TestMethod]
    public void Dark_light_and_system_themes_restyle_the_running_window_without_a_restart()
    {
        using var shell = Start();
        var themes = WpfTestHost.Run(() => new ThemeManager(Application.Current, highContrast: () => false));

        var dark = SurfaceColourAfter(shell, themes, AppTheme.Dark);
        var light = SurfaceColourAfter(shell, themes, AppTheme.Light);
        var system = SurfaceColourAfter(shell, themes, AppTheme.System);

        Assert.AreNotEqual(dark, light, "Switching theme has to change what the running window renders.");
        Assert.IsTrue(
            system == dark || system == light,
            "System has to resolve to whichever appearance Windows is actually using.");
    }

    [TestMethod]
    public void Windows_high_contrast_hands_the_palette_to_the_system()
    {
        using var shell = Start();

        var ordinary = SurfaceColourAfter(
            shell,
            WpfTestHost.Run(() => new ThemeManager(Application.Current, highContrast: () => false)),
            AppTheme.Dark);

        var contrast = SurfaceColourAfter(
            shell,
            WpfTestHost.Run(() => new ThemeManager(Application.Current, highContrast: () => true)),
            AppTheme.Dark);

        Assert.AreNotEqual(ordinary, contrast, "High contrast must not leave the dashboard's own palette in place.");
        Assert.AreEqual(
            SystemColors.WindowColor,
            contrast,
            "Under high contrast the dashboard defers to the colours Windows was told to use.");
    }

    [TestMethod]
    public void Windows_high_contrast_makes_the_surface_opaque_rather_than_blending_with_the_desktop()
    {
        _settings.Ui.SurfaceOpacityPercent = 80;

        Assert.AreEqual(
            0.8d,
            SurfaceOpacityOfRunningWindow(highContrast: false),
            0.001,
            "Ordinarily the owner's chosen translucency is what the surface renders at.");

        Assert.AreEqual(
            1d,
            SurfaceOpacityOfRunningWindow(highContrast: true),
            0.001,
            "A high-contrast palette blended with the desktop behind it is no longer high contrast.");
    }

    /// <summary>
    /// The opacity the running window's glass surface actually renders at — read off the brush the
    /// window paints with, not off the setting that was asked for.
    /// </summary>
    private double SurfaceOpacityOfRunningWindow(bool highContrast)
    {
        var paths = new AppPaths(Path.Combine(_workspace.Root, "appdata"));
        var viewModel = new DashboardViewModel(
            _settings,
            new SettingsStore(paths),
            _cache,
            new FakeProcessRunner(),
            () => highContrast);

        using var shell = RunningShell.Start(viewModel, _settings, _startup);

        return WpfTestHost.Run(() =>
        {
            shell.Window.UpdateLayout();
            var root = (Border)shell.Window.FindName("GlassRoot");
            return ((SolidColorBrush)root.Background).Opacity;
        });
    }

    private static Color SurfaceColourAfter(RunningShell shell, ThemeManager themes, AppTheme theme) =>
        WpfTestHost.Run(() =>
        {
            themes.Apply(theme);
            shell.Window.UpdateLayout();

            var root = (Border)shell.Window.FindName("GlassRoot");
            return ((SolidColorBrush)root.Background).Color;
        });

    // ---- Launch at sign-in ---------------------------------------------------------------------

    [TestMethod]
    public void Launch_at_sign_in_is_off_until_asked_for_explicitly_and_is_reversible()
    {
        using var shell = Start();

        Assert.IsFalse(_startup.IsEnabled(), "Installing the dashboard must not change what starts at sign-in.");

        shell.ClickTray("Launch at sign-in");
        Assert.IsTrue(_startup.IsEnabled(), "The owner asked for it, so it has to actually be registered.");
        Assert.IsTrue(shell.TrayItemIsChecked("Launch at sign-in"));

        shell.ClickTray("Launch at sign-in");
        Assert.IsFalse(_startup.IsEnabled(), "It has to be reversible from the same command.");
        Assert.IsFalse(shell.TrayItemIsChecked("Launch at sign-in"));
    }

    // ---- Hotkey ----------------------------------------------------------------------------------

    [TestMethod]
    public void No_global_shortcut_is_claimed_until_the_owner_configures_one()
    {
        using var shell = Start();
        var handle = shell.Handle;

        using var hotkey = WpfTestHost.Run(() => new GlobalHotkey(handle, () => { }));

        Assert.IsFalse(WpfTestHost.Run(() => hotkey.Bind(_settings.Ui.ClickThroughHotkey)),
            "There is no default binding, so binding the default must claim nothing.");
        Assert.IsTrue(WpfTestHost.Run(() => hotkey.Bind("Ctrl+Alt+F9")),
            "A gesture the owner configured has to register with Windows against the real window.");

        WpfTestHost.Run(hotkey.Unbind);
    }

    // ---- Keyboard and screen readers ---------------------------------------------------------------

    [TestMethod]
    public void Every_control_the_owner_can_tab_to_announces_itself()
    {
        using var shell = Start();

        // What a screen reader reads is the automation peer's name, not the explicit property:
        // a button whose content is already a word announces that word without being told to.
        var unnamed = WpfTestHost.Run(() =>
            WpfTestHost.Descendants<ButtonBase>(shell.Window)
                .Where(b => b.Focusable && b.IsTabStop && WpfTestHost.IsRendered(b))
                .Where(b => string.IsNullOrWhiteSpace(
                    UIElementAutomationPeer.CreatePeerForElement(b)?.GetName()))
                .Select(b => b.GetType().Name)
                .ToList());

        Assert.AreEqual(
            0,
            unnamed.Count,
            $"A control a screen reader can land on has to have something to say: {string.Join(", ", unnamed)}");
    }

    [TestMethod]
    public void No_focus_gesture_is_claimed_until_the_owner_configures_one()
    {
        Assert.IsNull(
            new DashboardSettings().Ui.FocusHotkey,
            "The dashboard claims no global shortcut uninvited, including this one.");

        using var shell = Start();
        using var hotkey = WpfTestHost.Run(() =>
            new GlobalHotkey(shell.Handle, () => { }, GlobalHotkey.FocusId));

        Assert.IsFalse(
            WpfTestHost.Run(() => hotkey.Bind(_settings.Ui.FocusHotkey)),
            "With nothing configured, there is nothing to register.");
    }

    /// <summary>A gesture that has been pressed, and the registration it was pressed against.</summary>
    private readonly record struct GestureHeld(GlobalHotkey Hotkey) : IDisposable
    {
        public void Dispose() => Hotkey.Dispose();
    }

    /// <summary>
    /// The focus gesture as the owner performs it: a global hotkey they configured, registered
    /// against the real window, and answered by the shell's own handler. Driving
    /// <c>FocusForKeyboard</c> directly would not do — it is the shell's response to the gesture,
    /// and a test that called it would be asserting that a method it just called ran.
    /// <para>
    /// Two halves of the exchange belong to Windows, and they are not equally reproducible here.
    /// The registration is real: the shell asks Windows to claim <c>Ctrl+Shift+F9</c> against its
    /// live HWND, and Windows either accepts it or does not. The press is not, and cannot be —
    /// Windows delivers a hotkey from the desktop that is receiving input, which the suite
    /// deliberately is not on, so it is delivered here as the message Windows would have posted.
    /// The grant that follows is stood in for too: the shell does ask Windows for the keyboard,
    /// and a desktop with no input queue refuses, so the activation is performed alongside it.
    /// Everything the shell then does — lifting its refusal, moving focus inside itself, restoring
    /// the refusal when it is deactivated — is its own, driven by the real messages. docs/testing.md
    /// records what that leaves unproven.
    /// </para>
    /// </summary>
    private static GestureHeld FocusByPressingTheOwner_s_Gesture(RunningShell shell)
    {
        var handle = shell.Handle;

        var hotkey = WpfTestHost.Run(() =>
            new GlobalHotkey(handle, shell.Window.FocusForKeyboard, GlobalHotkey.FocusId));

        Assert.IsTrue(
            WpfTestHost.Run(() => hotkey.Bind("Ctrl+Shift+F9")),
            "The gesture has to register with Windows against the real window.");

        WpfTestHost.Run(() => TheKeyboard.PressTheConfiguredGesture(handle, GlobalHotkey.FocusId));
        RunningShell.Pump();

        WpfTestHost.Run(() => Win32Window.Activate(handle));
        RunningShell.Pump();

        Assert.AreEqual(
            handle,
            ActiveWindow(),
            "The gesture has to leave the dashboard holding the keyboard, or nothing after it means anything.");

        return new GestureHeld(hotkey);
    }

    [TestMethod]
    public void The_dashboard_takes_the_keyboard_only_when_the_owner_asks_for_it()
    {
        using var elsewhere = TheOtherWindow.Open();
        using var shell = Start();
        var handle = shell.Handle;

        Assert.IsTrue(
            Win32Window.HasExtendedStyle(handle, Win32Window.WsExNoActivate),
            "Until asked, the overlay refuses activation so a refresh cannot interrupt typing.");
        Assert.AreEqual(elsewhere.Handle, ActiveWindow(), "Until asked, the keyboard stays where the owner left it.");

        using var focus = FocusByPressingTheOwner_s_Gesture(shell);

        Assert.IsFalse(
            Win32Window.HasExtendedStyle(handle, Win32Window.WsExNoActivate),
            "A window that still refuses activation cannot hold the keyboard.");
        Assert.IsTrue(
            WpfTestHost.Run(() => Keyboard.FocusedElement is DependencyObject focused
                && WpfTestHost.Descendants<DependencyObject>(shell.Window).Contains(focused)),
            "Focus has to land on something inside the dashboard.");
    }

    [TestMethod]
    public void Tab_moves_between_the_running_window_s_controls()
    {
        using var shell = Start();
        var handle = shell.Handle;

        using var focus = FocusByPressingTheOwner_s_Gesture(shell);

        var before = WpfTestHost.Run(() => Keyboard.FocusedElement);
        WpfTestHost.Run(() => TheKeyboard.Press(handle, TheKeyboard.Tab));

        // The key goes onto the window's message queue and comes back out through WPF's own input
        // handling, so it lands when it lands.
        IInputElement? after = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            RunningShell.Pump();
            after = WpfTestHost.Run(() => Keyboard.FocusedElement);
            if (!ReferenceEquals(before, after))
            {
                break;
            }

            Thread.Sleep(20);
        }

        Assert.AreNotEqual(
            before,
            after,
            "Pressing Tab has to move to the next control in the running window.");
    }

    [TestMethod]
    public void A_control_reached_by_keyboard_can_be_operated_by_keyboard()
    {
        using var shell = Start();
        var handle = shell.Handle;

        using var focus = FocusByPressingTheOwner_s_Gesture(shell);

        // Reaching a control is only half of operable; the owner has to be able to act on it.
        var focused = WpfTestHost.Run(() =>
        {
            var refresh = WpfTestHost.Descendants<ButtonBase>(shell.Window)
                .FirstOrDefault(b => AutomationProperties.GetName(b) == "Refresh now");

            return refresh is not null && refresh.Focus();
        });

        Assert.IsTrue(focused, "The refresh command has to be reachable from the keyboard at all.");

        var before = _viewModel.RefreshId;
        WpfTestHost.Run(() => TheKeyboard.Press(handle, TheKeyboard.Space));

        for (var attempt = 0; attempt < 50 && _viewModel.RefreshCommand.ExecutionTask is null; attempt++)
        {
            RunningShell.Pump();
            Thread.Sleep(20);
        }

        _viewModel.RefreshCommand.ExecutionTask?.GetAwaiter().GetResult();

        Assert.AreNotEqual(
            before,
            _viewModel.RefreshId,
            "Pressing space on the focused command has to actually run it.");
    }

    [TestMethod]
    public void The_overlay_goes_back_to_refusing_focus_once_the_owner_moves_on()
    {
        using var elsewhere = TheOtherWindow.Open();
        using var shell = Start();
        var handle = shell.Handle;

        using var focus = FocusByPressingTheOwner_s_Gesture(shell);

        Assert.IsFalse(Win32Window.HasExtendedStyle(handle, Win32Window.WsExNoActivate));

        var before = shell.Deactivations;

        // Whatever the owner turns to next takes the keyboard, and the overlay must let go.
        elsewhere.TakeTheKeyboard();
        RunningShell.Pump();

        Assert.AreNotEqual(before, shell.Deactivations, "The shell has to actually see itself deactivated.");
        Assert.IsTrue(
            Win32Window.HasExtendedStyle(handle, Win32Window.WsExNoActivate),
            "Leaving activation enabled would let the next refresh take the keyboard.");
    }

    // ---- Reduced motion ------------------------------------------------------------------------------

    [TestMethod]
    public void The_running_shell_honours_the_owner_s_and_Windows_own_reduced_motion_choice()
    {
        _settings.Ui.ReducedMotion = false;
        using var shell = Start();

        Assert.AreEqual(
            !SystemParameters.ClientAreaAnimation,
            _viewModel.ReducedMotion,
            "With no preference of its own the dashboard has to follow the Windows animation setting.");

        _settings.Ui.ReducedMotion = true;
        _viewModel.NotifyAppearanceChanged();

        Assert.IsTrue(_viewModel.ReducedMotion, "The owner's own choice always reduces motion.");
    }

    [TestMethod]
    public void The_running_shell_animates_nothing_so_there_is_no_motion_to_suppress()
    {
        using var shell = Start();

        var animated = WpfTestHost.Run(() =>
            WpfTestHost.Descendants<FrameworkElement>(shell.Window)
                .Count(e => e.Triggers.Any(t => t.EnterActions.OfType<BeginStoryboard>().Any()
                    || t.ExitActions.OfType<BeginStoryboard>().Any())));

        Assert.AreEqual(0, animated, "Nothing in the shell animates, so reduced motion has nothing left to disable.");
    }
}
