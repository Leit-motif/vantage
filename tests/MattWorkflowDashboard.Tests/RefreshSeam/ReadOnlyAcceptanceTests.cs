using MattWorkflowDashboard.Infrastructure.Acceptance;
using MattWorkflowDashboard.Infrastructure.Processes;
using MattWorkflowDashboard.Tests.TestSupport;

namespace MattWorkflowDashboard.Tests.RefreshSeam;

/// <summary>
/// The instrument the live run is measured with. Evidence that a run over the owner's real
/// workspaces changed nothing is only worth as much as the two things producing it: a boundary
/// that would actually stop a write, and a comparison that would actually notice one.
/// </summary>
[TestClass]
public sealed class ReadOnlyAcceptanceTests
{
    private WorkspaceFixture _workspace = null!;

    [TestInitialize]
    public void SetUp() => _workspace = new WorkspaceFixture();

    [TestCleanup]
    public void TearDown() => _workspace.Dispose();

    [TestMethod]
    public async Task A_command_that_would_change_something_is_refused_rather_than_run()
    {
        var inner = new FakeProcessRunner();
        var runner = new ReadOnlyProcessRunner(inner);

        string[][] writes =
        [
            ["-C", "repo", "commit", "-m", "message"],
            ["-C", "repo", "checkout", "--", "file.md"],
            ["-C", "repo", "config", "--unset", "remote.origin.url"],
            ["-C", "repo", "config", "--list", "--unset", "remote.origin.url"],
            ["-C", "repo", "remote", "set-url", "origin", "https://example.invalid"],
            ["-C", "repo", "push"],
        ];

        foreach (var arguments in writes)
        {
            var result = await runner.RunAsync("git", arguments, null, CancellationToken.None);
            Assert.IsFalse(result.Succeeded, $"'git {string.Join(' ', arguments)}' was allowed through.");
        }

        string[][] ghWrites =
        [
            ["issue", "close", "5"],
            ["issue", "comment", "5", "--body", "hello"],
            ["issue", "edit", "5", "--add-label", "bug"],
            ["label", "create", "bug"],
            ["api", "-X", "POST", "repos/o/r/issues"],
            ["api", "repos/o/r/issues", "-f", "title=x"],

            // Both of these only read. Both are also a way to enumerate an account, which is the
            // other promise this boundary is holding.
            ["repo", "list", "Leit-motif"],
            ["api", "user/repos"],
        ];

        foreach (var arguments in ghWrites)
        {
            var result = await runner.RunAsync("gh", arguments, null, CancellationToken.None);
            Assert.IsFalse(result.Succeeded, $"'gh {string.Join(' ', arguments)}' was allowed through.");
        }

        Assert.AreEqual(
            writes.Length + ghWrites.Length,
            runner.Refused.Count,
            "Every refusal is recorded; a silent refusal would read as a command that never happened.");

        Assert.AreEqual(0, inner.Invocations.Count, "A refused command must not reach a real process at all.");
    }

    /// <summary>
    /// The other half, and the half that makes the first one mean anything: a boundary that
    /// refuses everything would also report that nothing was written.
    /// </summary>
    [TestMethod]
    public async Task Every_read_the_dashboard_actually_makes_gets_through()
    {
        var inner = new FakeProcessRunner();
        var runner = new ReadOnlyProcessRunner(inner);

        string[][] reads =
        [
            ["-C", "repo", "rev-parse", "--is-inside-work-tree"],
            ["-C", "repo", "rev-parse", "HEAD"],
            ["-C", "repo", "remote", "get-url", "origin"],
            ["-C", "repo", "log", "--branches", "--no-color", "-n2000", "--name-only"],
            ["-C", "repo", "status", "--porcelain"],
            ["-C", "repo", "config", "--list", "--local"],
            ["-C", "repo", "show-ref"],
        ];

        foreach (var arguments in reads)
        {
            await runner.RunAsync("git", arguments, null, CancellationToken.None);
        }

        await runner.RunAsync("gh", ["auth", "status"], null, CancellationToken.None);
        await runner.RunAsync(
            "gh",
            ["issue", "list", "--repo", "acme/widget", "--state", "all", "--limit", "200", "--json", "number"],
            null,
            CancellationToken.None);
        await runner.RunAsync("gh", ["label", "list", "--repo", "acme/widget", "--json", "name"], null, CancellationToken.None);

        Assert.AreEqual(0, runner.Refused.Count, $"Refused: {string.Join("; ", runner.Refused.Select(r => $"{r.FileName} {r.Arguments}"))}");
        Assert.AreEqual(reads.Length + 3, inner.Invocations.Count);

        CollectionAssert.AreEquivalent(
            new[] { "acme/widget" },
            runner.RepositoriesQueried.ToArray(),
            "Which repositories gh was pointed at is what proves the account was never enumerated.");
    }

