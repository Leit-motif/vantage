namespace MattWorkflowDashboard.Tests.TestSupport;

/// <summary>
/// The categories that decide which tests a plain <c>dotnet test</c> runs.
/// </summary>
public static class TestCategories
{
    /// <summary>
    /// A test that sends real keyboard input and therefore needs the dashboard to genuinely hold
    /// the Windows foreground. Windows delivers keystrokes to whatever is in front, so any use of
    /// the machine during the run — a click, an alt-tab, a notification taking focus — sends them
    /// somewhere else. These are excluded from the default run by <c>.runsettings</c> and run
    /// deliberately, on a desktop nobody is using:
    /// <code>dotnet test --filter TestCategory=RequiresIdleDesktop</code>
    /// </summary>
    public const string RequiresIdleDesktop = "RequiresIdleDesktop";
}
