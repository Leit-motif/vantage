using System.Runtime.InteropServices;

namespace MattWorkflowDashboard.Tests.TestSupport;

/// <summary>
/// What Windows itself says about a window. These tests are about the running shell, so the
/// claims they make are read back from the operating system rather than from the WPF objects
/// that asked for them: a <see cref="System.Windows.Window.Topmost"/> property that was set is
/// not evidence that the window is topmost.
/// </summary>
public static class Win32Window
{
    private const int GwlExStyle = -20;

    public const int WsExTransparent = 0x00000020;
    public const int WsExToolWindow = 0x00000080;
    public const int WsExTopmost = 0x00000008;
    public const int WsExLayered = 0x00080000;
    public const int WsExNoActivate = 0x08000000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLongW(nint hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hWnd, out Rect lpRect);

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;

        public readonly int Height => Bottom - Top;
    }

    public static int ExtendedStyle(nint handle) => GetWindowLongW(handle, GwlExStyle);

    public static bool HasExtendedStyle(nint handle, int style) => (ExtendedStyle(handle) & style) == style;

    public static bool IsVisible(nint handle) => IsWindowVisible(handle);

    public static bool Exists(nint handle) => handle != 0 && IsWindow(handle);

    public static nint Foreground() => GetForegroundWindow();

    public static Rect BoundsOf(nint handle) =>
        GetWindowRect(handle, out var rect) ? rect : throw new InvalidOperationException("The window has no bounds.");
}
