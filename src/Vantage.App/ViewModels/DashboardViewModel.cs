using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vantage.Core;
using Vantage.Core.Projection;
using Vantage.Infrastructure.Persistence;
using Vantage.Infrastructure.Processes;
using Vantage.Infrastructure.Refresh;
using Vantage.Infrastructure.Settings;

namespace Vantage.App.ViewModels;

/// <summary>The operational questions the dashboard is built to answer quickly.</summary>
public enum DashboardFilter
{
    Recent,
    AllRemaining,
    InProgress,
    Blocked,
    Pinned,
    Archive,
}

/// <summary>
/// The dashboard's state. Refreshes are asynchronous and cancellable, and a refresh never
/// moves focus — the owner is working in another window while this one updates.
/// </summary>
public sealed partial class DashboardViewModel : ObservableObject
{
    private readonly DashboardSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    /// <summary>
    /// The refresh boundary, held for the life of the dashboard rather than rebuilt per refresh.
    /// It owns the per-project gate that keeps overlapping passes from interleaving, and a gate
    /// rebuilt with each refresh could never serialize one refresh against the next.
    /// </summary>
    private readonly RefreshService _refresh;

    private CancellationTokenSource? _inFlight;
    private DashboardSnapshot _snapshot;

    private readonly Func<bool> _highContrast;

    public DashboardViewModel(
        DashboardSettings settings,
        SettingsStore settingsStore,
        DashboardCache cache,
        IProcessRunner processRunner,
        Func<bool>? highContrast = null)
    {
        _settings = settings;
        _settingsStore = settingsStore;
        _highContrast = highContrast ?? (() => System.Windows.SystemParameters.HighContrast);
        _snapshot = DashboardSnapshot.Empty("initial", DateTimeOffset.UtcNow);

        // The refresh boundary owns persisting the registry intent it produces: a newly discovered
        // project, a first-seen remote, or a remote now waiting on confirmation.
        _refresh = new RefreshService(settings, processRunner, cache, settingsStore: settingsStore);

        Projects = [];
        AllProjects = [];
        VisibleProjects = [];

        // Through the field rather than the property: opening in the view the owner left the
        // dashboard in is not a change to persist, and the mode's own change handler rebuilds
        // rows that do not exist until the three collections above are assigned.
        _mode = settings.Ui.ViewMode;
        _lastStandardMode = _mode == DashboardViewMode.Ribbon ? DashboardViewMode.Compact : _mode;
    }

    /// <summary>Everything the current filter matched.</summary>
    public ObservableCollection<ProjectItemViewModel> Projects { get; }

    /// <summary>What the window currently shows, which is a question the mode answers.</summary>
    public ObservableCollection<ProjectItemViewModel> VisibleProjects { get; }

    public ObservableCollection<ProjectItemViewModel> AllProjects { get; }

    public const int CompactRowLimit = 3;

    [ObservableProperty]
    private DashboardViewMode _mode;

    /// <summary>
    /// The view the chevron restores to. The ribbon is a place the owner collapses *from*, so
    /// coming back out of it has to land where they were rather than at a fixed default.
    /// </summary>
    private DashboardViewMode _lastStandardMode;

    /// <summary>
    /// Whether the detail pane is showing. Kept as its own question because the detail pane, the
    /// tray's label and conflict navigation all ask exactly this and nothing finer; setting it is
    /// how a caller says "open the evidence" without having to know the mode vocabulary.
    /// </summary>
    public bool IsExpanded
    {
        get => Mode is DashboardViewMode.Expanded or DashboardViewMode.Full;
        set => Mode = value ? DashboardViewMode.Expanded : DashboardViewMode.Compact;
    }

    public bool IsRibbon => Mode == DashboardViewMode.Ribbon;

    /// <summary>
    /// The view the ribbon is folded down from, and the one the chevron unfolds back to. Public
    /// because the ribbon borrows that view's width rather than having one of its own, so anything
    /// recording a width while collapsed has to know whose it is.
    /// </summary>
    public DashboardViewMode StandardMode => _lastStandardMode;

