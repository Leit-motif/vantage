using Vantage.App.ViewModels;
using Vantage.Core;
using Vantage.Infrastructure.Settings;
using Vantage.Tests.TestSupport;

namespace Vantage.Tests.RefreshSeam;

/// <summary>
/// Registry intent along the path a choice actually travels: made through the Settings view model,
/// written to a real settings file, honoured by the next refresh, and still true after a restart
/// that re-reads that file rather than reusing the object the choice was made on.
///
/// These are automated tests over temporary fixtures. They drive the view model and the refresh
/// boundary, not the running WPF application, and they prove nothing about the real configured
/// roots — live-root and running-shell acceptance belong to their own tickets.
///
/// Refresh-seam subject matter, in the shell suite, and only for the half that needs to be:
/// <see cref="SettingsViewModel"/> builds a <c>StartupRegistration</c> and reads the Windows
/// <c>Run</c> key on construction, so every test that opens Settings is a Windows test whatever
/// it is about. The half that never opens Settings is portable and lives in
/// <c>Vantage.Tests</c>'s <c>RegistryIntentTests</c>.
/// </summary>
[TestClass]
public sealed class RegistryControlTests
{
    private const string Origin = "https://github.com/acme/widget.git";

    private WorkspaceFixture _workspace = null!;
    private FakeProcessRunner _runner = null!;
    private RefreshHarness _harness = null!;

    [TestInitialize]
    public void SetUp()
    {
        _workspace = new WorkspaceFixture();
        _runner = new FakeProcessRunner().GhAuthenticated().GhIssues(Fixtures.GhIssues());
        _harness = new RefreshHarness(_workspace, _runner, DateTimeOffset.UtcNow).WithRealGit();
    }

    [TestCleanup]
    public void TearDown()
    {
        _harness.Dispose();
        _workspace.Dispose();
    }

    private SettingsViewModel OpenSettings() => new(_harness.Settings, _harness.SettingsStore, () => { });

    private ProjectRegistryEntry PersistedEntry(string path) =>
        _harness.SettingsStore.Load().Settings.FindProject(path)
        ?? throw new AssertFailedException($"No persisted registry entry for '{path}'.");

    [TestMethod]
    public async Task Hiding_a_project_persists_survives_restart_and_keeps_the_entry()
    {
        _workspace.NewProject("visible");
        var hidden = _workspace.NewProject("hidden");
        await _harness.RefreshAsync();

        var settings = OpenSettings();
        settings.Projects.Single(p => p.Path == hidden).State = ProjectRegistryState.Hidden;

        Assert.AreEqual(ProjectRegistryState.Hidden, PersistedEntry(hidden).State, "A registry choice must be written when it is made.");

        _harness.Restart();
        var snapshot = await _harness.RefreshAsync();

        Assert.AreEqual(1, snapshot.Projects.Count);
        Assert.AreEqual("visible", snapshot.Projects[0].Identity.Name);
        Assert.AreEqual(ProjectRegistryState.Hidden, PersistedEntry(hidden).State, "Hiding must preserve registry intent, not forget the project.");
    }

    [TestMethod]
    public async Task Excluding_a_project_persists_and_survives_restart()
    {
        _workspace.NewProject("wanted");
        var excluded = _workspace.NewProject("unwanted");
        await _harness.RefreshAsync();

        var settings = OpenSettings();
        settings.Projects.Single(p => p.Path == excluded).State = ProjectRegistryState.Excluded;

        _harness.Restart();
        var snapshot = await _harness.RefreshAsync();

        Assert.AreEqual(1, snapshot.Projects.Count);
        Assert.AreEqual("wanted", snapshot.Projects[0].Identity.Name);
        Assert.AreEqual(ProjectRegistryState.Excluded, PersistedEntry(excluded).State);
    }

