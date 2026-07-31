using Vantage.Core;
using Vantage.Core.Projection;
using Vantage.Tests.TestSupport;

namespace Vantage.Tests.RefreshSeam;

[TestClass]
public sealed class BlockerAndStateTests
{
    private WorkspaceFixture _workspace = null!;
    private RefreshHarness _harness = null!;

    [TestInitialize]
    public void SetUp()
    {
        _workspace = new WorkspaceFixture();
        _harness = new RefreshHarness(_workspace);
    }

    [TestCleanup]
    public void TearDown()
    {
        _harness.Dispose();
        _workspace.Dispose();
    }

    private string Effort(string name = "feature") => _workspace.NewEffort(_workspace.NewProject("app"), name);

    [TestMethod]
    public async Task An_open_blocker_prevents_actionability()
    {
        var effort = Effort();
        _workspace.WriteTicket(effort, "001.md", Fixtures.Ticket("First", "ready"));
        _workspace.WriteTicket(effort, "002.md", Fixtures.Ticket("Second", "ready", blockedBy: "001"));

        var view = (await _harness.RefreshAsync()).Project("app");

        Assert.IsTrue(view.Ticket("001").IsActionable);
        Assert.IsFalse(view.Ticket("002").IsActionable);
        Assert.IsTrue(view.Ticket("002").IsBlocked);
    }

    [TestMethod]
    public async Task A_completed_blocker_releases_the_work_it_was_holding()
    {
        var effort = Effort();
        _workspace.WriteTicket(effort, "001.md", Fixtures.Ticket("First", "done"));
        _workspace.WriteTicket(effort, "002.md", Fixtures.Ticket("Second", "ready", blockedBy: "001"));

        var view = (await _harness.RefreshAsync()).Project("app");

        Assert.IsTrue(view.Ticket("002").IsActionable);
    }

    [TestMethod]
    public async Task Blockers_never_resolve_outside_their_own_effort()
    {
        var project = _workspace.NewProject("app");
        var one = _workspace.NewEffort(project, "alpha");
        var two = _workspace.NewEffort(project, "beta");
        _workspace.WriteTicket(one, "001.md", Fixtures.Ticket("Alpha one", "done"));
        _workspace.WriteTicket(two, "002.md", Fixtures.Ticket("Beta two", "ready", blockedBy: "001"));

        var view = (await _harness.RefreshAsync()).Project("app");

        Assert.IsFalse(view.Ticket("002").IsActionable, "A same-named ticket in another effort must not satisfy a dependency.");
        Assert.IsTrue(view.HasDiagnostic(DiagnosticCode.BlockerMissing));
    }

    [TestMethod]
    public async Task Missing_self_cyclic_and_duplicate_blockers_all_fail_visibly()
    {
        var effort = Effort();
        _workspace.WriteTicket(effort, "001.md", Fixtures.Ticket("Missing", "ready", blockedBy: "999"));
        _workspace.WriteTicket(effort, "002.md", Fixtures.Ticket("Self", "ready", blockedBy: "002"));
        _workspace.WriteTicket(effort, "003.md", Fixtures.Ticket("Cycle A", "ready", blockedBy: "004"));
        _workspace.WriteTicket(effort, "004.md", Fixtures.Ticket("Cycle B", "ready", blockedBy: "003"));
        _workspace.WriteTicket(effort, "005.md", Fixtures.Ticket("Base", "ready"));
        _workspace.WriteTicket(effort, "006.md", Fixtures.Ticket("Duplicate", "ready", blockedBy: "005, 005"));

        var view = (await _harness.RefreshAsync()).Project("app");

        foreach (var id in new[] { "001", "002", "003", "004", "006" })
        {
            Assert.IsFalse(view.Ticket(id).IsActionable, $"'{id}' has an invalid dependency and must not be actionable.");
        }

        foreach (var code in new[]
        {
            DiagnosticCode.BlockerMissing, DiagnosticCode.BlockerSelf,
            DiagnosticCode.BlockerCycle, DiagnosticCode.BlockerDuplicate,
        })
        {
            Assert.IsTrue(view.HasDiagnostic(code), $"Expected a {code} diagnostic.");
        }
    }

    [TestMethod]
    public async Task Claimed_work_makes_the_project_in_progress()
    {
        var effort = Effort();
        _workspace.WriteTicket(effort, "001.md", Fixtures.Ticket("Claimed", "in-progress"));
        _workspace.WriteTicket(effort, "002.md", Fixtures.Ticket("Waiting", "ready"));

        var view = (await _harness.RefreshAsync()).Project("app");

        Assert.AreEqual(ProjectState.InProgress, view.State);
    }

    [TestMethod]
    public async Task Explicit_unblocked_work_makes_the_project_ready()
    {
        var effort = Effort();
        _workspace.WriteTicket(effort, "001.md", Fixtures.Ticket("Available", "ready"));

        var view = (await _harness.RefreshAsync()).Project("app");

        Assert.AreEqual(ProjectState.Ready, view.State);
    }

    [TestMethod]
    public async Task One_blocked_ticket_does_not_make_a_live_project_blocked()
    {
        var effort = Effort();
        _workspace.WriteTicket(effort, "001.md", Fixtures.Ticket("Available", "ready"));
        _workspace.WriteTicket(effort, "002.md", Fixtures.Ticket("Stuck", "blocked"));

        var view = (await _harness.RefreshAsync()).Project("app");

        Assert.AreEqual(ProjectState.Ready, view.State);
    }

    [TestMethod]
    public async Task A_project_is_blocked_only_when_nothing_can_move()
    {
        var effort = Effort();
        _workspace.WriteTicket(effort, "001.md", Fixtures.Ticket("Stuck", "blocked"));
        _workspace.WriteTicket(effort, "002.md", Fixtures.Ticket("Waiting on stuck", "ready", blockedBy: "001"));

        var view = (await _harness.RefreshAsync()).Project("app");

        Assert.AreEqual(ProjectState.Blocked, view.State);
    }

    [TestMethod]
    public async Task Unclear_readiness_is_reported_as_idle_rather_than_guessed()
    {
        var effort = Effort();
        _workspace.WriteTicket(effort, "001.md", Fixtures.Ticket("Open, no map", "open"));

        var view = (await _harness.RefreshAsync()).Project("app");

        Assert.AreEqual(ProjectState.Idle, view.State, "Openness is not readiness outside a map.");
    }

    [TestMethod]
    public async Task A_project_is_complete_only_when_no_work_remains()
    {
        var effort = Effort();
        _workspace.WriteTicket(effort, "001.md", Fixtures.Ticket("Finished", "done"));
        _workspace.WriteTicket(effort, "002.md", Fixtures.Ticket("Also finished", "resolved"));

        var view = (await _harness.RefreshAsync()).Project("app");

        Assert.AreEqual(ProjectState.Complete, view.State);
        Assert.AreEqual(0, view.Progress.Remaining);
    }

    [TestMethod]
    public async Task Every_state_carries_the_reason_that_produced_it()
    {
        var effort = Effort();
        _workspace.WriteTicket(effort, "001.md", Fixtures.Ticket("Available", "ready"));

        var view = (await _harness.RefreshAsync()).Project("app");

        Assert.IsFalse(string.IsNullOrWhiteSpace(view.StateReason));
    }
}
