using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MattWorkflowDashboard.Infrastructure.Discovery;
using MattWorkflowDashboard.Infrastructure.Settings;

namespace MattWorkflowDashboard.App.ViewModels;

/// <summary>
/// One registered project, with the intent the owner has expressed about it. Every change is
/// saved as it is made: a registry choice that is not written is a choice the next restart loses.
/// </summary>
public sealed partial class ProjectRegistryRow : ObservableObject
{
    private readonly Action _onChanged;

    /// <summary>
    /// The pending remote as it was when this row was shown. A refresh behind an open Settings
    /// window can move the entry on to a newer remote, and confirming must never adopt an origin
    /// that was never on screen.
    /// </summary>
    private string? _shownPendingOrigin;

    public ProjectRegistryRow(ProjectRegistryEntry entry, Action onChanged)
    {
        Entry = entry;
        _onChanged = onChanged;
        _shownPendingOrigin = entry.PendingOrigin;
    }

    public ProjectRegistryEntry Entry { get; }

    public string Path => Entry.Path;

    public string Origin => Entry.ConfirmedOrigin ?? "—";

    /// <summary>The remote waiting on confirmation, if the repository's origin has moved.</summary>
    public string? PendingOrigin => _shownPendingOrigin;

    public bool HasPendingRelink => _shownPendingOrigin is not null;

    public string RelinkDescription => _shownPendingOrigin is null
        ? "The remote matches the confirmed association."
        : $"The remote now reads {_shownPendingOrigin}. Confirm to adopt it.";

    public ProjectRegistryState State
    {
        get => Entry.State;
        set => Set(() => Entry.State = value);
    }

    public bool Pinned
    {
        get => Entry.Pinned;
        set => Set(() => Entry.Pinned = value);
    }

    public bool NestedOptIn
    {
        get => Entry.NestedOptIn;
        set => Set(() => Entry.NestedOptIn = value);
    }

    /// <summary>
    /// Adopts the pending remote — the one the owner was shown — as the confirmed association.
    /// Relinking is deliberate: a remote change must never attach unrelated GitHub work on its own.
    /// </summary>
    [RelayCommand]
    public void ConfirmRelink()
    {
        if (_shownPendingOrigin is null)
        {
            return;
        }

        Entry.ConfirmedOrigin = _shownPendingOrigin;

        // If the remote has already moved past what was shown, that newer remote is still waiting
        // on the owner; only the origin actually confirmed here stops being pending.
        if (string.Equals(Entry.PendingOrigin, _shownPendingOrigin, StringComparison.OrdinalIgnoreCase))
        {
            Entry.PendingOrigin = null;
        }

        _shownPendingOrigin = null;

        OnPropertyChanged(nameof(Origin));
        OnPropertyChanged(nameof(PendingOrigin));
        OnPropertyChanged(nameof(HasPendingRelink));
        OnPropertyChanged(nameof(RelinkDescription));
        _onChanged();
    }

    private void Set(Action mutate, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        mutate();
        OnPropertyChanged(name);
        _onChanged();
    }
}