    [TestMethod]
    public async Task An_enabled_choice_brings_a_hidden_project_back_on_the_next_refresh()
    {
        var project = _workspace.NewProject("returning");
        await _harness.RefreshAsync();

        var hide = OpenSettings();
        hide.Projects.Single(p => p.Path == project).State = ProjectRegistryState.Hidden;
        Assert.AreEqual(0, (await _harness.RefreshAsync()).Projects.Count);

        _harness.Restart();
        var show = OpenSettings();
        show.Projects.Single(p => p.Path == project).State = ProjectRegistryState.Enabled;

        var snapshot = await _harness.RefreshAsync();

        Assert.AreEqual("returning", snapshot.Project("returning").Identity.Name);
        Assert.AreEqual(ProjectRegistryState.Enabled, PersistedEntry(project).State);

        // Enabling is a choice like any other: it has to survive a restart of its own, not just
        // affect the refresh that followed it.
        _harness.Restart();
        var afterRestart = await _harness.RefreshAsync();

        Assert.AreEqual("returning", afterRestart.Project("returning").Identity.Name, "The enabled choice has to survive a restart too.");
        Assert.AreEqual(ProjectRegistryState.Enabled, PersistedEntry(project).State);
    }

    [TestMethod]
    public async Task Hiding_takes_effect_on_the_next_refresh_before_any_restart()
    {
        _workspace.NewProject("visible");
        var hidden = _workspace.NewProject("hidden");
        await _harness.RefreshAsync();

        var settings = OpenSettings();
        settings.Projects.Single(p => p.Path == hidden).State = ProjectRegistryState.Hidden;

        var snapshot = await _harness.RefreshAsync();

        Assert.AreEqual(1, snapshot.Projects.Count, "The choice affects the very next refresh, not only the one after a restart.");
        Assert.AreEqual("visible", snapshot.Projects[0].Identity.Name);
    }

    [TestMethod]
    public async Task An_excluded_choice_takes_effect_on_the_next_refresh_before_any_restart()
    {
        _workspace.NewProject("wanted");
        var excluded = _workspace.NewProject("unwanted");
        await _harness.RefreshAsync();

        var settings = OpenSettings();
        settings.Projects.Single(p => p.Path == excluded).State = ProjectRegistryState.Excluded;

        var snapshot = await _harness.RefreshAsync();

        Assert.AreEqual(1, snapshot.Projects.Count);
        Assert.AreEqual("wanted", snapshot.Projects[0].Identity.Name);
    }

    [TestMethod]
    public async Task A_nested_opt_in_takes_effect_on_the_next_refresh_before_any_restart()
    {
        var host = _workspace.NewProject("host");
        var vendored = Path.Combine(host, "node_modules", "companion");
        _workspace.WriteFile(Path.Combine(vendored, "AGENTS.md"), "# under a vendor tree\n");

        var first = await _harness.RefreshAsync();
        Assert.AreEqual(1, first.Projects.Count);

        var settings = OpenSettings();
        settings.NewNestedProject = vendored;
        settings.AddNestedProjectCommand.Execute(null);

        var second = await _harness.RefreshAsync();

        CollectionAssert.AreEquivalent(
            new[] { "host", "companion" },
            second.Projects.Select(p => p.Identity.Name).ToArray(),
            "The opt-in affects the very next refresh, with no restart in between.");
    }

    [TestMethod]
    public async Task A_nested_opt_in_added_in_Settings_persists_and_is_discovered_after_restart()
    {
        var host = _workspace.NewProject("host");
        var vendored = Path.Combine(host, "node_modules", "companion");
        _workspace.WriteFile(Path.Combine(vendored, "AGENTS.md"), "# independent, but under a vendor tree\n");

        await _harness.RefreshAsync();

        var settings = OpenSettings();
        settings.NewNestedProject = vendored;
        settings.AddNestedProjectCommand.Execute(null);

        Assert.IsTrue(PersistedEntry(vendored).NestedOptIn, "The opt-in must be written when it is made.");

        _harness.Restart();
        var snapshot = await _harness.RefreshAsync();

        CollectionAssert.AreEquivalent(
            new[] { "host", "companion" },
            snapshot.Projects.Select(p => p.Identity.Name).ToArray(),
            "An opt-in recorded in Settings must still be honoured after a restart.");
    }

