using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
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

// WinForms is referenced for the tray icon; here the WPF types are meant.
using Brushes = System.Windows.Media.Brushes;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using Orientation = System.Windows.Controls.Orientation;
using Point = System.Windows.Point;
using ScrollBar = System.Windows.Controls.Primitives.ScrollBar;

namespace Vantage.Tests.ShellSeam;

/// <summary>
/// What the detail pane puts where, read from the real window's own content tree laid out at the
/// widths the owner uses. The pane is the thing whose whole job is to be read, so what is asserted
/// here is the geometry a reader depends on: a bar the size the theme asked for, a gutter that
/// does not move, and no label pushed past the edge of what is shown.
/// <para>
/// `07-detail-pane-scrollbar.md` was written believing the bar was drawn over the content. It is
/// not — the stock ScrollViewer template reserves the column. What was wrong is that the stock
/// ScrollBar template's MinWidth, taken from the system metric, beat the theme's own Width, so the
/// pane rendered a 16.8-unit bar against a surface styled for an 8-unit one. These tests read
/// the arranged width rather than the Width property for exactly that reason: the property said 8
/// throughout.
/// </para>
/// </summary>
[TestClass]
public sealed class DetailPaneLayoutTests
{
    /// <summary>
    /// What the theme is required to ask a scrollbar to be. Asserted against the resource itself
    /// rather than used as the expected arranged width: a test that compared arranged widths to a
    /// constant of its own would stay green if the template stopped reading the resource, which is
    /// the whole of "from the theme".
    /// </summary>
    private const double RequiredThickness = 8;

    private const double ShellWidth = 720;
    private const double ShellHeight = 620;

    private WorkspaceFixture _workspace = null!;
    private DashboardCache _cache = null!;
    private DashboardSettings _settings = null!;
    private DashboardViewModel _viewModel = null!;

    [TestInitialize]
    public void SetUp()
    {
        _workspace = new WorkspaceFixture();

        // The live read fails, so the project falls back to the last-known-good view the cache
        // holds — which is where the fixture's efforts and tickets are put.
        var runner = new FakeProcessRunner().Throwing("git", "rev-parse");

        _cache = DashboardCache.Open(_workspace.CacheFile);
        _settings = new DashboardSettings { Roots = [_workspace.WorkspacesRoot] };

        var paths = new AppPaths(Path.Combine(_workspace.Root, "appdata"));
        _viewModel = new DashboardViewModel(_settings, new SettingsStore(paths), _cache, runner);
    }

    [TestCleanup]
    public void TearDown()
    {
        _cache.Dispose();
        _workspace.Dispose();
    }

    [TestMethod]
    public void The_panes_scrollbar_is_the_width_the_theme_asks_for()
    {
        var pane = PaneShowing(tickets: 12);

        WpfTestHost.Run(() =>
        {
            var themed = ThemeThickness();
            var system = SystemParameters.VerticalScrollBarWidth;
            var bar = VerticalBar(pane);

            // Stated as a measurement rather than only as a lookup, because the defect was a
            // measurement: the bar arranged at the system metric while the theme's number sat
            // there unread. A test that only compared against the resource would go red on the
            // unfixed code for the resource being absent, which is a different fact.
            Assert.IsTrue(
                bar.ActualWidth < system,
                $"The pane's bar arranged at {bar.ActualWidth}, which is the system metric "
                    + $"({system}) rather than anything the theme asked for.");

            Assert.AreEqual(
                RequiredThickness,
                themed,
                0.01,
                $"The theme publishes {themed} as ScrollBarThickness — NaN means it publishes none "
                    + $"— not the {RequiredThickness} the ticket settled on.");

            Assert.AreEqual(
                themed,
                bar.ActualWidth,
                0.01,
                $"The pane's bar arranged at {bar.ActualWidth}, not the {themed} the theme sets.");
        });
    }

    /// <summary>
    /// The gutter is reserved at every content height. A pane that gives the space back when the
    /// bar leaves re-trims every title it holds, so a title's width would depend on how much else
    /// the project had to say.
    /// </summary>
    [TestMethod]
    public void The_pane_does_not_reflow_when_the_bar_arrives()
    {
        // The pane is built outside the dispatcher on purpose: refreshing waits on the view
        // model's own task, and waiting for it *on* the UI thread is waiting for the thread that
        // has to finish it.
        var busy = PaneShowing(tickets: 12);
        var overflowing = WpfTestHost.Run(() => Widths(busy));

        TearDown();
        SetUp();

        // The same width, laid out tall enough that one ticket has nothing to scroll. Height is
        // what is varied because bar-or-no-bar is the variable under test and width is the reading.
        var quiet = PaneShowing(tickets: 1, height: 1000);
        var fitting = WpfTestHost.Run(() => Widths(quiet));

        Assert.AreEqual(
            overflowing.Scrollable,
            true,
            "The busy fixture was meant to overflow the pane and did not.");
        Assert.AreEqual(
            fitting.Scrollable,
            false,
            "The quiet fixture was meant to fit inside the pane and did not.");

        Assert.AreEqual(
            overflowing.Viewport,
            fitting.Viewport,
            0.01,
            $"The viewport moved {overflowing.Viewport} → {fitting.Viewport} when the bar left.");
        Assert.AreEqual(
            overflowing.TitleColumn,
            fitting.TitleColumn,
            0.01,
            $"The ticket title column moved {overflowing.TitleColumn} → {fitting.TitleColumn} "
                + "when the bar left.");
    }

