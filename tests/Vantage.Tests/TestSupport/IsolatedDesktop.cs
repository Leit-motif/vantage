using System.Runtime.InteropServices;
using System.Text;

namespace Vantage.Tests.TestSupport;

/// <summary>
/// A Windows desktop of the suite's own, so that shell-seam tests never borrow the owner's.
/// <para>
/// A desktop is the container Windows keeps windows, an active window and an input queue in. The
/// owner's is called <c>Default</c>; this creates a second one and puts the thread that hosts
/// every test window onto it. Two things follow, and both are the point. The dashboard's windows
/// are no longer on any screen the owner is looking at, so they cannot appear over their work or
/// take activation from it. And the input the tests deliver is confined to a desktop the owner's
/// keyboard never visits.
/// </para>
/// <para>
/// Isolation is required rather than preferred. A shell test that could not get its own desktop
/// would activate windows on the owner's, which is the behaviour this exists to prevent — so
/// failing to create one is an error the suite stops on, never something it continues without.
/// </para>
/// </summary>
public static class IsolatedDesktop
{
    private const uint GenericAll = 0x10000000;
    private const uint GenericRead = 0x80000000;
    private const int UserObjectName = 2;
    private const uint ApartmentThreaded = 0x2;

    private static readonly Lock Gate = new();
    private static nint _desktop;