    [TestMethod]
    public async Task An_opt_in_typed_as_a_junction_route_keeps_both_its_identity_and_its_route()
    {
        var host = _workspace.NewProject("host");
        var target = Path.Combine(_workspace.Root, "external-companion");
        _workspace.WriteFile(Path.Combine(target, "AGENTS.md"), "# independent, and itself a junction\n");

        var typed = Path.Combine(host, "node_modules", "companion");
        Directory.CreateDirectory(Path.Combine(host, "node_modules"));
        if (!DirectoryLink.TryCreate(typed, target))
        {
            Assert.Inconclusive("This host cannot create a directory link.");
        }

        await _harness.RefreshAsync();

        var settings = OpenSettings();
        settings.NewNestedProject = typed;
        settings.AddNestedProjectCommand.Execute(null);

        var entry = PersistedEntry(target);
        Assert.IsTrue(entry.NestedOptIn);
        Assert.AreEqual(typed, entry.OptInPath, "The route the owner typed is what discovery can walk.");

        _harness.Restart();
        var snapshot = await _harness.RefreshAsync();

        CollectionAssert.AreEquivalent(
            new[] { host, target },
            snapshot.Projects.Select(p => p.Identity.CanonicalPath).ToArray(),
            "A project that is itself a junction out of an excluded location is still reached after a restart.");
    }

    [TestMethod]
    public async Task An_opt_in_typed_through_a_junctioned_excluded_location_is_stored_by_where_it_lives()
    {
        var host = _workspace.NewProject("host");
        var target = Path.Combine(_workspace.Root, "external-deps");
        _workspace.WriteFile(Path.Combine(target, "companion", "AGENTS.md"), "# a plain directory inside a linked tree\n");

        if (!DirectoryLink.TryCreate(Path.Combine(host, "node_modules"), target))
        {
            Assert.Inconclusive("This host cannot create a directory link.");
        }

        await _harness.RefreshAsync();

        var typed = Path.Combine(host, "node_modules", "companion");
        var settings = OpenSettings();
        settings.NewNestedProject = typed;
        settings.AddNestedProjectCommand.Execute(null);

        // The junction is above the typed path, so only resolving every segment gets the identity
        // discovery will emit.
        var entry = PersistedEntry(Path.Combine(target, "companion"));
        Assert.IsTrue(entry.NestedOptIn);
        Assert.AreEqual(typed, entry.OptInPath);

        _harness.Restart();
        var snapshot = await _harness.RefreshAsync();

        CollectionAssert.AreEquivalent(
            new[] { host, Path.Combine(target, "companion") },
            snapshot.Projects.Select(p => p.Identity.CanonicalPath).ToArray(),
            "The opt-in must land on the identity discovery emits, not on an alias beside it.");
    }

    [TestMethod]
    public async Task Opting_in_a_project_that_is_already_registered_records_its_route_instead_of_dropping_it()
    {
        var host = _workspace.NewProject("host");

        // A second root that holds the project directly, so it is registered before any opt-in.
        var directRoot = Path.Combine(_workspace.Root, "direct");
        var companion = Path.Combine(directRoot, "companion");
        _workspace.WriteFile(Path.Combine(companion, "AGENTS.md"), "# reachable two ways\n");
        _harness.Settings.Roots.Add(directRoot);

        if (!DirectoryLink.TryCreate(Path.Combine(host, "node_modules"), directRoot))
        {
            Assert.Inconclusive("This host cannot create a directory link.");
        }

        await _harness.RefreshAsync();
        Assert.IsFalse(PersistedEntry(companion).NestedOptIn, "It was found directly, so nothing was opted in.");

        var typed = Path.Combine(host, "node_modules", "companion");
        var settings = OpenSettings();
        settings.NewNestedProject = typed;
        settings.AddNestedProjectCommand.Execute(null);

        var entry = PersistedEntry(companion);
        Assert.IsTrue(entry.NestedOptIn, "The opt-in must land on the entry that already exists.");
        Assert.AreEqual(typed, entry.OptInPath);
        Assert.AreEqual(
            1,
            _harness.SettingsStore.Load().Settings.Projects.Count(p =>
                string.Equals(p.Path, companion, StringComparison.OrdinalIgnoreCase)),
            "One place on disk keeps one registry entry.");

        // The direct route goes away; the recorded route is now the only way there.
        _harness.Settings.Roots.Remove(directRoot);
        _harness.SettingsStore.Save(_harness.Settings);
        _harness.Restart();

        var snapshot = await _harness.RefreshAsync();

        CollectionAssert.Contains(
            snapshot.Projects.Select(p => p.Identity.CanonicalPath).ToArray(),
            companion,
            "With the direct root gone, the route recorded on the opt-in has to still reach it.");
    }