    /// <summary>
    /// Whether the window is filling the display. Its geometry is the monitor's rather than the
    /// owner's while it is, which is what everything that saves a size needs to know.
    /// </summary>
    public bool IsFullScreen => Mode == DashboardViewMode.Full;

    /// <summary>Whether the window is showing a project list, and so all the chrome around one.</summary>
    public bool IsStandardView => Mode != DashboardViewMode.Ribbon;

    /// <summary>
    /// The one project the ribbon carries: the first pinned project the current filter matched,
    /// and with nothing pinned, whichever moved most recently. Chosen by stated priority rather
    /// than by list order, because the filters that do not sort pinned-first would otherwise bury
    /// the project the owner pinned precisely to keep in sight.
    /// </summary>
    public ProjectItemViewModel? RibbonProject =>
        Projects.FirstOrDefault(p => p.IsPinned)
        ?? Projects.MaxBy(p => p.LastActivityAt ?? DateTimeOffset.MinValue);

    public bool HasRibbonProject => RibbonProject is not null;

    /// <summary>The whole ribbon read as one sentence, since it is a single control's worth of content.</summary>
    public string RibbonSummary => RibbonProject is { } project
        ? $"{project.Name}. {project.StateLabel}. Next action: {project.NextActionText}. Progress {project.ProgressText}."
        : "No projects match the current filter.";

    [ObservableProperty]
    private bool _isRefreshing;

    [ObservableProperty]
    private DashboardFilter _filter = DashboardFilter.Recent;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private ProjectItemViewModel? _selectedProject;

    [ObservableProperty]
    private string _statusLine = "Not refreshed yet.";

    [ObservableProperty]
    private int _activeCount;

    [ObservableProperty]
    private int _readyCount;

    [ObservableProperty]
    private int _blockedCount;

    [ObservableProperty]
    private int _diagnosticCount;

    [ObservableProperty]
    private bool _isStale;

    [ObservableProperty]
    private bool _isOffline;

    /// <summary>
    /// Surface opacity as WPF wants it (0–1), derived from the percentage the owner sets.
    /// Only the background carries it; content layers stay fully opaque and legible.
    /// <para>
    /// Under Windows high contrast there is no translucency at all. A high-contrast palette is a
    /// legibility guarantee, and blending any of it with whatever happens to be on the desktop
    /// behind the overlay is exactly the guarantee being broken.
    /// </para>
    /// </summary>
    public double SurfaceOpacity => _highContrast()
        ? 1d
        : Math.Clamp(_settings.Ui.SurfaceOpacityPercent, 10, 100) / 100d;

