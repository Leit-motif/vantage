namespace MattWorkflowDashboard.Infrastructure;

/// <summary>
/// Where the dashboard keeps its own state. Everything lives under local application data;
/// the application never writes dashboard state into a monitored repository.
/// </summary>
public sealed class AppPaths
{
    public const string ProductFolder = "MattWorkflowDashboard";

    public AppPaths(string? rootOverride = null)
    {
        Root = rootOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ProductFolder);

        SettingsFile = Path.Combine(Root, "settings.json");
        CacheFile = Path.Combine(Root, "cache.db");
        LogDirectory = Path.Combine(Root, "logs");
    }

    public string Root { get; }

    public string SettingsFile { get; }

    public string CacheFile { get; }

    public string LogDirectory { get; }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogDirectory);
    }
}
