using MattWorkflowDashboard.Core;
using MattWorkflowDashboard.Core.Projection;
using MattWorkflowDashboard.Tests.TestSupport;

namespace MattWorkflowDashboard.Tests.RefreshSeam;

[TestClass]
public sealed class GitHubReconciliationTests
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
    public async Task An_explicitly_linked_ticket_is_counted_once()
    {
        LinkedProject(Fixtures.Ticket("Build the thing", "ready", gitHub: "#7"));
        _runner.GhIssues(Fixtures.GhIssues((7, "Build the thing", "OPEN", ["ready-for-agent"], "2026-07-29T10:00:00Z")));

        var view = (await _harness.RefreshAsync()).Project("widget");

        Assert.AreEqual(1, view.Progress.Total, "A mirrored ticket must not inflate the work.");
        Assert.AreEqual(1, view.Efforts.SelectMany(e => e.Tickets).Count());
    }

    [TestMethod]
    public async Task Similar_titles_are_never_treated_as_the_same_ticket()
    {
        LinkedProject(Fixtures.Ticket("Build the thing", "ready"));
        _runner.GhIssues(Fixtures.GhIssues((7, "Build the thing", "OPEN", ["ready-for-agent"], "2026-07-29T10:00:00Z")));

        var view = (await _harness.RefreshAsync()).Project("widget");

        Assert.AreEqual(2, view.Progress.Total, "Without an explicit link these are two separate work units.");
    }

    [TestMethod]
    public async Task Local_facts_win_a_linked_disagreement_and_the_conflict_is_reported()
    {
        LinkedProject(Fixtures.Ticket("Local title", "ready", gitHub: "#7"));
        _runner.GhIssues(Fixtures.GhIssues((7, "Remote title", "CLOSED", [], "2026-07-29T10:00:00Z")));

        var view = (await _harness.RefreshAsync()).Project("widget");

        Assert.AreEqual("Local title", view.Ticket("001").Title);
        Assert.IsFalse(view.Ticket("001").Status.IsComplete, "Local state is authoritative.");

        CollectionAssert.AreEquivalent(
            new[] { ConflictField.Title, ConflictField.State },
            view.Conflicts.Select(c => c.Field).Distinct().ToArray());
    }

    [TestMethod]
    public async Task Information_only_GitHub_has_is_enrichment_rather_than_conflict()
    {
        LinkedProject(Fixtures.Ticket("Build the thing", "ready", gitHub: "#7"));
        _runner.GhIssues(Fixtures.GhIssues((7, "Build the thing", "OPEN", ["ready-for-agent"], "2026-07-29T10:00:00Z")));

        var view = (await _harness.RefreshAsync()).Project("widget");

        Assert.AreEqual(0, view.Conflicts.Count, "Missing local detail is asymmetry, not disagreement.");
        Assert.IsTrue(
            view.Ticket("001").EnrichmentProvenance.Any(p => p.Source == EvidenceSource.GitHubCli),
            "Enrichment must stay traceable to the source that supplied it.");
    }

    [TestMethod]
    public async Task Unclassified_GitHub_issues_are_remaining_work_but_never_a_next_action()
    {
        LinkedProject(Fixtures.Ticket("Local ready work", "ready"));
        _runner.GhIssues(Fixtures.GhIssues(
            (11, "Someone filed a bug", "OPEN", [], "2026-07-29T10:00:00Z"),
            (12, "Agent-ready remote work", "OPEN", ["ready-for-agent"], "2026-07-29T10:00:00Z")));

        var view = (await _harness.RefreshAsync()).Project("widget");

        Assert.AreEqual(3, view.Progress.Total);
        Assert.IsFalse(view.Ticket("gh#11").IsActionable, "An unclassified issue is backlog, not readiness.");
        Assert.IsTrue(view.Ticket("gh#12").IsActionable, "A classifying label makes remote work ordinary workflow work.");
    }

    [TestMethod]
    public async Task Closed_GitHub_issues_count_as_completed_and_stay_out_of_recent()
    {
        LinkedProject(Fixtures.Ticket("Local work", "ready"));
        _runner.GhIssues(Fixtures.GhIssues(
            (11, "Finished remote work", "CLOSED", [], DateTimeOffset.UtcNow.ToString("O"))));

        var view = (await _harness.RefreshAsync()).Project("widget");

        Assert.AreEqual(1, view.Progress.Completed);
        Assert.IsFalse(
            view.RecentActivity.Any(a => a.TicketId == "gh#11"),
            "A closed issue must not displace actionable work in Recent.");
    }

    [TestMethod]
    public async Task GitHub_is_queried_only_for_the_associated_repository_and_never_for_the_account()
    {
        LinkedProject(Fixtures.Ticket("Local work", "ready"));
        _runner.GhIssues(Fixtures.GhIssues());

        await _harness.RefreshAsync();

        var ghCalls = _runner.Invocations.Where(i => i.FileName == "gh").ToList();
        Assert.IsTrue(ghCalls.Count > 0);
        foreach (var call in ghCalls.Where(c => c.Arguments.Contains("issue")))
        {
            var repoIndex = call.Arguments.ToList().IndexOf("--repo");
            Assert.IsTrue(repoIndex >= 0, "Every issue query must be scoped to a repository.");
            Assert.AreEqual("acme/widget", call.Arguments[repoIndex + 1]);
        }

        Assert.IsFalse(
            ghCalls.Any(c => c.Arguments.Contains("search") || c.Arguments.Contains("--owner")),
            "The dashboard must never enumerate the account.");
    }

    [TestMethod]
    public async Task A_changed_remote_is_reported_rather_than_silently_adopted()
    {
        var project = LinkedProject(Fixtures.Ticket("Local work", "ready"));
        _runner.GhIssues(Fixtures.GhIssues());

        await _harness.RefreshAsync();

        _workspace.Git(project, "remote", "set-url", "origin", "https://github.com/someone-else/other.git");
        var view = (await _harness.RefreshAsync()).Project("widget");

        Assert.IsTrue(view.HasDiagnostic(DiagnosticCode.OriginChanged));
        Assert.AreEqual("acme/widget", view.Origin!.Slug, "Relinking requires confirmation.");
    }

    [TestMethod]
    public async Task A_lost_gh_session_leaves_local_evidence_intact()
    {
        LinkedProject(Fixtures.Ticket("Local work", "ready"));
        _runner.GhIssues(Fixtures.GhIssues());
        await _harness.RefreshAsync();

        var offlineRunner = new FakeProcessRunner().GhUnauthenticated();
        offlineRunner.Fallback = _runner.Fallback;
        var offline = new RefreshHarness(_workspace, offlineRunner, DateTimeOffset.UtcNow);
        offline.Settings.Roots.Clear();
        offline.Settings.Roots.Add(_workspace.WorkspacesRoot);

        var snapshot = await offline.RefreshAsync();
        offline.Dispose();

        Assert.IsTrue(snapshot.Offline);
        Assert.AreEqual(1, snapshot.Project("widget").Progress.Total, "Local work stays visible without GitHub.");
    }

    [TestMethod]
    public async Task Malformed_gh_output_is_reported_without_losing_the_project()
    {
        LinkedProject(Fixtures.Ticket("Local work", "ready"));
        _runner.GhIssues("{ this is not the array we asked for");

        var view = (await _harness.RefreshAsync()).Project("widget");

        Assert.IsTrue(view.HasDiagnostic(DiagnosticCode.GitHubMalformedOutput));
        Assert.AreEqual(1, view.Progress.Total);
        Assert.IsFalse(view.GitHubAvailable);
    }

    [TestMethod]
    public async Task A_hung_gh_call_is_bounded_and_the_dashboard_still_answers()
    {
        LinkedProject(Fixtures.Ticket("Local work", "ready"));
        _runner.WhenCommand("gh", "issue", FakeProcessRunner.TimedOut());

        var view = (await _harness.RefreshAsync()).Project("widget");

        Assert.IsTrue(view.HasDiagnostic(DiagnosticCode.GitHubUnavailable));
        Assert.AreEqual(1, view.Progress.Total);
    }

    [TestMethod]
    public async Task Partial_issue_data_is_shown_rather_than_discarded()
    {
        LinkedProject(Fixtures.Ticket("Local work", "ready"));
        _runner.GhIssues("""[{"number": 42, "state": "OPEN"}]""");

        var view = (await _harness.RefreshAsync()).Project("widget");

        Assert.AreEqual("#42", view.Ticket("gh#42").Title);
    }
}
