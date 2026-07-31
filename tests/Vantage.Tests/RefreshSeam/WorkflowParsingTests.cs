using Vantage.Core;
using Vantage.Core.Workflow;
using Vantage.Tests.TestSupport;

namespace Vantage.Tests.RefreshSeam;

[TestClass]
public sealed class WorkflowParsingTests
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
    public async Task Accepts_plain_and_bold_metadata_syntax()
    {
        var effort = _workspace.NewEffort(_workspace.NewProject("app"), "feature");
        _workspace.WriteTicket(effort, "001-plain.md", Fixtures.Ticket("Plain", "ready"));
        _workspace.WriteTicket(effort, "002-bold.md", Fixtures.Ticket("Bold", "in-progress", bold: true));

        var project = (await _harness.RefreshAsync()).Project("app");

        Assert.AreEqual(WorkflowStatus.Ready, project.Ticket("001-plain").Status.Status);
        Assert.AreEqual(WorkflowStatus.InProgress, project.Ticket("002-bold").Status.Status);
    }

    [TestMethod]
    public async Task Recognizes_effort_artifacts_regardless_of_filename_casing()
    {
        var project = _workspace.NewProject("app");
        var effort = _workspace.NewEffort(project, "feature");
        _workspace.WriteFile(Path.Combine(effort, "MAP.md"), "# Map\n\n- [One](issues/001-one.md)\n");
        _workspace.WriteFile(Path.Combine(effort, "Spec.MD"), "# Spec\n");
        _workspace.WriteFile(Path.Combine(effort, "prd.Md"), "# PRD\n");
        _workspace.WriteTicket(effort, "001-one.md", Fixtures.Ticket("One", "ready"));

        var view = (await _harness.RefreshAsync()).Project("app");
        var featureEffort = view.Efforts.Single(e => e.Name == "feature");

        Assert.IsTrue(featureEffort.IsWayfinderContext, "A map named MAP.md must still be a map.");
        Assert.AreEqual(1, featureEffort.Tickets.Count, "Map, spec, and PRD are planning artifacts, not tickets.");
    }

    [TestMethod]
    public async Task Directory_without_planning_artifacts_is_not_an_effort()
    {
        var project = _workspace.NewProject("app");
        Directory.CreateDirectory(Path.Combine(project, ".scratch", "notes"));
        _workspace.WriteFile(Path.Combine(project, ".scratch", "notes", "thought.txt"), "not markdown");

        var view = (await _harness.RefreshAsync()).Project("app");

        Assert.AreEqual(0, view.Efforts.Count);
    }

    [TestMethod]
    public async Task Recognizes_resolved_and_done_as_completion()
    {
        var effort = _workspace.NewEffort(_workspace.NewProject("app"), "feature");
        _workspace.WriteTicket(effort, "001.md", Fixtures.Ticket("First", "resolved"));
        _workspace.WriteTicket(effort, "002.md", Fixtures.Ticket("Second", "done"));

        var view = (await _harness.RefreshAsync()).Project("app");

        Assert.AreEqual(2, view.Progress.Completed);
        Assert.AreEqual(2, view.Progress.Total);
    }

    [TestMethod]
    public async Task Free_form_completion_is_recognized_through_a_visible_alias_that_keeps_the_raw_value()
    {
        var effort = _workspace.NewEffort(_workspace.NewProject("app"), "feature");
        _workspace.WriteTicket(effort, "001.md", Fixtures.Ticket("Shipped", "DONE ✅"));

        var ticket = (await _harness.RefreshAsync()).Project("app").Ticket("001");

        Assert.AreEqual(WorkflowStatus.Done, ticket.Status.Status);
        Assert.AreEqual("DONE ✅", ticket.Status.RawValue, "The observed value must survive normalization.");
        Assert.AreEqual("done", ticket.Status.AliasApplied, "The alias that produced the reading must be visible.");
    }

    [TestMethod]
    public async Task Unrecognized_status_stays_incomplete_and_is_reported()
    {
        var effort = _workspace.NewEffort(_workspace.NewProject("app"), "feature");
        _workspace.WriteTicket(effort, "001.md", Fixtures.Ticket("Odd", "marinating"));

        var view = (await _harness.RefreshAsync()).Project("app");

        Assert.AreEqual(WorkflowStatus.Unknown, view.Ticket("001").Status.Status);
        Assert.AreEqual(0, view.Progress.Completed);
        Assert.AreEqual(1, view.Progress.Total, "An unknown state is still remaining work.");
        Assert.IsTrue(view.HasDiagnostic(DiagnosticCode.TicketAmbiguousStatus));
    }

    [TestMethod]
    public async Task Artifact_without_a_status_is_excluded_from_progress_and_reported()
    {
        var effort = _workspace.NewEffort(_workspace.NewProject("app"), "feature");
        _workspace.WriteTicket(effort, "001.md", Fixtures.Ticket("Real", "ready"));
        _workspace.WriteTicket(effort, "notes.md", "# Just some notes\n\nNo metadata here.\n");

        var view = (await _harness.RefreshAsync()).Project("app");

        Assert.AreEqual(1, view.Progress.Total, "An uninterpretable artifact must not enter the denominator.");
        Assert.AreEqual(1, view.Progress.Excluded);
        Assert.IsTrue(view.HasDiagnostic(DiagnosticCode.TicketUnrecognized));
    }

    [TestMethod]
    public async Task Container_work_units_do_not_count_as_completed_work()
    {
        var effort = _workspace.NewEffort(_workspace.NewProject("app"), "feature");
        _workspace.WriteTicket(effort, "000-epic.md", Fixtures.Ticket("Epic", "done", type: "container"));
        _workspace.WriteTicket(effort, "001.md", Fixtures.Ticket("Child", "ready"));

        var view = (await _harness.RefreshAsync()).Project("app");

        Assert.AreEqual(1, view.Progress.Total, "Structure is not a work unit.");
        Assert.AreEqual(0, view.Progress.Completed);
    }

    [TestMethod]
    public async Task Progress_spans_the_whole_pipeline_not_only_implementation()
    {
        var effort = _workspace.NewEffort(_workspace.NewProject("app"), "feature");
        _workspace.WriteTicket(effort, "001.md", Fixtures.Ticket("Plan it", "done", type: "planning"));
        _workspace.WriteTicket(effort, "002.md", Fixtures.Ticket("Research it", "done", type: "research"));
        _workspace.WriteTicket(effort, "003.md", Fixtures.Ticket("Grill it", "ready", type: "grilling"));
        _workspace.WriteTicket(effort, "004.md", Fixtures.Ticket("Prototype it", "open", type: "prototype"));
        _workspace.WriteTicket(effort, "005.md", Fixtures.Ticket("Build it", "open", type: "implementation"));
        _workspace.WriteTicket(effort, "006.md", Fixtures.Ticket("Review it", "open", type: "review"));
        _workspace.WriteTicket(effort, "007.md", Fixtures.Ticket("Release it", "open", type: "release"));

        var view = (await _harness.RefreshAsync()).Project("app");

        Assert.AreEqual(7, view.Progress.Total);
        Assert.AreEqual(2, view.Progress.Completed);
        CollectionAssert.AreEqual(
            new[]
            {
                WorkUnitKind.Planning, WorkUnitKind.Research, WorkUnitKind.Grilling, WorkUnitKind.Prototype,
                WorkUnitKind.Implementation, WorkUnitKind.Review, WorkUnitKind.Release,
            },
            view.Pipeline.Select(p => p.Kind).ToArray());
    }

    [TestMethod]
    public async Task Internal_stage_is_shown_without_affecting_completion()
    {
        var effort = _workspace.NewEffort(_workspace.NewProject("app"), "feature");
        _workspace.WriteTicket(effort, "001.md", Fixtures.Ticket("Half done", "in-progress", stage: "tests written"));

        var view = (await _harness.RefreshAsync()).Project("app");

        Assert.AreEqual("tests written", view.Ticket("001").Stage);
        Assert.AreEqual(0, view.Progress.Completed, "A ticket counts once, at completion — never by stage.");
    }

    [TestMethod]
    public async Task Two_efforts_may_each_hold_a_ticket_with_the_same_filename()
    {
        var project = _workspace.NewProject("app");
        var alpha = _workspace.NewEffort(project, "alpha");
        var beta = _workspace.NewEffort(project, "beta");
        _workspace.WriteTicket(alpha, "001.md", Fixtures.Ticket("Alpha one", "ready"));
        _workspace.WriteTicket(beta, "001.md", Fixtures.Ticket("Beta one", "open"));

        // The second pass exercises the persisted snapshot, where colliding ids would clash.
        await _harness.RefreshAsync();
        var view = (await _harness.RefreshAsync()).Project("app");

        Assert.IsFalse(view.IsStale, "Neither effort should have failed to index.");
        Assert.AreEqual(2, view.Progress.Total);
        CollectionAssert.AreEquivalent(
            new[] { "Alpha one", "Beta one" },
            view.Efforts.SelectMany(e => e.Tickets).Select(t => t.Title).ToArray());
    }

    [TestMethod]
    public async Task A_map_orders_work_but_never_overrides_a_ticket_fact()
    {
        var project = _workspace.NewProject("app");
        var effort = _workspace.NewEffort(project, "feature");
        _workspace.WriteMap(effort, """
            # Map

            Status: done

            1. [Second](issues/002.md)
            2. [First](issues/001.md)
            """);
        _workspace.WriteTicket(effort, "001.md", Fixtures.Ticket("First", "ready"));
        _workspace.WriteTicket(effort, "002.md", Fixtures.Ticket("Second", "ready"));

        var view = (await _harness.RefreshAsync()).Project("app");

        Assert.AreEqual(0, view.Progress.Completed, "The map's own metadata must not complete the tickets.");
        Assert.AreEqual("feature/002", view.NextAction!.TicketId, "Map order decides direction.");
    }
}
