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

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, nint dwExtraInfo);

    private const byte VkTab = 0x09;

    public const byte VkControl = 0x11;

    public const byte VkShift = 0x10;

    public const byte VkF9 = 0x78;

    private const uint KeyEventKeyUp = 0x0002;

    /// <summary>
    /// Presses a chord as the owner's keyboard would: modifiers down, key, then everything back
    /// up. Used to deliver a real global hotkey, which is the only way the shell's focus gesture
    /// can be exercised as the owner experiences it — Windows grants a process the right to take
    /// the foreground when it receives a hotkey, and withholds it otherwise.
    /// </summary>
    public static void PressChord(byte[] modifiers, byte key)
    {
        foreach (var modifier in modifiers)
        {
            keybd_event(modifier, 0, 0, 0);
        }

        keybd_event(key, 0, 0, 0);
        keybd_event(key, 0, KeyEventKeyUp, 0);

        foreach (var modifier in modifiers.Reverse())
        {
            keybd_event(modifier, 0, KeyEventKeyUp, 0);
        }
    }

    /// <summary>
    /// Presses Tab as the owner's keyboard would, through Windows rather than through WPF. It
    /// goes wherever the foreground window is, so a caller must have established that first.
    /// </summary>
    public static void PressTab() => PressKey(VkTab);

    /// <summary>Presses and releases one key, through Windows, wherever the foreground is.</summary>
    public static void PressKey(byte key)
    {
        keybd_event(key, 0, 0, 0);
        keybd_event(key, 0, KeyEventKeyUp, 0);
    }

    public const byte VkSpace = 0x20;

    public static Rect BoundsOf(nint handle) =>
        GetWindowRect(handle, out var rect) ? rect : throw new InvalidOperationException("The window has no bounds.");
}
