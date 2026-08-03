using System.Windows;
using System.Windows.Automation;
using Vantage.App.ViewModels;
using Vantage.App.Views;
using Vantage.Core;
using Vantage.Core.Projection;
using Vantage.Core.Workflow;
using Vantage.Infrastructure;
using Vantage.Infrastructure.Discovery;
using Vantage.Infrastructure.Persistence;
using Vantage.Infrastructure.Settings;
using Vantage.Tests.TestSupport;

// WinForms is referenced for the tray icon; here the WPF control is meant.
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;

namespace Vantage.Tests.ShellSeam;

/// <summary>
/// What the owner can see and reach when two observations of one item disagree. These assert the
/// real window's own content tree, laid out but never shown, so a disagreement's evidence is
/// proven to be on screen rather than merely present in a view model.
/// <para>
/// The remote reconciliation that used to produce a <c>ConflictReport</c> is gone, and its
/// replacements are `03-conflicts-between-any-two-observations.md`. The affordance is what is
/// under test here, not the producer, so the disagreement is put into the dashboard's cache and
/// reached the way a project whose live read failed reaches its last-known-good view. That path
/// is the product's own — nothing here builds a view model by hand — and it is exactly the case
/// `AttachConflictsToItems` exists for.
/// </para>
/// </summary>
[TestClass]
public sealed class ConflictInspectionTests
{
    /// <summary>
    /// How tall the shell is laid out. The default is tall enough to read and short enough that a
    /// busy project's detail pane scrolls; a test that needs a whole list on screen at once says
    /// so by raising it.
    /// </summary>
    private double _shellHeight = 620;

    private WorkspaceFixture _workspace = null!;
    private FakeProcessRunner _runner = null!;
    private DashboardCache _cache = null!;
    private DashboardSettings _settings = null!;
    private DashboardViewModel _viewModel = null!;

    [TestInitialize]
    public void SetUp()
    {
        _workspace = new WorkspaceFixture();

        // The live read fails, so every project falls back to the last-known-good view the cache
        // holds for it — which is where each fixture's disagreement is put.
        _runner = new FakeProcessRunner().Throwing("git", "rev-parse");

        _cache = DashboardCache.Open(_workspace.CacheFile);
        _settings = new DashboardSettings { Roots = [_workspace.WorkspacesRoot] };

        var paths = new AppPaths(Path.Combine(_workspace.Root, "appdata"));
        _viewModel = new DashboardViewModel(_settings, new SettingsStore(paths), _cache, _runner);
    }

    [TestCleanup]
    public void TearDown()
    {
        _cache.Dispose();
        _workspace.Dispose();
    }

