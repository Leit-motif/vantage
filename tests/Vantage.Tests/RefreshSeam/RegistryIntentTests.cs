using Vantage.Core;
using Vantage.Infrastructure.Settings;
using Vantage.Tests.TestSupport;

namespace Vantage.Tests.RefreshSeam;

/// <summary>
/// Registry intent as the engine keeps it: written when it is made, moved with a project whose
/// identity resolves elsewhere, held through a cancelled pass, and reported when an opt-in names
/// somewhere discovery cannot reach. Driven through <c>DashboardSettings</c> and
/// <c>SettingsStore</c> directly, so none of it needs a window or a registry key and all of it
/// runs wherever the engine does.
///
/// The same subject reached through the Settings view model is
/// <c>Vantage.Tests.Shell</c>'s <c>RegistryControlTests</c> — that class constructs a
/// <c>StartupRegistration</c>, which reads the Windows <c>Run</c> key, so it cannot live here.
/// </summary>
[TestClass]
public sealed class RegistryIntentTests
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

    private ProjectRegistryEntry PersistedEntry(string path) =>
        _harness.SettingsStore.Load().Settings.FindProject(path)
        ?? throw new AssertFailedException($"No persisted registry entry for '{path}'.");

    [TestMethod]
    public async Task Registry_intent_recorded_under_a_linked_root_follows_the_project_to_its_resolved_path()
    {
        // A configured root with a link above its final segment: the older behaviour resolved only
        // that final segment, so entries were recorded under the alias.
        var actual = Path.Combine(_workspace.Root, "actual");
        var project = Path.Combine(actual, "workspaces", "project");
        _workspace.WriteFile(Path.Combine(project, "AGENTS.md"), "# recorded under an alias\n");

        var alias = Path.Combine(_workspace.Root, "alias");
        if (!DirectoryLink.TryCreate(alias, actual))
        {
            Assert.Inconclusive("This host cannot create a directory link.");
        }

        var aliasedProject = Path.Combine(alias, "workspaces", "project");

        _harness.Settings.Roots.Clear();
        _harness.Settings.Roots.Add(Path.Combine(alias, "workspaces"));
        _harness.Settings.Projects.Add(new ProjectRegistryEntry
        {
            Path = aliasedProject,
            State = ProjectRegistryState.Hidden,
            ConfirmedOrigin = "acme/widget",
        });
        _harness.SettingsStore.Save(_harness.Settings);

        var snapshot = await _harness.RefreshAsync();

        Assert.AreEqual(0, snapshot.Projects.Count, "A choice recorded under the old name still has to be honoured.");

        var persisted = _harness.SettingsStore.Load().Settings;
        Assert.AreEqual(1, persisted.Projects.Count, "The intent moves to the resolved path; it is not duplicated beside it.");
        Assert.AreEqual(ProjectRegistryState.Hidden, persisted.Projects[0].State);
        Assert.AreEqual("acme/widget", persisted.Projects[0].ConfirmedOrigin);
        Assert.AreEqual(project, persisted.Projects[0].Path);
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

    /// <summary>
    /// The interleaving itself: a choice made while a write is already in flight. The atomic write
    /// stages a temp file first, and its appearance is the signal that the write has begun — so the
    /// change can be made inside the window without a hook in the product. If the window is missed
    /// the test reports inconclusive rather than passing on nothing.
    /// </summary>
    [TestMethod]
    public async Task A_choice_made_while_a_write_is_in_flight_is_not_reported_as_written()
    {
        var project = _workspace.NewProject("widget");
        await _harness.RefreshAsync();

        var settings = _harness.Settings;
        var entry = settings.FindProject(project)!;

        // Enough bulk that staging the file takes long enough to act inside. Only a scalar on an
        // existing entry is touched later, never the collection a save is enumerating.
        for (var i = 0; i < 20_000; i++)
        {
            settings.Projects.Add(new ProjectRegistryEntry { Path = $@"C:\fixture\filler-{i}" });
        }

        settings.MarkChanged();
        _harness.SettingsStore.Save(settings);
        Assert.IsFalse(settings.HasUnsavedChanges, "Everything is written before the interleaving starts.");

        var staged = _harness.SettingsStore.FilePath + ".tmp";
        var pinnedBefore = entry.Pinned;
        var observedWriteInFlight = false;

        var saving = Task.Run(() =>
        {
            settings.MarkChanged();
            _harness.SettingsStore.Save(settings);
        });

        while (!saving.IsCompleted)
        {
            if (File.Exists(staged))
            {
                observedWriteInFlight = true;
                break;
            }
        }

        // The choice the owner makes while that write is in flight.
        entry.Pinned = !pinnedBefore;
        settings.MarkChanged();

        await saving;

        if (!observedWriteInFlight)
        {
            Assert.Inconclusive("The write finished before the change could be made inside it.");
        }

        Assert.IsTrue(
            settings.HasUnsavedChanges,
            "A choice made during a write must stay outstanding: that write did not capture it.");

        _harness.SettingsStore.Save(settings);
        Assert.AreEqual(
            entry.Pinned,
            _harness.SettingsStore.Load().Settings.FindProject(project)!.Pinned,
            "And the save that follows it does capture it.");
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
}
