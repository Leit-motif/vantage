using MattWorkflowDashboard.Core;
using MattWorkflowDashboard.Infrastructure.Settings;

namespace MattWorkflowDashboard.Infrastructure.Discovery;

/// <summary>A directory that looks like a project, before registry intent is applied.</summary>
public sealed record DiscoveredProject(
    string CanonicalPath,
    string Name,
    IReadOnlyList<string> Markers,
    bool IsNested,
    string? ParentProjectPath,
    string? ExcludedLocation = null)
{
    public bool IsGitRepository => Markers.Contains(ProjectMarkers.Git);

    /// <summary>
    /// The vendor, dependency, tool, build, or cache directory this project sits beneath, if any.
    /// Nesting on its own is not exclusion: only a project inside one of these locations needs an
    /// opt-in before it is tracked.
    /// </summary>
    public bool IsBeneathExcludedLocation => ExcludedLocation is not null;
}

public static class ProjectMarkers
{
    public const string Git = ".git";
    public const string Agents = "AGENTS.md";
    public const string IssueTracker = "docs/agents/issue-tracker.md";
    public const string Scratch = ".scratch";

    public static readonly IReadOnlyList<string> All = [Git, Agents, IssueTracker, Scratch];
}

public sealed record DiscoveryResult(
    IReadOnlyList<DiscoveredProject> Projects,
    IReadOnlyList<Diagnostic> Diagnostics,
    bool Truncated);

/// <summary>
/// Finds candidate projects beneath the configured roots. A Git repository is not required:
/// structured non-Git work is visible alongside repositories. Traversal is bounded so a large
/// root cannot turn discovery into an unbounded crawl.
/// </summary>
public sealed class ProjectDiscovery(DashboardSettings settings)
{
    public DiscoveryResult Discover(CancellationToken cancellationToken)
    {
        var excluded = new HashSet<string>(settings.ExcludedDirectoryNames, StringComparer.OrdinalIgnoreCase);

        // The only reason to walk into a vendor, dependency, tool, build, or cache tree is a
        // project the owner explicitly opted in down there. Those paths are followed one at a
        // time, so an opt-in never turns an excluded tree into a crawl.
        var optIns = settings.Projects
            .Where(p => p.NestedOptIn && p.Path.Length > 0)
            .Select(p => Canonicalize(p.Path))
            .ToList();

        var found = new List<DiscoveredProject>();
        var diagnostics = new List<Diagnostic>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scanned = 0;
        var truncated = false;

        foreach (var root in settings.Roots)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!Directory.Exists(root))
            {
                diagnostics.Add(Diagnostic.Info(
                    DiagnosticCode.ProjectScanFailed,
                    $"Configured root '{root}' does not exist.",
                    root));
                continue;
            }

            var queue = new Queue<(string Path, int Depth, string? Owner, string? ExcludedLocation)>();
            queue.Enqueue((Canonicalize(root), 0, null, null));

            while (queue.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var (path, depth, owner, excludedLocation) = queue.Dequeue();
                if (!visited.Add(path))
                {
                    continue;
                }

                if (++scanned > settings.MaxDirectoriesScanned || found.Count >= settings.MaxProjects)
                {
                    truncated = true;
                    break;
                }

                var markers = DetectMarkers(path, diagnostics);
                var owningProject = owner;

                if (markers.Count > 0)
                {
                    found.Add(new DiscoveredProject(
                        path,
                        Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)),
                        markers,
                        IsNested: owner is not null,
                        ParentProjectPath: owner,
                        ExcludedLocation: excludedLocation));
                    owningProject = path;
                }

                if (depth >= settings.MaxDiscoveryDepth)
                {
                    continue;
                }

                foreach (var child in EnumerateChildren(path, diagnostics))
                {
                    var childExcludedLocation = excludedLocation
                        ?? (excluded.Contains(Path.GetFileName(child)) ? child : null);

                    if (childExcludedLocation is not null && !LeadsToOptIn(child, optIns))
                    {
                        continue;
                    }

                    queue.Enqueue((child, depth + 1, owningProject, childExcludedLocation));
                }
            }

            if (truncated)
            {
                break;
            }
        }

        if (truncated)
        {
            diagnostics.Add(Diagnostic.Warning(
                DiagnosticCode.ScanTruncated,
                $"Discovery stopped after {scanned} directories or {found.Count} projects; raise the limits in Settings to see more.",
                string.Join("; ", settings.Roots)));
        }

        return new DiscoveryResult(found, diagnostics, truncated);
    }

    /// <summary>
    /// The canonical normalized local path, which is the project's identity. Link targets are
    /// resolved so a junction and its target do not become two identities for one project.
    /// </summary>
    public static string Canonicalize(string path)
    {
        var full = Path.GetFullPath(path);
        try
        {
            var info = new DirectoryInfo(full);
            if (info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
            {
                full = Path.GetFullPath(target.FullName);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable link is still a usable path; fall back to the literal one.
        }

        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static List<string> DetectMarkers(string path, ICollection<Diagnostic> diagnostics)
    {
        var markers = new List<string>();
        try
        {
            if (Directory.Exists(Path.Combine(path, ".git")) || File.Exists(Path.Combine(path, ".git")))
            {
                markers.Add(ProjectMarkers.Git);
            }

            if (File.Exists(Path.Combine(path, "AGENTS.md")))
            {
                markers.Add(ProjectMarkers.Agents);
            }

            if (File.Exists(Path.Combine(path, "docs", "agents", "issue-tracker.md")))
            {
                markers.Add(ProjectMarkers.IssueTracker);
            }

            if (Directory.Exists(Path.Combine(path, ".scratch")))
            {
                markers.Add(ProjectMarkers.Scratch);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(Diagnostic.Warning(
                DiagnosticCode.ProjectScanFailed,
                $"Could not inspect '{path}': {ex.Message}",
                path));
        }

        return markers;
    }

    /// <summary>
    /// True when <paramref name="directory"/> is, or is on the way to, a path the owner opted in.
    /// </summary>
    private static bool LeadsToOptIn(string directory, IReadOnlyList<string> optIns)
    {
        var prefix = directory + Path.DirectorySeparatorChar;
        return optIns.Any(optIn =>
            string.Equals(optIn, directory, StringComparison.OrdinalIgnoreCase)
            || optIn.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> EnumerateChildren(
        string path,
        ICollection<Diagnostic> diagnostics)
    {
        string[] children;
        try
        {
            children = Directory.GetDirectories(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(Diagnostic.Info(
                DiagnosticCode.ProjectScanFailed,
                $"Could not enumerate '{path}': {ex.Message}",
                path));
            yield break;
        }

        foreach (var child in children)
        {
            var name = Path.GetFileName(child);
            if (name.Length == 0)
            {
                continue;
            }

            // Repository internals and scratch contents are indexed by their own adapters,
            // never treated as places to look for further projects.
            if (string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, ".scratch", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return Canonicalize(child);
        }
    }
}