    [TestMethod]
    public async Task An_untouched_workspace_compares_equal_and_a_touched_one_does_not()
    {
        var project = _workspace.NewProject("repo");
        var effort = _workspace.NewEffort(project, "feature");
        var ticket = _workspace.WriteTicket(effort, "001.md", Fixtures.Ticket("Work", "ready"));
        _workspace.InitGitRepository(project, "https://github.com/acme/widget.git");
        _workspace.Commit(project, "add planning");

        using var bounded = new BoundedProcessRunner(4, TimeSpan.FromSeconds(30));
        var runner = new ReadOnlyProcessRunner(bounded);
        var reader = new MonitoredStateReader(runner, path => path.ToLowerInvariant(), 200);

        var before = await reader.ReadAsync([project], includeGitHub: false, CancellationToken.None);
        var unchanged = await reader.ReadAsync([project], includeGitHub: false, CancellationToken.None);

        Assert.AreEqual(0, MonitoredStateReader.Diff(before, unchanged).Count, "Reading twice is not a change.");
        Assert.AreEqual(0, runner.Refused.Count, "The comparison itself must be made of reads.");

        Assert.IsTrue(before[0].IsGitRepository);
        Assert.IsTrue(before[0].WorkflowFileCount >= 2, "The markers and the whole .scratch tree are compared.");

        _workspace.WriteFile(ticket, Fixtures.Ticket("Work", "resolved"));
        var afterEdit = await reader.ReadAsync([project], includeGitHub: false, CancellationToken.None);

        var changes = MonitoredStateReader.Diff(before, afterEdit);
        Assert.IsTrue(
            changes.Any(c => c.Subject == "workflow content"),
            "An edited ticket has to show up, or 'nothing changed' means nothing.");
        Assert.IsTrue(
            changes.Any(c => c.Subject == "git status"),
            "The working tree moving has to show up too.");
    }

    [TestMethod]
    public async Task A_new_or_renamed_workflow_file_is_a_change_even_when_the_content_is_not()
    {
        var project = _workspace.NewProject("repo");
        var effort = _workspace.NewEffort(project, "feature");
        var ticket = _workspace.WriteTicket(effort, "001.md", Fixtures.Ticket("Work", "ready"));

        using var bounded = new BoundedProcessRunner(4, TimeSpan.FromSeconds(30));
        var reader = new MonitoredStateReader(
            new ReadOnlyProcessRunner(bounded),
            path => path.ToLowerInvariant(),
            200);

        var before = await reader.ReadAsync([project], includeGitHub: false, CancellationToken.None);

        File.Move(ticket, Path.Combine(Path.GetDirectoryName(ticket)!, "002.md"));
        var after = await reader.ReadAsync([project], includeGitHub: false, CancellationToken.None);

        Assert.IsTrue(
            MonitoredStateReader.Diff(before, after).Any(c => c.Subject == "workflow content"),
            "Names are part of the comparison: a rename moves the owner's work without editing a byte.");
    }
}
