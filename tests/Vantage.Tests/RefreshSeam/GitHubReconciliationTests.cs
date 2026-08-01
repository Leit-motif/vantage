using Vantage.Core;
using Vantage.Core.Projection;
using Vantage.Infrastructure.Processes;
using Vantage.Infrastructure.Settings;
using Vantage.Tests.TestSupport;

namespace Vantage.Tests.RefreshSeam;

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
        _runner.GhIssues(Fixtures.GhIssues(new Fixtures.GhIssue(7, "Build the thing", "OPEN", ["ready-for-agent"], "2026-07-29T10:00:00Z")));

        var view = (await _harness.RefreshAsync()).Project("widget");

        Assert.AreEqual(1, view.Progress.Total, "A mirrored ticket must not inflate the work.");
        Assert.AreEqual(1, view.Efforts.SelectMany(e => e.Tickets).Count());
    }

    [TestMethod]
    public async Task Similar_titles_are_never_treated_as_the_same_ticket()
    {
        LinkedProject(Fixtures.Ticket("Build the thing", "ready"));
        _runner.GhIssues(Fixtures.GhIssues(new Fixtures.GhIssue(7, "Build the thing", "OPEN", ["ready-for-agent"], "2026-07-29T10:00:00Z")));

        var view = (await _harness.RefreshAsync()).Project("widget");

        Assert.AreEqual(2, view.Progress.Total, "Without an explicit link these are two separate work units.");
    }

    [TestMethod]
    public async Task Local_facts_win_a_linked_disagreement_and_the_conflict_is_reported()
    {
        LinkedProject(Fixtures.Ticket("Local title", "ready", gitHub: "#7"));
        _runner.GhIssues(Fixtures.GhIssues(new Fixtures.GhIssue(7, "Remote title", "CLOSED", [], "2026-07-29T10:00:00Z")));

        var view = (await _harness.RefreshAsync()).Project("widget");

        Assert.AreEqual("Local title", view.Ticket("001").Title);
        Assert.IsFalse(view.Ticket("001").Status.IsComplete, "Local state is authoritative.");

        CollectionAssert.AreEquivalent(
            new[] { ConflictField.Title, ConflictField.State },
            view.Conflicts.Select(c => c.Field).Distinct().ToArray());
    }

    [TestMethod]
    public async Task A_conflict_carries_both_values_the_resolution_and_each_side_s_provenance()
    {
        LinkedProject(Fixtures.Ticket("Local title", "ready", gitHub: "#7"));
        _runner.GhIssues(Fixtures.GhIssues(new Fixtures.GhIssue(7, "Remote title", "OPEN", [], "2026-07-29T10:00:00Z")));

        var view = (await _harness.RefreshAsync()).Project("widget");
        var conflict = view.Conflicts.Single(c => c.Field == ConflictField.Title);

        Assert.AreEqual("Local title", conflict.LocalValue);
        Assert.AreEqual("Remote title", conflict.RemoteValue);
        StringAssert.Contains(conflict.Resolution, "Local", "The resolution has to say which side was kept.");
        Assert.IsTrue(
            conflict.LocalProvenance.Source is EvidenceSource.LocalFile or EvidenceSource.LocalGit,
            $"The local side must be traceable to a local source, not {conflict.LocalProvenance.Source}.");
        Assert.AreEqual(EvidenceSource.GitHubCli, conflict.RemoteProvenance.Source);
        Assert.AreEqual(
            view.Ticket("001").Provenance.RefreshId,
            conflict.LocalProvenance.RefreshId,
            "Every side of a conflict is traceable to the refresh that produced it.");
    }

    [TestMethod]
    public async Task A_conflict_is_attached_to_the_item_it_is_about()
    {
        LinkedProject(Fixtures.Ticket("First local", "ready", gitHub: "#7"));
        _workspace.WriteTicket(
            Path.Combine(_workspace.WorkspacesRoot, "widget", ".scratch", "feature"),
            "002.md",
            Fixtures.Ticket("Second local", "ready", gitHub: "#8"));

        _runner.GhIssues(Fixtures.GhIssues(
            new Fixtures.GhIssue(7, "First remote", "OPEN", [], "2026-07-29T10:00:00Z"),
            new Fixtures.GhIssue(8, "Second remote", "OPEN", [], "2026-07-29T10:00:00Z")));

        var view = (await _harness.RefreshAsync()).Project("widget");

        Assert.AreEqual("First remote", view.Ticket("001").Conflicts.Single().RemoteValue);
        Assert.AreEqual(
            "Second remote",
            view.Ticket("002").Conflicts.Single().RemoteValue,
            "A disagreement belongs to the work it is about, not to a list beside it.");
    }

    [TestMethod]
    public async Task Information_only_GitHub_has_is_enrichment_rather_than_conflict()
    {
        LinkedProject(Fixtures.Ticket("Build the thing", "ready", gitHub: "#7"));
        _runner.GhIssues(Fixtures.GhIssues(new Fixtures.GhIssue(7, "Build the thing", "OPEN", ["ready-for-agent"], "2026-07-29T10:00:00Z")));

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
            new Fixtures.GhIssue(11, "Someone filed a bug", "OPEN", [], "2026-07-29T10:00:00Z"),
            new Fixtures.GhIssue(12, "Agent-ready remote work", "OPEN", ["ready-for-agent"], "2026-07-29T10:00:00Z")));

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
            new Fixtures.GhIssue(11, "Finished remote work", "CLOSED", [], DateTimeOffset.UtcNow.ToString("O"))));

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

    /// <summary>
    /// #9: v1 ships local-only. A project with a GitHub origin and a linked ticket would issue
    /// several `gh` calls if enrichment were on — this proves that with the flag at its shipped
    /// default, none of them happen at all, not merely that their results go unused.
    /// </summary>
    [TestMethod]
    public async Task A_fresh_run_with_default_settings_makes_no_gh_call()
    {
        LinkedProject(Fixtures.Ticket("Local work", "ready", gitHub: "#7"));

        var runner = new FakeProcessRunner();
        runner.Fallback = _runner.Fallback;
        using var defaultHarness = new RefreshHarness(
            _workspace, runner, DateTimeOffset.UtcNow, gitHubEnrichmentEnabled: new DashboardSettings().GitHubEnrichmentEnabled);

        var snapshot = await defaultHarness.RefreshAsync();

        Assert.IsFalse(
            runner.Invocations.Any(i => i.FileName == "gh"),
            "Default settings must never invoke gh, not merely ignore what it returns.");
        Assert.AreEqual(1, snapshot.Project("widget").Progress.Total, "Local work stays visible.");
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

    /// <summary>
    /// A repository with its issue tracker switched off has answered the question: it has no
    /// issues. That is a fact about the repository, not a source that could not be reached — and
    /// the difference matters, because an unreachable source marks the project as showing cached
    /// evidence on every refresh for a read that can never succeed. Forks arrive with issues
    /// disabled by default, so this is the ordinary case rather than an edge.
    /// </summary>
    [TestMethod]
    public async Task A_repository_with_issues_switched_off_has_no_issues_rather_than_no_answer()
    {
        LinkedProject(Fixtures.Ticket("Local work", "ready"));
        _runner.WhenCommand(
            "gh",
            "issue",
            new ProcessResult(1, string.Empty, "the 'owner/widget' repository has disabled issues", false, false));

        var view = (await _harness.RefreshAsync()).Project("widget");

        Assert.IsTrue(view.GitHubAvailable, "Issues being switched off is an answer, not a failure to reach the source.");
        Assert.IsFalse(view.IsStale, "So the project is not showing last-known-good, and refreshing can clear it.");
        Assert.IsFalse(view.HasDiagnostic(DiagnosticCode.GitHubUnavailable));
        Assert.AreEqual(1, view.Progress.Total, "The local half is unaffected.");
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
