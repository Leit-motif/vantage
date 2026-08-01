using Vantage.App.Shell;
using Vantage.App.ViewModels;
using Vantage.Core.Projection;
using Vantage.Infrastructure.Persistence;
using Vantage.Infrastructure.Settings;
using Vantage.Tests.TestSupport;

namespace Vantage.Tests.ShellSeam;

/// <summary>
/// The Windows shell seam, exercised through user-visible commands and observable state rather
/// than the visual tree. Logic that can be expressed as a snapshot or a command result stays
/// below the visual tree; these tests prove wiring and Windows behaviour.
/// </summary>
[TestClass]
public sealed class ShellBehaviourTests
{
    private WorkspaceFixture _workspace = null!;
    private DashboardCache _cache = null!;
    private DashboardSettings _settings = null!;
    private DashboardViewModel _viewModel = null!;

    [TestInitialize]
    public void SetUp()
    {
        _workspace = new WorkspaceFixture();
        _cache = DashboardCache.Open(_workspace.CacheFile);
        _settings = new DashboardSettings { Roots = [_workspace.WorkspacesRoot] };

        var paths = new Vantage.Infrastructure.AppPaths(Path.Combine(_workspace.Root, "appdata"));
        _viewModel = new DashboardViewModel(_settings, new SettingsStore(paths), _cache, new FakeProcessRunner());
    }

    [TestCleanup]
    public void TearDown()
    {
        _cache.Dispose();
        _workspace.Dispose();
    }

    [TestMethod]
    public void Compact_and_expanded_modes_change_the_shell_width_the_owner_chose()
    {
        _settings.Ui.Geometry.CompactWidth = 380;
        _settings.Ui.Geometry.ExpandedWidth = 720;

        _viewModel.IsExpanded = false;
        Assert.AreEqual(380, _viewModel.ShellWidth);

        _viewModel.CycleViewModeCommand.Execute(null);
        Assert.IsTrue(_viewModel.IsExpanded);
        Assert.AreEqual(720, _viewModel.ShellWidth);

        // The ribbon is the small view folded down, whatever it was folded down from.
        _viewModel.ToggleRibbonCommand.Execute(null);
        Assert.AreEqual(380, _viewModel.ShellWidth);
    }

    /// <summary>
    /// Collapsing is how the dashboard gets put down, so picking it back up hands the owner the
    /// same small window every time rather than an outcome that depends on what they were doing
    /// beforehand — most of all when what they were doing was filling the screen.
    /// </summary>
    [TestMethod]
    public void Opening_the_dashboard_again_always_lands_on_the_small_view()
    {
        foreach (var folded in new[] { DashboardViewMode.Compact, DashboardViewMode.Expanded, DashboardViewMode.Full })
        {
            _viewModel.Mode = folded;
            _viewModel.ToggleRibbonCommand.Execute(null);
            Assert.AreEqual(DashboardViewMode.Ribbon, _viewModel.Mode);

            _viewModel.ToggleRibbonCommand.Execute(null);
            Assert.AreEqual(
                DashboardViewMode.Compact,
                _viewModel.Mode,
                $"Folded away from {folded}, opening it again still lands on the small view.");
            Assert.AreEqual(380, _viewModel.ShellWidth);
        }
    }

    [TestMethod]
    public void The_ribbon_carries_exactly_one_project()
    {
        SixProjects();

        _viewModel.Filter = DashboardFilter.AllRemaining;
        Refresh();

        _viewModel.Mode = DashboardViewMode.Expanded;
        _viewModel.ToggleRibbonCommand.Execute(null);

        Assert.AreEqual(DashboardViewMode.Ribbon, _viewModel.Mode);
        Assert.AreEqual(1, _viewModel.VisibleProjects.Count, "A ribbon is one line, so it is one project.");
        Assert.IsTrue(_viewModel.HasRibbonProject);
    }

