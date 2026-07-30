using MattWorkflowDashboard.App.ViewModels;
using MattWorkflowDashboard.Core;
using MattWorkflowDashboard.Infrastructure.Settings;
using MattWorkflowDashboard.Tests.TestSupport;

namespace MattWorkflowDashboard.Tests.RefreshSeam;

/// <summary>
/// Registry intent, proven the way the owner experiences it: a choice made in the Settings
/// window, written to the settings file, honoured by the next refresh, and still true after a
/// restart. Restarting here re-reads the real settings file rather than reusing the object the
/// choice was made on.
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
    public async Task A_first_seen_origin_is_confirmed_once_and_still_requires_confirmation_after_restart()
    {
        var project = _workspace.NewProject("widget");
        _workspace.InitGitRepository(project, Origin);
        _workspace.Commit(project, "initial");

        var view = (await _harness.RefreshAsync()).Project("widget");
        Assert.AreEqual("acme/widget", view.Origin!.Slug);
        Assert.AreEqual("acme/widget", PersistedEntry(project).ConfirmedOrigin, "The confirmed association must persist.");

        _workspace.Git(project, "remote", "set-url", "origin", "https://github.com/someone-else/other.git");
        _harness.Restart();

        var afterRestart = (await _harness.RefreshAsync()).Project("widget");

        Assert.IsTrue(
            afterRestart.HasDiagnostic(DiagnosticCode.OriginChanged),
            "After a restart the association is still the owner's confirmed one, so a changed remote is still a relink.");
        Assert.AreEqual("acme/widget", afterRestart.Origin!.Slug);
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
    }
}
