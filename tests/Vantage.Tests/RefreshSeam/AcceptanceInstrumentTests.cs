using Vantage.Infrastructure;
using Vantage.Infrastructure.Acceptance;
using Vantage.Infrastructure.Processes;
using Vantage.Infrastructure.Settings;
using Vantage.Tests.TestSupport;

namespace Vantage.Tests.RefreshSeam;

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
        var hook = WriteMarkingProgram("fsmonitor", mark);
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

        // Never executed — this test asserts the value git resolves, not a program observed
        // running — but named the way the platform would name one, so it reads as what it is.
        var hook = Path.Combine(_workspace.Root, ProgramName("difftool")).Replace('\\', '/');
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
            maxWorkflowFiles: 4);

        var states = await reader.ReadAsync([project], CancellationToken.None);

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
            path => path.ToLowerInvariant());

        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => reader.ReadAsync([project], source.Token));
    }

    /// <summary>
    /// No <c>gh</c> process is started, under any setting. The fixture is the case that used to
    /// produce every gh call there was — a project whose remote is a GitHub origin — and there is
    /// no longer a setting that could turn one on, so the whole run's outward footprint is git.
    /// The refusal list is asserted alongside the issued one: a gh call stopped at the boundary
    /// would be recorded rather than run, and that is still a gh call the product tried to make.
    /// </summary>
    [TestMethod]
    public async Task A_run_over_a_repository_with_a_github_remote_issues_no_gh_command_at_all()
    {
        var project = _workspace.NewProject("widget");
        _workspace.InitGitRepository(project, "https://github.com/acme/widget.git");
        _workspace.Commit(project, "initial");

        var state = new AppPaths(Path.Combine(_workspace.Root, "scan-state"));
        var settings = new DashboardSettings { Roots = [_workspace.WorkspacesRoot] };

        var report = await new ReadOnlyAcceptanceRun(settings, state, "test").ExecuteAsync(CancellationToken.None);

        Assert.IsFalse(
            report.Safety.CommandsIssued.Keys.Any(command => command.StartsWith("gh", StringComparison.Ordinal)),
            $"A run must not invoke gh. Issued: {string.Join(", ", report.Safety.CommandsIssued.Keys)}");
        Assert.IsFalse(
            report.Safety.RefusedCommands.Any(c => c.FileName == "gh"),
            "And it must not have tried to: a refused gh call is still one the product attempted.");
        Assert.IsTrue(
            report.Safety.CommandsIssued.Keys.Any(command => command.StartsWith("git", StringComparison.Ordinal)),
            "Precondition: the run has to have observed the repository at all.");
    }

    /// <summary>
    /// The guard exists because the documented example violated it. A run whose own output lands
    /// inside what it observes writes into a monitored workspace, in both fingerprints at once.
    /// </summary>
    [TestMethod]
    public async Task A_run_that_would_write_inside_a_monitored_root_refuses_to_start()
    {
        _workspace.NewProject("repo");

        var settings = new DashboardSettings { Roots = [_workspace.WorkspacesRoot] };

        var inside = new AppPaths(Path.Combine(_workspace.WorkspacesRoot, "repo", "scan"));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => new ReadOnlyAcceptanceRun(settings, inside, "test").ExecuteAsync(CancellationToken.None));

        Assert.IsFalse(
            Directory.Exists(inside.Root),
            "Refusing after creating the directory would already have changed the workspace.");
    }

    /// <summary>What a program a repository could plant would actually be called on this host.</summary>
    private static string ProgramName(string stem) =>
        OperatingSystem.IsWindows() ? $"{stem}.bat" : $"{stem}.sh";

    /// <summary>
    /// A program git will really run, which leaves a file behind when it does. The attack half of
    /// these tests has to be able to fire, so this is written in whatever the host actually
    /// executes — a batch file for cmd, or a shell script with the executable bit set.
    /// </summary>
    private string WriteMarkingProgram(string stem, string mark)
    {
        var path = Path.Combine(_workspace.Root, ProgramName(stem));

        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(path, $"@echo off\r\n> \"{mark}\" echo ran\r\n");
            return path;
        }

        File.WriteAllText(path, $"#!/bin/sh\necho ran > \"{mark}\"\n");
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        return path;
    }
}