/// <summary>
/// Configuration, in its own window so it never crowds the operational dashboard. Every change
/// is written atomically to the settings file the moment it is applied.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly DashboardSettings _settings;
    private readonly SettingsStore _store;
    private readonly Action _onApplied;

    public SettingsViewModel(DashboardSettings settings, SettingsStore store, Action onApplied)
    {
        _settings = settings;
        _store = store;
        _onApplied = onApplied;

        Roots = [.. settings.Roots];
        Projects = [.. settings.Projects.Select(p => new ProjectRegistryRow(p, Apply))];
        // Assigned to the backing field: reading the current state must not rewrite it.
        _launchAtSignIn = Shell.StartupRegistration.IsEnabled();
    }

    public ObservableCollection<string> Roots { get; }

    public ObservableCollection<ProjectRegistryRow> Projects { get; }

    public IReadOnlyList<AppTheme> Themes { get; } = Enum.GetValues<AppTheme>();

    public AppTheme Theme
    {
        get => _settings.Ui.Theme;
        set => Set(() => _settings.Ui.Theme = value);
    }

    /// <summary>
    /// Opacity, stated as what the owner sees: 100 is fully solid, lower is more see-through.
    /// The user's "80% transparency" target is expressed here as an opacity value so the
    /// visible result is never ambiguous.
    /// </summary>
    public int SurfaceOpacityPercent
    {
        get => _settings.Ui.SurfaceOpacityPercent;
        set => Set(() => _settings.Ui.SurfaceOpacityPercent = Math.Clamp(value, 10, 100));
    }

    public string OpacityDescription =>
        $"{SurfaceOpacityPercent}% opaque · {100 - SurfaceOpacityPercent}% see-through";

    public bool EdgeSnap
    {
        get => _settings.Ui.EdgeSnap;
        set => Set(() => _settings.Ui.EdgeSnap = value);
    }

    public bool ReducedMotion
    {
        get => _settings.Ui.ReducedMotion;
        set => Set(() => _settings.Ui.ReducedMotion = value);
    }

    /// <summary>Empty by default: no global shortcut is claimed unless one is typed here.</summary>
    public string ClickThroughHotkey
    {
        get => _settings.Ui.ClickThroughHotkey ?? string.Empty;
        set => Set(() => _settings.Ui.ClickThroughHotkey = string.IsNullOrWhiteSpace(value) ? null : value.Trim());
    }

    public bool GitHubEnrichmentEnabled
    {
        get => _settings.GitHubEnrichmentEnabled;
        set => Set(() => _settings.GitHubEnrichmentEnabled = value);
    }

    public int RecentWindowHours
    {
        get => _settings.RecentWindowHours;
        set => Set(() => _settings.RecentWindowHours = Math.Clamp(value, 1, 168));
    }

    public int MaxDiscoveryDepth
    {
        get => _settings.MaxDiscoveryDepth;
        set => Set(() => _settings.MaxDiscoveryDepth = Math.Clamp(value, 1, 12));
    }

    [ObservableProperty]
    private bool _launchAtSignIn;

    [ObservableProperty]
    private string _newRoot = string.Empty;

    /// <summary>
    /// A project beneath an excluded vendor, dependency, tool, build, or cache location. Discovery
    /// never walks those trees uninvited, so an intentionally independent project down there is
    /// named here once and then tracked like any other.
    /// </summary>
    [ObservableProperty]
    private string _newNestedProject = string.Empty;

    partial void OnLaunchAtSignInChanged(bool value)
    {
        _settings.LaunchAtSignIn = value;
        Shell.StartupRegistration.Set(value, Environment.ProcessPath ?? string.Empty);
        Apply();
    }

    [RelayCommand]
    public void AddRoot()
    {
        var root = NewRoot.Trim();
        if (root.Length == 0 || Roots.Contains(root, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        Roots.Add(root);
        _settings.Roots.Add(root);
        NewRoot = string.Empty;
        Apply();
    }

    [RelayCommand]
    public void RemoveRoot(string? root)
    {
        if (root is null)
        {
            return;
        }

        Roots.Remove(root);
        _settings.Roots.RemoveAll(r => string.Equals(r, root, StringComparison.OrdinalIgnoreCase));
        Apply();
    }

    [RelayCommand]
    public void AddNestedProject()
    {
        var typed = NewNestedProject.Trim();
        if (typed.Length == 0)
        {
            return;
        }

        string path;
        string route;
        try
        {
            path = ProjectDiscovery.Canonicalize(typed);
            route = ProjectDiscovery.Lexical(typed);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // A path the owner mistyped is not worth a crash; leave it in the box to be corrected.
            return;
        }

        if (_settings.FindProject(path) is not null)
        {
            return;
        }

        var entry = new ProjectRegistryEntry
        {
            Path = path,
            State = ProjectRegistryState.Enabled,
            NestedOptIn = true,

            // Identity is canonical, but a project that is itself a link out of an excluded location
            // cannot be walked to by that name — so the route as typed is kept alongside it.
            OptInPath = string.Equals(route, path, StringComparison.OrdinalIgnoreCase) ? null : route,
        };

        _settings.Projects.Add(entry);
        Projects.Add(new ProjectRegistryRow(entry, Apply));
        NewNestedProject = string.Empty;
        Apply();
    }

    /// <summary>
    /// Empty while configuration is being written successfully. A failed write says so here rather
    /// than looking like it worked.
    /// </summary>
    [ObservableProperty]
    private string _saveError = string.Empty;

    [RelayCommand]
    public void Apply()
    {
        // Marked before the write, never after: if the write fails, the change the owner made is
        // still outstanding and the next refresh has a reason to try again.
        _settings.MarkChanged();

        try
        {
            _store.Save(_settings);
            SaveError = string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SaveError = $"Settings could not be saved: {ex.Message}. The change is still pending and will be retried.";
        }

        _onApplied();
    }

    private void Set(Action mutate, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        mutate();
        OnPropertyChanged(name);
        OnPropertyChanged(nameof(OpacityDescription));
        Apply();
    }
}
