using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MattWorkflowDashboard.Core;
using MattWorkflowDashboard.Core.Projection;
using MattWorkflowDashboard.Infrastructure.Persistence;
using MattWorkflowDashboard.Infrastructure.Processes;
using MattWorkflowDashboard.Infrastructure.Refresh;
using MattWorkflowDashboard.Infrastructure.Settings;

namespace MattWorkflowDashboard.App.ViewModels;

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
    private readonly DashboardCache _cache;
    private readonly IProcessRunner _processRunner;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private CancellationTokenSource? _inFlight;
    private DashboardSnapshot _snapshot;

    public DashboardViewModel(
        DashboardSettings settings,
        SettingsStore settingsStore,
        DashboardCache cache,
        IProcessRunner processRunner)
    {
        _settings = settings;
        _settingsStore = settingsStore;
        _cache = cache;
        _processRunner = processRunner;
        _snapshot = DashboardSnapshot.Empty("initial", DateTimeOffset.UtcNow);

        IsExpanded = !settings.Ui.StartCompact;
        Projects = [];
        AllProjects = [];
        VisibleProjects = [];
    }

    /// <summary>Everything the current filter matched.</summary>
    public ObservableCollection<ProjectItemViewModel> Projects { get; }

    /// <summary>
    /// What the window shows: the compact view deliberately stops at three rows so continuous
    /// awareness costs almost no screen space.
    /// </summary>
    public ObservableCollection<ProjectItemViewModel> VisibleProjects { get; }

    public ObservableCollection<ProjectItemViewModel> AllProjects { get; }

    public const int CompactRowLimit = 3;

    [ObservableProperty]
    private bool _isExpanded;

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
    /// </summary>
    public double SurfaceOpacity => Math.Clamp(_settings.Ui.SurfaceOpacityPercent, 10, 100) / 100d;

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

    public double ShellWidth => IsExpanded
        ? _settings.Ui.Geometry.ExpandedWidth
        : _settings.Ui.Geometry.CompactWidth;

    public bool ReducedMotion => _settings.Ui.ReducedMotion;

    public IReadOnlyList<Diagnostic> Diagnostics => _snapshot.Diagnostics;

    public string RefreshId => _snapshot.RefreshId;

    partial void OnFilterChanged(DashboardFilter value) => ApplyFilter();

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnIsExpandedChanged(bool value)
    {
        _settings.Ui.StartCompact = !value;
        OnPropertyChanged(nameof(ShellWidth));
        UpdateVisibleProjects();
        SaveSettings();
    }

    [RelayCommand]
    public void ToggleExpanded() => IsExpanded = !IsExpanded;

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
            var service = new RefreshService(_settings, _processRunner, _cache);
            service.RegistryDiscovered += _ => SaveSettings();

            var snapshot = await service.RefreshAsync(token).ConfigureAwait(true);
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

    private void Apply(DashboardSnapshot snapshot)
    {
        _snapshot = snapshot;

        AllProjects.Clear();
        foreach (var project in snapshot.Projects.Select(p => new ProjectItemViewModel(p)))
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
            DashboardFilter.Pinned => AllProjects.Where(p => p.IsPinned),
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
        VisibleProjects.Clear();
        foreach (var project in IsExpanded ? Projects : Projects.Take(CompactRowLimit))
        {
            VisibleProjects.Add(project);
        }
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
