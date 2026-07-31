namespace MattWorkflowDashboard.Tests.TestSupport;

/// <summary>
/// The one thing a shell-seam test cannot own: the desktop it runs on. A handful of these tests
/// need the dashboard to genuinely hold the Windows foreground, because they deliver real
/// keystrokes and Windows delivers those to whatever is in front. The machine has an owner, and a
/// click of theirs takes the foreground away mid-test — which is not the dashboard failing.
/// <para>
/// So a lost foreground is classified rather than simply asserted on: interference makes a test
/// inconclusive and names what interfered, while a dashboard that never took the keyboard at all
/// fails exactly as it always did. What is never treated as interference is the shell's own
/// behaviour — the point of these tests survives, because the only thing excused is a window
/// somewhere else on the desktop taking the foreground away.
/// </para>
/// </summary>
public static class TheDesktop
{
    /// <summary>
    /// Ends the test as inconclusive when the foreground has gone somewhere neither the test
    /// started against nor the dashboard itself. A foreground still sitting where the test found
    /// it is not interference, so this returns and lets the caller assert.
    /// </summary>
    /// <param name="ours">The dashboard's window.</param>
    /// <param name="before">The foreground window the test started against.</param>
    /// <param name="now">The foreground window as just observed.</param>
    /// <param name="what">What the test was doing, for the message.</param>
    public static void StopIfSomethingElseTookTheForeground(nint ours, nint before, nint now, string what)
    {
        if (now == ours || now == before)
        {
            return;
        }

        Stop(
            $"{Win32Window.Describe(now)} took the foreground from {Win32Window.Describe(before)} while {what}",
            what);
    }

    /// <summary>
    /// Ends the test as inconclusive because the dashboard was given the keyboard and then lost
    /// it. Past that point any interruption is interference by definition: the gesture had already
    /// proven the dashboard could take the foreground, so what follows is the machine being used.
    /// </summary>
    public static void StopBecauseTheDashboardLostTheKeyboard(nint ours, string what)
    {
        var now = Win32Window.Foreground();
        var where = now == ours
            ? "it has since been handed back, so the interruption is only visible in the shell's own reaction to it"
            : $"{Win32Window.Describe(now)} has it now";

        Stop($"the dashboard was given the keyboard and lost it again while {what} — {where}", what);
    }

    private static void Stop(string what, string during) =>
        Assert.Inconclusive(
            $"The desktop was in use: {what}. This test sends real keyboard input, which Windows "
            + "delivers to whatever window is in front, so it can only be run on a desktop nobody is "
            + $"using. Nothing is claimed about the dashboard's behaviour while {during}.");
}
