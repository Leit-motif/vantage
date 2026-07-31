using Microsoft.Win32;

namespace Vantage.App.Shell;

/// <summary>
/// Launch at sign-in. It is explicit and reversible: installing the dashboard never changes
/// startup behaviour on its own. The key and value are addressable so that verification can
/// prove the behaviour without ever writing to what really starts on the owner's machine.
/// </summary>
public sealed class StartupRegistration(string? keyPath = null, string? valueName = null)
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string DefaultValueName = "Vantage";

    private readonly string _keyPath = keyPath ?? RunKey;
    private readonly string _valueName = valueName ?? DefaultValueName;

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath);
        return key?.GetValue(_valueName) is string;
    }

    public void Set(bool enabled, string executablePath)
    {
        if (!enabled)
        {
            using var existing = Registry.CurrentUser.OpenSubKey(_keyPath, writable: true);
            existing?.DeleteValue(_valueName, throwOnMissingValue: false);
            return;
        }

        using var key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(_keyPath);

        key.SetValue(_valueName, $"\"{executablePath}\"");
    }
}
