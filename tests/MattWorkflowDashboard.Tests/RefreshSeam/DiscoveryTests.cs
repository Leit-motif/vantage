using MattWorkflowDashboard.Infrastructure.Settings;
using MattWorkflowDashboard.Tests.TestSupport;

namespace MattWorkflowDashboard.Tests.RefreshSeam;

[TestClass]
public sealed class DiscoveryTests
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
    public async Task Discovers_structured_projects_that_are_not_git_repositories()
    {
        _workspace.NewProject("plain-agents-project");

        var snapshot = await _harness.RefreshAsync();

        Assert.AreEqual(1, snapshot.Projects.Count);
        Assert.AreEqual("plain-agents-project", snapshot.Projects[0].Identity.Name);
        Assert.IsFalse(snapshot.Projects[0].GitAvailable, "A non-repository should report Git as unavailable, not fail.");
    }

    [TestMethod]
    public async Task Recognizes_every_candidate_marker()
    {
        var scratchOnly = Path.Combine(_workspace.WorkspacesRoot, "scratch-only");
        Directory.CreateDirectory(Path.Combine(scratchOnly, ".scratch"));

        var trackerOnly = Path.Combine(_workspace.WorkspacesRoot, "tracker-only");
        _workspace.WriteFile(Path.Combine(trackerOnly, "docs", "agents", "issue-tracker.md"), "# Tracker\n");

        _workspace.NewProject("agents-only");

        var snapshot = await _harness.RefreshAsync();

        CollectionAssert.AreEquivalent(
            new[] { "agents-only", "scratch-only", "tracker-only" },
            snapshot.Projects.Select(p => p.Identity.Name).ToArray());
    }

    [TestMethod]
    public async Task Excludes_projects_nested_in_vendor_and_build_directories()
    {
        var project = _workspace.NewProject("host");
        _workspace.WriteFile(Path.Combine(project, "node_modules", "dep", "AGENTS.md"), "# vendored\n");
        _workspace.WriteFile(Path.Combine(project, "obj", "generated", "AGENTS.md"), "# build output\n");

        var snapshot = await _harness.RefreshAsync();

        Assert.AreEqual(1, snapshot.Projects.Count);
        Assert.AreEqual("host", snapshot.Projects[0].Identity.Name);
    }

    [TestMethod]
    public async Task An_ordinary_nested_project_is_discovered_without_an_opt_in()
    {
        var project = _workspace.NewProject("host");
        var nested = Path.Combine(project, "sub", "companion");
        _workspace.WriteFile(Path.Combine(nested, "AGENTS.md"), "# nested but independent\n");

        var snapshot = await _harness.RefreshAsync();

        CollectionAssert.AreEquivalent(
            new[] { "host", "companion" },
            snapshot.Projects.Select(p => p.Identity.Name).ToArray(),
            "Nesting alone is not a vendor, dependency, tool, build, or cache location.");
    }

    [TestMethod]
    public async Task A_project_beneath_an_excluded_location_is_discovered_only_once_it_is_opted_in()
    {
        var project = _workspace.NewProject("host");
        var vendored = Path.Combine(project, "node_modules", "companion");
        _workspace.WriteFile(Path.Combine(vendored, "AGENTS.md"), "# independent, but living under a vendor tree\n");

        var first = await _harness.RefreshAsync();
        Assert.AreEqual(1, first.Projects.Count, "A project under an excluded location stays out until it is opted in.");

        _harness.Settings.Projects.Add(new ProjectRegistryEntry { Path = vendored, NestedOptIn = true });
        var second = await _harness.RefreshAsync();

        CollectionAssert.AreEquivalent(
            new[] { "host", "companion" },
            second.Projects.Select(p => p.Identity.Name).ToArray(),
            "An explicit opt-in must reach a project beneath an excluded location.");
    }

    [TestMethod]
    public async Task An_opt_in_does_not_pull_in_its_excluded_neighbours()
    {
        var project = _workspace.NewProject("host");
        var vendored = Path.Combine(project, "node_modules", "companion");
        _workspace.WriteFile(Path.Combine(vendored, "AGENTS.md"), "# opted in\n");
        _workspace.WriteFile(Path.Combine(project, "node_modules", "other-dep", "AGENTS.md"), "# still vendored\n");

        _harness.Settings.Projects.Add(new ProjectRegistryEntry { Path = vendored, NestedOptIn = true });
        var snapshot = await _harness.RefreshAsync();

        CollectionAssert.AreEquivalent(
            new[] { "host", "companion" },
            snapshot.Projects.Select(p => p.Identity.Name).ToArray(),
            "Opting one project in must not turn the excluded tree into a crawl.");
    }

    [TestMethod]
    public async Task Registry_state_controls_visibility_without_discarding_the_entry()
    {
        _workspace.NewProject("visible");
        var hidden = _workspace.NewProject("hidden");

        await _harness.RefreshAsync();
        _harness.Settings.FindProject(hidden)!.State = ProjectRegistryState.Hidden;

        var snapshot = await _harness.RefreshAsync();

        Assert.AreEqual(1, snapshot.Projects.Count);
        Assert.AreEqual("visible", snapshot.Projects[0].Identity.Name);
        Assert.IsNotNull(_harness.Settings.FindProject(hidden), "Hiding must preserve registry intent.");
    }

    [TestMethod]
    public async Task An_excluded_location_that_is_a_junction_is_still_excluded()
    {
        var project = _workspace.NewProject("host");
        var target = Path.Combine(_workspace.Root, "external-deps");
        _workspace.WriteFile(Path.Combine(target, "dep", "AGENTS.md"), "# vendored elsewhere\n");

        var junction = Path.Combine(project, "node_modules");
        if (!TryCreateJunction(junction, target))
        {
            Assert.Inconclusive("This host cannot create a directory junction.");
        }

        var snapshot = await _harness.RefreshAsync();

        CollectionAssert.AreEquivalent(
            new[] { "host" },
            snapshot.Projects.Select(p => p.Identity.Name).ToArray(),
            "The exclusion follows the name on disk; a junction must not route around it.");
    }

    [TestMethod]
    public async Task An_opt_in_beneath_a_junctioned_excluded_location_is_still_reached()
    {
        var project = _workspace.NewProject("host");
        var target = Path.Combine(_workspace.Root, "external-deps");
        _workspace.WriteFile(Path.Combine(target, "companion", "AGENTS.md"), "# opted in, behind a junction\n");
        _workspace.WriteFile(Path.Combine(target, "other-dep", "AGENTS.md"), "# still vendored\n");

        var junction = Path.Combine(project, "node_modules");
        if (!TryCreateJunction(junction, target))
        {
            Assert.Inconclusive("This host cannot create a directory junction.");
        }

        // Opted in the way the owner sees it on disk, not the way the junction resolves.
        _harness.Settings.Projects.Add(new ProjectRegistryEntry
        {
            Path = Path.Combine(junction, "companion"),
            NestedOptIn = true,
        });

        var snapshot = await _harness.RefreshAsync();

        CollectionAssert.AreEquivalent(
            new[] { "host", "companion" },
            snapshot.Projects.Select(p => p.Identity.Name).ToArray(),
            "An opt-in written as the owner sees it must survive a junction along the way.");
    }

    /// <summary>A junction needs no elevation, but a host may still refuse it.</summary>
    private static bool TryCreateJunction(string link, string target)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            ArgumentList = { "/c", "mklink", "/J", link, target },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

        process.WaitForExit();
        return process.ExitCode == 0 && Directory.Exists(link);
    }

    [TestMethod]
    public async Task Bounded_traversal_reports_truncation_instead_of_scanning_without_limit()
    {
        for (var i = 0; i < 5; i++)
        {
            _workspace.NewProject($"project-{i}");
        }

        _harness.Settings.MaxProjects = 2;
        var snapshot = await _harness.RefreshAsync();

        Assert.IsTrue(snapshot.ScanTruncated);
        Assert.IsTrue(snapshot.Diagnostics.Any(d => d.Code == Core.DiagnosticCode.ScanTruncated));
    }
}