    [TestMethod]
    public void The_ribbon_shows_the_pinned_project_ahead_of_the_one_that_moved_most_recently()
    {
        SixProjects();
        _viewModel.Filter = DashboardFilter.AllRemaining;
        Refresh();

        _viewModel.Mode = DashboardViewMode.Ribbon;
        var byRecency = _viewModel.RibbonProject;
        Assert.IsNotNull(byRecency, "With nothing pinned the ribbon still has to say something.");

        // Pin whichever project recency did not already choose, so the assertion is about the
        // priority rule rather than about the order the fixture happened to produce.
        var toPin = _viewModel.Projects.First(p => p.Path != byRecency.Path);
        _settings.FindProject(toPin.Path)!.Pinned = true;
        Refresh();

        Assert.AreEqual(
            toPin.Path,
            _viewModel.RibbonProject?.Path,
            "A pin is the owner naming the project that matters; recency does not outrank it.");
        Assert.AreEqual(1, _viewModel.VisibleProjects.Count);
    }

    [TestMethod]
    public void The_cycle_control_never_steps_into_the_ribbon_by_accident()
    {
        _viewModel.Mode = DashboardViewMode.Compact;

        _viewModel.CycleViewModeCommand.Execute(null);
        Assert.AreEqual(DashboardViewMode.Expanded, _viewModel.Mode);

        _viewModel.CycleViewModeCommand.Execute(null);
        Assert.AreEqual(DashboardViewMode.Full, _viewModel.Mode);

        _viewModel.CycleViewModeCommand.Execute(null);
        Assert.AreEqual(
            DashboardViewMode.Compact,
            _viewModel.Mode,
            "Looking for a different size must never fold the dashboard away.");
    }

    [TestMethod]
    public void Full_screen_is_a_wider_expanded_view_rather_than_a_different_one()
    {
        SixProjects();
        _viewModel.Filter = DashboardFilter.AllRemaining;
        Refresh();

        _viewModel.Mode = DashboardViewMode.Full;

        Assert.IsTrue(_viewModel.IsFullScreen);
        Assert.IsTrue(_viewModel.IsExpanded, "Filling the display is asking to read the evidence, so the detail pane stays.");
        Assert.AreEqual(6, _viewModel.VisibleProjects.Count, "And every row the filter matched stays with it.");
    }

    private void SixProjects()
    {
        for (var i = 0; i < 6; i++)
        {
            var project = _workspace.NewProject($"project-{i}");
            var effort = _workspace.NewEffort(project, "feature");
            _workspace.WriteTicket(effort, "001.md", Fixtures.Ticket($"Work {i}", "ready"));
        }
    }

    private void Refresh()
    {
        _viewModel.RefreshCommand.Execute(null);
        _viewModel.RefreshCommand.ExecutionTask!.GetAwaiter().GetResult();
    }

    [TestMethod]
    public void The_compact_view_shows_at_most_three_rows_and_the_expanded_view_shows_them_all()
    {
        SixProjects();

        _viewModel.Filter = DashboardFilter.AllRemaining;
        Refresh();

        _viewModel.IsExpanded = false;
        Assert.AreEqual(DashboardViewModel.CompactRowLimit, _viewModel.VisibleProjects.Count);

        _viewModel.IsExpanded = true;
        Assert.AreEqual(6, _viewModel.VisibleProjects.Count);
    }

    [TestMethod]
    public void Opacity_is_stated_as_what_the_owner_sees_and_stays_within_a_legible_range()
    {
        _viewModel.SurfaceOpacityPercent = 80;
        Assert.AreEqual(0.8d, _viewModel.SurfaceOpacity, 0.001);

        _viewModel.SurfaceOpacityPercent = 0;
        Assert.AreEqual(10, _viewModel.SurfaceOpacityPercent, "Transparency must never make the dashboard unreadable.");

        _viewModel.SurfaceOpacityPercent = 400;
        Assert.AreEqual(100, _viewModel.SurfaceOpacityPercent);
    }

    [TestMethod]
    public void A_settings_change_reaches_the_running_window_without_a_restart()
    {
        var changed = new List<string>();
        _viewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? string.Empty);

        _settings.Ui.SurfaceOpacityPercent = 45;
        _viewModel.NotifyAppearanceChanged();

