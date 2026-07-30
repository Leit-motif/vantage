using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace MattWorkflowDashboard.App.Shell;

/// <summary>
/// An optional global hotkey. There is no default binding — the dashboard never claims a
/// system-wide shortcut the owner did not ask for.
/// </summary>
public sealed partial class GlobalHotkey : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 0xB0B;

    [Flags]
    private enum Modifiers
    {
        Alt = 0x0001,
        Control = 0x0002,
        Shift = 0x0004,
        Win = 0x0008,
        NoRepeat = 0x4000,
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(nint hWnd, int id);

    private readonly nint _handle;
    private readonly HwndSource? _source;
    private readonly Action _onPressed;
    private bool _registered;

    public GlobalHotkey(nint handle, Action onPressed)
    {
        _handle = handle;
        _onPressed = onPressed;

        // Without a window there is nothing to register against; binding simply never succeeds.
        _source = handle == 0 ? null : HwndSource.FromHwnd(handle);
        _source?.AddHook(OnMessage);
    }

    /// <summary>
    /// Binds a gesture such as <c>Ctrl+Alt+D</c>. An empty or unparseable value simply leaves the
    /// dashboard without a hotkey rather than guessing one.
    /// </summary>
    public bool Bind(string? gesture)
    {
        Unbind();

        if (_handle == 0 || string.IsNullOrWhiteSpace(gesture) || !TryParse(gesture, out var modifiers, out var key))
        {
            return false;
        }

        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        _registered = RegisterHotKey(_handle, HotkeyId, (uint)(modifiers | Modifiers.NoRepeat), virtualKey);
        return _registered;
    }

    public void Unbind()
    {
        if (_registered)
        {
            UnregisterHotKey(_handle, HotkeyId);
            _registered = false;
        }
    }

    private nint OnMessage(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam == HotkeyId)
        {
            _onPressed();
            handled = true;
        }

        return 0;
    }

    private static bool TryParse(string gesture, out Modifiers modifiers, out Key key)
    {
        modifiers = 0;
        key = Key.None;

        foreach (var part in gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    modifiers |= Modifiers.Control;
                    break;
                case "alt":
                    modifiers |= Modifiers.Alt;
                    break;
                case "shift":
                    modifiers |= Modifiers.Shift;
                    break;
                case "win":
                    modifiers |= Modifiers.Win;
                    break;
                default:
                    if (!Enum.TryParse(part, ignoreCase: true, out key))
                    {
                        return false;
                    }

                    break;
            }
        }

        return modifiers != 0 && key != Key.None;
    }

    public void Dispose()
    {
        Unbind();
        _source?.RemoveHook(OnMessage);
    }
}
