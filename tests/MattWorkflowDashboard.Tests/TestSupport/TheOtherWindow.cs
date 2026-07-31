using System.Windows;
using System.Windows.Interop;

namespace MattWorkflowDashboard.Tests.TestSupport;

/// <summary>
/// Whatever the owner was doing before the dashboard appeared, and turns back to afterwards.
/// <para>
/// Every claim about the overlay refusing focus is really a claim about two windows: that the one
/// already holding the keyboard keeps it. On the owner's desktop that other window is whatever
/// they happen to have open, which is why these tests used to depend on the machine being left
/// alone — and why an interrupted run and a real failure looked so much alike. The suite's own
/// desktop starts out empty, and an empty desktop cannot express the claim at all: a window shown
/// where nothing else is running becomes the active one for want of a competitor, refusal or no
/// refusal.
/// </para>
/// <para>
/// So the competitor is supplied. It is an ordinary window with no special styles, holding the
/// keyboard the way the owner's editor would, and the dashboard has to leave it alone.
/// </para>
/// </summary>
public sealed class TheOtherWindow : IDisposable
{
    private readonly Window _window;
    private bool _disposed;

    private TheOtherWindow(Window window) => _window = window;

    /// <summary>The real window handle Windows knows this by.</summary>
    public nint Handle => WpfTestHost.Run(() => new WindowInteropHelper(_window).Handle);

    /// <summary>Opens it and leaves it holding the keyboard, as the owner's work would be.</summary>
    public static TheOtherWindow Open()
    {
        var opened = WpfTestHost.Run(() =>
        {
            var window = new Window
            {
                Title = "what the owner was doing",
                Width = 320,
                Height = 240,
                ShowInTaskbar = false,
            };

            window.Show();
            return new TheOtherWindow(window);
        });

        opened.TakeTheKeyboard();
        return opened;
    }

    /// <summary>The owner turns back to it, and the overlay has to let go.</summary>
    public void TakeTheKeyboard()
    {
        WpfTestHost.Run(() => Win32Window.Activate(new WindowInteropHelper(_window).Handle));
        WpfTestHost.Drain();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        WpfTestHost.Run(_window.Close);
    }
}
