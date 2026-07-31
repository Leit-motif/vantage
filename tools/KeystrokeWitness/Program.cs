using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vantage.KeystrokeWitness;

/// <summary>
/// Watches the interactive desktop for synthetic keystrokes while another command runs.
/// <para>
/// The test suite drives the dashboard's keyboard, and it does so on a machine somebody is using.
/// The claim that none of that reaches their session is the kind of claim that is easy to assert
/// and easy to be wrong about — a suite that types on the owner once every hundred runs still
/// passes ninety-nine of them. So it is watched from the outside, by the one mechanism that sees
/// everything arriving on a desktop, and watched while the machine is in normal use.
/// </para>
/// </summary>
internal static class Program
{
    private const int WhKeyboardLl = 13;
    private const int HcAction = 0;
    private const int LowLevelKeyboardInjected = 0x10;

    /// <summary>
    /// The control keystroke. F24 exists on no keyboard anyone owns and is mapped by nothing, so
    /// putting one into the owner's session costs them nothing — and a witness that cannot see it
    /// is a witness whose silence means nothing either.
    /// </summary>
    private const byte VirtualKeyF24 = 0x87;

    private const uint KeyEventKeyUp = 0x0002;

    private static readonly List<SyntheticKeystroke> Synthetic = [];
    private static readonly Lock Gate = new();
    private static long _realKeyEventsDiscarded;
    private static long _watchingFrom;
    private static HookProc? _hook;

    private delegate nint HookProc(int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SetWindowsHookExW(int idHook, HookProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessageW(out Message lpMsg, nint hWnd, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessageW(ref Message lpMsg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref Message lpMsg);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, nint dwExtraInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Hwnd;
        public uint Msg;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine(
                "usage: KeystrokeWitness <report.json> [--stamp <commit>] <command> [arguments...]\n"
                + "Runs the command while watching the interactive desktop for synthetic keystrokes.");
            return 2;
        }

        var reportPath = Path.GetFullPath(args[0]);
        var rest = args.Skip(1).ToArray();

        var stamp = string.Empty;
        if (rest is ["--stamp", var given, ..])
        {
            stamp = given;
            rest = rest.Skip(2).ToArray();
        }

        if (rest.Length == 0)
        {
            Console.Error.WriteLine("There is no command to watch.");
            return 2;
        }

        var command = rest[0];
        var arguments = rest.Skip(1).ToArray();

        _hook = OnKey;
        var hook = SetWindowsHookExW(WhKeyboardLl, _hook, 0, 0);
        if (hook == 0)
        {
            Console.Error.WriteLine($"Could not watch the desktop (error {Marshal.GetLastWin32Error()}).");
            return 4;
        }

        try
        {
            // Calibration first. Everything after this is an argument from silence, and silence
            // from an instrument that was never listening is not evidence.
            _watchingFrom = Stopwatch.GetTimestamp();
            keybd_event(VirtualKeyF24, 0, 0, 0);
            keybd_event(VirtualKeyF24, 0, KeyEventKeyUp, 0);
            Pump(TimeSpan.FromMilliseconds(750));

            var control = Take();
            var controlObserved = control.Any(k => k.VirtualKey == VirtualKeyF24);

            _watchingFrom = Stopwatch.GetTimestamp();
            var started = DateTimeOffset.UtcNow;
            var exitCode = RunWatched(command, arguments, out var elapsed);

            // Anything in flight when the command exited still belongs to the watched window.
            Pump(TimeSpan.FromMilliseconds(750));
            var observed = Take();

            var report = new Report(
                StartedUtc: started,
                Stamp: stamp,
                Command: string.Join(' ', new[] { command }.Concat(arguments)),
                CommandExitCode: exitCode,
                WatchedSeconds: Math.Round(elapsed.TotalSeconds, 1),
                ControlObserved: controlObserved,
                SyntheticKeystrokes: observed,
                RealKeyEventsSeenAndDiscarded: Interlocked.Read(ref _realKeyEventsDiscarded),
                Verdict: !controlObserved
                    ? "void: the control keystroke was not observed, so silence proves nothing"
                    : observed.Count == 0
                        ? "clean: nothing synthetic reached the interactive desktop"
                        : $"FAILED: {observed.Count} synthetic key events reached the interactive desktop");

            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            File.WriteAllText(
                reportPath,
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

            Console.WriteLine(report.Verdict);
            Console.WriteLine($"  control observed         : {controlObserved}");
            Console.WriteLine($"  synthetic keystrokes seen: {observed.Count}");
            Console.WriteLine($"  real key events discarded: {report.RealKeyEventsSeenAndDiscarded}");
            Console.WriteLine($"  command exited           : {exitCode}");
            Console.WriteLine($"  report                   : {reportPath}");

            if (!controlObserved)
            {
                return 4;
            }

            return observed.Count == 0 ? 0 : 3;
        }
        finally
        {
            UnhookWindowsHookEx(hook);
        }
    }

    /// <summary>
    /// A low-level hook sees every keystroke on the desktop, including the owner's. What they
    /// typed is none of this instrument's business and is never recorded — a real key event is
    /// counted and thrown away, and only synthetic ones are described.
    /// </summary>
    private static nint OnKey(int code, nint wParam, nint lParam)
    {
        if (code == HcAction)
        {
            // KBDLLHOOKSTRUCT: vkCode, scanCode, flags, time, dwExtraInfo.
            var flags = Marshal.ReadInt32(lParam, 8);

            if ((flags & LowLevelKeyboardInjected) != 0)
            {
                lock (Gate)
                {
                    Synthetic.Add(new SyntheticKeystroke(
                        Marshal.ReadInt32(lParam, 0),
                        Math.Round(Stopwatch.GetElapsedTime(_watchingFrom).TotalSeconds, 2)));
                }
            }
            else
            {
                Interlocked.Increment(ref _realKeyEventsDiscarded);
            }
        }

        return CallNextHookEx(0, code, wParam, lParam);
    }

    private static int RunWatched(string command, string[] arguments, out TimeSpan elapsed)
    {
        var info = new ProcessStartInfo(command) { UseShellExecute = false };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        var from = Stopwatch.GetTimestamp();
        using var child = Process.Start(info)
            ?? throw new InvalidOperationException($"Could not start '{command}'.");

        // Pumping is not optional: Windows delivers a low-level hook through this thread's message
        // queue, and drops a hook whose thread stops answering.
        while (!child.WaitForExit(10))
        {
            PumpOnce();
        }

        elapsed = Stopwatch.GetElapsedTime(from);
        return child.ExitCode;
    }

    private static void Pump(TimeSpan duration)
    {
        var until = Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < until)
        {
            PumpOnce();
            Thread.Sleep(5);
        }
    }

    private static void PumpOnce()
    {
        while (PeekMessageW(out var message, 0, 0, 0, 1))
        {
            TranslateMessage(ref message);
            DispatchMessageW(ref message);
        }
    }

    private static List<SyntheticKeystroke> Take()
    {
        lock (Gate)
        {
            var taken = Synthetic.ToList();
            Synthetic.Clear();
            return taken;
        }
    }

    private sealed record SyntheticKeystroke(
        [property: JsonPropertyName("virtualKey")] int VirtualKey,
        [property: JsonPropertyName("secondsIn")] double SecondsIn);

    private sealed record Report(
        DateTimeOffset StartedUtc,
        string Stamp,
        string Command,
        int CommandExitCode,
        double WatchedSeconds,
        bool ControlObserved,
        List<SyntheticKeystroke> SyntheticKeystrokes,
        long RealKeyEventsSeenAndDiscarded,
        string Verdict);
}
