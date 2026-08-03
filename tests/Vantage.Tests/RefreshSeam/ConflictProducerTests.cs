using System.Reflection;
using Vantage.Core;
using Vantage.Core.Projection;
using Vantage.Infrastructure.Persistence;
using Vantage.Tests.TestSupport;

namespace Vantage.Tests.RefreshSeam;

/// <summary>
/// What now produces a disagreement, and — just as much the point — what does not. A conflict the
/// owner cannot act on is worse than one that goes unreported, because it teaches them to stop
/// reading the badge, so every producer here is paired with the near-miss it has to stay silent on.
/// </summary>
[TestClass]
public sealed class ConflictProducerTests
{
    private WorkspaceFixture _workspace = null!;

    [TestInitialize]
    public void SetUp() => _workspace = new WorkspaceFixture();

    [TestCleanup]
    public void TearDown() => _workspace.Dispose();

    private RefreshHarness NewHarness() =>
        new RefreshHarness(_workspace, new FakeProcessRunner(), DateTimeOffset.UtcNow).WithRealGit();

    private static ConflictReport Single(Vantage.Core.Projection.ProjectView project, ConflictField field) =>
        project.Conflicts.SingleOrDefault(c => c.Field == field)
        ?? throw new AssertFailedException(
            $"No {field} conflict. Found: {string.Join(", ", project.Conflicts.Select(c => $"{c.Field} {c.First.Value}/{c.Second.Value}"))}");

