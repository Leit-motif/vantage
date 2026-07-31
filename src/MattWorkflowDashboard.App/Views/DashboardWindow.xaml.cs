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

        var (safeLeft, safeTop) = WindowInterop.EnsureOnScreen(left, top, Width, Height, DpiScale);
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
        // to be converted before asking which monitor it is on.
        var scale = DpiScale;
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
