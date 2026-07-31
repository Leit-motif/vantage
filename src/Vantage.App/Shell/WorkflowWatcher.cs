namespace Vantage.App.Shell;

/// <summary>
/// Watches the configured roots for workflow changes and asks for a refresh once the burst
/// settles. Watching is read-only and the debounce provides the backpressure: a noisy checkout
/// must not turn into a refresh per file.
/// </summary>
public sealed class WorkflowWatcher : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly System.Timers.Timer _debounce;
    private readonly Action _onSettled;

    public WorkflowWatcher(IEnumerable<string> roots, Action onSettled, TimeSpan? quietPeriod = null)
    {
        _onSettled = onSettled;
        _debounce = new System.Timers.Timer((quietPeriod ?? TimeSpan.FromSeconds(3)).TotalMilliseconds)
        {
            AutoReset = false,
        };
        _debounce.Elapsed += (_, _) => _onSettled();

        foreach (var root in roots.Where(Directory.Exists))
        {
            try
            {
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    Filter = "*.md",
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
                    InternalBufferSize = 64 * 1024,
                    EnableRaisingEvents = true,
                };

                watcher.Changed += OnChanged;
                watcher.Created += OnChanged;
                watcher.Deleted += OnChanged;
                watcher.Renamed += OnChanged;

                // A dropped buffer means changes were missed, so a full refresh is the honest response.
                watcher.Error += (_, _) => Nudge();

                _watchers.Add(watcher);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // A root that cannot be watched still gets picked up by the periodic refresh.
            }
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        // Only planning artifacts matter here; everything else is noise for this dashboard.
        if (e.FullPath.Contains($"{Path.DirectorySeparatorChar}.scratch{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            || e.Name?.EndsWith("AGENTS.md", StringComparison.OrdinalIgnoreCase) == true)
        {
            Nudge();
        }
    }

    private void Nudge()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }

        _debounce.Dispose();
    }
}