    /// <summary>
    /// The whole acceptance claim of the rename, asserted structurally rather than by reading the
    /// source: a model whose members still say local, remote, or GitHub is a model that still
    /// describes the one producer it was built for.
    /// </summary>
    [TestMethod]
    public void No_type_or_member_of_the_conflict_model_names_a_local_a_remote_or_github()
    {
        string[] forbidden = ["local", "remote", "github"];

        var members = new List<string>();

        foreach (var type in new[] { typeof(ConflictReport), typeof(ObservedValue), typeof(ConflictField) })
        {
            members.Add(type.Name);
            members.AddRange(type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Select(m => m.Name));
        }

        var offending = members
            .Where(name => forbidden.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .Distinct()
            .ToList();

        Assert.AreEqual(0, offending.Count, $"Named after a producer rather than after what they are: {string.Join(", ", offending)}");
    }

    /// <summary>
    /// Producer one. The ticket file on disk says one thing and the last commit says another, and
    /// the difference matters because every other checkout — another worktree, another agent, CI —
    /// still reads the committed value.
    /// </summary>
    [TestMethod]
    public async Task A_ticket_edited_but_not_committed_disagrees_with_what_the_last_commit_recorded()
    {
        var project = _workspace.NewProject("repo");
        var effort = _workspace.NewEffort(project, "feature");
        var ticket = Path.Combine(effort, "issues", "001.md");

        _workspace.WriteTicket(effort, "001.md", Fixtures.Ticket("Draw the grip", "ready"));
        _workspace.InitGitRepository(project);
        _workspace.Commit(project, "add planning");

        // Uncommitted, and therefore invisible to anybody else.
        File.WriteAllText(ticket, Fixtures.Ticket("Draw a solid grip", "in progress"));

        using var harness = NewHarness();
        var view = (await harness.RefreshAsync()).Project("repo");

        var status = Single(view, ConflictField.WorkflowStatus);
        Assert.AreEqual("in progress", status.First.Value);
        Assert.AreEqual("ready", status.Second.Value);
        StringAssert.Contains(status.First.Provenance.Origin, "working tree");
        StringAssert.Contains(status.Second.Provenance.Origin, "last commit");

        var title = Single(view, ConflictField.Title);
        Assert.AreEqual("Draw a solid grip", title.First.Value);
        Assert.AreEqual("Draw the grip", title.Second.Value);

        Assert.AreEqual(
            view.Conflicts.Count,
            view.Ticket("001").Conflicts.Count,
            "A disagreement has to travel with the item it is about, not only sit on the project.");
    }

    /// <summary>
    /// The near-miss for producer one: work in progress is not a disagreement. An uncommitted edit
    /// to a ticket's prose changes nothing the dashboard concluded from it.
    /// </summary>
    [TestMethod]
    public async Task An_uncommitted_edit_that_changes_no_stated_fact_reports_nothing()
    {
        var project = _workspace.NewProject("repo");
        var effort = _workspace.NewEffort(project, "feature");
        var ticket = Path.Combine(effort, "issues", "001.md");

        _workspace.WriteTicket(effort, "001.md", Fixtures.Ticket("Draw the grip", "ready"));
        _workspace.InitGitRepository(project);
        _workspace.Commit(project, "add planning");

        File.WriteAllText(ticket, Fixtures.Ticket("Draw the grip", "ready") + "\nA paragraph of new prose.\n");

        using var harness = NewHarness();
        var view = (await harness.RefreshAsync()).Project("repo");

        Assert.AreEqual(
            0,
            view.Conflicts.Count,
            $"Editing a ticket is not disagreeing with it: {string.Join(", ", view.Conflicts.Select(c => c.Field.ToString()))}");
    }

    /// <summary>
    /// Producer two. The ticket says it is waiting; the ticket it names says it is finished. The
    /// owner's frontier is wrong by one item, and nothing else in the dashboard says so — the
    /// item simply reads as blocked.
    /// </summary>
    [TestMethod]
    public async Task A_ticket_that_still_says_blocked_over_finished_work_disagrees_with_what_it_names()
    {
        var project = _workspace.NewProject("repo");
        var effort = _workspace.NewEffort(project, "feature");

        _workspace.WriteTicket(effort, "001.md", Fixtures.Ticket("Decide the shape", "resolved"));
        _workspace.WriteTicket(effort, "002.md", Fixtures.Ticket("Build it", "blocked", blockedBy: "001"));

        using var harness = NewHarness();
        var view = (await harness.RefreshAsync()).Project("repo");

        var conflict = Single(view, ConflictField.Blockers);
        Assert.AreEqual("feature/002", conflict.TicketId);
        StringAssert.Contains(conflict.First.Value, "blocked");
        StringAssert.Contains(conflict.Second.Value, "Decide the shape");
        StringAssert.Contains(conflict.Second.Value, "resolved");

        // Each side is named by its own evidence, and here that is the only thing telling two
        // observations of the same kind apart.
        StringAssert.Contains(conflict.First.Provenance.Origin, "002.md", StringComparison.OrdinalIgnoreCase);
        StringAssert.Contains(conflict.Second.Provenance.Origin, "001.md", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The near-miss for producer two: a ticket waiting on work that really is unfinished is
    /// simply blocked, which the dashboard already reports as its state.
    /// </summary>
    [TestMethod]
    public async Task A_ticket_blocked_by_work_that_is_not_finished_reports_nothing()
    {
        var project = _workspace.NewProject("repo");
        var effort = _workspace.NewEffort(project, "feature");

        _workspace.WriteTicket(effort, "001.md", Fixtures.Ticket("Decide the shape", "ready"));
        _workspace.WriteTicket(effort, "002.md", Fixtures.Ticket("Build it", "blocked", blockedBy: "001"));

        using var harness = NewHarness();
        var view = (await harness.RefreshAsync()).Project("repo");

        Assert.AreEqual(0, view.Conflicts.Count);
    }

    /// <summary>
    /// Producer three. Completion is the status that ends an item's life in every count the
    /// dashboard makes, so a file claiming it over its own open boxes disagrees with itself where
    /// it costs most.
    /// </summary>
    [TestMethod]
    public async Task A_ticket_that_calls_itself_finished_over_unticked_boxes_disagrees_with_its_own_evidence()
    {
        var project = _workspace.NewProject("repo");
        var effort = _workspace.NewEffort(project, "feature");

        _workspace.WriteTicket(
            effort,
            "001.md",
            Fixtures.Ticket("Ship it", "resolved") + "\n## Acceptance\n\n- [x] Built\n- [ ] Proven\n");

        using var harness = NewHarness();
        var view = (await harness.RefreshAsync()).Project("repo");

        var conflict = Single(view, ConflictField.WorkflowStatus);
        Assert.AreEqual("resolved", conflict.First.Value);
        StringAssert.Contains(conflict.Second.Value, "1 of 2");
        StringAssert.Contains(conflict.Second.Value, "acceptance");
        Assert.AreNotEqual(
            conflict.First.Provenance.Origin,
            conflict.Second.Provenance.Origin,
            "Two readings of one file still have to be told apart by where in it they were read.");
    }

    /// <summary>
    /// The near-miss for producer three, three ways: a finished ticket whose acceptance boxes are
    /// all ticked, an unfinished one whose boxes are not, and — the one a whole document scan gets
    /// wrong — a finished ticket carrying open boxes that are not acceptance at all. A ticket that
    /// records deferred work under its own heading is not contradicting its status.
    /// </summary>
    [TestMethod]
    public async Task Unticked_boxes_that_are_not_acceptance_report_nothing()
    {
        var project = _workspace.NewProject("repo");
        var effort = _workspace.NewEffort(project, "feature");

        _workspace.WriteTicket(
            effort,
            "001.md",
            Fixtures.Ticket("Ship it", "resolved") + "\n## Acceptance\n\n- [x] Built\n- [X] Proven\n");

        _workspace.WriteTicket(
            effort,
            "002.md",
            Fixtures.Ticket("Still going", "ready") + "\n## Acceptance\n\n- [ ] Built\n- [ ] Proven\n");

        _workspace.WriteTicket(
            effort,
            "003.md",
            Fixtures.Ticket("Shipped, with follow-ups", "resolved")
            + "\n## Acceptance\n\n- [x] Built\n\n## Deferred\n\n- [ ] The nice-to-have\n- [ ] The other one\n");

        using var harness = NewHarness();
        var view = (await harness.RefreshAsync()).Project("repo");

        Assert.AreEqual(
            0,
            view.Conflicts.Count,
            $"Reported: {string.Join(", ", view.Conflicts.Select(c => $"{c.TicketId} {c.Second.Value}"))}");
    }

    /// <summary>
    /// A satisfied edge is one row per edge, because a value assembled from three files cannot be
    /// attributed to anywhere a reader could go and look — which is the property that separates a
    /// conflict from a warning.
    /// </summary>
    [TestMethod]
    public async Task Each_satisfied_edge_is_reported_against_the_file_that_states_it()
    {
        var project = _workspace.NewProject("repo");
        var effort = _workspace.NewEffort(project, "feature");

        _workspace.WriteTicket(effort, "001.md", Fixtures.Ticket("Decide the shape", "resolved"));
        _workspace.WriteTicket(effort, "002.md", Fixtures.Ticket("Prepare the way", "done"));
        _workspace.WriteTicket(effort, "003.md", Fixtures.Ticket("Build it", "blocked", blockedBy: "001, 002"));

        using var harness = NewHarness();
        var view = (await harness.RefreshAsync()).Project("repo");

        var edges = view.Conflicts.Where(c => c.Field == ConflictField.Blockers).ToList();
        Assert.AreEqual(2, edges.Count, "Two finished edges are two disagreements, not one summary of both.");

        CollectionAssert.AreEquivalent(
            new[] { "'Decide the shape' is resolved", "'Prepare the way' is done" },
            edges.Select(e => e.Second.Value).ToArray());

        foreach (var edge in edges)
        {
            var subject = edge.Second.Value.Split('\'')[1];
            var file = subject == "Decide the shape" ? "001.md" : "002.md";

            StringAssert.Contains(
                edge.Second.Provenance.Origin,
                file,
                "Each side has to point at the one file it was read from.",
                StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// A producer that could not run says so. `git status` can fail on its own — a corrupt index is
    /// enough — while the probe and the history read both succeed, and evidence that is quietly
    /// missing a producer must never be presented as complete.
    /// </summary>
    [TestMethod]
    public async Task A_working_tree_git_could_not_report_is_named_rather_than_passed_over()
    {
        var project = _workspace.NewProject("repo");
        var effort = _workspace.NewEffort(project, "feature");

        _workspace.WriteTicket(effort, "001.md", Fixtures.Ticket("Draw the grip", "ready"));
        _workspace.InitGitRepository(project);
        _workspace.Commit(project, "add planning");

        File.WriteAllText(Path.Combine(effort, "issues", "001.md"), Fixtures.Ticket("Draw a solid grip", "in progress"));

        // Only the working-tree read fails; the probe and the history read are the real ones.
        var runner = new FakeProcessRunner()
            .When(
                (name, args) => name == "git" && args.Contains("status"),
                () => new Vantage.Infrastructure.Processes.ProcessResult(
                    128, string.Empty, "fatal: index file corrupt", false, false));

        using var harness = new RefreshHarness(_workspace, runner, DateTimeOffset.UtcNow).WithRealGit();
        var view = (await harness.RefreshAsync()).Project("repo");

        Assert.AreEqual(
            0,
            view.Conflicts.Count,
            "Precondition: the comparison cannot be made, so there is nothing for it to report.");

        Assert.IsTrue(
            view.Diagnostics.Any(d => d.Code == DiagnosticCode.GitUnavailable
                && d.Message.Contains("uncommitted", StringComparison.OrdinalIgnoreCase)),
            $"The outage has to be visible: {string.Join(" | ", view.Diagnostics.Select(d => d.Code + " " + d.Message))}");
    }

    /// <summary>
    /// The file cap bounds how many processes start; it does not bound how many bytes one hands
    /// back. A ticket that is small now can have a very large blob at HEAD, and reading it whole
    /// would exhaust memory on a repository that satisfies every other limit.
    /// </summary>
    [TestMethod]
    public async Task A_committed_copy_too_large_to_read_is_skipped_and_reported()
    {
        var project = _workspace.NewProject("repo");
        var effort = _workspace.NewEffort(project, "feature");
        var ticket = Path.Combine(effort, "issues", "001.md");

        // Committed large, edited small: the working copy passes the indexer's own size limit.
        var padding = new string('x', 1_200_000);
        _workspace.WriteTicket(effort, "001.md", Fixtures.Ticket("Draw the grip", "ready") + "\n" + padding + "\n");
        _workspace.InitGitRepository(project);
        _workspace.Commit(project, "add planning");

        File.WriteAllText(ticket, Fixtures.Ticket("Draw a solid grip", "in progress"));

        using var harness = NewHarness();
        var view = (await harness.RefreshAsync()).Project("repo");

        Assert.AreEqual(0, view.Conflicts.Count, "The committed side was never read, so nothing can be claimed about it.");

        Assert.IsTrue(
            view.HasDiagnostic(DiagnosticCode.ConflictScanTruncated),
            $"A skipped comparison has to be said out loud: {string.Join(" | ", view.Diagnostics.Select(d => d.Code))}");
    }

    /// <summary>
    /// The cost of the rename, paid where it falls. A projection stored by an earlier build names
    /// a conflict's sides local and remote, and this build would read neither — leaving a
    /// disagreement on screen with two blank halves. A stored projection is the one thing in the
    /// cache the next refresh rebuilds in full, so it is dropped; activity, which cannot be
    /// rederived, has to survive the same migration.
    /// </summary>
    [TestMethod]
    public void A_projection_stored_under_the_old_side_names_is_dropped_rather_than_read_back_blank()
    {
        var path = Path.Combine(_workspace.Root, "cache", "schema3.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        const string LegacyPayload = """
            {"Identity":{"CanonicalPath":"c:/legacy","Name":"legacy"},"State":"Ready","StateReason":"because",
             "Progress":{"Completed":0,"Total":1,"Excluded":0},"Efforts":[],
             "Conflicts":[{"TicketId":"feature/001","Field":"Title","LocalValue":"Working tree title",
               "RemoteValue":"Committed title","Resolution":"Working-tree value kept.",
               "LocalProvenance":{"Source":"LocalFile","Locator":"001.md","TimestampKind":"FileSystem","RefreshId":"r1"},
               "RemoteProvenance":{"Source":"LocalGit","Locator":"001.md@HEAD","TimestampKind":"GitCommit","RefreshId":"r1"}}]}
            """;

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
                    source_path TEXT NOT NULL, labels TEXT NOT NULL DEFAULT '', assignees TEXT NOT NULL DEFAULT '',
                    blockers TEXT NOT NULL DEFAULT '', comment_count INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (project_path, ticket_id));
                CREATE TABLE project_snapshot (
                    project_path TEXT PRIMARY KEY, payload TEXT NOT NULL, captured_at TEXT NOT NULL);
                CREATE TABLE artifact_snapshot (
                    project_path TEXT NOT NULL, path TEXT NOT NULL, kind TEXT NOT NULL,
                    semantic_hash TEXT NOT NULL, PRIMARY KEY (project_path, path));
                INSERT INTO activity (project_path, occurred_at, kind, summary, ticket_id, source, locator, timestamp_kind, refresh_id)
                    VALUES ('c:/legacy', '2026-07-30T09:00:00.0000000+00:00', 'LocalCommit', 'A commit nobody can rederive',
                            NULL, 'LocalGit', 'c:/legacy#abc123', 'GitCommit', 'r0');
                PRAGMA user_version=3;
                """;
            command.ExecuteNonQuery();
            command.CommandText = "INSERT INTO project_snapshot VALUES ('c:/legacy', $payload, '2026-07-30T09:00:00.0000000+00:00');";
            command.Parameters.AddWithValue("$payload", LegacyPayload);
            command.ExecuteNonQuery();
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        using var cache = DashboardCache.Open(path);

        Assert.AreEqual(0, cache.Diagnostics.Count, "A migratable cache must be migrated, not discarded.");

        var (view, _) = cache.LoadProjectSnapshot("c:/legacy");
        Assert.IsNull(view, "A projection whose sides this build cannot read must not be offered as last-known-good.");

        var activity = cache.LoadActivity("c:/legacy", DateTimeOffset.Parse("2026-07-01T00:00:00Z"));
        Assert.AreEqual(1, activity.Count, "History is not derived from anything, so a migration must never drop it.");
    }

    /// <summary>
    /// Every producer at once, on a workspace where nothing disagrees. Silence is the state the
    /// dashboard is in nearly all the time, and a badge that is ever-present is a badge nobody
    /// reads.
    /// </summary>
    [TestMethod]
    public async Task A_workspace_where_nothing_disagrees_raises_no_conflict_at_all()
    {
        var project = _workspace.NewProject("repo");
        var effort = _workspace.NewEffort(project, "feature");

        _workspace.WriteTicket(
            effort,
            "001.md",
            Fixtures.Ticket("Decide the shape", "resolved") + "\n## Acceptance\n\n- [x] Done\n");
        _workspace.WriteTicket(effort, "002.md", Fixtures.Ticket("Build it", "blocked", blockedBy: "003"));
        _workspace.WriteTicket(effort, "003.md", Fixtures.Ticket("Prepare the way", "in progress"));

        _workspace.InitGitRepository(project);
        _workspace.Commit(project, "add planning");

        using var harness = NewHarness();
        var view = (await harness.RefreshAsync()).Project("repo");

        Assert.AreEqual(
            0,
            view.Conflicts.Count,
            $"Reported: {string.Join(", ", view.Conflicts.Select(c => $"{c.Field} {c.First.Value}/{c.Second.Value}"))}");
    }
}
