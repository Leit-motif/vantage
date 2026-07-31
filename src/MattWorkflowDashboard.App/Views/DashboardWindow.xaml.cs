using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using MattWorkflowDashboard.App.Shell;
using MattWorkflowDashboard.App.ViewModels;
using MattWorkflowDashboard.Infrastructure.Settings;

namespace MattWorkflowDashboard.App.Views;

/// <summary>
/// The overlay itself. It stays above ordinary and borderless-windowed applications, never takes
/// focus, hides rather than exits when closed, and recovers its geometry safely across display
/// changes. It makes no promise about exclusive-fullscreen or other topmost windows.
/// </summary>
public partial class DashboardWindow : Window
{
    private readonly DashboardViewModel _viewModel;
    private readonly DashboardSettings _settings;
    private readonly Action _saveSettings;
    private bool _reallyExiting;

    public DashboardWindow(DashboardViewModel viewModel, DashboardSettings settings, Action saveSettings)
    {
        _viewModel = viewModel;
        _settings = settings;
        _saveSettings = saveSettings;

        InitializeComponent();
        DataContext = viewModel;

        SourceInitialized += OnSourceInitialized;
        LocationChanged += (_, _) => PersistGeometry();
        SizeChanged += (_, _) => PersistGeometry();
    }

    public event Action? SettingsRequested;

    /// <summary>
    /// How many physical pixels this window's layout unit is currently worth. Read live rather
    /// than cached: the answer changes when the window moves to a differently scaled display.
    /// </summary>
    private double DpiScale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = WindowInterop.HandleOf(this);
        WindowInterop.ApplyOverlayStyles(handle);
        WindowInterop.SetClickThrough(handle, _settings.Ui.ClickThrough);
        RestoreGeometry();
    }

    /// <summary>Closing hides the dashboard; only the tray's Exit really ends the session.</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_reallyExiting)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    public void ExitForReal()
    {
        _reallyExiting = true;
        Close();
    }

    /// <summary>Shows the window without taking activation away from whatever the owner is doing.</summary>
    public void ShowWithoutStealingFocus()
    {
        Show();
        WindowInterop.BringToTopWithoutActivating(WindowInterop.HandleOf(this));
    }

    /// <summary>
    /// Hands the dashboard the keyboard because the owner asked for it, and only then.
    /// <para>
    /// The overlay refuses activation outright so that a refresh or a watcher event can never
    /// interrupt typing. That refusal is also what puts the dashboard out of the keyboard's reach,
    /// so the one gesture the owner binds for this lifts it for exactly as long as they are here:
    /// activation is refused again the moment they move on. Nothing else in the shell may call
    /// this — every other path shows the window without taking focus.
    /// </para>
    /// </summary>
    public void FocusForKeyboard()
    {
        if (!IsVisible)
        {
            ShowWithoutStealingFocus();
        }

        var handle = WindowInterop.HandleOf(this);

        WindowInterop.SetNoActivate(handle, enabled: false);
        WindowInterop.TakeForeground(handle);
        Activate();

        // Land on something the owner can act on, rather than on the window's own chrome.
        MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    /// <summary>
    /// The owner has moved on, so the overlay goes back to refusing focus. Leaving activation
    /// enabled would let a later show or refresh take the keyboard, which is the whole thing
    /// the no-activate style exists to prevent.
    /// </summary>
    protected override void OnDeactivated(EventArgs e)
    {
        WindowInterop.SetNoActivate(WindowInterop.HandleOf(this), enabled: true);
        base.OnDeactivated(e);
    }

    public void SetClickThrough(bool enabled)
    {
        _settings.Ui.ClickThrough = enabled;
        WindowInterop.SetClickThrough(WindowInterop.HandleOf(this), enabled);
        _saveSettings();
    }

    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        DragMove();

        if (_settings.Ui.EdgeSnap)
        {
            var (left, top) = WindowInterop.SnapToEdge(Left, Top, ActualWidth, ActualHeight, DpiScale);
            Left = left;
            Top = top;
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();

    private void OnHideClick(object sender, RoutedEventArgs e) => Hide();

    private void RestoreGeometry()
    {
        var geometry = _settings.Ui.Geometry;
        Height = geometry.Height;
        Width = _viewModel.IsExpanded ? geometry.ExpandedWidth : geometry.CompactWidth;

        if (geometry.Left is not { } left || geometry.Top is not { } top)
        {
            PlaceNearTopRight();
            return;
        }

        // The saved position was measured on whatever display it was saved on; read it in the
        // units of the one the window is opening on before deciding whether it is still visible.
        var scale = DpiScale;
        var (savedLeft, savedTop) = WindowInterop.Reinterpret(left, top, geometry.DpiScale, scale);

        var (safeLeft, safeTop) = WindowInterop.EnsureOnScreen(savedLeft, savedTop, Width, Height, scale);
        Left = safeLeft;
        Top = safeTop;
    }

    private void PlaceNearTopRight()
    {
        var (left, top) = WindowInterop.EnsureOnScreen(double.MinValue, double.MinValue, Width, Height, DpiScale);
        Left = left;
        Top = top;
    }

    private void PersistGeometry()
    {
        if (!IsLoaded || WindowState != WindowState.Normal)
        {
            return;
        }

        var geometry = _settings.Ui.Geometry;
        geometry.Left = Left;
        geometry.Top = Top;
        geometry.Height = Height;

        if (_viewModel.IsExpanded)
        {
            geometry.ExpandedWidth = Width;
        }
        else
        {
            geometry.CompactWidth = Width;
        }

        // Windows identifies a display by a physical-pixel point, so the window's own units have
        // to be converted before asking which monitor it is on. The scale is saved with the
        // position: without it the position cannot be read back on a differently scaled display.
        var scale = DpiScale;
        geometry.DpiScale = scale;
        geometry.MonitorDeviceName = System.Windows.Forms.Screen
            .FromPoint(new System.Drawing.Point((int)(Left * scale), (int)(Top * scale))).DeviceName;
    }

    /// <summary>Re-applies the size the owner chose for the mode they just switched into.</summary>
    public void ApplyModeWidth()
    {
        Width = _viewModel.IsExpanded
            ? _settings.Ui.Geometry.ExpandedWidth
            : _settings.Ui.Geometry.CompactWidth;

        var (left, top) = WindowInterop.EnsureOnScreen(Left, Top, Width, Height, DpiScale);
        Left = left;
        Top = top;
    }

    public HwndSource? Source => (HwndSource?)PresentationSource.FromVisual(this);
}
