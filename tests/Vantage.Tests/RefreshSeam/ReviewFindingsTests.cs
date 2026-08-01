using Vantage.Core.Projection;
using Vantage.Infrastructure.Persistence;
using Vantage.Tests.TestSupport;

namespace Vantage.Tests.RefreshSeam;

/// <summary>
/// Behaviour the two-axis review found missing or wrong. Each test names the conclusion the
/// dashboard must not draw.
/// </summary>
[TestClass]
public sealed class ReviewFindingsTests
{
    private WorkspaceFixture _workspace = null!;
    private RefreshHarness _harness = null!;

    [TestInitialize]
    public void SetUp()
    {
        _workspace = new WorkspaceFixture();
        _harness = new RefreshHarness(_workspace, new FakeProcessRunner(), DateTimeOffset.UtcNow).WithRealGit();
    }

    [TestCleanup]
    public void TearDown()
    {
        _harness.Dispose();
        _workspace.Dispose();
    }

    [TestMethod]
    public async Task A_project_with_no_work_units_is_idle_rather_than_complete()
    {
        _workspace.NewProject("ordinary-repo");

        var view = (await _harness.RefreshAsync()).Project("ordinary-repo");

        Assert.AreEqual(ProjectState.Idle, view.State, "Never having tracked work is not the same as having finished it.");
        Assert.AreEqual(0d, view.Progress.Fraction, "An empty denominator is not 100% done.");
    }

    [TestMethod]
    public async Task A_ticket_whose_name_merely_contains_spec_is_still_a_ticket()
    {
        var project = _workspace.NewProject("app");
        var effort = _workspace.NewEffort(project, "feature");
        _workspace.WriteFile(Path.Combine(effort, "spec.md"), "# The effort's spec\n");
        _workspace.WriteTicket(effort, "001-spec.md", Fixtures.Ticket("Write the spec", "ready", type: "planning"));
        _workspace.WriteTicket(effort, "002-map-out-the-api.md", Fixtures.Ticket("Map out the API", "open"));

        var view = (await _harness.RefreshAsync()).Project("app");

        Assert.AreEqual(2, view.Progress.Total, "Only the effort's own spec.md is a planning artifact.");
        Assert.AreEqual("Write the spec", view.Ticket("001-spec").Title);
    }

    /// <summary>
    /// Each kind of change is reported for what it is rather than collapsed into "something
    /// changed". The linked-issue half of this finding went with the remote source; labels,
    /// assignment and blockers are all things a ticket file states about itself, so the finding is
    /// asserted against the file the owner actually edits.
    /// </summary>
    [TestMethod]
    public async Task Labels_assignments_and_blockers_each_report_their_own_kind_of_movement()
    {
        var project = _workspace.NewProject("widget");
        var effort = _workspace.NewEffort(project, "feature");
        _workspace.WriteTicket(effort, "002.md", Fixtures.Ticket("Prerequisite", "open"));

        var ticket = _workspace.WriteTicket(
            effort,
            "001.md",
            Fixtures.Ticket("Build the thing", "ready", labels: "needs-triage"));

        await _harness.RefreshAsync();

        _workspace.WriteFile(ticket, Fixtures.Ticket(
            "Build the thing",
            "ready",
            blockedBy: "002",
            labels: "needs-triage, ready-for-agent",
            assignee: "someone"));

        var view = (await _harness.RefreshAsync()).Project("widget");
        var kinds = view.RecentActivity.Select(a => a.Kind).ToArray();

        CollectionAssert.Contains(kinds, ActivityKind.LabelChanged);
        CollectionAssert.Contains(kinds, ActivityKind.AssignmentChanged);
        CollectionAssert.Contains(kinds, ActivityKind.BlockerChanged);
    }

    [TestMethod]
    public async Task A_changed_map_or_spec_is_movement_without_becoming_a_work_unit()
    {
        var project = _workspace.NewProject("app");
        var effort = _workspace.NewEffort(project, "feature");
        _workspace.WriteMap(effort, "# Map\n\n- [One](issues/001.md)\n");
        _workspace.WriteFile(Path.Combine(effort, "spec.md"), "# Spec\n\nThe original scope.\n");
        _workspace.WriteTicket(effort, "001.md", Fixtures.Ticket("One", "open"));

        var before = (await _harness.RefreshAsync()).Project("app");
        Assert.AreEqual(1, before.Progress.Total, "Planning artifacts are not work units.");

        _workspace.WriteMap(effort, "# Map\n\n- [One](issues/001.md)\n- [Two](issues/002.md)\n");
        _workspace.WriteFile(Path.Combine(effort, "spec.md"), "# Spec\n\nThe scope grew.\n");

        var view = (await _harness.RefreshAsync()).Project("app");
        var kinds = view.RecentActivity.Select(a => a.Kind).ToArray();

        CollectionAssert.Contains(kinds, ActivityKind.MapChanged);
        CollectionAssert.Contains(kinds, ActivityKind.SpecChanged);
        Assert.AreEqual(1, view.Progress.Total);
    }

    [TestMethod]
    public void A_version_one_cache_migrates_forward_without_losing_what_it_held()
    {
        var path = Path.Combine(_workspace.Root, "cache", "legacy.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // A cache exactly as schema 1 wrote it.
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE activity (
                    id INTEGER PRIMARY KEY AUTOINCREMENT, project_path TEXT NOT NULL, occurred_at TEXT NOT NULL,
                    kind TEXT NOT NULL, summary TEXT NOT NULL, ticket_id TEXT NULL, source TEXT NOT NULL,
                    locator TEXT NOT NULL, timestamp_kind TEXT NOT NULL, refresh_id TEXT NOT NULL,
                    UNIQUE (project_path, occurred_at, kind, locator, summary));
                CREATE INDEX ix_activity_project_time ON activity (project_path, occurred_at);
                CREATE TABLE ticket_snapshot (
                    project_path TEXT NOT NULL, ticket_id TEXT NOT NULL, semantic_hash TEXT NOT NULL,
                    title TEXT NOT NULL, raw_status TEXT NOT NULL, is_complete INTEGER NOT NULL,
                    source_path TEXT NOT NULL, PRIMARY KEY (project_path, ticket_id));
                CREATE TABLE project_snapshot (
                    project_path TEXT PRIMARY KEY, payload TEXT NOT NULL, captured_at TEXT NOT NULL);
                INSERT INTO ticket_snapshot VALUES ('legacy-project', 'feature/001', 'hash', 'Kept', 'ready', 0, 'legacy-project/001.md');
                PRAGMA user_version=1;
                """;
            command.ExecuteNonQuery();
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        using var cache = DashboardCache.Open(path);

        Assert.AreEqual(0, cache.Diagnostics.Count, "A migratable cache must be migrated, not discarded.");
        var kept = cache.LoadTicketSnapshots("legacy-project");
        Assert.AreEqual("Kept", kept["feature/001"].Title);
        Assert.AreEqual(string.Empty, kept["feature/001"].Labels, "New columns take their defaults.");
    }
}
