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
    public async Task Excludes_nested_projects_by_default_and_includes_them_once_opted_in()
    {
        var project = _workspace.NewProject("host");
        var nested = Path.Combine(project, "tools", "companion");
        _workspace.WriteFile(Path.Combine(nested, "AGENTS.md"), "# nested but independent\n");

        var first = await _harness.RefreshAsync();
        Assert.AreEqual(1, first.Projects.Count, "A nested project is excluded until it is opted in.");

        _harness.Settings.FindProject(nested)!.NestedOptIn = true;
        var second = await _harness.RefreshAsync();

        CollectionAssert.AreEquivalent(
            new[] { "host", "companion" },
            second.Projects.Select(p => p.Identity.Name).ToArray());
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
