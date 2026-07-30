using MattWorkflowDashboard.Core.Projection;
using MattWorkflowDashboard.Tests.TestSupport;

namespace MattWorkflowDashboard.Tests.RefreshSeam;

[TestClass]
public sealed class NextActionTests
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

    [TestMethod]
    public async Task The_wayfinder_frontier_decides_the_next_action_when_a_map_exists()
    {
        var effort = _workspace.NewEffort(_workspace.NewProject("app"), "feature");
        _workspace.WriteMap(effort, "# Map\n\n1. [Third](issues/003.md)\n2. [First](issues/001.md)\n3. [Second](issues/002.md)\n");
        _workspace.WriteTicket(effort, "001.md", Fixtures.Ticket("First", "open"));
        _workspace.WriteTicket(effort, "002.md", Fixtures.Ticket("Second", "open"));
        _workspace.WriteTicket(effort, "003.md", Fixtures.Ticket("Third", "open", blockedBy: "001"));

        var view = (await _harness.RefreshAsync()).Project("app");

        Assert.AreEqual("feature/001", view.NextAction!.TicketId, "The map's first unblocked item wins, not simply its first item.");
        Assert.AreEqual(NextActionSource.WayfinderFrontier, view.NextAction.Source);
    }

    [TestMethod]
    public async Task An_ordinary_open_ticket_is_actionable_only_inside_a_map_context()
    {
        var project = _workspace.NewProject("app");
        var mapped = _workspace.NewEffort(project, "mapped");
        var unmapped = _workspace.NewEffort(project, "unmapped");

        _workspace.WriteMap(mapped, "# Map\n\n- [Mapped](issues/010.md)\n");
        _workspace.WriteTicket(mapped, "010.md", Fixtures.Ticket("Mapped open", "open"));
        _workspace.WriteTicket(unmapped, "020.md", Fixtures.Ticket("Unmapped open", "open"));

        var view = (await _harness.RefreshAsync()).Project("app");

        Assert.IsTrue(view.Ticket("010").IsActionable);
        Assert.IsFalse(view.Ticket("020").IsActionable);
    }

    [TestMethod]
    public async Task Explicit_ready_work_is_the_fallback_when_no_map_is_present()
    {
        var effort = _workspace.NewEffort(_workspace.NewProject("app"), "feature");
        _workspace.WriteTicket(effort, "001.md", Fixtures.Ticket("Not ready", "open"));
        _workspace.WriteTicket(effort, "002.md", Fixtures.Ticket("Ready", "ready"));

        var view = (await _harness.RefreshAsync()).Project("app");

        Assert.AreEqual("feature/002", view.NextAction!.TicketId);
        Assert.AreEqual(NextActionSource.ReadyFallback, view.NextAction.Source);
    }

    [TestMethod]
    public async Task Claimed_work_is_not_offered_as_the_next_action()
    {
        var effort = _workspace.NewEffort(_workspace.NewProject("app"), "feature");
        _workspace.WriteTicket(effort, "001.md", Fixtures.Ticket("Taken", "in-progress"));

        var view = (await _harness.RefreshAsync()).Project("app");

        Assert.IsNull(view.NextAction, "Work someone already holds is not the next thing to pick up.");
    }

    [TestMethod]
    public async Task The_next_action_explains_itself_and_points_at_its_source()
    {
        var effort = _workspace.NewEffort(_workspace.NewProject("app"), "feature");
        _workspace.WriteTicket(effort, "001.md", Fixtures.Ticket("Ready", "ready"));

        var action = (await _harness.RefreshAsync()).Project("app").NextAction!;

        Assert.IsFalse(string.IsNullOrWhiteSpace(action.Reason));
        StringAssert.Contains(action.Provenance.Locator, "001");
    }
}
