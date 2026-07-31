using Vantage.Core;
using Vantage.Core.Projection;
using Vantage.Core.Workflow;
using Vantage.Infrastructure;
using Vantage.Infrastructure.Persistence;
using Vantage.Infrastructure.Settings;
using Vantage.Tests.TestSupport;

namespace Vantage.Tests.RefreshSeam;

[TestClass]
public sealed class ResilienceTests
{
    private WorkspaceFixture _workspace = null!;

    [TestInitialize]
    public void SetUp() => _workspace = new WorkspaceFixture();

    [TestCleanup]
    public void TearDown() => _workspace.Dispose();

    private RefreshHarness NewHarness(FakeProcessRunner? runner = null) =>
        new(_workspace, runner ?? new FakeProcessRunner().GhUnauthenticated(), DateTimeOffset.UtcNow);

    [TestMethod]
    public async Task Observing_a_workspace_changes_nothing_in_it()
    {
        var project = _workspace.NewProject("repo");
        var effort = _workspace.NewEffort(project, "feature");
        _workspace.WriteTicket(effort, "001.md", Fixtures.Ticket("Work", "ready"));
        _workspace.InitGitRepository(project, "https://github.com/acme/widget.git");
        _workspace.Commit(project, "add planning");

        var runner = new FakeProcessRunner().GhAuthenticated().GhIssues(Fixtures.GhIssues());
        using var harness = NewHarness(runner).WithRealGit();

        var contentBefore = WorkspaceFixture.Fingerprint(_workspace.WorkspacesRoot, excludingSegment: ".git");
        var headBefore = _workspace.Git(project, "rev-parse", "HEAD");
        var statusBefore = _workspace.Git(project, "status", "--porcelain");
        var configBefore = _workspace.Git(project, "config", "--get", "remote.origin.url");

        await harness.RefreshAsync();
        await harness.RefreshAsync();

        Assert.AreEqual(contentBefore, WorkspaceFixture.Fingerprint(_workspace.WorkspacesRoot, excludingSegment: ".git"));
        Assert.AreEqual(headBefore, _workspace.Git(project, "rev-parse", "HEAD"));
        Assert.AreEqual(statusBefore, _workspace.Git(project, "status", "--porcelain"));
        Assert.AreEqual(configBefore, _workspace.Git(project, "config", "--get", "remote.origin.url"));
    }

