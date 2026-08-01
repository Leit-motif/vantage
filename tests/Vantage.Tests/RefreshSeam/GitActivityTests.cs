using Vantage.Core;
using Vantage.Core.Projection;
using Vantage.Tests.TestSupport;

namespace Vantage.Tests.RefreshSeam;

[TestClass]
public sealed class GitActivityTests
{
    private WorkspaceFixture _workspace = null!;
    private RefreshHarness _harness = null!;

    [TestInitialize]
    public void SetUp()
    {
        _workspace = new WorkspaceFixture();
        _harness = new RefreshHarness(_workspace, new FakeProcessRunner(), DateTimeOffset.UtcNow)
            .WithRealGit();
    }

    [TestCleanup]
    public void TearDown()
    {
        _harness.Dispose();
        _workspace.Dispose();
    }

    [TestMethod]
    public async Task Local_commits_appear_once_even_when_several_branches_reach_them()
    {
        var project = _workspace.NewProject("repo");
        var effort = _workspace.NewEffort(project, "feature");
        _workspace.WriteTicket(effort, "001.md", Fixtures.Ticket("Work", "ready"));

        _workspace.InitGitRepository(project);
        var sha = _workspace.Commit(project, "add planning");
        _workspace.Git(project, "branch", "duplicate");
        _workspace.Git(project, "branch", "another");

        var view = (await _harness.RefreshAsync()).Project("repo");
        var commits = view.RecentActivity.Where(a => a.Kind == ActivityKind.LocalCommit).ToList();

        Assert.AreEqual(1, commits.Count, "One commit reachable from three branches is one piece of activity.");
        StringAssert.Contains(commits[0].Provenance.Locator, sha);
    }

    [TestMethod]
    public async Task History_that_exists_only_on_a_remote_ref_is_not_local_activity()
    {
        var project = _workspace.NewProject("repo");
        var effort = _workspace.NewEffort(project, "feature");
        _workspace.WriteTicket(effort, "001.md", Fixtures.Ticket("Work", "ready"));

        _workspace.InitGitRepository(project);
        _workspace.Commit(project, "local work");

        _workspace.Git(project, "checkout", "-b", "temporary");
        _workspace.WriteTicket(effort, "002.md", Fixtures.Ticket("Remote only", "ready"));
        var remoteOnly = _workspace.Commit(project, "remote-only work");
        _workspace.Git(project, "checkout", "main");
        _workspace.Git(project, "update-ref", "refs/remotes/origin/temporary", remoteOnly);
        _workspace.Git(project, "branch", "-D", "temporary");

        var view = (await _harness.RefreshAsync()).Project("repo");

        Assert.IsFalse(
            view.RecentActivity.Any(a => a.Provenance.Locator.Contains(remoteOnly, StringComparison.OrdinalIgnoreCase)),
            "A commit reachable only from a remote ref has not been seen locally.");
    }

    [TestMethod]
    public async Task Tracked_files_take_their_timestamp_from_git_rather_than_the_filesystem()
    {
        var project = _workspace.NewProject("repo");
        var effort = _workspace.NewEffort(project, "feature");
        _workspace.WriteTicket(effort, "001.md", Fixtures.Ticket("Work", "ready"));

        _workspace.InitGitRepository(project);
        _workspace.Commit(project, "add planning");

        var view = (await _harness.RefreshAsync()).Project("repo");
        var ticket = view.Ticket("001");

        Assert.AreEqual(TimestampProvenance.GitCommit, ticket.Provenance.TimestampKind);
        Assert.AreEqual(EvidenceSource.LocalGit, ticket.Provenance.Source);
    }

    [TestMethod]
    public async Task Untracked_planning_files_are_indexed_but_never_dated_by_the_filesystem()
    {
        var project = _workspace.NewProject("repo");
        _workspace.InitGitRepository(project);
        _workspace.WriteFile(Path.Combine(project, ".gitignore"), ".scratch/\n");
        _workspace.Commit(project, "ignore scratch");

        var effort = _workspace.NewEffort(project, "feature");
        _workspace.WriteTicket(effort, "001.md", Fixtures.Ticket("Ignored but real", "ready"));

        var view = (await _harness.RefreshAsync()).Project("repo");
        var ticket = view.Ticket("001");

        Assert.AreEqual("Ignored but real", ticket.Title, "Ignored planning work must still be visible.");
        Assert.AreEqual(TimestampProvenance.FileSystem, ticket.Provenance.TimestampKind);
        Assert.IsFalse(
            ticket.Provenance.IsActivityGradeTimestamp,
            "A filesystem timestamp is not evidence that something happened.");
    }

    [TestMethod]
    public async Task A_first_scan_reports_no_activity_for_files_it_has_simply_never_seen_before()
    {
        var project = _workspace.NewProject("repo");
        var effort = _workspace.NewEffort(project, "feature");
        _workspace.WriteTicket(effort, "001.md", Fixtures.Ticket("Work", "ready"));

        var view = (await _harness.RefreshAsync()).Project("repo");

        Assert.AreEqual(0, view.RecentActivity.Count, "Copying or checking out files is not activity.");
    }

    [TestMethod]
    public async Task A_later_semantic_change_becomes_reliable_activity()
    {
        var project = _workspace.NewProject("repo");
        var effort = _workspace.NewEffort(project, "feature");
        var ticket = _workspace.WriteTicket(effort, "001.md", Fixtures.Ticket("Work", "open"));

        await _harness.RefreshAsync();

        _workspace.WriteFile(ticket, Fixtures.Ticket("Work", "ready"));
        var view = (await _harness.RefreshAsync()).Project("repo");

        var change = view.RecentActivity.SingleOrDefault(a => a.TicketId == "feature/001");
        Assert.IsNotNull(change, "A status change observed between refreshes is real movement.");
        Assert.AreEqual(
            TimestampProvenance.ObservedChange,
            change.Provenance.TimestampKind,
            "No watcher ran; the timestamp is when the change was noticed and must say so.");
        Assert.IsTrue(change.Provenance.IsActivityGradeTimestamp);
        StringAssert.Contains(change.Summary, "ready");
    }

    [TestMethod]
    public async Task Finishing_work_does_not_show_up_as_recent_activity()
    {
        var project = _workspace.NewProject("repo");
        var effort = _workspace.NewEffort(project, "feature");
        var first = _workspace.WriteTicket(effort, "001.md", Fixtures.Ticket("Work", "open"));
        _workspace.WriteTicket(effort, "002.md", Fixtures.Ticket("Other", "open"));

        await _harness.RefreshAsync();

        _workspace.WriteFile(first, Fixtures.Ticket("Work", "done"));
        var view = (await _harness.RefreshAsync()).Project("repo");

        Assert.IsFalse(view.RecentActivity.Any(a => a.TicketId == "feature/001"), "Closure is not movement worth surfacing.");
    }

    [TestMethod]
    public async Task Activity_older_than_the_recent_window_is_kept_but_not_shown_as_recent()
    {
        var project = _workspace.NewProject("repo");
        var effort = _workspace.NewEffort(project, "feature");
        var ticket = _workspace.WriteTicket(effort, "001.md", Fixtures.Ticket("Work", "open"));

        await _harness.RefreshAsync();
        _workspace.WriteFile(ticket, Fixtures.Ticket("Work", "ready"));
        await _harness.RefreshAsync();

        _harness.Clock.Advance(TimeSpan.FromHours(30));
        var view = (await _harness.RefreshAsync()).Project("repo");

        Assert.AreEqual(0, view.RecentActivity.Count);
        Assert.IsNotNull(view.LastActivityAt, "History survives the window even when Recent does not show it.");
    }
}
