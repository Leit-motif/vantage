using MattWorkflowDashboard.App.ViewModels;
using MattWorkflowDashboard.Core;
using MattWorkflowDashboard.Infrastructure.Settings;
using MattWorkflowDashboard.Tests.TestSupport;

namespace MattWorkflowDashboard.Tests.RefreshSeam;

/// <summary>
/// Registry intent along the path a choice actually travels: made through the Settings view model,
/// written to a real settings file, honoured by the next refresh, and still true after a restart
/// that re-reads that file rather than reusing the object the choice was made on.
///
/// These are automated tests over temporary fixtures. They drive the view model and the refresh
/// boundary, not the running WPF application, and they prove nothing about the real configured
/// roots — live-root and running-shell acceptance belong to their own tickets.
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
    public async Task An_opt_in_typed_as_a_junction_route_keeps_both_its_identity_and_its_route()
    {
        var host = _workspace.NewProject("host");
        var target = Path.Combine(_workspace.Root, "external-companion");
        _workspace.WriteFile(Path.Combine(target, "AGENTS.md"), "# independent, and itself a junction\n");

        var typed = Path.Combine(host, "node_modules", "companion");
        Directory.CreateDirectory(Path.Combine(host, "node_modules"));
        if (!DiscoveryTests.TryCreateJunction(typed, target))
        {
            Assert.Inconclusive("This host cannot create a directory junction.");
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
    public async Task A_pending_relink_survives_a_repository_whose_remote_cannot_be_read()
    {
        var project = _workspace.NewProject("widget");
        _workspace.InitGitRepository(project, Origin);
        _workspace.Commit(project, "initial");
        await _harness.RefreshAsync();

        _workspace.Git(project, "remote", "set-url", "origin", "https://github.com/someone-else/other.git");
        await _harness.RefreshAsync();
        Assert.AreEqual("someone-else/other", PersistedEntry(project).PendingOrigin);

        // The remote goes away entirely — a detached checkout, a pruned remote, an unreadable repo.
        _workspace.Git(project, "remote", "remove", "origin");
        await _harness.RefreshAsync();

        Assert.AreEqual(
            "someone-else/other",
            PersistedEntry(project).PendingOrigin,
            "Not being able to read the remote must not cancel a relink that is waiting on the owner.");
        Assert.AreEqual("acme/widget", PersistedEntry(project).ConfirmedOrigin);
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
    public async Task A_project_discovered_by_a_cancelled_refresh_is_still_registered_after_restart()
    {
        var project = _workspace.NewProject("widget");
        _workspace.InitGitRepository(project, Origin);
        _workspace.Commit(project, "initial");

        // Cancel once indexing has started, the way a newer refresh or a watcher burst supersedes
        // a pass that has already registered what it discovered.
        using var cancelled = new CancellationTokenSource();
        _runner.When(
            (name, _) => name == "git",
            () =>
            {
                cancelled.Cancel();
                throw new OperationCanceledException(cancelled.Token);
            });

        try
        {
            await _harness.RefreshAsync(cancelled.Token);
            Assert.Fail("The refresh was expected to be cancelled.");
        }
        catch (OperationCanceledException)
        {
            // The superseded pass is the point of the test.
        }

        _harness.Restart();

        Assert.AreEqual(
            ProjectRegistryState.Enabled,
            PersistedEntry(project).State,
            "A superseded refresh must not drop the registry intent it already produced.");
    }

    [TestMethod]
    public async Task A_project_registered_before_a_cancelled_session_check_is_still_registered_after_restart()
    {
        var project = _workspace.NewProject("widget");

        // Cancellation lands on the gh session check, before any project is indexed — the earliest
        // point at which the registry has already changed.
        using var cancelled = new CancellationTokenSource();
        var cancellingRunner = new FakeProcessRunner().When(
            (name, args) => name == "gh" && args.Contains("auth"),
            () =>
            {
                cancelled.Cancel();
                throw new OperationCanceledException(cancelled.Token);
            });

        using var cancelledRun = new RefreshHarness(_workspace, cancellingRunner, DateTimeOffset.UtcNow);

        try
        {
            await cancelledRun.RefreshAsync(cancelled.Token);
            Assert.Fail("The refresh was expected to be cancelled.");
        }
        catch (OperationCanceledException)
        {
            // The point of the test.
        }

        Assert.AreEqual(
            ProjectRegistryState.Enabled,
            PersistedEntry(project).State,
            "Cancellation during the session check must not drop a project already registered.");
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
    public async Task A_save_reports_written_only_for_the_revision_it_captured()
    {
        var project = _workspace.NewProject("widget");
        await _harness.RefreshAsync();

        var settings = _harness.Settings;
        var beforeChange = settings.Revision;

        settings.FindProject(project)!.Pinned = true;
        settings.MarkChanged();
        Assert.IsTrue(settings.HasUnsavedChanges);

        _harness.SettingsStore.Save(settings);

        Assert.IsFalse(settings.HasUnsavedChanges, "A completed save covers what it captured.");
        Assert.AreNotEqual(beforeChange, settings.SavedRevision, "And it records which revision that was.");
        Assert.AreEqual(
            settings.Revision,
            settings.SavedRevision,
            "A save reports written only for the revision it read, so a later change stays outstanding.");
    }

    [TestMethod]
    public async Task Persisting_registry_intent_writes_only_under_the_dashboard_s_own_app_data()
    {
        var project = _workspace.NewProject("widget");
        _workspace.InitGitRepository(project, Origin);
        _workspace.Commit(project, "initial");

        var before = WorkspaceFixture.Fingerprint(_workspace.WorkspacesRoot, excludingSegment: ".git");
        await _harness.RefreshAsync();

        Assert.AreEqual("acme/widget", PersistedEntry(project).ConfirmedOrigin, "The refresh did write registry intent.");
        Assert.AreEqual(
            before,
            WorkspaceFixture.Fingerprint(_workspace.WorkspacesRoot, excludingSegment: ".git"),
            "Registry intent belongs under the dashboard's own app data, never in a monitored root.");
        Assert.IsTrue(
            _harness.SettingsStore.FilePath.StartsWith(_harness.Paths.Root, StringComparison.OrdinalIgnoreCase),
            "Settings must live under the dashboard's local application data.");
    }

    [TestMethod]
    public async Task An_opt_in_that_discovery_cannot_reach_is_reported_rather_than_silent()
    {
        _workspace.NewProject("host");
        var missing = Path.Combine(_workspace.WorkspacesRoot, "host", "node_modules", "typo");
        _harness.Settings.Projects.Add(new ProjectRegistryEntry { Path = missing, NestedOptIn = true });

        var snapshot = await _harness.RefreshAsync();

        Assert.IsTrue(
            snapshot.Diagnostics.Any(d => d.Code == DiagnosticCode.ProjectScanFailed && d.Locator == missing),
            "An opt-in that never resolves to a project must say so.");
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
