using MattWorkflowDashboard.Core.Projection;
using MattWorkflowDashboard.Infrastructure.Persistence;
using MattWorkflowDashboard.Tests.TestSupport;

namespace MattWorkflowDashboard.Tests.RefreshSeam;

/// <summary>
/// Behaviour the two-axis review found missing or wrong. Each test names the conclusion the
/// dashboard must not draw.
/// </summary>
[TestClass]
public sealed class ReviewFindingsTests
{
    private const string Origin = "https://github.com/acme/widget.git";

    private WorkspaceFixture _workspace = null!;
    private FakeProcessRunner _runner = null!;
    private RefreshHarness _harness = null!;

    [TestInitialize]
    public void SetUp()
    {
        _workspace = new WorkspaceFixture();
        _runner = new FakeProcessRunner().GhAuthenticated();
        _harness = new RefreshHarness(_workspace, _runner, DateTimeOffset.UtcNow).WithRealGit();
    }

    [TestCleanup]
    public void TearDown()
    {
        _harness.Dispose();
        _workspace.Dispose();
    }

    private string LinkedProject(string ticketBody)
    {
        var project = _workspace.NewProject("widget");
        var effort = _workspace.NewEffort(project, "feature");
        _workspace.WriteTicket(effort, "001.md", ticketBody);
        _workspace.InitGitRepository(project, Origin);
        _workspace.Commit(project, "add planning");
        return project;
    }

    [TestMethod]
    public async Task A_project_with_no_work_units_is_idle_rather_than_complete()
    {
        _workspace.NewProject("ordinary-repo");
        _runner.GhIssues(Fixtures.GhIssues());

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

    [TestMethod]
    public async Task An_effort_disagreement_in_a_linked_issue_body_is_a_conflict()
    {
        LinkedProject(Fixtures.Ticket("Build the thing", "ready", gitHub: "#7"));
        _runner.GhIssues(Fixtures.GhIssues(
            new Fixtures.GhIssue(7, "Build the thing", "OPEN", [], "2026-07-29T10:00:00Z")
            {
                Body = "Effort: something-else\nBlocked by: 099\n",
            }));

        var view = (await _harness.RefreshAsync()).Project("widget");

        CollectionAssert.Contains(view.Conflicts.Select(c => c.Field).ToArray(), ConflictField.Effort);
        Assert.AreEqual("feature", view.Conflicts.Single(c => c.Field == ConflictField.Effort).LocalValue);
    }

    [TestMethod]
    public async Task Blockers_stated_only_in_a_linked_issue_body_are_compared_against_the_local_ones()
    {
        LinkedProject(Fixtures.Ticket("Build the thing", "ready", gitHub: "#7", blockedBy: "002"));
        _workspace.WriteTicket(
            Path.Combine(_workspace.WorkspacesRoot, "widget", ".scratch", "feature"),
            "002.md",
            Fixtures.Ticket("Prerequisite", "done"));

        _runner.GhIssues(Fixtures.GhIssues(
            new Fixtures.GhIssue(7, "Build the thing", "OPEN", [], "2026-07-29T10:00:00Z")
            {
                Body = "Blocked by: 003\n",
            }));

        var view = (await _harness.RefreshAsync()).Project("widget");

        CollectionAssert.Contains(view.Conflicts.Select(c => c.Field).ToArray(), ConflictField.Blockers);
        Assert.IsTrue(view.Ticket("001").IsActionable, "The local blocker is done, and local facts win.");
    }

    [TestMethod]
    public async Task Withdrawn_remote_work_leaves_the_totals_instead_of_counting_as_finished()
    {
        LinkedProject(Fixtures.Ticket("Local work", "ready"));
        _runner.GhIssues(Fixtures.GhIssues(
            new Fixtures.GhIssue(11, "Not doing this", "OPEN", ["wontfix"], "2026-07-29T10:00:00Z")));

        var view = (await _harness.RefreshAsync()).Project("widget");

        Assert.AreEqual(1, view.Progress.Total, "A won't-fix issue is neither remaining work nor completed work.");
        Assert.AreEqual(0, view.Progress.Completed);
    }

    [TestMethod]
    public async Task Labels_assignments_blockers_and_comments_each_report_their_own_kind_of_movement()
    {
        LinkedProject(Fixtures.Ticket("Build the thing", "ready", gitHub: "#7"));

        var payload = Fixtures.GhIssues(
            new Fixtures.GhIssue(7, "Build the thing", "OPEN", ["needs-triage"], "2026-07-29T10:00:00Z"));

        _runner.When((name, args) => name == "gh" && args.Contains("issue"), () => FakeProcessRunner.Ok(payload));
        await _harness.RefreshAsync();

        payload = Fixtures.GhIssues(
            new Fixtures.GhIssue(7, "Build the thing", "OPEN", ["needs-triage", "ready-for-agent"], DateTimeOffset.UtcNow.ToString("O"))
            {
                Assignees = ["someone"],
                Comments = 2,
            });

        var view = (await _harness.RefreshAsync()).Project("widget");
        var kinds = view.RecentActivity.Select(a => a.Kind).ToArray();

        CollectionAssert.Contains(kinds, ActivityKind.LabelChanged);
        CollectionAssert.Contains(kinds, ActivityKind.AssignmentChanged);
        CollectionAssert.Contains(kinds, ActivityKind.CommentAdded);
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