    [TestMethod]
    public void No_label_is_pushed_past_the_edge_of_what_is_shown()
    {
        // Both states, and each says which one it is meant to be. The arms drifted into being the
        // same state once the fixture grew a conflicts section, and a pair of arms that silently
        // became one arm is how a cell gets ticked for a case nothing ran.
        foreach (var (tickets, height, scrolls) in
                 new[] { (12, ShellHeight, true), (1, 1000d, false) })
        {
            TearDown();
            SetUp();

            var pane = PaneShowing(tickets, height);

            WpfTestHost.Run(() =>
            {
                var scroller = Scroller(pane);

                Assert.AreEqual(
                    scrolls,
                    scroller.ScrollableHeight > 0,
                    $"The {tickets}-ticket arm was meant to be the "
                        + $"{(scrolls ? "overflowing" : "fitting")} state and is not, so this run "
                        + "does not cover the state it was written for.");

                foreach (var text in WpfTestHost.Descendants<TextBlock>(scroller)
                             .Where(t => WpfTestHost.IsRendered(t) && t.Text.Length > 0))
                {
                    var right = text.TransformToAncestor(scroller).Transform(new Point(0, 0)).X
                        + text.ActualWidth;

                    Assert.IsTrue(
                        right <= scroller.ViewportWidth + 0.01,
                        $"\"{text.Text}\" reaches {right}, past a viewport of {scroller.ViewportWidth} "
                            + $"({tickets} tickets).");
                }
            });
        }
    }

