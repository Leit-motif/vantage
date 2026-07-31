using System.ComponentModel;
using System.Windows;
using Microsoft.Win32;

namespace Vantage.App.Shell;

/// <summary>
/// Notices when Windows changes how things should look. Selecting the System theme is a promise
/// to follow Windows, and a promise that is only kept when something else happens to re-apply the
/// theme is not kept at all: switching Windows between light and dark has to reach the running
/// window on its own.
/// <para>
/// Two different sources have to be watched. High contrast arrives as a WPF system parameter,
/// while the light/dark preference arrives only as a user-preference change from the shell.
/// </para>
/// </summary>
public sealed class AppearanceWatcher : IDisposable
{
    private bool _disposed;

    public AppearanceWatcher()
    {
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
    }

    /// <summary>Raised when anything the dashboard's appearance depends on has moved.</summary>
    public event Action? Changed;

    /// <summary>
    /// Whether a user-preference change is one the dashboard's appearance depends on. Windows
    /// raises this event for a great many unrelated preferences, and re-applying the theme for
    /// every one of them would rebuild the palette on changes that cannot affect it.
    /// </summary>
    public static bool Affects(UserPreferenceCategory category) =>
        category is UserPreferenceCategory.General
            or UserPreferenceCategory.Color
            or UserPreferenceCategory.VisualStyle
            or UserPreferenceCategory.Accessibility;

    /// <summary>Reports a preference change, raising <see cref="Changed"/> only if it matters.</summary>
    public void Report(UserPreferenceCategory category)
    {
        if (Affects(category))
        {
            Changed?.Invoke();
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) => Report(e.Category);

    private void OnSystemParameterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast))
        {
            Changed?.Invoke();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;
    }
}
