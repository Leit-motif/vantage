using MattWorkflowDashboard.Infrastructure;
using MattWorkflowDashboard.Infrastructure.Acceptance;
using MattWorkflowDashboard.Infrastructure.Processes;
using MattWorkflowDashboard.Infrastructure.Settings;
using MattWorkflowDashboard.Tests.TestSupport;

namespace MattWorkflowDashboard.Tests.RefreshSeam;

/// <summary>
/// The acceptance instrument driven end to end and at its edges, over sanitized fixtures. The live
/// report is produced by this code, so a number in it can only be believed as far as this is: an
/// instrument reporting on itself is not evidence about itself.
/// </summary>
[TestClass]
public sealed class AcceptanceInstrumentTests
{
    private WorkspaceFixture _workspace = null!;

    [TestInitialize]
    public void SetUp() => _workspace = new WorkspaceFixture();

    [TestCleanup]
    public void TearDown() => _workspace.Dispose();

    /// <summary>
    /// `core.fsmonitor` names a program that `git status` runs, and it is set by the repository being
    /// observed. This plants one that would leave a mark, and requires the mark never to appear —
    /// then removes the hardening and requires it to appear, because a test that cannot see the
    /// attack succeed proves nothing about it being stopped.
    /// </summary>
    [TestMethod]
    public async Task Configuration_in_a_monitored_repository_cannot_make_git_run_a_program()
    {
        var project = _workspace.NewProject("repo");
        _workspace.InitGitRepository(project);
        _workspace.Commit(project, "initial");

        var mark = Path.Combine(_workspace.Root, "fsmonitor-ran.txt");
        var hook = Path.Combine(_workspace.Root, "fsmonitor.bat");

        // A batch file rather than a script: it is what cmd will actually execute on this machine.
        File.WriteAllText(hook, $"@echo off\r\n> \"{mark}\" echo ran\r\n");
        _workspace.Git(project, "config", "core.fsmonitor", hook.Replace('\\', '/'));

        using var hardened = new BoundedProcessRunner(2, TimeSpan.FromSeconds(30));
        await hardened.RunAsync("git", ["-C", project, "status", "--porcelain"], project, CancellationToken.None);

        Assert.IsFalse(
            File.Exists(mark),
            "A repository's own configuration must not be able to make an observing read run a program.");

        // The same command with the hardening lifted. If this does not run the hook either, the
        // assertion above is about nothing.
        using var unhardened = new BoundedProcessRunner(
            2,
            TimeSpan.FromSeconds(30),
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["GIT_CONFIG_COUNT"] = null });

        await unhardened.RunAsync("git", ["-C", project, "status", "--porcelain"], project, CancellationToken.None);