    /// <summary>
    /// A horizontal bar pages sideways. The orientation arm swaps the track's commands as well as
    /// its geometry, and a bar that looked right while scrolling nothing is exactly the defect the
    /// review caught, so it is worth a test of its own rather than an inspection of the XAML.
    /// </summary>
    [TestMethod]
    public void A_horizontal_bar_pages_sideways()
    {
        WpfTestHost.Run(() =>
        {
            var scroller = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Visible,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = new Border { Width = 2000, Height = 40 },
            };

            WpfTestHost.LayOut(new Border { Child = scroller }, 300, 120);

            var bar = scroller.Template.FindName("PART_HorizontalScrollBar", scroller) as ScrollBar
                ?? throw new InvalidOperationException("The scroller has no horizontal bar.");

            Assert.AreEqual(Orientation.Horizontal, bar.Orientation);
            Assert.AreEqual(
                ThemeThickness(),
                bar.ActualHeight,
                0.01,
                $"The horizontal bar is {bar.ActualHeight} tall, not the theme's thickness.");

            var track = bar.Template.FindName("PART_Track", bar) as Track
                ?? throw new InvalidOperationException("The bar has no track.");

            Page(track.IncreaseRepeatButton);
            Assert.IsTrue(
                scroller.HorizontalOffset > 0,
                "Paging the far half of a horizontal track moved nothing.");

            var reached = scroller.HorizontalOffset;
            Page(track.DecreaseRepeatButton);
            Assert.IsTrue(
                scroller.HorizontalOffset < reached,
                "Paging the near half of a horizontal track did not come back.");

            void Page(ButtonBase? half)
            {
                var button = half ?? throw new InvalidOperationException("The track has no halves.");
                ((RoutedCommand)button.Command).Execute(button.CommandParameter, button);
                scroller.UpdateLayout();
            }
        });
    }

    /// <summary>
    /// The title is the star column and the two beside it are `Auto`, so the title is what gives
    /// way. Read as trimming rather than as column arithmetic: what the owner loses is characters.
    /// </summary>
    [TestMethod]
    public void The_title_gives_way_and_the_status_beside_it_does_not()
    {
        var pane = PaneShowing(tickets: 12);

        WpfTestHost.Run(() =>
        {
            var rows = WpfTestHost.Descendants<TextBlock>(pane)
                .Where(t => t.Text.StartsWith("A ticket title", StringComparison.Ordinal))
                .ToList();
            var statuses = WpfTestHost.Descendants<TextBlock>(pane)
                .Where(t => t.Text.Equals("ready-for-agent", StringComparison.Ordinal))
                .ToList();

            Assert.IsTrue(rows.Count > 0 && statuses.Count > 0, "The fixture's rows were not found.");

            var title = rows[^1];
            Assert.IsTrue(
                title.ActualWidth < Natural(title) - 1,
                $"The title arranged at {title.ActualWidth} with {Natural(title)} to say, so nothing "
                    + "was trimmed and the fixture no longer tests trimming.");

            // Narrower than the text needs is clipping; the cell asks for an ellipsis, which is a
            // separate property and would go on passing the width check if it were dropped.
            Assert.AreEqual(
                TextTrimming.CharacterEllipsis,
                title.TextTrimming,
                "The title is constrained but not ellipsized, so the owner loses characters with "
                    + "nothing to say they are gone.");

            var status = statuses[^1];
            Assert.IsTrue(
                status.ActualWidth >= Natural(status) - 0.5,
                $"The status label was trimmed: {status.ActualWidth} arranged against "
                    + $"{Natural(status)} needed.");

            // Named, not merely "a button with a command": the scrollbar's own paging halves are
            // ButtonBase with a command too, and their template is empty, so a broad query would
            // measure nothing and pass.
            var controls = WpfTestHost.Descendants<ButtonBase>(pane)
                .Where(b => AutomationProperties.GetName(b).StartsWith(
                    "Inspect", StringComparison.Ordinal))
                .ToList();

            Assert.IsTrue(
                controls.Count > 0,
                "No conflict control was found, so this cell would have been ticked on nothing.");

            foreach (var control in controls)
            {
                Assert.IsTrue(
                    WpfTestHost.IsRendered(control),
                    "A conflict control is not on screen at all.");

                // DesiredSize carries the margin and ActualWidth does not, so the margin comes back
                // off before the two are comparable.
                var wanted = control.DesiredSize.Width - control.Margin.Left - control.Margin.Right;
                Assert.IsTrue(
                    control.ActualWidth >= wanted - 0.5,
                    $"A conflict control was squeezed out of its own column: {control.ActualWidth} "
                        + $"arranged against {wanted} wanted.");
            }
        });
    }

    /// <summary>
    /// The settings window scrolls through the same style. Replacing a stock template replaces the
    /// parts that do the scrolling, so what this owes is not that a bar is present and the right
    /// size but that clicking it still moves the content — which is why the paging half of the
    /// track is invoked here rather than the scroller being told to scroll itself.
    /// </summary>
    [TestMethod]
    public void The_settings_window_scrolls_through_the_same_bar()
    {
        WpfTestHost.Run(() =>
        {
            var paths = new AppPaths(Path.Combine(_workspace.Root, "settings-appdata"));
            var window = new SettingsWindow(
                new SettingsViewModel(_settings, new SettingsStore(paths), () => { }));

            var root = WpfTestHost.HostContent(window, 560, 320);
            var scroller = WpfTestHost.Descendants<ScrollViewer>(root).First();

            Assert.IsTrue(
                scroller.ScrollableHeight > 0,
                "The settings window was laid out too tall to need scrolling, so this proves nothing.");

            var bar = OwnBar(scroller);

            Assert.IsTrue(
                bar.ActualWidth < SystemParameters.VerticalScrollBarWidth,
                $"The settings bar arranged at {bar.ActualWidth}, the system metric rather than "
                    + "the theme's.");
            Assert.AreEqual(
                ThemeThickness(),
                bar.ActualWidth,
                0.01,
                "The settings bar is not the theme's.");

            Assert.AreEqual(0d, scroller.VerticalOffset, 0.01, "The window did not start at the top.");

            var track = bar.Template.FindName("PART_Track", bar) as Track
                ?? throw new InvalidOperationException("The bar has no track.");
            var pageOn = track.IncreaseRepeatButton
                ?? throw new InvalidOperationException("The track has no paging half to click.");

            ((RoutedCommand)pageOn.Command).Execute(pageOn.CommandParameter, pageOn);
            root.UpdateLayout();

            Assert.IsTrue(
                scroller.VerticalOffset > 0,
                "Paging the settings window's own scrollbar moved nothing, so the replacement "
                    + "template kept the bar and lost the scrolling.");
        });
    }

    /// <summary>
    /// The thickness the running theme actually publishes, not a copy of it. A theme that
    /// publishes none reads as NaN rather than throwing, so a test that depends on it fails on
    /// its own assertion with its own message instead of on a resource lookup.
    /// </summary>
    private static double ThemeThickness() =>
        System.Windows.Application.Current.TryFindResource("ScrollBarThickness") as double? ?? double.NaN;

    /// <summary>
    /// What the text would take if nothing constrained it. Read from the glyphs rather than from
    /// <c>DesiredSize</c>, which is already the constrained answer and so would agree with the
    /// arranged width however much had been trimmed away.
    /// </summary>
    private static double Natural(TextBlock text) =>
        new FormattedText(
            text.Text,
            System.Globalization.CultureInfo.CurrentCulture,
            text.FlowDirection,
            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch),
            text.FontSize,
            Brushes.Black,
            VisualTreeHelper.GetDpi(text).PixelsPerDip).Width;

    private (double Viewport, double TitleColumn, bool Scrollable) Widths(FrameworkElement pane)
    {
        var scroller = Scroller(pane);
        var title = WpfTestHost.Descendants<TextBlock>(pane)
            .First(t => t.Text.StartsWith("A ticket title", StringComparison.Ordinal));

        return (scroller.ViewportWidth, title.ActualWidth, scroller.ScrollableHeight > 0);
    }

    private static ScrollViewer Scroller(FrameworkElement pane) =>
        WpfTestHost.Descendants<ScrollViewer>(pane).Single();

    private static ScrollBar VerticalBar(FrameworkElement pane) => OwnBar(Scroller(pane));

    /// <summary>
    /// A scroller's own bar, taken from its template rather than by searching its subtree: the
    /// content inside a scroller has scrollbars of its own — the settings window's drop-downs do —
    /// and a search cannot tell which one belongs to the viewport under test.
    /// </summary>
    private static ScrollBar OwnBar(ScrollViewer scroller) =>
        scroller.Template.FindName("PART_VerticalScrollBar", scroller) as ScrollBar
            ?? throw new InvalidOperationException("The scroller has no vertical bar in its template.");

    private FrameworkElement PaneShowing(int tickets, double height = ShellHeight)
    {
        BusyProject(tickets);
        Refresh();

        var root = Shell(height);

        return WpfTestHost.Run(() => WpfTestHost.Region(root, "Project details"))
            ?? throw new InvalidOperationException("The detail pane was not found.");
    }

    /// <summary>
    /// A project whose detail pane has more to say than fits: ticket titles longer than the column
    /// they are given, each beside a status label, and as many of them as the caller wants.
    /// </summary>
    private void BusyProject(int count)
    {
        var project = _workspace.NewProject("widget");
        Directory.CreateDirectory(Path.Combine(project, ".git"));

        var canonical = ProjectDiscovery.CanonicalizeFully(project);
        var tickets = new List<WorkflowTicket>();
        var conflicts = new List<ConflictReport>();

        for (var index = 1; index <= count; index++)
        {
            var source = Path.Combine(project, ".scratch", "feature", "issues", $"{index:000}.md");
            var working = new Provenance(
                EvidenceSource.LocalFile, source, TimestampProvenance.WatcherEvent, DateTimeOffset.UtcNow, "r1");
            var committed = new Provenance(
                EvidenceSource.LocalGit, $"{source}@HEAD", TimestampProvenance.GitCommit, DateTimeOffset.UtcNow, "r1");

            // Every ticket disagrees with itself, so every row carries the conflict control that
            // sits closest to the gutter this ticket changes. A row without one would let the
            // control's cell be ticked on a button that was never on screen.
            conflicts.Add(new ConflictReport(
                $"feature/{index:000}",
                ConflictField.Title,
                new ObservedValue($"A ticket title long enough to need the whole column it was given, number {index}", working),
                new ObservedValue($"An older title for {index}", committed),
                "Working-tree value kept: it is the more recent observation."));

            tickets.Add(new WorkflowTicket
            {
                Id = $"feature/{index:000}",
                LocalKey = $"{index:000}",
                EffortId = "feature",
                Title = $"A ticket title long enough to need the whole column it was given, number {index}",
                Status = new StatusReading(WorkflowStatus.Ready, "ready-for-agent"),
                Kind = WorkUnitKind.Implementation,
                SourcePath = source,
                SemanticHash = $"hash-{index}",
                Provenance = new Provenance(
                    EvidenceSource.LocalFile,
                    source,
                    TimestampProvenance.WatcherEvent,
                    DateTimeOffset.UtcNow,
                    "r1"),
            });
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
                        Tickets = tickets,
                    },
                ],
            },
            new ProjectionOptions { Now = DateTimeOffset.UtcNow, Conflicts = conflicts });

        _cache.SaveProjectSnapshot(view, DateTimeOffset.UtcNow);
    }

    private void Refresh()
    {
        _viewModel.Filter = DashboardFilter.AllRemaining;
        _viewModel.RefreshCommand.Execute(null);
        _viewModel.RefreshCommand.ExecutionTask!.GetAwaiter().GetResult();
        _viewModel.SelectedProject = _viewModel.AllProjects.Single(p => p.Name == "widget");
    }

    private FrameworkElement Shell(double height)
    {
        _viewModel.IsExpanded = true;

        return WpfTestHost.Run(() => WpfTestHost.HostContent(
            new DashboardWindow(_viewModel, _settings, () => { }),
            ShellWidth,
            height));
    }
}
