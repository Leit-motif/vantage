using System.Windows;
using Vantage.Infrastructure.Settings;
using Microsoft.Win32;

namespace Vantage.App.Shell;

/// <summary>
/// Applies System, Dark, or Light appearance, and defers entirely to the system palette when
/// Windows high contrast is on. Theme changes re-style the running window without a restart.
/// </summary>
public sealed class ThemeManager(Application application, Func<bool>? highContrast = null)
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private readonly Func<bool> _highContrast = highContrast ?? (() => SystemParameters.HighContrast);

    public void Apply(AppTheme theme)
    {
        var source = _highContrast()
            ? "HighContrast"
            : Resolve(theme) == AppTheme.Light ? "Light" : "Dark";

        // The assembly is named explicitly: a bare pack URI resolves against the entry assembly,
        // which is the application only when the application is what started the process.
        var dictionary = new ResourceDictionary
        {
            Source = new Uri(
                $"pack://application:,,,/Vantage;component/Themes/{source}.xaml",
                UriKind.Absolute),
        };

        if (_highContrast())
        {
            // The palette Windows was told to use has to win outright — including the raw colours
            // the glass surface composes its own translucency from, which a brush cannot carry.
            dictionary["SurfaceColor"] = System.Windows.SystemColors.WindowColor;
        }

        var merged = application.Resources.MergedDictionaries;

        // The palette always sits first so the control styles resolve against the new colours.
        if (merged.Count > 0)
        {
            merged[0] = dictionary;
        }
        else
        {
            merged.Add(dictionary);
        }
    }

    private static AppTheme Resolve(AppTheme theme)
    {
        if (theme != AppTheme.System)
        {
            return theme;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0
                ? AppTheme.Light
                : AppTheme.Dark;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return AppTheme.Dark;
        }
    }
}
