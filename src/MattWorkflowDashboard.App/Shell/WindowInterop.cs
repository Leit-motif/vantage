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
    /// </summary>
    public static (double Left, double Top) EnsureOnScreen(double left, double top, double width, double height)
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        var candidate = new System.Drawing.Rectangle((int)left, (int)top, (int)Math.Max(width, 1), (int)Math.Max(height, 1));

        foreach (var screen in screens)
        {
            if (screen.WorkingArea.IntersectsWith(candidate))
            {
                return (left, top);
            }
        }

        var primary = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea
            ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);

        return (
            primary.Right - width - 24,
            primary.Top + 24);
    }

    /// <summary>Snaps to the nearest working-area edge when the owner has asked for it.</summary>
    public static (double Left, double Top) SnapToEdge(double left, double top, double width, double height, int threshold = 24)
    {
        var area = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)left, (int)top)).WorkingArea;

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
}
