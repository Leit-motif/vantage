using MattWorkflowDashboard.App.Shell;
using Microsoft.Win32;

namespace MattWorkflowDashboard.Tests.ShellSeam;

/// <summary>
/// Choosing the System theme is a promise to follow Windows. Windows announces an appearance
/// change as a user-preference notification, and the dashboard has to hear it — a theme that
/// only catches up when something else happens to re-apply it is not following anything.
/// </summary>
[TestClass]
public sealed class AppearanceWatcherTests
{
    [TestMethod]
    public void A_change_to_how_Windows_looks_reaches_the_dashboard()
    {
        using var watcher = new AppearanceWatcher();
        var heard = 0;
        watcher.Changed += () => heard++;

        watcher.Report(UserPreferenceCategory.General);

        Assert.AreEqual(1, heard, "Switching Windows between light and dark has to reach the running window.");
    }

    [TestMethod]
    public void Every_preference_the_dashboard_s_appearance_depends_on_is_listened_for()
    {
        foreach (var category in new[]
                 {
                     UserPreferenceCategory.General,
                     UserPreferenceCategory.Color,
                     UserPreferenceCategory.VisualStyle,
                     UserPreferenceCategory.Accessibility,
                 })
        {
            Assert.IsTrue(AppearanceWatcher.Affects(category), $"{category} changes how the dashboard should look.");
        }
    }

    [TestMethod]
    public void Preferences_that_cannot_change_how_the_dashboard_looks_are_left_alone()
    {
        using var watcher = new AppearanceWatcher();
        var heard = 0;
        watcher.Changed += () => heard++;

        foreach (var category in new[]
                 {
                     UserPreferenceCategory.Keyboard,
                     UserPreferenceCategory.Mouse,
                     UserPreferenceCategory.Power,
                     UserPreferenceCategory.Locale,
                 })
        {
            watcher.Report(category);
        }

        Assert.AreEqual(0, heard, "Rebuilding the palette for unrelated preferences is work that changes nothing.");
    }
}
