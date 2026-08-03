using System.Reflection;
using Vantage.Core.Projection;
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
        Assert.AreNotEqual(
            conflict.First.Provenance.Origin,
            conflict.Second.Provenance.Origin,
            "Two readings of one file still have to be told apart by where in it they were read.");
    }

    /// <summary>
    /// The near-miss for producer three, twice over: a finished ticket whose boxes are all ticked,
    /// and an unfinished one whose boxes are not. Neither is a disagreement.
    /// </summary>
    [TestMethod]
    public async Task Unticked_boxes_on_work_that_does_not_claim_to_be_finished_report_nothing()
    {
        var project = _workspace.NewProject("repo");
        var effort = _workspace.NewEffort(project, "feature");

        _workspace.WriteTicket(
            effort,
            "001.md",
            Fixtures.Ticket("Ship it", "resolved") + "\n- [x] Built\n- [X] Proven\n");

        _workspace.WriteTicket(
            effort,
            "002.md",
            Fixtures.Ticket("Still going", "ready") + "\n- [ ] Built\n- [ ] Proven\n");

        using var harness = NewHarness();
        var view = (await harness.RefreshAsync()).Project("repo");

        Assert.AreEqual(0, view.Conflicts.Count);
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

        _workspace.WriteTicket(effort, "001.md", Fixtures.Ticket("Decide the shape", "resolved") + "\n- [x] Done\n");
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