    /// <summary>
    /// Kept alive for as long as the process is. The thread Windows starts calls back through this
    /// pointer, and a delegate the collector has moved or freed is a crash rather than a test
    /// failure.
    /// </summary>
    private static readonly List<ThreadBody> Bodies = [];

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ThreadBody(nint parameter);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateThread(
        nint attributes,
        nuint stackSize,
        nint start,
        nint parameter,
        uint flags,
        out uint threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(nint reserved, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateDesktopW(
        string lpszDesktop,
        string? lpszDevice,
        nint pDevmode,
        uint dwFlags,
        uint dwDesiredAccess,
        nint lpsa);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadDesktop(nint hDesktop);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(nint hDesktop);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetThreadDesktop(uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint OpenInputDesktop(
        uint dwFlags,
        [MarshalAs(UnmanagedType.Bool)] bool fInherit,
        uint dwDesiredAccess);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformationW(
        nint hObj,
        int nIndex,
        byte[] pvInfo,
        uint nLength,
        out uint lpnLengthNeeded);

    /// <summary>
    /// The suite's own desktop, named for this process so that two runs at once cannot collide.
    /// </summary>
    public static string Name { get; } = $"VantageTests-{Environment.ProcessId}";

    /// <summary>
    /// Creates the suite's desktop, once. Separate from attaching to it because the two have to
    /// happen on different threads: a thread cannot create the desktop it is about to move onto
    /// without doing work first, and doing work first is exactly what makes the move fail.
    /// </summary>
    public static void Create()
    {
        lock (Gate)
        {
            if (_desktop != 0)
            {
                return;
            }

            _desktop = CreateDesktopW(Name, null, 0, 0, GenericAll, 0);
            if (_desktop == 0)
            {
                throw new InvalidOperationException(
                    $"The tests need a desktop of their own and Windows would not create '{Name}' "
                    + $"(error {Marshal.GetLastWin32Error()}). Running the shell tests on the owner's "
                    + "desktop would put windows on their screen and take activation from their work, "
                    + "so the suite stops here rather than doing that.");
            }
        }
    }

    /// <summary>
    /// Starts a single-threaded-apartment thread that lives on the suite's desktop, and runs
    /// <paramref name="body"/> on it. Everything the thread goes on to create — windows, message
    /// queue, dispatcher — belongs to that desktop.
    /// <para>
    /// The thread is made by Windows rather than by the runtime, and that is not incidental.
    /// Windows refuses to move a thread that already owns a window, and a thread declared STA
    /// before it starts is in its apartment — hidden OLE window and all — before its own first
    /// instruction runs, so it can never move. Nor can it be moved the other way round: the
    /// runtime fixes a thread's apartment when the thread starts and will not change it
    /// afterwards. Starting the thread here breaks that deadlock by putting the two steps in the
    /// only order that works — desktop first, apartment second — which needs a thread that has not
    /// run any runtime code yet. Once <c>CoInitializeEx</c> has entered the apartment, the runtime
    /// reads it back off the operating system and agrees the thread is STA, which is all WPF asks.
    /// </para>
    /// </summary>
    /// <param name="name">A name for the thread, for anyone reading a debugger or a dump.</param>
    /// <param name="body">What to run, once the thread is somewhere the owner is not.</param>
    /// <param name="onFailure">
    /// Called instead of <paramref name="body"/> when the thread could not be placed. Failing to
    /// isolate is never continued through: the body is the part that makes windows.
    /// </param>
    public static void StartStaThreadOnIt(string name, Action body, Action<Exception> onFailure)
    {
        Create();

        ThreadBody entry = _ =>
        {
            try
            {
                Place(name);
            }
            catch (Exception error)
            {
                onFailure(error);
                return 0;
            }

            body();
            return 0;
        };

        lock (Gate)
        {
            Bodies.Add(entry);
        }

        var thread = CreateThread(0, 0, Marshal.GetFunctionPointerForDelegate(entry), 0, 0, out _);
        if (thread == 0)
        {
            throw new InvalidOperationException(
                $"Windows would not start a thread for '{name}' (error {Marshal.GetLastWin32Error()}).");
        }

        // The thread runs on without it; this is only the handle to it.
        CloseHandle(thread);
    }

    /// <summary>
    /// The first two things the thread does, in the order that is the whole reason it exists.
    /// </summary>
    private static void Place(string name)
    {
        if (!SetThreadDesktop(_desktop))
        {
            throw new InvalidOperationException(
                $"'{name}' could not be moved onto '{Name}' (error {Marshal.GetLastWin32Error()}). "
                + "Windows refuses once a thread owns a window or a hook, so nothing may run on this "
                + "thread before this does.");
        }

        // S_OK, or S_FALSE for an apartment that was already entered.
        var result = CoInitializeEx(0, ApartmentThreaded);
        if (result < 0)
        {
            throw new InvalidOperationException(
                $"'{name}' could not enter a single-threaded apartment (HRESULT 0x{result:X8}).");
        }

        Thread.CurrentThread.Name = name;

        // Windows started this thread, so the runtime does not already know to let the process end
        // while it is still running its message loop.
        Thread.CurrentThread.IsBackground = true;
    }

    /// <summary>
    /// Whether the calling thread is somewhere the owner's keyboard and screen do not reach. Read
    /// by comparing the thread's own desktop against the one Windows is currently sending input
    /// to, rather than by remembering what was asked for: the question worth answering before
    /// delivering input is where this thread actually is.
    /// </summary>
    public static bool IsCurrentThreadIsolated() =>
        !string.Equals(CurrentThreadDesktopName(), InputDesktopName(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Refuses to go on unless the calling thread is off the owner's desktop. Guards everything
    /// that delivers input to a window, so that no future change can quietly start driving the
    /// keyboard on the interactive desktop again.
    /// </summary>
    public static void RequireIsolation(string about)
    {
        if (IsCurrentThreadIsolated())
        {
            return;
        }

        throw new InvalidOperationException(
            $"Refusing to {about}: this thread is on '{CurrentThreadDesktopName()}', which is the desktop "
            + "Windows is sending the owner's input to. Anything delivered from here can reach their "
            + "session. Shell-seam work belongs on the suite's own desktop — see IsolatedDesktop.");
    }

    /// <summary>The desktop the calling thread's windows live on.</summary>
    public static string CurrentThreadDesktopName() => NameOf(GetThreadDesktop(GetCurrentThreadId()));

    /// <summary>The desktop Windows is currently sending keyboard and mouse input to.</summary>
    public static string InputDesktopName()
    {
        var input = OpenInputDesktop(0, false, GenericRead);
        if (input == 0)
        {
            return $"(no readable input desktop, error {Marshal.GetLastWin32Error()})";
        }

        try
        {
            return NameOf(input);
        }
        finally
        {
            CloseDesktop(input);
        }
    }

    private static string NameOf(nint desktop)
    {
        if (desktop == 0)
        {
            return "(none)";
        }

        var buffer = new byte[512];
        if (!GetUserObjectInformationW(desktop, UserObjectName, buffer, (uint)buffer.Length, out var needed))
        {
            return $"(unreadable, error {Marshal.GetLastWin32Error()})";
        }

        // The length includes the terminating null, which is two bytes of UTF-16.
        return Encoding.Unicode.GetString(buffer, 0, (int)Math.Max(0, needed - 2));
    }
}
