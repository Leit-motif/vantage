using System.Diagnostics;
using System.Windows.Threading;
using MattWorkflowDashboard.App.Shell;
using MattWorkflowDashboard.App.ViewModels;
using MattWorkflowDashboard.Infrastructure;
using MattWorkflowDashboard.Infrastructure.Persistence;
using MattWorkflowDashboard.Infrastructure.Settings;
using MattWorkflowDashboard.Tests.TestSupport;

namespace MattWorkflowDashboard.Tests.ShellSeam;

/// <summary>
/// What a large, noisy workspace does to the running shell: a burst of workflow changes has to
/// cost one refresh rather than one per file, and a scan large enough to take real time must not
/// stop the window answering the owner.
/// </summary>
[TestClass]
public sealed class WatcherAndResponsivenessTests
{
    private WorkspaceFixture _workspace = null!;

    [TestInitialize]
    public void SetUp() => _workspace = new WorkspaceFixture();

    [TestCleanup]
    public void TearDown() => _workspace.Dispose();

    /// <summary>
    /// The debounce is the backpressure. A checkout or an agent rewriting a whole effort produces
    /// hundreds of events, and each one asking for its own refresh would be the dashboard indexing
    /// the workspace continuously instead of watching it.
    /// </summary>
    [TestMethod]
    public void A_burst_of_workflow_changes_costs_one_refresh_rather_than_one_per_file()
    {
        var project = _workspace.NewProject("repo");
        var effort = _workspace.NewEffort(project, "feature");

        var refreshes = 0;
        using var watcher = new WorkflowWatcher(
            [_workspace.WorkspacesRoot],
            () => Interlocked.Increment(ref refreshes),
            TimeSpan.FromMilliseconds(400));

        for (var i = 0; i < 200; i++)
        {
            _workspace.WriteTicket(effort, $"{i:D3}.md", Fixtures.Ticket($"Work {i}", "ready"));
        }

        Assert.IsTrue(WaitUntil(() => Volatile.Read(ref refreshes) >= 1), "The burst has to reach the dashboard at all.");
        Thread.Sleep(1200);

        Assert.AreEqual(
            1,
            Volatile.Read(ref refreshes),
            "Two hundred workflow writes are one thing happening, and cost one refresh.");

        _workspace.WriteTicket(effort, "later.md", Fixtures.Ticket("Later", "ready"));

        Assert.IsTrue(
            WaitUntil(() => Volatile.Read(ref refreshes) >= 2),
            "A change after the burst settled is new movement and asks for another refresh.");
    }

    /// <summary>
    /// The scan is the dashboard's own work; the window belongs to the owner. Under a tree large
    /// enough for a refresh to take real time, the dispatcher has to keep turning — a command the
    /// owner presses mid-scan must not wait for the scan to finish.
    /// </summary>
    [TestMethod]
    public async Task The_running_window_keeps_answering_while_a_large_scan_is_in_flight()
    {
        _workspace.NewTree(directories: 4000, projectsEvery: 25);

        var paths = new AppPaths(Path.Combine(_workspace.Root, "appdata"));
        paths.EnsureCreated();

        var settings = new DashboardSettings
        {
            Roots = [_workspace.WorkspacesRoot],
            GitHubEnrichmentEnabled = false,
        };

        var store = new SettingsStore(paths);
        store.Save(settings);

        using var cache = DashboardCache.Open(_workspace.CacheFile);
        var viewModel = new DashboardViewModel(
            settings,
            store,
            cache,
            new FakeProcessRunner().GhUnauthenticated(),
            highContrast: () => false);

        var scan = Stopwatch.StartNew();

        // Queued rather than invoked. Invoking would block this thread through whatever the refresh
        // does before its first real await — which is exactly the part being measured, so a test
        // that waited for it would sample only the time after the freeze it is looking for.
        var refresh = WpfTestHost.Ui()
            .InvokeAsync(() => viewModel.RefreshCommand.ExecuteAsync(null), DispatcherPriority.Normal)
            .Task
            .Unwrap();

        var worstLatency = TimeSpan.Zero;
        var samples = 0;

        while (!refresh.IsCompleted)
        {
            var pressed = Stopwatch.StartNew();

            // Exactly what a keypress or a click is: work queued at input priority, which the
            // dispatcher can only reach between whatever else it is doing.
            await WpfTestHost.Ui().InvokeAsync(() => { }, DispatcherPriority.Input).Task;

            if (pressed.Elapsed > worstLatency)
            {
                worstLatency = pressed.Elapsed;
            }

            samples++;
            await Task.Delay(20);
        }

        await refresh;
        scan.Stop();

        Assert.IsTrue(
            scan.Elapsed > TimeSpan.FromMilliseconds(600),
            $"The tree has to be big enough for responsiveness to be a real question; the scan took {scan.ElapsedMilliseconds}ms.");

        Assert.IsTrue(samples > 3, $"Only {samples} samples were taken during the scan.");

        Assert.IsTrue(
            worstLatency < TimeSpan.FromMilliseconds(400),
            $"The owner waited {worstLatency.TotalMilliseconds:F0}ms for the window to answer during a {scan.ElapsedMilliseconds}ms scan.");
    }

    private static bool WaitUntil(Func<bool> condition, int timeoutMilliseconds = 15_000)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds < timeoutMilliseconds)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(25);
        }

        return condition();
    }
}
