using MattWorkflowDashboard.Core;
using MattWorkflowDashboard.Infrastructure.Settings;

namespace MattWorkflowDashboard.Infrastructure.Discovery;

/// <summary>A directory that looks like a project, before registry intent is applied.</summary>
public sealed record DiscoveredProject(
    string CanonicalPath,
    string Name,
    IReadOnlyList<string> Markers,
    bool IsNested,
    string? ParentProjectPath)
{
    public bool IsGitRepository => Markers.Contains(ProjectMarkers.Git);
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

            var queue = new Queue<(string Path, int Depth, string? Owner)>();
            queue.Enqueue((Canonicalize(root), 0, null));

            while (queue.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var (path, depth, owner) = queue.Dequeue();
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
                        ParentProjectPath: owner));
                    owningProject = path;
                }

                if (depth >= settings.MaxDiscoveryDepth)
                {
                    continue;
                }

                foreach (var child in EnumerateChildren(path, excluded, diagnostics))
                {
                    queue.Enqueue((child, depth + 1, owningProject));
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
        catch (IOException)
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

    private static IEnumerable<string> EnumerateChildren(
        string path,
        HashSet<string> excludedNames,
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
            if (name.Length == 0 || excludedNames.Contains(name))
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
