using System.Windows;
using MattWorkflowDashboard.Infrastructure.Settings;
using Microsoft.Win32;

namespace MattWorkflowDashboard.App.Shell;

/// <summary>
/// Applies System, Dark, or Light appearance, and defers entirely to the system palette when
/// Windows high contrast is on. Theme changes re-style the running window without a restart.
/// </summary>
public sealed class ThemeManager(Application application)
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public void Apply(AppTheme theme)
    {
        var source = SystemParameters.HighContrast
            ? "HighContrast"
            : Resolve(theme) == AppTheme.Light ? "Light" : "Dark";

        var dictionary = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Themes/{source}.xaml", UriKind.Absolute),
        };

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