    [TestMethod]
    public async Task Monitored_content_is_data_and_never_reaches_a_command_line()
    {
        var hostile = "Status: ready; rm -rf /\nBlocked by: $(whoami)\n--repo evil/evil\n";
        var project = _workspace.NewProject("repo");
        var effort = _workspace.NewEffort(project, "feature");
        _workspace.WriteTicket(effort, "ticket & $(whoami) ; echo.md", "# Hostile\n\n" + hostile);

        var runner = new FakeProcessRunner().GhUnauthenticated();
        using var harness = NewHarness(runner);

        var snapshot = await harness.RefreshAsync();

        Assert.AreEqual(1, snapshot.Project("repo").Efforts.SelectMany(e => e.Tickets).Count(),
            "Hostile names and bodies are ordinary data and must still be indexed.");

        foreach (var argument in runner.Invocations.SelectMany(i => i.Arguments))
        {
            Assert.IsFalse(argument.Contains("rm -rf", StringComparison.Ordinal), "Monitored content must never be interpolated into a command.");
            Assert.IsFalse(argument.Contains("evil/evil", StringComparison.Ordinal));
            Assert.IsFalse(argument.Contains("$(whoami)", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public async Task A_broken_project_falls_back_to_its_last_known_good_view_and_says_so()
    {
        var project = _workspace.NewProject("repo");
        var effort = _workspace.NewEffort(project, "feature");
        _workspace.WriteTicket(effort, "001.md", Fixtures.Ticket("Work", "ready"));
        _workspace.InitGitRepository(project);
        _workspace.Commit(project, "add planning");

        using var healthy = NewHarness(new FakeProcessRunner().GhUnauthenticated()).WithRealGit();
        var before = (await healthy.RefreshAsync()).Project("repo");
        Assert.AreEqual(1, before.Progress.Total);
        Assert.IsFalse(before.IsStale);
        healthy.Dispose();

        using var broken = NewHarness(new FakeProcessRunner().GhUnauthenticated().Throwing("git", "rev-parse"));
        var after = (await broken.RefreshAsync()).Project("repo");

        Assert.IsTrue(after.IsStale, "A stale view must never be mistaken for a fresh one.");
        Assert.AreEqual(1, after.Progress.Total, "The last thing known to be true stays visible.");
        Assert.IsTrue(after.HasDiagnostic(DiagnosticCode.ProjectScanFailed));
    }

    /// <summary>
    /// A projection stored by an earlier build records a project's disagreements but nothing on
    /// its items. A stale view is still a view the owner has to be able to inspect, so the
    /// attachment has to come back on the way out of the cache rather than waiting for the next
    /// successful refresh.
    /// </summary>
    [TestMethod]
    public void A_snapshot_stored_before_items_carried_their_conflicts_still_offers_them()
    {
        var provenance = new Provenance(EvidenceSource.LocalFile, "001.md", TimestampProvenance.FileSystem, null, "r1");
        var conflict = new ConflictReport(
            "feature/001",
            ConflictField.Title,
            "Local title",
            "Remote title",
            "Local value kept.",
            provenance,
            new Provenance(EvidenceSource.GitHubCli, "acme/widget#7", TimestampProvenance.GitHubApi, null, "r1"));

        var ticket = new TicketView(
            "feature/001",
            "Local title",
            "feature",
            new StatusReading(WorkflowStatus.Ready, "ready"),
            WorkUnitKind.Implementation,
            null,
            true,
            false,
            [],
            null,
            "001.md",
            provenance,
            []);

        // Exactly the shape an earlier build wrote: the project knows about the disagreement, the
        // item it is about does not.
        var legacy = new ProjectView
        {
            Identity = new ProjectIdentity("c:/legacy", "legacy"),
            State = ProjectState.Ready,
            StateReason = "because",
            Progress = new ProgressSummary(0, 1, 0),
            Efforts = [new EffortView("feature", "feature", "feature", false, new ProgressSummary(0, 1, 0), [ticket])],
            Conflicts = [conflict],
        };

        using var cache = DashboardCache.Open(Path.Combine(_workspace.Root, "cache", "legacy-view.db"));
        cache.SaveProjectSnapshot(legacy, DateTimeOffset.UtcNow);

        var (loaded, _) = cache.LoadProjectSnapshot("c:/legacy");

        Assert.IsNotNull(loaded);
        Assert.AreEqual(
            "Remote title",
            loaded.Efforts.Single().Tickets.Single().Conflicts.Single().RemoteValue,
            "A stale item must still offer the disagreement it is the subject of.");
    }

    /// <summary>
    /// A project associated with a repository that could not be read is showing only its local half.
    /// Its counts are not wrong, but they are not the whole answer, and a view that says nothing is
    /// indistinguishable from a complete one.
    /// </summary>
    [TestMethod]
    public async Task A_project_missing_its_github_half_says_so_rather_than_looking_complete()
    {
        var project = _workspace.NewProject("repo");
        var effort = _workspace.NewEffort(project, "feature");
        _workspace.WriteTicket(effort, "001.md", Fixtures.Ticket("Work", "ready"));
        _workspace.InitGitRepository(project, "https://github.com/acme/widget.git");
        _workspace.Commit(project, "add planning");

        using var online = NewHarness(new FakeProcessRunner().GhAuthenticated().GhIssues(Fixtures.GhIssues())).WithRealGit();
        var connected = (await online.RefreshAsync()).Project("repo");
        Assert.IsFalse(connected.IsStale, "Nothing is missing while the session is good.");
        online.Dispose();

        using var lost = NewHarness(new FakeProcessRunner().GhUnauthenticated()).WithRealGit();
        var view = (await lost.RefreshAsync()).Project("repo");

        Assert.AreEqual(1, view.Progress.Total, "The local half is still shown.");
        Assert.IsTrue(view.IsStale, "And it is marked as not being the whole picture.");
        Assert.IsTrue(view.HasDiagnostic(DiagnosticCode.GitHubUnavailable));
    }

    [TestMethod]
    public async Task One_broken_project_does_not_blank_the_others()
    {
        _workspace.NewEffort(_workspace.NewProject("healthy"), "feature");
        _workspace.WriteTicket(
            Path.Combine(_workspace.WorkspacesRoot, "healthy", ".scratch", "feature"),
            "001.md",
            Fixtures.Ticket("Fine", "ready"));

        var broken = _workspace.NewProject("broken");
        Directory.CreateDirectory(Path.Combine(broken, ".git"));

        using var harness = NewHarness(new FakeProcessRunner().GhUnauthenticated().Throwing("git", "rev-parse"));
        var snapshot = await harness.RefreshAsync();

        Assert.AreEqual(2, snapshot.Projects.Count);
        Assert.AreEqual(1, snapshot.Project("healthy").Progress.Total);
    }

    [TestMethod]
    public async Task A_refresh_stops_promptly_when_it_is_cancelled()
    {
        for (var i = 0; i < 20; i++)
        {
            var project = _workspace.NewProject($"project-{i}");
            var effort = _workspace.NewEffort(project, "feature");
            _workspace.WriteTicket(effort, "001.md", Fixtures.Ticket("Work", "ready"));
        }

        using var harness = NewHarness();
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => harness.RefreshAsync(source.Token));
    }

    [TestMethod]
    public async Task A_corrupt_cache_is_rebuilt_rather_than_allowed_to_block_startup()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_workspace.CacheFile)!);
        await File.WriteAllTextAsync(_workspace.CacheFile, "this is not a database");

        _workspace.NewEffort(_workspace.NewProject("repo"), "feature");
        _workspace.WriteTicket(
            Path.Combine(_workspace.WorkspacesRoot, "repo", ".scratch", "feature"),
            "001.md",
            Fixtures.Ticket("Work", "ready"));

        using var harness = NewHarness();
        var snapshot = await harness.RefreshAsync();

        Assert.IsTrue(snapshot.Diagnostics.Any(d => d.Code == DiagnosticCode.CacheRebuilt));
        Assert.AreEqual(1, snapshot.Project("repo").Progress.Total);
    }