    [TestMethod]
    public async Task A_relink_confirms_the_origin_on_screen_even_when_a_refresh_has_moved_on()
    {
        var project = _workspace.NewProject("widget");
        _workspace.InitGitRepository(project, Origin);
        _workspace.Commit(project, "initial");
        await _harness.RefreshAsync();

        _workspace.Git(project, "remote", "set-url", "origin", "https://github.com/someone-else/other.git");
        await _harness.RefreshAsync();

        // The Settings window is open, showing 'someone-else/other'.
        var settings = OpenSettings();
        var row = settings.Projects.Single(p => p.Path == project);
        Assert.AreEqual("someone-else/other", row.PendingOrigin);

        // A refresh behind the window finds a third remote before the owner clicks.
        _workspace.Git(project, "remote", "set-url", "origin", "https://github.com/third/party.git");
        await _harness.RefreshAsync();

        row.ConfirmRelinkCommand.Execute(null);

        Assert.AreEqual(
            "someone-else/other",
            PersistedEntry(project).ConfirmedOrigin,
            "Confirming adopts what was on screen, never an origin the owner never saw.");
        Assert.AreEqual(
            "third/party",
            PersistedEntry(project).PendingOrigin,
            "The newer remote is still waiting on the owner.");
    }

    [TestMethod]
    public async Task A_settings_write_that_fails_stays_pending_and_is_written_by_the_next_refresh()
    {
        var hidden = _workspace.NewProject("hidden");
        await _harness.RefreshAsync();

        var settings = OpenSettings();

        // Hold the settings file open so the atomic swap cannot complete. Reads still work, so the
        // test can see that nothing reached the file.
        using (var _ = new FileStream(
            _harness.SettingsStore.FilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read))
        {
            settings.Projects.Single(p => p.Path == hidden).State = ProjectRegistryState.Hidden;

            Assert.AreNotEqual(string.Empty, settings.SaveError, "A failed write must say so rather than look like it worked.");
            Assert.IsTrue(_harness.Settings.HasUnsavedChanges, "The change the owner made is still outstanding.");
            Assert.AreEqual(
                ProjectRegistryState.Enabled,
                PersistedEntry(hidden).State,
                "Nothing reached the file while it was locked.");
        }

        await _harness.RefreshAsync();

        Assert.AreEqual(
            ProjectRegistryState.Hidden,
            PersistedEntry(hidden).State,
            "The next refresh retries the write rather than losing the choice.");
    }

    [TestMethod]
    public async Task A_confirmed_relink_adopts_the_origin_the_owner_was_shown_and_survives_restart()
    {
        var project = _workspace.NewProject("widget");
        _workspace.InitGitRepository(project, Origin);
        _workspace.Commit(project, "initial");
        await _harness.RefreshAsync();

        _workspace.Git(project, "remote", "set-url", "origin", "https://github.com/someone-else/other.git");
        var pending = (await _harness.RefreshAsync()).Project("widget");
        Assert.IsTrue(pending.HasDiagnostic(DiagnosticCode.OriginChanged));
        Assert.AreEqual("acme/widget", pending.Origin!.Slug, "Nothing is adopted before the owner confirms.");

        // The remote moves again before the owner acts. Confirming must adopt what they were
        // shown, never whatever the remote happens to say at the moment of the click.
        _workspace.Git(project, "remote", "set-url", "origin", "https://github.com/third/party.git");

        var settings = OpenSettings();
        settings.Projects.Single(p => p.Path == project).ConfirmRelinkCommand.Execute(null);

        Assert.AreEqual("someone-else/other", PersistedEntry(project).ConfirmedOrigin, "A confirmed relink must be written.");

        _harness.Restart();
        var confirmed = (await _harness.RefreshAsync()).Project("widget");

        Assert.AreEqual("someone-else/other", confirmed.Origin!.Slug, "The confirmed association must survive a restart and refresh.");
        Assert.IsTrue(
            confirmed.HasDiagnostic(DiagnosticCode.OriginChanged),
            "The remote has moved on again, so the next relink is still waiting on the owner.");

        // Refreshing on the confirmed association means querying it: the slug the owner confirmed
        // is the repository this refresh actually read from.
        var lastIssueQuery = _runner.Invocations
            .Where(i => i.FileName == "gh" && i.Arguments.Contains("issue"))
            .Last()
            .Arguments
            .ToList();

        Assert.AreEqual(
            "someone-else/other",
            lastIssueQuery[lastIssueQuery.IndexOf("--repo") + 1],
            "GitHub evidence must be fetched for the confirmed association, not the old one or the pending one.");
    }
}
