using System.Reflection;
using System.Runtime.InteropServices;
using MattWorkflowDashboard.Tests.TestSupport;

namespace MattWorkflowDashboard.Tests.ShellSeam;

/// <summary>
/// The suite's own safety, asserted about the suite rather than about the dashboard.
/// <para>
/// These tests drive a real window with a real keyboard's worth of messages, and the machine they
/// run on belongs to somebody. On 2026-07-30 a loop of runs opened ten Notepad windows on that
/// person's desktop, because a keystroke put into the session's input stream goes wherever Windows
/// is sending input and no check made just before it can promise that is still the dashboard.
/// </para>
/// <para>
/// What replaced that is structural, and these two tests are what keep it structural: the suite
/// has no way to inject input, and the windows it drives are not on the owner's desktop. Neither
/// depends on anyone remembering the rule.
/// </para>
/// </summary>
[TestClass]
public sealed class KeyboardSafetyTests
{
    /// <summary>
    /// The calls that put input into the session rather than into a window. Every one of these
    /// reaches whatever the owner is using, so the suite links none of them — a press that has no
    /// target cannot be aimed away from somebody's work.
    /// </summary>
    private static readonly string[] WaysToTypeIntoSomebodyElse_sSession =
    [
        "keybd_event",
        "SendInput",
        "mouse_event",
        "BlockInput",
    ];

    [TestMethod]
    public void Nothing_in_this_suite_can_put_a_keystroke_into_the_owner_s_session()
    {
        var found = typeof(Win32Window).Assembly
            .GetTypes()
            .SelectMany(type => type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(method => method.Attributes.HasFlag(MethodAttributes.PinvokeImpl))
            .Select(method => (Method: method, Entry: EntryPointOf(method)))
            .Where(p => WaysToTypeIntoSomebodyElse_sSession.Contains(p.Entry, StringComparer.Ordinal))
            .Select(p => $"{p.Method.DeclaringType?.Name}.{p.Method.Name} -> {p.Entry}")
            .ToList();

        Assert.AreEqual(
            0,
            found.Count,
            "These tests must have no way to deliver input except at a named window. Found: "
            + string.Join(", ", found));
    }

    [TestMethod]
    public void The_windows_these_tests_drive_are_not_on_the_owner_s_desktop()
    {
        // Asked on the thread that owns them, because this is a per-thread question.
        var theirs = WpfTestHost.Run(IsolatedDesktop.CurrentThreadDesktopName);

        Assert.AreEqual(
            IsolatedDesktop.Name,
            theirs,
            "Every window the suite makes belongs to the host thread, so this is where they appear.");

        Assert.AreNotEqual(
            IsolatedDesktop.InputDesktopName(),
            theirs,
            "A suite sharing the desktop Windows is sending input to is a suite that can be typed on, "
            + "and can type back.");

        Assert.IsTrue(
            WpfTestHost.Run(IsolatedDesktop.IsCurrentThreadIsolated),
            "This is the condition every keystroke the suite delivers is checked against.");
    }

    private static string EntryPointOf(MethodInfo method) =>
        method.GetCustomAttribute<DllImportAttribute>()?.EntryPoint is { Length: > 0 } named
            ? named
            : method.Name;
}
