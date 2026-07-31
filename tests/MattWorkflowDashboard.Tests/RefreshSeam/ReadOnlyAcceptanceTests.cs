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
    /// A verb allowlist is not structural. Each of these reads by verb and writes a file, or points
    /// git at configuration that can name a program to run.
    /// </summary>
    [TestMethod]
    public async Task An_option_that_writes_is_refused_even_when_the_verb_only_reads()
    {
        var inner = new FakeProcessRunner();
        var runner = new ReadOnlyProcessRunner(inner);

        string[][] disguised =
        [
            ["-C", "repo", "log", "--output=C:\\evidence\\planted.txt", "-1"],
            ["-C", "repo", "log", "--output", "C:\\evidence\\planted.txt"],
            ["-c", "core.fsmonitor=C:\\hostile.exe", "-C", "repo", "status", "--porcelain"],
            ["-c", "alias.status=!C:\\hostile.exe", "-C", "repo", "status"],
            ["--git-dir", "C:\\elsewhere\\.git", "rev-parse", "HEAD"],
        ];

        foreach (var arguments in disguised)
        {
            var result = await runner.RunAsync("git", arguments, null, CancellationToken.None);
            Assert.IsFalse(result.Succeeded, $"'git {string.Join(' ', arguments)}' was allowed through.");
        }

        Assert.AreEqual(0, inner.Invocations.Count);
    }

    /// <summary>
    /// A refusal is the moment a command line is most likely to hold a private path, and the record
    /// of it is committed. It has to describe the refusal without reproducing it.
    /// </summary>
    [TestMethod]
    public async Task A_refusal_records_what_was_attempted_without_recording_the_workspace()
    {
        var runner = new ReadOnlyProcessRunner(new FakeProcessRunner());
        var secret = @"C:\Users\Example\Workspaces\a-private-client-project";

        await runner.RunAsync("git", ["-C", secret, "commit", "-m", "in " + secret], null, CancellationToken.None);

        var refused = runner.Refused.Single();
        var serialized = System.Text.Json.JsonSerializer.Serialize(refused);

        Assert.IsFalse(
            serialized.Contains("a-private-client-project", StringComparison.OrdinalIgnoreCase),
            $"A refusal must not carry the workspace into committed evidence: {serialized}");

        Assert.AreEqual("git commit", refused.Shape, "It still has to say what was attempted.");
        Assert.AreEqual(5, refused.ArgumentCount);
        Assert.AreNotEqual(string.Empty, refused.ArgumentsDigest, "Two different refusals have to be tellable apart.");
    }

    /// <summary>
    /// The output of a run must not land inside what the run is observing. A cache written under a
    /// monitored root is a change to that workspace — and one present in *both* fingerprints, so the
    /// comparison would report that nothing moved.
    /// </summary>
    [TestMethod]
    public void An_output_directory_inside_a_monitored_root_is_refused()
    {
        var roots = new[] { _workspace.WorkspacesRoot };

        Assert.IsNotNull(
            ReadOnlyAcceptanceRun.RejectOutputInsideARoot(
                Path.Combine(_workspace.WorkspacesRoot, "repo", "scan"),
                roots),
            "Writing the run's own state into a monitored project must be refused.");

        Assert.IsNotNull(
            ReadOnlyAcceptanceRun.RejectOutputInsideARoot(_workspace.WorkspacesRoot, roots));

        Assert.IsNull(
            ReadOnlyAcceptanceRun.RejectOutputInsideARoot(Path.Combine(_workspace.Root, "scan"), roots),
            "Somewhere outside every root is exactly what the run is for.");
    }

    /// <summary>
    /// Two absent digests compare equal. Without saying so, a source that was never read would be
    /// indistinguishable from one read twice and found identical — and the report would be at its
    /// most reassuring when it had observed the least.
    /// </summary>
    [TestMethod]
    public async Task A_source_that_could_not_be_read_is_reported_rather_than_counted_as_unchanged()
    {
        var project = _workspace.NewProject("repo");
        _workspace.NewEffort(project, "feature");
        Directory.CreateDirectory(Path.Combine(project, ".git"));

        // A directory named .git with nothing in it: every git read against it fails, exactly as an
        // unreadable or broken repository would.
        using var bounded = new BoundedProcessRunner(4, TimeSpan.FromSeconds(30));
        var reader = new MonitoredStateReader(
            new ReadOnlyProcessRunner(bounded),
            path => path.ToLowerInvariant(),
            200);

        var before = await reader.ReadAsync([project], includeGitHub: false, CancellationToken.None);
        var after = await reader.ReadAsync([project], includeGitHub: false, CancellationToken.None);

        Assert.AreEqual(0, MonitoredStateReader.Diff(before, after).Count, "Nothing was observed to change.");

        var gaps = MonitoredStateReader.Gaps(before, after);
        Assert.IsTrue(gaps.Count > 0, "But nothing was observed at all, and that is not the same thing.");
        Assert.IsTrue(gaps.Any(g => g.Subject == "git status"));
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

        Assert.AreEqual(0, runner.Refused.Count, $"Refused: {string.Join("; ", runner.Refused.Select(r => r.Shape))}");
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

    /// <summary>
    /// A project whose remote changed is waiting on the owner to confirm the relink. Fingerprinting
    /// the newly observed remote would send issue and label queries to a repository they never
    /// approved — the exact boundary story 11 draws.
    /// </summary>
    [TestMethod]
    public async Task The_fingerprint_queries_the_confirmed_association_and_not_the_current_remote()
    {
        var project = _workspace.NewProject("repo");
        _workspace.InitGitRepository(project, "https://github.com/someone-else/unapproved.git");

        var inner = new FakeProcessRunner()
            .GhIssues("[]")
            .When((name, args) => name == "gh" && args.Contains("label"), () => FakeProcessRunner.Ok("[]"));

        inner.Fallback = new BoundedProcessRunner(4, TimeSpan.FromSeconds(30));

        var runner = new ReadOnlyProcessRunner(inner);
        var reader = new MonitoredStateReader(
            runner,
            path => path.ToLowerInvariant(),
            200,
            confirmedOriginOf: _ => "acme/confirmed");

        await reader.ReadAsync([project], includeGitHub: true, CancellationToken.None);

        CollectionAssert.AreEquivalent(
            new[] { "acme/confirmed" },
            runner.RepositoriesQueried.ToArray(),
            "Only the confirmed association may be queried, never the remote that is awaiting confirmation.");
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
