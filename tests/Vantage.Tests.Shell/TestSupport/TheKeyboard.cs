using System.Runtime.InteropServices;

namespace Vantage.Tests.TestSupport;

/// <summary>
/// Keyboard input, delivered to one named window rather than to whatever happens to be in front.
/// <para>
/// These tests used to press keys with <c>keybd_event</c>, which puts them into the session's
/// input stream — so they went wherever Windows was sending input, and a test that lost the
/// foreground typed into the owner's work. That is not a flaw in how the tests were written: a
/// system-wide press has no target, and no amount of checking who holds the foreground can make
/// one safe, because the owner can take it back between the check and the keystroke.
/// </para>
/// <para>
/// So the press is aimed instead. A key posted to a window goes onto that window's own message
/// queue, and the only thing that can receive it is that window. What still happens for real is
/// everything above the queue: WPF reads these messages through the same <c>HwndSource</c> hook as
/// any other, and its input manager, keyboard focus, tab navigation and command routing all run
/// exactly as they do for the owner. What no longer happens is the part below it — the raw input
/// thread, and hardware scan codes becoming virtual keys. docs/testing.md records that trade and
/// why it was taken.
/// </para>
/// </summary>
public static class TheKeyboard
{
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmHotkey = 0x0312;
    private const uint MapVirtualKeyToScanCode = 0;

    public const byte Tab = 0x09;
    public const byte Space = 0x20;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyW(uint uCode, uint uMapType);

    /// <summary>
    /// Presses and releases one key at one window. Refuses to do anything at all from a thread on
    /// the owner's desktop: a test that reached this point uninsulated is a test that would type
    /// into their session, and stopping is the only honest outcome.
    /// </summary>
    public static void Press(nint window, byte key)
    {
        IsolatedDesktop.RequireIsolation($"press a key at {Win32Window.Describe(window)}");

        // The scan code and the transition bits are what a key press looks like coming off real
        // hardware, and WPF reads them: repeat count, then the key's scan code, then — on release
        // — the bits that say the key was already down and is now going up.
        var scanCode = MapVirtualKeyW(key, MapVirtualKeyToScanCode);
        var down = (nint)(1 | (scanCode << 16));
        var up = (nint)(1 | (scanCode << 16) | (1u << 30) | (1u << 31));

        PostMessageW(window, WmKeyDown, key, down);
        PostMessageW(window, WmKeyUp, key, up);
    }

    /// <summary>
    /// Delivers the global hotkey the owner configured, as the message Windows sends when they
    /// press it.
    /// <para>
    /// The registration itself is real and is asserted where it happens: the shell binds the
    /// gesture against its own live HWND and Windows either accepts it or does not. What is
    /// reproduced here is only the delivery. Windows delivers a hotkey by posting this message to
    /// the window that registered it, and it does so from the input desktop — which the suite
    /// deliberately is not on, so on that desktop the press could never arrive.
    /// </para>
    /// </summary>
    public static void PressTheConfiguredGesture(nint window, int hotkeyId)
    {
        IsolatedDesktop.RequireIsolation($"deliver a hotkey to {Win32Window.Describe(window)}");

        PostMessageW(window, WmHotkey, hotkeyId, 0);
    }
}
