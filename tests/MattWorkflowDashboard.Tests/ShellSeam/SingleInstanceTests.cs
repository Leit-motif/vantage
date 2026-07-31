using System.Diagnostics;
using MattWorkflowDashboard.App.Shell;

namespace MattWorkflowDashboard.Tests.ShellSeam;

/// <summary>
/// One overlay, one indexer. This is proven across real processes: the built application is
/// launched and asked what it decided about the instance already running, so the evidence is the
/// shipped executable's own startup decision rather than a re-implementation of it in a test.
/// </summary>
[TestClass]
public sealed class SingleInstanceTests
{
    /// <summary>Exit codes the application reports when asked to decide and stop.</summary>
    private const int FirstInstance = 0;

    private const int AlreadyRunning = 2;

    [TestMethod]
    public void The_application_starts_normally_when_nothing_else_is_running()
    {
        Assert.AreEqual(
            FirstInstance,
            LaunchProbe(),
            "With no dashboard running, the application must take the session for itself.");
    }

    [TestMethod]
    public void A_second_application_stands_down_instead_of_opening_a_competing_overlay()
    {
        using var running = new SingleInstanceGuard();

        Assert.IsTrue(running.IsOnlyInstance, "The test holds the session first.");
        Assert.AreEqual(
            AlreadyRunning,
            LaunchProbe(),
            "A second launch must stand down rather than open a second overlay and a second indexer.");
    }

    [TestMethod]
    public void The_session_is_released_when_the_dashboard_exits()
    {
        using (var first = new SingleInstanceGuard())
        {
            Assert.IsTrue(first.IsOnlyInstance);
            Assert.AreEqual(AlreadyRunning, LaunchProbe());
        }

        Assert.AreEqual(
            FirstInstance,
            LaunchProbe(),
            "Once the dashboard exits, the next launch has to be allowed to start.");
    }

    /// <summary>
    /// Runs the built application with the flag that makes it decide, report, and exit without
    /// ever building a window.
    /// </summary>
    private static int LaunchProbe()
    {
        var executable = LocateApplication();

        using var process = Process.Start(new ProcessStartInfo(executable, "--single-instance-probe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException($"Could not launch {executable}.");

        Assert.IsTrue(
            process.WaitForExit(TimeSpan.FromSeconds(30)),
            "The instance probe must decide and exit promptly, without building any UI.");

        return process.ExitCode;
    }

    private static string LocateApplication()
    {
        // The application is a project reference, so its executable sits beside the test assembly.
        var beside = Path.Combine(AppContext.BaseDirectory, "MattWorkflowDashboard.exe");
        if (File.Exists(beside))
        {
            return beside;
        }

        throw new AssertInconclusiveException(
            $"The built application was not found at {beside}; the running-shell seam needs it.");
    }
}
