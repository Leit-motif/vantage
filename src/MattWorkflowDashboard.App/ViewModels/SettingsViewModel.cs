using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MattWorkflowDashboard.Infrastructure.Settings;

namespace MattWorkflowDashboard.App.ViewModels;

/// <summary>One registered project, with the intent the owner has expressed about it.</summary>
public sealed partial class ProjectRegistryRow(ProjectRegistryEntry entry) : ObservableObject
{
    public ProjectRegistryEntry Entry { get; } = entry;

    public string Path => Entry.Path;

    public string Origin => Entry.ConfirmedOrigin ?? "—";

    public ProjectRegistryState State
    {
        get => Entry.State;
        set
        {
            Entry.State = value;
            OnPropertyChanged();
        }
    }

    public bool Pinned
    {
        get => Entry.Pinned;
        set
        {
            Entry.Pinned = value;
            OnPropertyChanged();
        }
    }

    public bool NestedOptIn
    {
        get => Entry.NestedOptIn;
        set
        {
            Entry.NestedOptIn = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Clears the confirmed origin so the next refresh adopts the current remote. Relinking is
    /// deliberate: a remote change must never attach unrelated GitHub work on its own.
    /// </summary>
    [RelayCommand]
    public void ConfirmRelink()
    {
        Entry.ConfirmedOrigin = null;
        OnPropertyChanged(nameof(Origin));
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
        Projects = [.. settings.Projects.Select(p => new ProjectRegistryRow(p))];
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
    public void Apply()
    {
        _store.Save(_settings);
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