    [TestMethod]
    public async Task Activity_expires_after_the_retention_window_while_current_snapshots_survive()
    {
        var project = _workspace.NewProject("repo");
        var effort = _workspace.NewEffort(project, "feature");
        var ticket = _workspace.WriteTicket(effort, "001.md", Fixtures.Ticket("Work", "open"));

        using var harness = NewHarness();
        await harness.RefreshAsync();
        _workspace.WriteFile(ticket, Fixtures.Ticket("Work", "ready"));
        await harness.RefreshAsync();

        Assert.AreEqual(1, harness.Cache.LoadActivity(project, DateTimeOffset.MinValue).Count);

        harness.Clock.Advance(TimeSpan.FromDays(91));
        var snapshot = await harness.RefreshAsync();

        Assert.AreEqual(0, harness.Cache.LoadActivity(project, DateTimeOffset.MinValue).Count, "Activity is retained for 90 days, not forever.");
        Assert.AreEqual(1, harness.Cache.LoadTicketSnapshots(project).Count, "The current snapshot outlives its activity history.");
        Assert.AreEqual(1, snapshot.Project("repo").Progress.Total);
    }

    [TestMethod]
    public void Settings_survive_an_unreadable_file_by_recovering_to_defaults()
    {
        var paths = new AppPaths(Path.Combine(_workspace.Root, "appdata"));
        paths.EnsureCreated();
        File.WriteAllText(paths.SettingsFile, "{ not json");

        var result = new SettingsStore(paths).Load();

        Assert.AreEqual(DashboardSettings.CurrentSchemaVersion, result.Settings.SchemaVersion);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCode.SettingsRecovered));
        Assert.IsTrue(File.Exists(paths.SettingsFile + ".invalid"), "The unreadable file is kept for inspection.");
    }

    [TestMethod]
    public void Settings_round_trip_through_an_atomic_write()
    {
        var paths = new AppPaths(Path.Combine(_workspace.Root, "appdata"));
        var store = new SettingsStore(paths);

        var settings = new DashboardSettings { RecentWindowHours = 12 };
        settings.Ui.SurfaceOpacityPercent = 55;
        settings.Projects.Add(new ProjectRegistryEntry { Path = @"C:\somewhere", Pinned = true });
        store.Save(settings);

        var reloaded = store.Load().Settings;

        Assert.AreEqual(12, reloaded.RecentWindowHours);
        Assert.AreEqual(55, reloaded.Ui.SurfaceOpacityPercent);
        Assert.IsTrue(reloaded.FindProject(@"C:\somewhere")!.Pinned);
    }

    [TestMethod]
    public void A_cache_written_by_a_newer_build_is_rebuilt_rather_than_misread()
    {
        var path = Path.Combine(_workspace.Root, "cache", "future.db");
        using (var cache = DashboardCache.Open(path))
        {
            Assert.AreEqual(0, cache.Diagnostics.Count);
        }

        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA user_version=999;";
            command.ExecuteNonQuery();
        }

        connection.Close();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        using var rebuilt = DashboardCache.Open(path);
        Assert.IsTrue(rebuilt.Diagnostics.Any(d => d.Code == DiagnosticCode.CacheRebuilt));
    }
}
