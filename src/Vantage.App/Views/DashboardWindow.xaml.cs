using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Vantage.App.Shell;
using Vantage.App.ViewModels;
using Vantage.Infrastructure.Settings;

namespace Vantage.App.Views;

/// <summary>
/// The overlay itself. It stays above ordinary and borderless-windowed applications, never takes
/// focus, hides rather than exits when closed, and recovers its geometry safely across display
/// changes. It makes no promise about exclusive-fullscreen or other topmost windows.
/// </summary>
public partial class DashboardWindow : Window
{
    /// <summary>
    /// How long a move or a resize has to stop for before where it ended up is written. A drag
    /// raises <see cref="Window.LocationChanged"/> for every frame of the movement and each save
    /// rewrites the settings file and swaps it into place, so the write follows the gesture rather
    /// than each frame of it. Long enough to coalesce a drag, short enough that a crash between
    /// the gesture and the write loses only the last moment of it.
    /// </summary>
    public static readonly TimeSpan GeometrySettleDelay = TimeSpan.FromMilliseconds(750);

    /// <summary>
    /// The floor for the views that show a project list — enough for the header, the filters and a
    /// row or two. The ribbon has no floor at all: being exactly as tall as one line of text is the
    /// thing it exists to be, and any minimum here would be a minimum on that.
    /// </summary>
    public const double StandardMinHeight = 220;

    /// <summary>The detail column's width in the views the owner sizes by hand.</summary>
    public const double DefaultDetailPaneWidth = 320;

    private readonly DashboardViewModel _viewModel;
    private readonly DashboardSettings _settings;
    private readonly Action _saveSettings;
    private readonly DispatcherTimer _geometrySettling;
    private bool _reallyExiting;
    private bool _applyingMode;

    /// <summary>The mode the window is currently shaped for, which is what a change is a change from.</summary>
    private DashboardViewMode _appliedMode;

    /// <summary>The window's own bounds before it filled the display, which are the ones to come back to.</summary>
    private Rect? _beforeFullScreen;

    public DashboardWindow(DashboardViewModel viewModel, DashboardSettings settings, Action saveSettings)
    {
        _viewModel = viewModel;
        _settings = settings;
        _saveSettings = saveSettings;
        _appliedMode = viewModel.Mode;

        InitializeComponent();
        DataContext = viewModel;

        _geometrySettling = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = GeometrySettleDelay,
        };
        _geometrySettling.Tick += (_, _) => FlushGeometry();

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
        // The session is ending, so a move or resize still waiting out its settling delay has no
        // later to be written at. This is the one path that reliably runs before the window goes
        // away; the application saves again on the way out for the ways it does not.
        FlushGeometry();

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
        ApplyDetailPaneShare();

        if (_viewModel.IsFullScreen)
        {
            // Placed on the display the saved position names, then filled: opening full screen on
            // the primary monitor when the owner left it on a second one is not restoring anything.
            PlaceForFullScreen(geometry);
            return;
        }

        ApplyModeSize();
        Width = _viewModel.ShellWidth;

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

    private void PlaceForFullScreen(WindowGeometry geometry)
    {
        if (geometry.Left is { } left && geometry.Top is { } top)
        {
            var (savedLeft, savedTop) = WindowInterop.Reinterpret(left, top, geometry.DpiScale, DpiScale);
            Left = savedLeft;
            Top = savedTop;
        }

        FillTheDisplay();
    }

    private void PlaceNearTopRight()
    {
        var (left, top) = WindowInterop.EnsureOnScreen(double.MinValue, double.MinValue, Width, Height, DpiScale);
        Left = left;
        Top = top;
    }

