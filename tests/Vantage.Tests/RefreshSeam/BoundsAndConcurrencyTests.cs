using System.Diagnostics;
using Vantage.Core;
using Vantage.Infrastructure.Processes;
using Vantage.Tests.TestSupport;

namespace Vantage.Tests.RefreshSeam;

/// <summary>
/// The operational bounds the dashboard promises under a scan large enough to need them:
/// traversal that stops where it was told to, external processes that stay within their global
/// cap, one index pass per project at a time, and one broken project that costs only itself.
/// Every tree here is a disposable sanitized fixture; nothing points at a live project.
/// </summary>
[TestClass]
public sealed class BoundsAndConcurrencyTests
{
    private WorkspaceFixture _workspace = null!;

    [TestInitialize]
    public void SetUp() => _workspace = new WorkspaceFixture();

    [TestCleanup]
    public void TearDown() => _workspace.Dispose();

    private RefreshHarness NewHarness() =>
        new(_workspace, new FakeProcessRunner(), DateTimeOffset.UtcNow);

    [TestMethod]
    public async Task Traversal_stops_at_the_directory_bound_rather_than_walking_a_large_root()
    {
        _workspace.NewTree(directories: 400, projectsEvery: 40);

        using var harness = NewHarness();
        harness.Settings.MaxDirectoriesScanned = 60;

        var snapshot = await harness.RefreshAsync();

        Assert.IsTrue(snapshot.ScanTruncated, "A scan that hit its bound must say so rather than look complete.");
        Assert.IsTrue(
            snapshot.Diagnostics.Any(d => d.Code == DiagnosticCode.ScanTruncated),
            "The owner is told which bound stopped the scan and where to raise it.");
    }

    [TestMethod]
    public async Task Traversal_stops_at_the_project_bound()
    {
        _workspace.NewTree(directories: 200, projectsEvery: 2);

        using var harness = NewHarness();
        harness.Settings.MaxProjects = 5;

        var snapshot = await harness.RefreshAsync();

        Assert.IsTrue(snapshot.ScanTruncated);
        Assert.IsTrue(
            snapshot.Projects.Count <= 5,
            $"The project bound is a bound: {snapshot.Projects.Count} projects came back for a limit of 5.");
    }

    [TestMethod]
    public async Task Traversal_never_goes_deeper_than_the_configured_depth()
    {
        var deep = Path.Combine(_workspace.WorkspacesRoot, "a", "b", "c", "deep");
        Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Combine(deep, "AGENTS.md"), "# Agents\n");

        var shallow = _workspace.NewProject("shallow");
        Assert.IsTrue(Directory.Exists(shallow));

        using var harness = NewHarness();
        harness.Settings.MaxDiscoveryDepth = 1;

        var snapshot = await harness.RefreshAsync();

