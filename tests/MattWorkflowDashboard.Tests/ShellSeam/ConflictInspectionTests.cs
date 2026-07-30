using System.Windows;
using System.Windows.Automation;
using MattWorkflowDashboard.App.ViewModels;
using MattWorkflowDashboard.App.Views;
using MattWorkflowDashboard.Infrastructure;
using MattWorkflowDashboard.Infrastructure.Persistence;
using MattWorkflowDashboard.Infrastructure.Processes;
using MattWorkflowDashboard.Infrastructure.Settings;
using MattWorkflowDashboard.Tests.TestSupport;

// WinForms is referenced for the tray icon; here the WPF control is meant.
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;

namespace MattWorkflowDashboard.Tests.ShellSeam;

/// <summary>
/// What the owner can see and reach when explicitly linked sources disagree. These assert the
/// real window's own content tree, laid out but never shown, so a disagreement's evidence is
/// proven to be on screen rather than merely present in a view model.
/// </summary>
[TestClass]
public sealed class ConflictInspectionTests
{
    private const string Origin = "https://github.com/acme/widget.git";

    private WorkspaceFixture _workspace = null!;
    private FakeProcessRunner _runner = null!;
    private BoundedProcessRunner _realProcesses = null!;
    private DashboardCache _cache = null!;
    private DashboardSettings _settings = null!;
    private DashboardViewModel _viewModel = null!;

    [TestInitialize]
    public void SetUp()
    {
        _workspace = new WorkspaceFixture();
        _realProcesses = new BoundedProcessRunner(4, TimeSpan.FromSeconds(30));
        _runner = new FakeProcessRunner().GhAuthenticated();
        _runner.Fallback = _realProcesses;

        _cache = DashboardCache.Open(_workspace.CacheFile);
        _settings = new DashboardSettings
        {
            Roots = [_workspace.WorkspacesRoot],
            GitHubEnrichmentEnabled = true,
        };

        var paths = new AppPaths(Path.Combine(_workspace.Root, "appdata"));
        _viewModel = new DashboardViewModel(_settings, new SettingsStore(paths), _cache, _runner);
    }

    [TestCleanup]
    public void TearDown()
    {
        _cache.Dispose();
        _realProcesses.Dispose();
        _workspace.Dispose();
    }

    /// <summary>
    /// A project whose local tickets are explicitly linked to GitHub issues that disagree with
    /// them. The titles differ, so every conflict has both a local and a remote value to show.
    /// </summary>
    private void DisagreeingProject(params (string LocalTitle, string RemoteTitle, int Number)[] tickets)
    {
        var project = _workspace.NewProject("widget");
        var effort = _workspace.NewEffort(project, "feature");

        var index = 1;
        foreach (var ticket in tickets)
        {
            _workspace.WriteTicket(
                effort,
                $"{index:000}.md",
                Fixtures.Ticket(ticket.LocalTitle, "ready", gitHub: $"#{ticket.Number}"));
            index++;
        }

        _workspace.InitGitRepository(project, Origin);
        _workspace.Commit(project, "add planning");

        _runner.GhIssues(Fixtures.GhIssues(
            [.. tickets.Select(t => new Fixtures.GhIssue(
                t.Number,
                t.RemoteTitle,
                "OPEN",
                [],
                DateTimeOffset.UtcNow.ToString("O")))]));
    }

    /// <summary>
    /// Refreshes before the window exists, so the collections the window binds to are only ever
    /// mutated on the thread that owns them afterwards.
    /// </summary>
    private ProjectItemViewModel Refresh()
    {
        _viewModel.Filter = DashboardFilter.AllRemaining;
        _viewModel.RefreshCommand.Execute(null);
        _viewModel.RefreshCommand.ExecutionTask!.GetAwaiter().GetResult();

        return _viewModel.AllProjects.Single(p => p.Name == "widget");
    }

    private FrameworkElement Shell(bool expanded)
    {
        _viewModel.IsExpanded = expanded;

        return WpfTestHost.Run(() => WpfTestHost.HostContent(
            new DashboardWindow(_viewModel, _settings, () => { }),
            expanded ? 720 : 380,
            620));
    }