    /// <summary>
    /// Records where the window now is. This only reaches settings.json through
    /// <see cref="FlushGeometry"/>: it is raised for every frame of a drag, and the owner's
    /// position would otherwise cost one rewrite-and-swap of the settings file per frame.
    /// </summary>
    private void PersistGeometry()
    {
        if (!IsLoaded || WindowState != WindowState.Normal || _applyingMode)
        {
            return;
        }

        // Full screen is the monitor's geometry rather than the owner's. Nothing about where the
        // window sits or how big it is while it is there describes the view they come back to, so
        // none of it is theirs to record.
        if (_viewModel.IsFullScreen)
        {
            return;
        }

        var geometry = _settings.Ui.Geometry;
        var before = Describe(geometry);

        geometry.Left = Left;
        geometry.Top = Top;

        // The ribbon's height is its content's rather than a size the owner chose. Writing it would
        // overwrite the height of the view they collapsed from, and they would come back out of the
        // ribbon into a one-line-tall window.
        if (!_viewModel.IsRibbon)
        {
            geometry.Height = Height;
        }

        // The ribbon is the small view folded down, so it shares that view's width: dragging
        // either one longer is the same statement about how much room the dashboard may take.
        if (_viewModel.Mode == DashboardViewMode.Expanded)
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

        // Layout raises these events for reasons that leave the geometry exactly as it was — the
        // first arrange of the session among them. Only a real move or resize is worth a write.
        if (Describe(geometry) == before)
        {
            return;
        }

        _settings.MarkChanged();

        // Restarted rather than left running: the delay is measured from the last change, so a
        // drag that keeps going keeps pushing the write out ahead of it.
        _geometrySettling.Stop();
        _geometrySettling.Start();
    }

    /// <summary>Everything about a saved geometry that a move or a resize can change.</summary>
    private static (double?, double?, double, double, double, double?, string?) Describe(WindowGeometry geometry) =>
        (geometry.Left, geometry.Top, geometry.Height, geometry.CompactWidth, geometry.ExpandedWidth,
            geometry.DpiScale, geometry.MonitorDeviceName);

    /// <summary>
    /// Writes the geometry the window has come to rest at, if anything is outstanding. Asking the
    /// settings rather than the timer is what makes this safe to call from either end: a save the
    /// owner triggered some other way in the meantime — click-through, the settings window — has
    /// already carried the position, and there is nothing left to write.
    /// </summary>
    private void FlushGeometry()
    {
        _geometrySettling.Stop();

        if (_settings.HasUnsavedChanges)
        {
            _saveSettings();
        }
    }

    /// <summary>Re-applies the size the owner chose for the mode they just switched into.</summary>
    public void ApplyMode()
    {
        var previous = _appliedMode;
        _appliedMode = _viewModel.Mode;

        if (_viewModel.IsFullScreen)
        {
            // Where the window was before it borrowed the display, so leaving can put it back
            // against the edge it was actually against.
            _beforeFullScreen = new Rect(Left, Top, ActualWidth, ActualHeight);

            _applyingMode = true;
            try
            {
                ApplyDetailPaneShare();
                FillTheDisplay();
            }
            finally
            {
                _applyingMode = false;
            }

            return;
        }

        // Which edge to hold is a question about where the window is now, so it is read before
        // anything moves. Coming out of full screen the answer is about where it was before it
        // went in: a window filling the whole display is near no edge in particular, and asking
        // there would anchor against the middle of a monitor it had only borrowed.
        var from = previous == DashboardViewMode.Full && _beforeFullScreen is { } outer
            ? outer
            : new Rect(Left, Top, ActualWidth, ActualHeight);

        var anchor = AnchorOf(from);

        // Re-shaping the window raises a size change per step of the re-shaping, and every one of
        // those looks exactly like the owner resizing it. Held shut for the duration, so the sizes
        // the window passes through on the way are not mistaken for a size they chose.
        _applyingMode = true;
        try
        {
            ApplyDetailPaneShare();

            Left = from.Left;
            Top = from.Top;

            ApplyModeSize();
            Width = _viewModel.ShellWidth;
        }
        finally
        {
            _applyingMode = false;
        }

        // After layout, because in the ribbon the new height is the content's and the content has
        // not been measured yet: anchoring before that holds an edge against the height of the view
        // being left rather than the one being entered.
        //
        // Measured here rather than waited for. Deferring the move to a later dispatcher callback
        // leaves the window at its new size against its old edge until that callback runs, and the
        // gap is real: the resize and the move are two separate trips to Windows, so a frame can be
        // composed between them and the ribbon is seen to jump up the screen before it settles.
        UpdateLayout();
        Reanchor(anchor);
    }