        Assert.IsTrue(snapshot.Projects.Any(p => p.Identity.Name == "shallow"));
        Assert.IsFalse(
            snapshot.Projects.Any(p => p.Identity.Name == "deep"),
            "A project below the configured depth is out of reach, not silently included.");
    }

    /// <summary>
    /// The global cap is on processes that are actually running, so it has to be measured inside
    /// the runner rather than at its door — a caller waiting on the semaphore is not a child
    /// process. The same workload is run twice at two different caps: without the second run,
    /// a peak of two would prove nothing about whether the cap is what produced it.
    /// </summary>
    [TestMethod]
    public async Task No_more_external_processes_run_at_once_than_the_owner_allowed()
    {
        const int Tasks = 6;

        using var capped = new BoundedProcessRunner(2, TimeSpan.FromSeconds(30));
        using var loose = new BoundedProcessRunner(Tasks, TimeSpan.FromSeconds(30));

        var cappedResults = await RunSlowCommandsAsync(capped, Tasks);
        var looseResults = await RunSlowCommandsAsync(loose, Tasks);

        Assert.IsTrue(cappedResults.All(r => r.Succeeded), "The bound queues work; it does not fail it.");
        Assert.IsTrue(looseResults.All(r => r.Succeeded));

        Assert.IsTrue(
            loose.PeakConcurrentProcesses > 2,
            $"The workload has to be able to exceed the cap, or the capped run proves nothing. Peak was {loose.PeakConcurrentProcesses}.");

        Assert.IsTrue(
            capped.PeakConcurrentProcesses <= 2,
            $"A cap of 2 let {capped.PeakConcurrentProcesses} processes run at once.");
    }

    /// <summary>
    /// Overlapping refreshes must not interleave for one project: a snapshot assembled from two
    /// half-finished passes is internally inconsistent. Each pass is two git calls, so
    /// interleaving is visible as those calls arriving out of their per-project order.
    /// </summary>
    [TestMethod]
    public async Task One_project_is_never_indexed_by_two_refreshes_at_once()
    {
        for (var i = 0; i < 4; i++)
        {
            var project = _workspace.NewProject($"repo-{i}");
            var effort = _workspace.NewEffort(project, "feature");
            _workspace.WriteTicket(effort, "001.md", Fixtures.Ticket("Work", "ready"));
            Directory.CreateDirectory(Path.Combine(project, ".git"));
        }

        var runner = new FakeProcessRunner()
            .Slow("git", "--is-inside-work-tree", TimeSpan.FromMilliseconds(20), "true")
            .Slow("git", "log", TimeSpan.FromMilliseconds(20));

        using var harness = new RefreshHarness(_workspace, runner, DateTimeOffset.UtcNow);
        var service = harness.Service;

        await Task.WhenAll(service.RefreshAsync(default), service.RefreshAsync(default));

        foreach (var project in runner.Invocations
            .Where(i => i.FileName == "git" && i.WorkingDirectory is not null)
            .GroupBy(i => i.WorkingDirectory!, StringComparer.OrdinalIgnoreCase))
        {
            var verbs = project
                .OrderBy(i => i.StartedAt)
                .Select(Verb)
                .ToList();

            Assert.AreEqual(
                4,
                verbs.Count,
                $"Each of the two passes over '{Path.GetFileName(project.Key)}' makes two calls.");

            CollectionAssert.AreEqual(
                new[] { "probe", "log", "probe", "log" },
                verbs,
                $"The two passes over '{Path.GetFileName(project.Key)}' interleaved: {string.Join(", ", verbs)}");
        }

        static string Verb(ProcessInvocation invocation) =>
            invocation.Arguments.Contains("--is-inside-work-tree") ? "probe" : "log";
    }

    [TestMethod]
    public async Task A_large_scan_survives_a_broken_project_with_every_other_one_intact()
    {
        for (var i = 0; i < 40; i++)
        {
            var project = _workspace.NewProject($"repo-{i:D2}");
            var effort = _workspace.NewEffort(project, "feature");
            _workspace.WriteTicket(effort, "001.md", Fixtures.Ticket("Work", "ready"));
        }

        var broken = _workspace.NewProject("broken");
        Directory.CreateDirectory(Path.Combine(broken, ".git"));

        using var harness = new RefreshHarness(
            _workspace,
            new FakeProcessRunner().Throwing("git", "rev-parse"),
            DateTimeOffset.UtcNow);

        var snapshot = await harness.RefreshAsync();

        Assert.AreEqual(41, snapshot.Projects.Count);
        Assert.AreEqual(
            40,
            snapshot.Projects.Count(p => p.Identity.Name != "broken" && p.Progress.Total == 1),
            "One unreadable repository costs only itself.");
        Assert.IsTrue(snapshot.Project("broken").HasDiagnostic(DiagnosticCode.ProjectScanFailed));
    }

    /// <summary>
    /// A child process that takes long enough for concurrency to be a real question. Ping against
    /// the loopback address is the shortest way to get a bounded, non-interactive delay out of a
    /// tool present on every platform this runs on; only the flag naming the count differs.
    /// </summary>
    private static async Task<IReadOnlyList<ProcessResult>> RunSlowCommandsAsync(IProcessRunner runner, int count)
    {
        string[] twoPings = OperatingSystem.IsWindows()
            ? ["-n", "2", "127.0.0.1"]
            : ["-c", "2", "127.0.0.1"];

        var started = Stopwatch.StartNew();
        var results = await Task.WhenAll(Enumerable.Range(0, count).Select(_ =>
            runner.RunAsync("ping", twoPings, null, CancellationToken.None)));

        Assert.IsTrue(started.Elapsed > TimeSpan.FromMilliseconds(500), "The workload must not be instantaneous.");
        return results;
    }
}