    /// <summary>
    /// A control the owner can actually reach: on screen, invokable, and announced by a name that
    /// says what it inspects.
    /// </summary>
    private static ButtonBase? ConflictControl(DependencyObject root, string named) =>
        WpfTestHost.Descendants<ButtonBase>(root)
            .SingleOrDefault(b => b.Command is not null
                && WpfTestHost.IsRendered(b)
                && (AutomationProperties.GetName(b) ?? string.Empty)
                    .Contains(named, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// What the conflicts region of the shell puts on screen. Scoping matters: a local title also
    /// appears in the ticket list and a remote one in recent activity, so an unscoped search would
    /// pass without the disagreement ever being shown.
    /// </summary>
    private static string ConflictEvidence(FrameworkElement shell) =>
        WpfTestHost.Run(() =>
        {
            var region = WpfTestHost.Region(shell, "Conflicts");
            Assert.IsNotNull(region, "The shell must give its conflicts an announced region of their own.");
            return WpfTestHost.RenderedText(region);
        });

    private static void Invoke(ButtonBase control, FrameworkElement shell, double width) =>
        WpfTestHost.Run(() =>
        {
            control.Command.Execute(control.CommandParameter);
            WpfTestHost.LayOut(shell, width, 620);
        });

    [TestMethod]
    public void The_conflict_shown_on_an_item_names_both_values_the_resolution_and_each_side_s_provenance()
    {
        DisagreeingProject(("Local title", "Remote title", 7));
        var project = Refresh();

        Assert.AreEqual(1, project.View.Conflicts.Count, "The fixture must produce exactly one disagreement.");
        var conflict = project.View.Conflicts.Single();

        _viewModel.SelectedProject = project;
        var shell = Shell(expanded: true);

        StringAssert.Contains(
            WpfTestHost.Run(() => WpfTestHost.RenderedText(shell)),
            "widget",
            "Precondition: the laid-out shell must render the project it was given.");

        var shown = ConflictEvidence(shell);

        StringAssert.Contains(shown, "Local title", "The local value must be readable, not only implied.");
        StringAssert.Contains(shown, "Remote title", "A disagreement whose other side is invisible is not evidence.");
        StringAssert.Contains(shown, conflict.Resolution);
        StringAssert.Contains(shown, conflict.LocalProvenance.Description);
        StringAssert.Contains(shown, conflict.RemoteProvenance.Description);
    }

    [TestMethod]
    public void The_project_conflict_badge_is_a_control_that_reaches_the_conflict_evidence()
    {
        DisagreeingProject(("Local title", "Remote title", 7));
        Refresh();

        var shell = Shell(expanded: false);
        StringAssert.Contains(
            WpfTestHost.Run(() => WpfTestHost.RenderedText(shell)),
            "widget",
            "Precondition: the laid-out shell must render the project row the badge sits on.");

        var badge = WpfTestHost.Run(() => ConflictControl(shell, "in project widget"));
        Assert.IsNotNull(badge, "An aggregate warning that cannot be invoked leads nowhere.");

        Invoke(badge, shell, 720);

        Assert.IsTrue(_viewModel.IsExpanded, "The evidence lives in the expanded view, so navigation has to open it.");
        Assert.AreEqual("widget", _viewModel.SelectedProject?.Name);

        var shown = ConflictEvidence(shell);
        StringAssert.Contains(shown, "Local title");
        StringAssert.Contains(shown, "Remote title");
    }

    [TestMethod]
    public void A_conflict_control_on_an_affected_item_narrows_the_inspection_to_that_item()
    {
        DisagreeingProject(("First local", "First remote", 7), ("Second local", "Second remote", 8));
        _viewModel.SelectedProject = Refresh();

        var shell = Shell(expanded: true);
        StringAssert.Contains(
            ConflictEvidence(shell),
            "Second remote",
            "Every disagreement is listed until one of them is inspected.");

        var control = WpfTestHost.Run(() => ConflictControl(shell, "on ticket First local"));
        Assert.IsNotNull(control, "Each affected item needs its own conflict control to inspect it in context.");

        Invoke(control, shell, 720);
        var shown = ConflictEvidence(shell);

        StringAssert.Contains(shown, "First remote", "Inspecting an item must show that item's disagreement.");
        Assert.IsFalse(
            shown.Contains("Second remote", StringComparison.Ordinal),
            "Inspecting one item in context must not leave every other disagreement on screen.");
    }
}