    /// <summary>
    /// Which edges the window holds still when it changes shape, and where they are. The nearest
    /// ones, decided from where the window sits right now rather than from anything recorded:
    /// parked against the right of the screen, growing to the right is growing off it, and the
    /// window ends up shoved sideways instead of simply opening.
    /// <para>
    /// Nothing about this is stored. A window dragged to the other side of the screen anchors to
    /// the other side the very next time, with no setting to notice or change.
    /// </para>
    /// </summary>
    private Anchor AnchorOf(Rect bounds)
    {
        var area = WindowInterop.WorkAreaAt(bounds.Left, bounds.Top, DpiScale);

        return new Anchor(
            HoldsRightEdge: bounds.Left + (bounds.Width / 2) > area.Left + (area.Width / 2),
            Right: bounds.Right,
            HoldsBottomEdge: bounds.Top + (bounds.Height / 2) > area.Top + (area.Height / 2),
            Bottom: bounds.Bottom);
    }

    private void Reanchor(Anchor anchor)
    {
        if (anchor.HoldsRightEdge)
        {
            Left = anchor.Right - ActualWidth;
        }

        if (anchor.HoldsBottomEdge)
        {
            Top = anchor.Bottom - ActualHeight;
        }

        KeepOnScreen();
    }


    /// <summary>
    /// How tall the window is allowed to be in the mode it is now in. The standard views keep the
    /// height the owner sized them to; the ribbon takes the height of its one line, which is why
    /// the floor has to come off before the content is asked.
    /// </summary>
    private void ApplyModeSize()
    {
        if (_viewModel.IsRibbon)
        {
            MinHeight = 0;
            SizeToContent = SizeToContent.Height;
            return;
        }

        // Read before anything is touched. Restoring the floor and turning manual sizing back on
        // each resize the window, and reading afterwards would be reading a height the window was
        // passing through rather than the one being restored.
        var restored = Math.Max(_settings.Ui.Geometry.Height, StandardMinHeight);

        SizeToContent = SizeToContent.Manual;
        MinHeight = StandardMinHeight;
        Height = restored;
    }

    /// <summary>
    /// Fills the display the window is currently on. The working area rather than the whole
    /// screen, and by explicit bounds rather than by maximizing: a borderless window told to
    /// maximize covers the taskbar, and this overlay is meant to sit alongside the desktop rather
    /// than take it over. It also never leaves the monitor the owner had it on for the primary one.
    /// </summary>
    private void FillTheDisplay()
    {
        SizeToContent = SizeToContent.Manual;
        MinHeight = StandardMinHeight;

        var area = WindowInterop.WorkAreaAt(Left, Top, DpiScale);
        Left = area.Left;
        Top = area.Top;
        Width = area.Width;
        Height = area.Height;
    }

    /// <summary>
    /// How much of the body the detail column takes. It is a fixed column at the widths the owner
    /// sizes by hand, because the list is what those views are for; filling the display is them
    /// asking to read the evidence, so there the column grows with the window instead of leaving
    /// every sentence in it wrapping at 320 units.
    /// </summary>
    private void ApplyDetailPaneShare()
    {
        if (_viewModel.IsFullScreen)
        {
            DetailColumn.Width = new GridLength(0.5, GridUnitType.Star);
            DetailPane.Width = double.NaN;
            return;
        }

        DetailColumn.Width = GridLength.Auto;
        DetailPane.Width = DefaultDetailPaneWidth;
    }

    private void KeepOnScreen()
    {
        var (left, top) = WindowInterop.EnsureOnScreen(Left, Top, ActualWidth, ActualHeight, DpiScale);
        Left = left;
        Top = top;
    }

    public HwndSource? Source => (HwndSource?)PresentationSource.FromVisual(this);

    /// <summary>The edges a re-shaped window holds still, and where they were before it changed.</summary>
    private readonly record struct Anchor(bool HoldsRightEdge, double Right, bool HoldsBottomEdge, double Bottom);
}
