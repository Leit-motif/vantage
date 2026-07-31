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

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextW(nint hWnd, char[] lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    /// <summary>
    /// A window as a person would recognise it — its title and the process behind it. A test that
    /// gives up because something else has the foreground has to say what that something was, or
    /// the report is indistinguishable from the dashboard failing.
    /// </summary>
    public static string Describe(nint handle)
    {
        if (!Exists(handle))
        {
            return handle == 0 ? "no window at all" : $"a window that no longer exists (0x{handle:X})";
        }

        var buffer = new char[512];
        var length = GetWindowTextW(handle, buffer, buffer.Length);
        var title = length > 0 ? new string(buffer, 0, length) : "(untitled)";

        var process = "an unknown process";
        if (GetWindowThreadProcessId(handle, out var processId) != 0)
        {
            try
            {
                using var owner = System.Diagnostics.Process.GetProcessById((int)processId);
                process = $"{owner.ProcessName} ({processId})";
            }
            catch (ArgumentException)
            {
                process = $"a process that has since exited ({processId})";
            }
            catch (InvalidOperationException)
            {
                process = $"a process that has since exited ({processId})";
            }
        }

        return $"'{title}' in {process}";
    }

    [DllImport("user32.dll")]
    private static extern nint GetActiveWindow();

    [DllImport("user32.dll")]
    private static extern nint SetActiveWindow(nint hWnd);

    /// <summary>
    /// The window Windows is sending the calling thread's keyboard input to. This is per thread,
    /// so it answers for the desktop the caller is on and has to be asked from the thread whose
    /// windows are in question — from anywhere else it describes a different desktop entirely.
    /// <para>
    /// Activation rather than the foreground is what these tests read, because the suite runs on a
    /// desktop of its own and a desktop nobody is looking at has no foreground window at all:
    /// <c>GetForegroundWindow</c> answers for the one desktop Windows is sending real input to.
    /// Activation is the part of the same idea that is real on every desktop, and it is the part
    /// the dashboard actually reacts to — its no-activate refusal is lifted and restored around
    /// <c>WM_ACTIVATE</c>.
    /// </para>
    /// </summary>
    public static nint Active() => GetActiveWindow();

    /// <summary>
    /// Gives a window the keyboard, as Windows does for a window it has decided should have it.
    /// <para>
    /// On the owner's desktop that decision is made by the raw input thread, in response to a
    /// click or a registered hotkey, and shows up as the foreground changing. The suite's own
    /// desktop has no input to make it with — so the grant Windows would have performed is
    /// performed here instead, and the window sees the same <c>WM_ACTIVATE</c> either way. What
    /// this does not stand in for is the dashboard's own request for the foreground: that is a
    /// call into Windows that a desktop with no input queue refuses. See TESTING.md.
    /// </para>
    /// </summary>
    public static void Activate(nint handle) => SetActiveWindow(handle);

    public static Rect BoundsOf(nint handle) =>
        GetWindowRect(handle, out var rect) ? rect : throw new InvalidOperationException("The window has no bounds.");
}