        CollectionAssert.Contains(changed, nameof(DashboardViewModel.SurfaceOpacity));
        Assert.AreEqual(0.45d, _viewModel.SurfaceOpacity, 0.001);
    }

    /// <summary>
    /// Every geometry answer is in a window's own layout units, so every geometry expectation has
    /// to be too. Reading the display's physical pixels as if they were layout units is exactly
    /// the mistake that strands a window on a scaled display.
    /// </summary>
    private static (System.Windows.Rect Area, double Scale) PrimaryDisplay()
    {
        var scale = ScaleOfPrimaryDisplay();
        return (WindowInterop.InDeviceIndependentUnits(System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea, scale), scale);
    }

    private static double ScaleOfPrimaryDisplay() =>
        WpfTestHost.Run(() => System.Windows.Media.VisualTreeHelper.GetDpi(new System.Windows.Controls.Border()).DpiScaleX);

    [TestMethod]
    public void Geometry_from_a_monitor_that_is_gone_is_brought_back_on_screen()
    {
        var (area, scale) = PrimaryDisplay();
        var (left, top) = WindowInterop.EnsureOnScreen(-40_000, -40_000, 380, 520, scale);

        Assert.IsTrue(
            area.IntersectsWith(new System.Windows.Rect(left, top, 380, 520)),
            "A display change must never strand the window off-screen.");
    }

    [TestMethod]
    public void Geometry_that_is_already_visible_is_left_exactly_where_the_owner_put_it()
    {
        var (area, scale) = PrimaryDisplay();
        var (left, top) = WindowInterop.EnsureOnScreen(area.Left + 100, area.Top + 100, 380, 520, scale);

        Assert.AreEqual(area.Left + 100, left);
        Assert.AreEqual(area.Top + 100, top);
    }

    [TestMethod]
    public void A_position_measured_at_one_scale_is_the_same_place_when_read_at_another()
    {
        // 1200 units on a 100% display is 1200 physical pixels, which is 960 units at 125%.
        var (left, top) = WindowInterop.Reinterpret(1200, 400, savedScale: 1.0, currentScale: 1.25);

        Assert.AreEqual(960, left, 0.001);
        Assert.AreEqual(320, top, 0.001);
    }

    [TestMethod]
    public void A_position_saved_before_the_scale_was_recorded_is_read_exactly_as_it_always_was()
    {
        var (left, top) = WindowInterop.Reinterpret(1200, 400, savedScale: null, currentScale: 1.25);

        Assert.AreEqual(1200, left, 0.001, "An older settings file must not have its geometry moved by an upgrade.");
        Assert.AreEqual(400, top, 0.001);
    }

    [TestMethod]
    public void Edge_snap_pulls_a_nearly_aligned_window_flush_and_leaves_a_distant_one_alone()
    {
        var (area, scale) = PrimaryDisplay();

        var (snappedLeft, _) = WindowInterop.SnapToEdge(area.Left + 6, area.Top + 200, 380, 520, scale);
        Assert.AreEqual(area.Left, snappedLeft);

        var (freeLeft, _) = WindowInterop.SnapToEdge(area.Left + 400, area.Top + 200, 380, 520, scale);
        Assert.AreEqual(area.Left + 400, freeLeft);
    }

    [TestMethod]
    public void Click_through_is_off_by_default()
    {
        Assert.IsFalse(new DashboardSettings().Ui.ClickThrough, "A window that ignores the mouse is hard to recover.");
    }

    [TestMethod]
    public void No_global_hotkey_is_claimed_by_default()
    {
        Assert.IsNull(new DashboardSettings().Ui.ClickThroughHotkey);
    }

    [TestMethod]
    public void An_unparseable_or_empty_hotkey_leaves_the_dashboard_without_one_rather_than_guessing()
    {
        using var hotkey = new GlobalHotkey(0, () => { });

        Assert.IsFalse(hotkey.Bind(null));
        Assert.IsFalse(hotkey.Bind(string.Empty));
        Assert.IsFalse(hotkey.Bind("NotAKey"));
        Assert.IsFalse(hotkey.Bind("D"), "A bare key with no modifier is not a safe global shortcut.");
    }

    [TestMethod]
    public void Filters_answer_the_operational_questions_they_are_named_for()
    {
        var ready = _workspace.NewProject("ready-project");
        _workspace.WriteTicket(_workspace.NewEffort(ready, "feature"), "001.md", Fixtures.Ticket("Available", "ready"));

        var finished = _workspace.NewProject("finished-project");
        _workspace.WriteTicket(_workspace.NewEffort(finished, "feature"), "001.md", Fixtures.Ticket("Finished", "done"));

        _viewModel.RefreshCommand.Execute(null);
        _viewModel.RefreshCommand.ExecutionTask!.GetAwaiter().GetResult();
        _viewModel.IsExpanded = true;

        _viewModel.Filter = DashboardFilter.AllRemaining;
        CollectionAssert.AreEquivalent(
            new[] { "ready-project" },
            _viewModel.VisibleProjects.Select(p => p.Name).ToArray());

        _viewModel.Filter = DashboardFilter.Archive;
        CollectionAssert.AreEquivalent(
            new[] { "finished-project" },
            _viewModel.VisibleProjects.Select(p => p.Name).ToArray());

        _viewModel.Filter = DashboardFilter.AllRemaining;
        _viewModel.SearchText = "nothing matches this";
        Assert.AreEqual(0, _viewModel.VisibleProjects.Count);
    }

    [TestMethod]
    public void A_pinned_but_finished_project_belongs_to_the_archive_not_to_pinned()
    {
        var finished = _workspace.NewProject("pinned-and-finished");
        _workspace.WriteTicket(_workspace.NewEffort(finished, "feature"), "001.md", Fixtures.Ticket("Finished", "done"));

        _viewModel.RefreshCommand.Execute(null);
        _viewModel.RefreshCommand.ExecutionTask!.GetAwaiter().GetResult();

        _settings.FindProject(finished)!.Pinned = true;
        _viewModel.RefreshCommand.Execute(null);
        _viewModel.RefreshCommand.ExecutionTask!.GetAwaiter().GetResult();

        _viewModel.IsExpanded = true;
        _viewModel.Filter = DashboardFilter.Pinned;
        Assert.AreEqual(0, _viewModel.VisibleProjects.Count);

        _viewModel.Filter = DashboardFilter.Archive;
        Assert.AreEqual(1, _viewModel.VisibleProjects.Count);
    }

    [TestMethod]
    public void Every_project_row_reads_as_one_sentence_for_a_screen_reader()
    {
        var project = _workspace.NewProject("described");
        _workspace.WriteTicket(_workspace.NewEffort(project, "feature"), "001.md", Fixtures.Ticket("Available", "ready"));

        _viewModel.RefreshCommand.Execute(null);
        _viewModel.RefreshCommand.ExecutionTask!.GetAwaiter().GetResult();
        _viewModel.Filter = DashboardFilter.AllRemaining;

        var row = _viewModel.VisibleProjects.Single();

        StringAssert.Contains(row.AccessibleSummary, "described");
        StringAssert.Contains(row.AccessibleSummary, "Ready");
        StringAssert.Contains(row.AccessibleSummary, "Available");
    }

    [TestMethod]
    public void State_is_carried_by_a_written_label_and_a_glyph_as_well_as_an_accent()
    {
        foreach (var state in Enum.GetValues<ProjectState>())
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(Glyphs.ForState(state)), $"{state} needs a glyph.");
        }

        var labels = Enum.GetValues<ProjectState>()
            .Select(s => new ProjectItemViewModel(new ProjectView
            {
                Identity = new Core.Workflow.ProjectIdentity("p", "p"),
                State = s,
                StateReason = "because",
                Progress = new ProgressSummary(0, 1, 0),
            }).StateLabel)
            .ToList();

        CollectionAssert.AllItemsAreUnique(labels, "Each state must read as a distinct word, not only a colour.");
    }
}
