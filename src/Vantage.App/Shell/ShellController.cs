using System.ComponentModel;
using Vantage.App.ViewModels;
using Vantage.App.Views;
using Vantage.Infrastructure.Settings;

namespace Vantage.App.Shell;

/// <summary>
/// What the notification area does to the running window. This is the shell's wiring, kept out of
/// the composition root so the lifecycle the owner depends on — showing, hiding, expanding,
/// recovering from click-through, exiting — can be exercised against a real window rather than
/// described.
/// <para>
/// It owns only the window and the tray. Everything that reaches outside them — the settings
/// window, the logs folder, ending the process — is raised as a request for the application to
/// answer.
/// </para>
/// </summary>
public sealed class ShellController : IDisposable
{
    private readonly DashboardWindow _window;
    private readonly TrayIcon _tray;
    private readonly DashboardViewModel _viewModel;
    private readonly DashboardSettings _settings;
    private readonly StartupRegistration _startup;
    private readonly Action _saveSettings;

    public ShellController(
        DashboardWindow window,
        TrayIcon tray,
        DashboardViewModel viewModel,
        DashboardSettings settings,
        StartupRegistration startup,
        Action saveSettings)
    {
        _window = window;
        _tray = tray;
        _viewModel = viewModel;
        _settings = settings;
        _startup = startup;
        _saveSettings = saveSettings;

        _tray.ShowHideRequested += ToggleVisibility;
        _tray.CompactExpandRequested += ToggleExpanded;
        _tray.ClickThroughToggled += ToggleClickThrough;
        _tray.RefreshRequested += RequestRefresh;
        _tray.SettingsRequested += () => SettingsRequested?.Invoke();
        _tray.LogsRequested += () => LogsRequested?.Invoke();
        _tray.LaunchAtSignInToggled += ToggleLaunchAtSignIn;
        _tray.ExitRequested += Exit;

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    public event Action? SettingsRequested;

    public event Action? LogsRequested;

    public event Action? ExitRequested;

    /// <summary>Puts the dashboard on screen without taking the owner's focus.</summary>
    public void Start()
    {
        _window.ShowWithoutStealingFocus();
        UpdateTrayState();
    }

    public void ToggleVisibility()
    {
        if (_window.IsVisible)
        {
            _window.Hide();
        }
        else
        {
            _window.ShowWithoutStealingFocus();
        }

        UpdateTrayState();
    }

    /// <summary>Flipping the mode is enough: the window follows the view model's own change.</summary>
    public void ToggleExpanded() => _viewModel.CycleViewMode();

    /// <summary>Folds the dashboard down to its one line, and back out to where the owner was.</summary>
    public void ToggleRibbon() => _viewModel.ToggleRibbon();

    /// <summary>
    /// Click-through is always recoverable: the tray owns the same toggle, so the overlay can
    /// never make itself impossible to get back.
    /// </summary>
    public void ToggleClickThrough()
    {
        _window.SetClickThrough(!_settings.Ui.ClickThrough);
        UpdateTrayState();
    }

    public void ToggleLaunchAtSignIn()
    {
        var enabled = !_startup.IsEnabled();
        _startup.Set(enabled, Environment.ProcessPath ?? string.Empty);
        _settings.LaunchAtSignIn = enabled;
        _saveSettings();
        UpdateTrayState();
    }

    public void RequestRefresh() => _ = _viewModel.RefreshCommand.ExecuteAsync(null);

    /// <summary>The one command that really ends the session rather than hiding the window.</summary>
    public void Exit()
    {
        _window.ExitForReal();
        ExitRequested?.Invoke();
    }

    public void UpdateTrayState() =>
        _tray.UpdateState(_window.IsVisible, _viewModel.Mode, _settings.Ui.ClickThrough, _startup.IsEnabled());

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DashboardViewModel.Mode))
        {
            _window.ApplyMode();
            UpdateTrayState();
        }

        if (e.PropertyName == nameof(DashboardViewModel.StatusLine))
        {
            _tray.UpdateTooltip(_viewModel.StatusLine);
        }
    }

    public void Dispose() => _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
}