    /// <summary>
    /// A project whose items are each the subject of one disagreement, projected by the product's
    /// own projector and stored as its last-known-good view. The two sides differ in title, so
    /// every conflict has both values and both provenances to show.
    /// </summary>
    private void DisagreeingProject(params (string First, string Second, int Number)[] tickets)
    {
        var project = _workspace.NewProject("widget");
        Directory.CreateDirectory(Path.Combine(project, ".git"));

        var canonical = ProjectDiscovery.CanonicalizeFully(project);
        var conflicts = new List<ConflictReport>();
        var workflowTickets = new List<WorkflowTicket>();

        var index = 1;
        foreach (var ticket in tickets)
        {
            var id = $"feature/{index:000}";
            var source = Path.Combine(project, ".scratch", "feature", "issues", $"{index:000}.md");

            var working = new Provenance(
                EvidenceSource.LocalFile, source, TimestampProvenance.WatcherEvent, DateTimeOffset.UtcNow, "r1");
            var committed = new Provenance(
                EvidenceSource.LocalGit, $"{source}@HEAD", TimestampProvenance.GitCommit, DateTimeOffset.UtcNow, "r1");

            conflicts.Add(new ConflictReport(
                id,
                ConflictField.Title,
                new ObservedValue(ticket.First, working),
                new ObservedValue(ticket.Second, committed),
                "Working-tree value kept: it is the more recent observation."));

            workflowTickets.Add(new WorkflowTicket
            {
                Id = id,
                LocalKey = $"{index:000}",
                EffortId = "feature",
                Title = ticket.First,
                Status = new StatusReading(WorkflowStatus.Ready, "ready"),
                Kind = WorkUnitKind.Implementation,
                SourcePath = source,
                SemanticHash = $"hash-{index}",
                Provenance = working,
            });

            index++;
        }

        var view = ProjectProjector.Project(
            new NormalizedProject
            {
                Identity = new ProjectIdentity(canonical, "widget"),
                Efforts =
                [
                    new WorkflowEffort
                    {
                        Id = "feature",
                        Name = "feature",
                        Path = Path.Combine(project, ".scratch", "feature"),
                        Tickets = workflowTickets,
                    },
                ],
            },
            new ProjectionOptions { Now = DateTimeOffset.UtcNow, Conflicts = conflicts });

        _cache.SaveProjectSnapshot(view, DateTimeOffset.UtcNow);
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
            _shellHeight));
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

    private void Invoke(ButtonBase control, FrameworkElement shell, double width)
    {
        WpfTestHost.Run(() =>
        {
            control.Command.Execute(control.CommandParameter);
            WpfTestHost.LayOut(shell, width, _shellHeight);
        });

        // The shell scrolls the region into view after layout has settled, exactly as it would
        // for the owner; the second pass lets that scroll take effect.
        WpfTestHost.Drain();
        WpfTestHost.Run(() => WpfTestHost.LayOut(shell, width, _shellHeight));
    }

    [TestMethod]
    public void The_conflict_shown_on_an_item_names_both_values_the_resolution_and_each_side_s_provenance()
    {
        DisagreeingProject(("Working tree title", "Committed title", 7));
        var project = Refresh();

        Assert.AreEqual(1, project.View.Conflicts.Count, "The fixture must carry exactly one disagreement.");
        var conflict = project.View.Conflicts.Single();

        _viewModel.SelectedProject = project;
        var shell = Shell(expanded: true);

        StringAssert.Contains(
            WpfTestHost.Run(() => WpfTestHost.RenderedText(shell)),
            "widget",
            "Precondition: the laid-out shell must render the project it was given.");

        var shown = ConflictEvidence(shell);

        StringAssert.Contains(shown, "Working tree title", "The first side must be readable, not only implied.");
        StringAssert.Contains(shown, "Committed title", "A disagreement whose other side is invisible is not evidence.");
        StringAssert.Contains(shown, conflict.Resolution);
        StringAssert.Contains(shown, conflict.First.Provenance.Description);
        StringAssert.Contains(shown, conflict.Second.Provenance.Description);

        // Neither side is named by a role the model assigns it: each is named by its own evidence,
        // which is the only thing that still tells them apart once there is no privileged source.
        StringAssert.Contains(shown, conflict.First.Provenance.Origin, "The first side must be named by where it was read.");
        StringAssert.Contains(shown, conflict.Second.Provenance.Origin, "The second side must be named by where it was read.");
    }

    [TestMethod]
    public void The_project_conflict_badge_is_a_control_that_reaches_the_conflict_evidence()
    {
        DisagreeingProject(("Working tree title", "Committed title", 7));
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
        StringAssert.Contains(shown, "Working tree title");
        StringAssert.Contains(shown, "Committed title");
    }

    [TestMethod]
    public void Navigation_scrolls_the_evidence_into_view_rather_than_merely_onto_the_pane()
    {
        // Enough work above the conflicts region to push it past the bottom of the detail pane.
        DisagreeingProject([.. Enumerable.Range(1, 14).Select(i => ($"Working {i:00}", $"Committed {i:00}", i))]);
        _viewModel.SelectedProject = Refresh();

        var shell = Shell(expanded: true);
        var region = WpfTestHost.Run(() => WpfTestHost.Region(shell, "Conflicts"));
        Assert.IsNotNull(region);

        Assert.IsFalse(
            WpfTestHost.Run(() => WpfTestHost.IsRendered(region)),
            "Precondition: this fixture has to leave the conflicts region below the fold, or the "
            + "test proves nothing about reaching it.");

        var badge = WpfTestHost.Run(() => ConflictControl(shell, "in project widget"));
        Assert.IsNotNull(badge, "The badge must be reachable from the project row while scrolled away.");

        Invoke(badge, shell, 720);

        Assert.IsTrue(
            WpfTestHost.Run(() => WpfTestHost.IsRendered(region)),
            "An aggregate warning has to lead to the evidence, not merely to the pane holding it.");
    }

    [TestMethod]
    public void A_conflict_control_on_an_affected_item_narrows_the_inspection_to_that_item()
    {
        DisagreeingProject(("First working tree", "First committed", 7), ("Second working tree", "Second committed", 8));
        _viewModel.SelectedProject = Refresh();

        // Both disagreements on screen at once, so what narrowing removes is what the owner
        // could actually see beforehand.
        _shellHeight = 1000;
        var shell = Shell(expanded: true);
        StringAssert.Contains(
            ConflictEvidence(shell),
            "Second committed",
            "Every disagreement is listed until one of them is inspected.");

        var control = WpfTestHost.Run(() => ConflictControl(shell, "on ticket First working tree"));
        Assert.IsNotNull(control, "Each affected item needs its own conflict control to inspect it in context.");

        Invoke(control, shell, 720);
        var shown = ConflictEvidence(shell);

        StringAssert.Contains(shown, "First committed", "Inspecting an item must show that item's disagreement.");
        Assert.IsFalse(
            shown.Contains("Second committed", StringComparison.Ordinal),
            "Inspecting one item in context must not leave every other disagreement on screen.");
    }
}