    public int SurfaceOpacityPercent
    {
        get => _settings.Ui.SurfaceOpacityPercent;
        set
        {
            var clamped = Math.Clamp(value, 10, 100);
            if (clamped == _settings.Ui.SurfaceOpacityPercent)
            {
                return;
            }

            _settings.Ui.SurfaceOpacityPercent = clamped;
            SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(SurfaceOpacity));
        }
    }

    /// <summary>Re-reads appearance settings so a change in the Settings window lands immediately.</summary>
    public void NotifyAppearanceChanged()
    {
        OnPropertyChanged(nameof(SurfaceOpacity));
        OnPropertyChanged(nameof(SurfaceOpacityPercent));
        OnPropertyChanged(nameof(ShellWidth));
        OnPropertyChanged(nameof(ReducedMotion));
    }

    /// <summary>
    /// The ribbon has no width of its own: it borrows the one belonging to the view it folded down
    /// from, so collapsing moves nothing sideways and unfolding lands on the same footprint. It is
    /// a strip, so length costs the owner nothing — only height was ever the thing in the way.
    /// </summary>
    public double ShellWidth => (Mode == DashboardViewMode.Ribbon ? StandardMode : Mode) switch
    {
        DashboardViewMode.Expanded => _settings.Ui.Geometry.ExpandedWidth,
        _ => _settings.Ui.Geometry.CompactWidth,
    };

    /// <summary>
    /// The owner's own preference, and Windows' when they have not expressed one: an accessibility
    /// setting made in Windows has to reach the dashboard without being set a second time here.
    /// </summary>
    public bool ReducedMotion =>
        _settings.Ui.ReducedMotion || !System.Windows.SystemParameters.ClientAreaAnimation;

    public IReadOnlyList<Diagnostic> Diagnostics => _snapshot.Diagnostics;

    public string RefreshId => _snapshot.RefreshId;

    partial void OnFilterChanged(DashboardFilter value) => ApplyFilter();

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnModeChanged(DashboardViewMode value)
    {
        if (value != DashboardViewMode.Ribbon)
        {
            _lastStandardMode = value;
        }

        _settings.Ui.ViewMode = value;

        OnPropertyChanged(nameof(IsExpanded));
        OnPropertyChanged(nameof(IsRibbon));
        OnPropertyChanged(nameof(IsFullScreen));
        OnPropertyChanged(nameof(IsStandardView));
        OnPropertyChanged(nameof(ShellWidth));

        UpdateVisibleProjects();
        SaveSettings();
    }

    /// <summary>
    /// Steps through the views that show a project list. The ribbon is deliberately not in the
    /// cycle: it is somewhere the owner goes on purpose, and stepping into it by accident while
    /// looking for a bigger window would be the opposite of what this control is for.
    /// </summary>
    [RelayCommand]
    public void CycleViewMode() => Mode = Mode switch
    {
        DashboardViewMode.Compact => DashboardViewMode.Expanded,
        DashboardViewMode.Expanded => DashboardViewMode.Full,
        DashboardViewMode.Full => DashboardViewMode.Compact,
        // Out of the ribbon this opens the view they were last in, not the start of the cycle.
        _ => _lastStandardMode,
    };

    /// <summary>Collapses to the ribbon, and back out to whichever standard view they left.</summary>
    [RelayCommand]
    public void ToggleRibbon() =>
        Mode = Mode == DashboardViewMode.Ribbon ? _lastStandardMode : DashboardViewMode.Ribbon;

    [RelayCommand]
    public void SetFilter(string filter) =>
        Filter = Enum.TryParse<DashboardFilter>(filter, ignoreCase: true, out var parsed) ? parsed : DashboardFilter.Recent;

    [RelayCommand]
    public async Task RefreshAsync()
    {
        // A second refresh replaces the first rather than queueing behind it: the owner asked
        // for the current answer, not for every intermediate one.
        var previous = _inFlight;
        _inFlight = new CancellationTokenSource();
        if (previous is not null)
        {
            await previous.CancelAsync();
        }

        var token = _inFlight.Token;
        await _refreshGate.WaitAsync(CancellationToken.None);

        try
        {
            IsRefreshing = true;

            // Handed to the thread pool rather than started here. A refresh is mostly filesystem
            // and process work, and only some of it ever awaits: discovery walks the roots
            // synchronously, and a project with nothing to wait on is indexed inline. Started on
            // the dispatcher, all of that runs on the dispatcher, and the window stops answering
            // the owner for as long as the scan lasts.
            var snapshot = await Task.Run(() => _refresh.RefreshAsync(token), token).ConfigureAwait(true);
            Apply(snapshot);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer refresh; the newer one owns the result.
        }
        catch (Exception ex)
        {
            StatusLine = $"Refresh failed: {ex.Message}";
        }
        finally
        {
            IsRefreshing = false;
            _refreshGate.Release();
        }
    }

    public void CancelRefresh() => _inFlight?.Cancel();

    /// <summary>
    /// Where a conflict control leads. The evidence lives in the expanded view's detail pane, so
    /// an aggregate badge on a compact row has to select its project and open that view — a
    /// warning the owner cannot follow to the disagreement is not navigation.
    /// </summary>
    private void NavigateToConflicts(ProjectItemViewModel project)
    {
        SelectedProject = project;
        IsExpanded = true;
    }

    private void Apply(DashboardSnapshot snapshot)
    {
        _snapshot = snapshot;

        AllProjects.Clear();
        foreach (var project in snapshot.Projects.Select(p => new ProjectItemViewModel(p, NavigateToConflicts)))
        {
            AllProjects.Add(project);
        }

        ActiveCount = snapshot.Counters.Active;
        ReadyCount = snapshot.Counters.Ready;
        BlockedCount = snapshot.Counters.Blocked;
        DiagnosticCount = snapshot.Counters.Diagnostics;
        IsStale = snapshot.IsStale;
        IsOffline = snapshot.Offline;

        StatusLine = $"{snapshot.Projects.Count} project(s) · refreshed {snapshot.GeneratedAt.ToLocalTime():HH:mm:ss}"
            + (snapshot.IsStale ? " · showing some cached evidence" : string.Empty)
            + (snapshot.Offline ? " · offline" : string.Empty);

        OnPropertyChanged(nameof(Diagnostics));
        OnPropertyChanged(nameof(RefreshId));
        ApplyFilter();
    }

    /// <summary>
    /// Recent answers "what changed now": only projects with work left and qualifying, non-closed
    /// activity in the window. The other filters answer the other operational questions.
    /// </summary>
    private void ApplyFilter()
    {
        var search = SearchText.Trim();

        IEnumerable<ProjectItemViewModel> candidates = Filter switch
        {
            DashboardFilter.Recent => AllProjects
                .Where(p => p.View.HasRemainingWork && p.RecentActivity.Count > 0)
                .OrderByDescending(p => p.IsPinned)
                .ThenByDescending(p => p.LastActivityAt),
            DashboardFilter.AllRemaining => AllProjects
                .Where(p => p.View.HasRemainingWork)
                .OrderByDescending(p => p.IsPinned)
                .ThenByDescending(p => p.LastActivityAt),
            DashboardFilter.InProgress => AllProjects.Where(p => p.State == ProjectState.InProgress),
            DashboardFilter.Blocked => AllProjects.Where(p => p.State == ProjectState.Blocked),
            // Pinned still answers a question about current work; finished work lives in Archive.
            DashboardFilter.Pinned => AllProjects.Where(p => p.IsPinned && p.State != ProjectState.Complete),
            DashboardFilter.Archive => AllProjects
                .Where(p => p.State == ProjectState.Complete)
                .OrderByDescending(p => p.LastActivityAt),
            _ => AllProjects,
        };

        if (search.Length > 0)
        {
            candidates = candidates.Where(p =>
                p.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || p.NextActionText.Contains(search, StringComparison.OrdinalIgnoreCase)
                || p.Efforts.Any(e => e.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || e.Tickets.Any(t => t.Title.Contains(search, StringComparison.OrdinalIgnoreCase))));
        }

        Projects.Clear();
        foreach (var project in candidates)
        {
            Projects.Add(project);
        }

        SelectedProject = Projects.FirstOrDefault(p => p.Path == SelectedProject?.Path) ?? Projects.FirstOrDefault();
        UpdateVisibleProjects();
    }

    private void UpdateVisibleProjects()
    {
        var selected = SelectedProject;

        VisibleProjects.Clear();
        foreach (var project in RowsForCurrentMode())
        {
            VisibleProjects.Add(project);
        }

        OnPropertyChanged(nameof(RibbonProject));
        OnPropertyChanged(nameof(HasRibbonProject));
        OnPropertyChanged(nameof(RibbonSummary));

        // Rebuilding the rows clears the list's own selection. A project that is still on screen
        // has to keep it, or switching between compact and expanded would empty the detail pane
        // and take the owner away from whatever they had just opened.
        SelectedProject = VisibleProjects.FirstOrDefault(p => p.Path == selected?.Path)
            ?? VisibleProjects.FirstOrDefault();
    }

    /// <summary>
    /// What the current view puts on screen. The ribbon shows its one project and nothing else;
    /// compact stops at three rows so continuous awareness costs almost no screen space.
    /// </summary>
    private IEnumerable<ProjectItemViewModel> RowsForCurrentMode()
    {
        if (Mode == DashboardViewMode.Ribbon)
        {
            return RibbonProject is { } single ? [single] : [];
        }

        return Mode == DashboardViewMode.Compact ? Projects.Take(CompactRowLimit) : Projects;
    }

    private void SaveSettings()
    {
        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusLine = $"Settings could not be saved: {ex.Message}";
        }
    }
}
