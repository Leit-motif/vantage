using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MattWorkflowDashboard.App.Shell;

/// <summary>
/// The Win32 window behaviour the dashboard depends on: staying visible over ordinary and
/// borderless-windowed applications, never taking focus, and optionally ignoring the mouse.
/// It makes no claim over exclusive-fullscreen applications or other topmost windows.
/// </summary>
public static partial class WindowInterop
{
    private const int GwlExStyle = -20;

    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExLayered = 0x00080000;
    private const int WsExNoActivate = 0x08000000;

    private static readonly nint HwndTopmost = -1;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int GetWindowLongW(nint hWnd, int nIndex);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int SetWindowLongW(nint hWnd, int nIndex, int dwNewLong);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    public static nint HandleOf(Window window) => new WindowInteropHelper(window).Handle;

    /// <summary>
    /// Keeps the window out of the taskbar and the Alt-Tab list, and stops it from ever taking
    /// activation — a refresh must not interrupt typing or gameplay.
    /// </summary>
    public static void ApplyOverlayStyles(nint handle)
    {
        if (handle == 0)
        {
            return;
        }

        var style = GetWindowLongW(handle, GwlExStyle);
        SetWindowLongW(handle, GwlExStyle, style | WsExToolWindow | WsExNoActivate);
    }

    /// <summary>
    /// Turns activation off or on. It is off for all but one moment in the dashboard's life: the
    /// overlay refuses focus so a refresh cannot interrupt typing, and the cost of that refusal is
    /// that the keyboard cannot reach it either. Lifting it is how the owner's focus gesture is
    /// answered, and it goes back on the moment they leave.
    /// </summary>
    public static void SetNoActivate(nint handle, bool enabled)
    {
        if (handle == 0)
        {
            return;
        }

        var style = GetWindowLongW(handle, GwlExStyle);
        style = enabled ? style | WsExNoActivate : style & ~WsExNoActivate;
        SetWindowLongW(handle, GwlExStyle, style);
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hWnd);

    /// <summary>Hands the window the keyboard, having first been allowed to take it.</summary>
    public static void TakeForeground(nint handle)
    {
        if (handle != 0)
        {
            SetForegroundWindow(handle);
        }
    }

    public static void BringToTopWithoutActivating(nint handle)
    {
        if (handle != 0)
        {
            SetWindowPos(handle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        }
    }

    /// <summary>
    /// Click-through. It is off by default and recoverable from the tray, because a window that
    /// ignores the mouse is otherwise very hard to get back.
    /// </summary>
    public static void SetClickThrough(nint handle, bool enabled)
    {
        if (handle == 0)
        {
            return;
        }

        var style = GetWindowLongW(handle, GwlExStyle);
        style = enabled
            ? style | WsExTransparent | WsExLayered
            : style & ~WsExTransparent;

        SetWindowLongW(handle, GwlExStyle, style);
    }

    /// <summary>
    /// Returns geometry that is guaranteed to be visible on some connected display. A monitor
    /// that has been unplugged must never strand the window off-screen.
    /// <para>
    /// Everything here is in the window's own device-independent units, while Windows reports
    /// display geometry in physical pixels. On a scaled display those are different numbers for
    /// the same place, and comparing one against the other is itself a way to strand a window:
    /// so the display is converted into the window's units before anything is decided.
    /// </para>
    /// </summary>
    public static (double Left, double Top) EnsureOnScreen(
        double left,
        double top,
        double width,
        double height,
        double dpiScale = 1d)
    {
        var candidate = new Rect(left, top, Math.Max(width, 1), Math.Max(height, 1));

        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            if (InDeviceIndependentUnits(screen.WorkingArea, dpiScale).IntersectsWith(candidate))
            {
                return (left, top);
            }
        }

        var primary = InDeviceIndependentUnits(
            System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea ?? new System.Drawing.Rectangle(0, 0, 1920, 1080),
            dpiScale);

        return (primary.Right - width - 24, primary.Top + 24);
    }

    /// <summary>Snaps to the nearest working-area edge when the owner has asked for it.</summary>
    public static (double Left, double Top) SnapToEdge(
        double left,
        double top,
        double width,
        double height,
        double dpiScale = 1d,
        int threshold = 24)
    {
        var scale = Normalise(dpiScale);
        var origin = new System.Drawing.Point((int)(left * scale), (int)(top * scale));
        var area = InDeviceIndependentUnits(System.Windows.Forms.Screen.FromPoint(origin).WorkingArea, dpiScale);

        var snappedLeft = left;
        var snappedTop = top;

        if (Math.Abs(left - area.Left) < threshold)
        {
            snappedLeft = area.Left;
        }
        else if (Math.Abs(area.Right - (left + width)) < threshold)
        {
            snappedLeft = area.Right - width;
        }

        if (Math.Abs(top - area.Top) < threshold)
        {
            snappedTop = area.Top;
        }
        else if (Math.Abs(area.Bottom - (top + height)) < threshold)
        {
            snappedTop = area.Bottom - height;
        }

        return (snappedLeft, snappedTop);
    }

    /// <summary>
    /// Re-reads a position that was saved on one display in the units of the display the window is
    /// opening on. Layout units are only meaningful alongside the scale they were measured at: a
    /// window at 1200 on a 100% display and a window at 1200 on a 150% display are nowhere near
    /// each other. Restoring without this step is how geometry saved on a secondary monitor comes
    /// back shifted, or on the wrong monitor entirely.
    /// </summary>
    public static (double Left, double Top) Reinterpret(
        double left,
        double top,
        double? savedScale,
        double currentScale)
    {
        var saved = Normalise(savedScale ?? currentScale);
        var current = Normalise(currentScale);

        // The physical place the owner left it, expressed in the units this window now speaks.
        return (left * saved / current, top * saved / current);
    }

    /// <summary>A display's physical-pixel rectangle expressed in a window's own layout units.</summary>
    public static Rect InDeviceIndependentUnits(System.Drawing.Rectangle area, double dpiScale)
    {
        var scale = Normalise(dpiScale);
        return new Rect(area.Left / scale, area.Top / scale, area.Width / scale, area.Height / scale);
    }

    private static double Normalise(double dpiScale) => dpiScale > 0 ? dpiScale : 1d;
}
