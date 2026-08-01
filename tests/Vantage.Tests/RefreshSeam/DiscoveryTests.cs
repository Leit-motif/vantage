using Vantage.Infrastructure.Settings;
using Vantage.Tests.TestSupport;

namespace Vantage.Tests.RefreshSeam;

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

    /// <summary>
    /// A fresh install must not walk a directory nobody named. The shipped default is empty, and the
    /// first refresh has to say so rather than presenting an empty dashboard as a finished scan.
    /// </summary>
    [TestMethod]
    public async Task A_fresh_install_scans_nothing_and_says_why()
    {
        Assert.AreEqual(
            0,
            new DashboardSettings().Roots.Count,
            "The shipped default must not name a directory on the author's machine.");

        _workspace.NewProject("would-have-been-found");
        _harness.Settings.Roots.Clear();

        var snapshot = await _harness.RefreshAsync();

        Assert.AreEqual(0, snapshot.Projects.Count);
        Assert.IsTrue(
            snapshot.Diagnostics.Any(d => d.Code == Core.DiagnosticCode.NoRootsConfigured),
            "Scanning nothing has to be explained, not shown as an empty workspace.");
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

    /// <summary>
    /// A checkout is how code arrives on a machine, not evidence that it is work the owner is
    /// doing. Vendored dependencies, cloned tools and reference material are all repositories, and
    /// a dashboard listing them is answering a question nobody asked.
    /// </summary>
    [TestMethod]
    public async Task A_repository_on_its_own_is_a_checkout_rather_than_a_project()
    {
        var cloned = Path.Combine(_workspace.WorkspacesRoot, "some-dependency");
        Directory.CreateDirectory(Path.Combine(cloned, ".git"));
        _workspace.WriteFile(Path.Combine(cloned, "README.md"), "# a tool someone cloned\n");

        _workspace.NewProject("actual-work");

        var snapshot = await _harness.RefreshAsync();

        CollectionAssert.AreEquivalent(
            new[] { "actual-work" },
            snapshot.Projects.Select(p => p.Identity.Name).ToArray(),
            "A bare repository is not somebody having set a place up to be worked in.");
    }

    /// <summary>
    /// The rule is about what qualifies, not about what is read: the marker still has to be
    /// detected, because it is what the git adapter reads a project's activity from.
    /// </summary>
    [TestMethod]
    public async Task A_repository_that_is_also_a_project_still_reports_its_git()
    {
        var project = _workspace.NewProject("tracked-and-versioned");
        _workspace.InitGitRepository(project);
        _workspace.Commit(project, "first");

        // Against real git: whether the marker is still read is exactly the claim being made, and
        // a faked runner would answer it either way.
        var snapshot = await _harness.WithRealGit().RefreshAsync();

        Assert.AreEqual(1, snapshot.Projects.Count);
        Assert.IsTrue(
            snapshot.Projects[0].GitAvailable,
            "Dropping .git as a qualifier must not stop it being read where the project qualifies anyway.");
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
    public async Task Every_excluded_category_keeps_its_nested_projects_out_by_default()
    {
        var project = _workspace.NewProject("host");

        // One representative directory per category the policy names.
        (string Category, string Directory)[] categories =
        [
            ("vendor", "vendor"),
            ("dependency", "node_modules"),
            ("tool", ".gradle"),
            ("build", "obj"),
            ("cache", ".cache"),
        ];

        foreach (var (category, directory) in categories)
        {
            _workspace.WriteFile(
                Path.Combine(project, directory, $"{category}-project", "AGENTS.md"),
                $"# a project inside a {category} location\n");
        }

        var snapshot = await _harness.RefreshAsync();

        CollectionAssert.AreEquivalent(
            new[] { "host" },
            snapshot.Projects.Select(p => p.Identity.Name).ToArray(),
            "Vendor, dependency, tool, build, and cache locations each keep their nested projects out.");
    }

    [TestMethod]
    public async Task Every_excluded_category_can_be_opted_back_into()
    {
        var project = _workspace.NewProject("host");
        string[] directories = ["vendor", "node_modules", ".gradle", "obj", ".cache"];

        foreach (var directory in directories)
        {
            var nested = Path.Combine(project, directory, "companion");
            _workspace.WriteFile(Path.Combine(nested, "AGENTS.md"), $"# opted in under {directory}\n");
            _harness.Settings.Projects.Add(new ProjectRegistryEntry { Path = nested, NestedOptIn = true });
        }

        var snapshot = await _harness.RefreshAsync();

        Assert.AreEqual(
            directories.Length + 1,
            snapshot.Projects.Count,
            "An opt-in must reach into every excluded category, not only some of them.");
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
    public async Task An_opt_in_beneath_a_junctioned_excluded_location_is_reached_by_its_route()
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

        // Identity is where the project physically is; the route is how the owner reaches it.
        _harness.Settings.Projects.Add(new ProjectRegistryEntry
        {
            Path = Path.Combine(target, "companion"),
            OptInPath = Path.Combine(junction, "companion"),
            NestedOptIn = true,
        });

        var snapshot = await _harness.RefreshAsync();

        CollectionAssert.AreEquivalent(
            new[] { project, Path.Combine(target, "companion") },
            snapshot.Projects.Select(p => p.Identity.CanonicalPath).ToArray(),
            "The route reaches it, and one place on disk stays one identity.");
    }

    [TestMethod]
    public async Task A_junctioned_excluded_location_still_reaches_an_opt_in_recorded_physically()
    {
        var project = _workspace.NewProject("host");
        var target = Path.Combine(_workspace.Root, "external-deps");
        _workspace.WriteFile(Path.Combine(target, "companion", "AGENTS.md"), "# opted in, recorded where it lives\n");

        if (!TryCreateJunction(Path.Combine(project, "node_modules"), target))
        {
            Assert.Inconclusive("This host cannot create a directory junction.");
        }

        // Only the physical path is registered — the walk has to notice that the excluded junction
        // points at it.
        _harness.Settings.Projects.Add(new ProjectRegistryEntry
        {
            Path = Path.Combine(target, "companion"),
            NestedOptIn = true,
        });

        var snapshot = await _harness.RefreshAsync();

        CollectionAssert.AreEquivalent(
            new[] { project, Path.Combine(target, "companion") },
            snapshot.Projects.Select(p => p.Identity.CanonicalPath).ToArray(),
            "An opt-in recorded where the project physically lives must still be reached through the junction.");
    }

    [TestMethod]
    public async Task A_second_route_to_a_directory_can_still_reach_what_only_it_can_see()
    {
        // The target sits at the depth limit under its own root, so its children are out of reach
        // that way; the junction under the other root is the only route that can see them.
        var deepRoot = Path.Combine(_workspace.Root, "deep");
        var target = Path.Combine(deepRoot, "a", "b", "target");
        _workspace.WriteFile(Path.Combine(target, "companion", "AGENTS.md"), "# only reachable through the junction\n");

        var project = _workspace.NewProject("host");
        if (!TryCreateJunction(Path.Combine(project, "node_modules"), target))
        {
            Assert.Inconclusive("This host cannot create a directory junction.");
        }

        _harness.Settings.MaxDiscoveryDepth = 3;
        _harness.Settings.Roots.Insert(0, deepRoot);
        _harness.Settings.Projects.Add(new ProjectRegistryEntry
        {
            Path = Path.Combine(target, "companion"),
            OptInPath = Path.Combine(project, "node_modules", "companion"),
            NestedOptIn = true,
        });

        var snapshot = await _harness.RefreshAsync();

        CollectionAssert.Contains(
            snapshot.Projects.Select(p => p.Identity.CanonicalPath).ToArray(),
            Path.Combine(target, "companion"),
            "Having walked a directory by one route must not discard the route that can go further.");
        Assert.IsFalse(
            snapshot.Diagnostics.Any(d => d.Message.Contains("was not reached", StringComparison.Ordinal)),
            "Nothing should be reported unreachable when a route to it exists.");
    }

    /// <summary>A junction needs no elevation, but a host may still refuse it.</summary>
    internal static bool TryCreateJunction(string link, string target)
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