        Assert.IsTrue(
            File.Exists(mark),
            "Without the hardening the planted program does run, which is what makes its absence above meaningful.");
    }

    /// <summary>
    /// The other half of the same hazard. `diff.external` names a program git runs when producing a
    /// diff, and the dashboard issues `git log` against every repository it observes.
    /// <para>
    /// Asserted one step earlier than the <c>core.fsmonitor</c> case above, and deliberately so: the
    /// claim here is that the repository's value never reaches git, not that a program was observed
    /// not running. Staging a real external-diff execution proved unreliable on this platform, and a
    /// test whose attack half does not fire would make its defence half prove nothing — which is
    /// exactly the trap the fsmonitor test avoids by checking both directions. So this checks the
    /// value git would actually use, in both directions.
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task An_external_diff_program_configured_by_a_repository_never_reaches_git()
    {
        var project = _workspace.NewProject("repo");
        _workspace.InitGitRepository(project);
        _workspace.Commit(project, "initial");

        var hook = Path.Combine(_workspace.Root, "difftool.bat").Replace('\\', '/');
        _workspace.Git(project, "config", "diff.external", hook);

        string[] read = ["-C", project, "config", "--get", "diff.external"];

        using var hardened = new BoundedProcessRunner(2, TimeSpan.FromSeconds(30));
        var underHardening = await hardened.RunAsync("git", read, project, CancellationToken.None);

        Assert.AreEqual(
            string.Empty,
            underHardening.StandardOutput.Trim(),
            "The repository's own choice of program must not be the one git resolves.");

        using var unhardened = new BoundedProcessRunner(
            2,
            TimeSpan.FromSeconds(30),
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["GIT_CONFIG_COUNT"] = null });

        var without = await unhardened.RunAsync("git", read, project, CancellationToken.None);

        Assert.AreEqual(
            hook,
            without.StandardOutput.Trim(),
            "Without the hardening git resolves the repository's value, which is what makes its absence above meaningful.");
    }

    /// <summary>
    /// The fingerprint has to complete as reliably as the scan it measures. Both bounds are asserted
    /// by lowering them rather than by building a tree big enough to hit the shipped ones.
    /// </summary>
    [TestMethod]
    public async Task The_fingerprint_stops_at_its_file_bound_and_says_so()
    {
        var project = _workspace.NewProject("repo");
        var effort = _workspace.NewEffort(project, "feature");

        for (var i = 0; i < 12; i++)
        {
            _workspace.WriteTicket(effort, $"{i:D3}.md", Fixtures.Ticket($"Work {i}", "ready"));
        }

        using var bounded = new BoundedProcessRunner(2, TimeSpan.FromSeconds(30));
        var reader = new MonitoredStateReader(
            new ReadOnlyProcessRunner(bounded),
            path => path.ToLowerInvariant(),
            200,
            maxWorkflowFiles: 4);

        var states = await reader.ReadAsync([project], includeGitHub: false, CancellationToken.None);

        Assert.AreEqual(4, states.Single().WorkflowFileCount, "The bound is a bound.");
        Assert.IsTrue(
            states.Single().Unavailable.Any(u => u.Contains("beyond the fingerprint bound", StringComparison.Ordinal)),
            "And what it stopped short of is reported rather than silently omitted.");
    }

    [TestMethod]
    public async Task The_fingerprint_stops_when_it_is_cancelled()
    {
        var project = _workspace.NewProject("repo");
        var effort = _workspace.NewEffort(project, "feature");

        for (var i = 0; i < 50; i++)
        {
            _workspace.WriteTicket(effort, $"{i:D3}.md", Fixtures.Ticket($"Work {i}", "ready"));
        }

        using var bounded = new BoundedProcessRunner(2, TimeSpan.FromSeconds(30));
        var reader = new MonitoredStateReader(
            new ReadOnlyProcessRunner(bounded),
            path => path.ToLowerInvariant(),
            200);

        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => reader.ReadAsync([project], includeGitHub: false, source.Token));
    }

    /// <summary>
    /// The live report's association numbers come out of this code, so they cannot vouch for it.
    /// Three repositories with known answers — one agreeing with its confirmed association, one
    /// disagreeing, one whose remote cannot be read — and the run has to count them correctly.
    /// </summary>
    [TestMethod]
    public async Task The_run_counts_agreeing_disagreeing_and_unreadable_remotes_for_what_they_are()
    {
        var agrees = _workspace.NewProject("agrees");
        _workspace.InitGitRepository(agrees, "https://github.com/acme/agrees.git");
        _workspace.Commit(agrees, "initial");

        var disagrees = _workspace.NewProject("disagrees");
        _workspace.InitGitRepository(disagrees, "https://github.com/acme/moved-away.git");
        _workspace.Commit(disagrees, "initial");

        var noRemote = _workspace.NewProject("no-remote");
        _workspace.InitGitRepository(noRemote);
        _workspace.Commit(noRemote, "initial");

        var state = new AppPaths(Path.Combine(_workspace.Root, "scan-state"));

        var settings = new DashboardSettings
        {
            Roots = [_workspace.WorkspacesRoot],

            // Local-only: the associations are still compared, and no gh call is made for them.
            GitHubEnrichmentEnabled = false,
        };

        // Both are recorded as already confirmed, so the run compares a confirmed association
        // against the remote rather than adopting whatever it reads.
        settings.Projects.Add(new ProjectRegistryEntry
        {
            Path = Infrastructure.Discovery.ProjectDiscovery.CanonicalizeFully(agrees),
            ConfirmedOrigin = "acme/agrees",
        });

        settings.Projects.Add(new ProjectRegistryEntry
        {
            Path = Infrastructure.Discovery.ProjectDiscovery.CanonicalizeFully(disagrees),
            ConfirmedOrigin = "acme/still-here",
        });

        var report = await new ReadOnlyAcceptanceRun(settings, state, "test").ExecuteAsync(CancellationToken.None);

        Assert.AreEqual(3, report.Associations.Projects);
        Assert.AreEqual(2, report.Associations.ProjectsWithAssociation, "Only the two confirmed ones carry an association.");
        Assert.AreEqual(1, report.Associations.AssociationMatchesObservedRemote, "'agrees' agrees with its remote.");
        Assert.AreEqual(
            1,
            report.Associations.AssociationDiffersFromObservedRemote,
            "'disagrees' is confirmed against a repository its remote no longer names, and that is not agreement.");

        Assert.AreEqual(0, report.Safety.MonitoredStateChanges.Count, "Observing a fixture changes nothing in it.");
        Assert.AreEqual(0, report.Safety.RefusedCommands.Count);
    }

    /// <summary>
    /// #9: the acceptance instrument runs its own offline probe (a real <c>gh auth status</c>)
    /// independent of the product's refresh path, to characterize behaviour with no session. That
    /// probe must not itself become the one gh invocation a local-only default run makes.
    /// </summary>
    [TestMethod]
    public async Task A_run_with_default_settings_issues_no_gh_command_at_all()
    {
        var project = _workspace.NewProject("widget");
        _workspace.InitGitRepository(project, "https://github.com/acme/widget.git");
        _workspace.Commit(project, "initial");

        var state = new AppPaths(Path.Combine(_workspace.Root, "scan-state"));
        var settings = new DashboardSettings { Roots = [_workspace.WorkspacesRoot] };

        Assert.IsFalse(settings.GitHubEnrichmentEnabled, "This proves the shipped default, not an explicit override.");

        var report = await new ReadOnlyAcceptanceRun(settings, state, "test").ExecuteAsync(CancellationToken.None);

        Assert.IsFalse(
            report.Safety.CommandsIssued.Keys.Any(command => command.StartsWith("gh", StringComparison.Ordinal)),
            "A local-only default run must not invoke gh, including from the instrument's own offline probe.");
    }

    /// <summary>
    /// The guard exists because the documented example violated it. A run whose own output lands
    /// inside what it observes writes into a monitored workspace, in both fingerprints at once.
    /// </summary>
    [TestMethod]
    public async Task A_run_that_would_write_inside_a_monitored_root_refuses_to_start()
    {
        _workspace.NewProject("repo");

        var settings = new DashboardSettings
        {
            Roots = [_workspace.WorkspacesRoot],
            GitHubEnrichmentEnabled = false,
        };

        var inside = new AppPaths(Path.Combine(_workspace.WorkspacesRoot, "repo", "scan"));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => new ReadOnlyAcceptanceRun(settings, inside, "test").ExecuteAsync(CancellationToken.None));

        Assert.IsFalse(
            Directory.Exists(inside.Root),
            "Refusing after creating the directory would already have changed the workspace.");
    }
}
