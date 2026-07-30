using System.Text.Json.Serialization;

namespace MattWorkflowDashboard.Infrastructure.Settings;

/// <summary>Registry intent for a discovered project. Hiding is not the same as forgetting.</summary>
public enum ProjectRegistryState
{
    Enabled,
    Hidden,
    Excluded,
}

public enum AppTheme
{
    System,
    Dark,
    Light,
}

public sealed class ProjectRegistryEntry
{
    public string Path { get; set; } = string.Empty;

    public ProjectRegistryState State { get; set; } = ProjectRegistryState.Enabled;

    public bool Pinned { get; set; }

    /// <summary>
    /// Opts a project inside a vendor, dependency, tool, build, or cache location back in. Ordinary
    /// nesting needs no opt-in; only those excluded locations do.
    /// </summary>
    public bool NestedOptIn { get; set; }

    /// <summary>
    /// The route to this project as the owner wrote it, kept only when it differs from the canonical
    /// <see cref="Path"/> — a project that is itself a link out of an excluded location has a
    /// canonical identity that leads nowhere near the excluded tree, and discovery would have no way
    /// back to it. Identity stays <see cref="Path"/>; this is only how the walk gets there.
    /// </summary>
    public string? OptInPath { get; set; }

    /// <summary>
    /// The GitHub origin the owner confirmed for this path. A remote that no longer matches is
    /// reported rather than silently adopted, so unrelated remote work cannot attach itself.
    /// </summary>
    public string? ConfirmedOrigin { get; set; }

    /// <summary>
    /// A remote that differs from the confirmed one and is waiting on the owner. Recording it means
    /// confirming a relink adopts the origin they were shown, not whatever the remote reads later.
    /// </summary>
    public string? PendingOrigin { get; set; }
}

public sealed class WindowGeometry
{
    /// <summary>Null until the window has been placed, so "unset" is representable in JSON.</summary>
    public double? Left { get; set; }

    public double? Top { get; set; }

    public double CompactWidth { get; set; } = 380;

    public double ExpandedWidth { get; set; } = 720;

    public double Height { get; set; } = 520;

    public string? MonitorDeviceName { get; set; }
}

public sealed class UiSettings
{
    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>
    /// Surface opacity as a percentage, where 100 is fully solid and 0 is fully see-through.
    /// Named for what the user sees, because "transparency" and "opacity" are routinely inverted
    /// by the underlying APIs.
    /// </summary>
    public int SurfaceOpacityPercent { get; set; } = 80;

    public bool StartCompact { get; set; } = true;

    /// <summary>Off by default, and only ever turned on deliberately: it is hard to undo by mouse.</summary>
    public bool ClickThrough { get; set; }

    /// <summary>No default binding — the dashboard never claims a global shortcut uninvited.</summary>
    public string? ClickThroughHotkey { get; set; }

    public bool EdgeSnap { get; set; }

    public bool ReducedMotion { get; set; }

    public WindowGeometry Geometry { get; set; } = new();
}

/// <summary>Atomic, versioned configuration. The only file the dashboard writes on the owner's behalf.</summary>
public sealed class DashboardSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public List<string> Roots { get; set; } =
    [
        @"C:\Users\Example\Workspaces",
        @"D:\Projects",
    ];

    public List<ProjectRegistryEntry> Projects { get; set; } = [];

    /// <summary>
    /// Directory names that mark vendored, dependency, tool, build, and cache trees. Nested
    /// projects underneath them are excluded unless opted back in.
    /// </summary>
    public List<string> ExcludedDirectoryNames { get; set; } =
    [
        "node_modules", "vendor", "packages", "bower_components", "third_party", "thirdparty",
        "bin", "obj", "build", "dist", "out", "target", "artifacts", "publish",
        ".venv", "venv", "env", "site-packages", "__pycache__",
        ".gradle", ".nuget", ".cargo", ".tox", ".terraform", ".pnpm-store",
        ".cache", "cache", "tmp", "temp", ".next", ".nuxt", ".svelte-kit", ".turbo",
        "Library", "Pods", "DerivedData", "mods", "downloads",
    ];

    public int MaxDiscoveryDepth { get; set; } = 6;

    public int MaxProjects { get; set; } = 300;

    public int MaxDirectoriesScanned { get; set; } = 40_000;

    public int RecentWindowHours { get; set; } = 24;

    public int ActivityRetentionDays { get; set; } = 90;

    public int MaxConcurrentExternalProcesses { get; set; } = 4;

    public int ExternalProcessTimeoutSeconds { get; set; } = 20;

    /// <summary>Bounds the per-repository GitHub read so an associated repo cannot become a crawl.</summary>
    public int MaxGitHubIssuesPerRepository { get; set; } = 200;

    public bool GitHubEnrichmentEnabled { get; set; } = true;

    public bool LaunchAtSignIn { get; set; }

    public UiSettings Ui { get; set; } = new();

    private long _revision;

    /// <summary>
    /// How many times this object has been changed, and how much of that a successful save has
    /// captured. Neither is part of the file: they describe this object.
    ///
    /// A version stamp rather than a dirty flag, because a save reads the object while a refresh
    /// worker may still be changing it. Clearing a flag would discard a change made mid-write; a
    /// stamp records only the revision actually serialized, so anything later stays outstanding.
    /// </summary>
    [JsonIgnore]
    public long Revision => Volatile.Read(ref _revision);

    [JsonIgnore]
    public long SavedRevision { get; set; }

    [JsonIgnore]
    public bool HasUnsavedChanges => Revision != SavedRevision;

    /// <summary>Records a change to be written. Safe to call from the parallel refresh workers.</summary>
    public void MarkChanged() => Interlocked.Increment(ref _revision);

    public ProjectRegistryEntry? FindProject(string canonicalPath) =>
        Projects.FirstOrDefault(p => string.Equals(p.Path, canonicalPath, StringComparison.OrdinalIgnoreCase));
}
