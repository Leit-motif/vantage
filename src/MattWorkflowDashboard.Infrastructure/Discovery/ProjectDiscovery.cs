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

/// <summary>
/// One opted-in project and every name discovery may meet it under: the path as written, its
/// lexical form, and its canonical form — plus the same for a separately recorded route, when the
/// canonical identity is not a path that can be navigated back to.
/// </summary>
internal sealed record OptIn(string Written, IReadOnlyList<string> Paths);

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
        //
        // Both forms are kept. An opt-in is usually written the way the owner sees it on disk
        // ('host\node_modules\companion'), which is not what canonicalizing produces when a
        // directory along the way is a junction — matching either form is what lets the walk
        // reach it, while identity stays canonical.
        var optInPaths = settings.Projects
            .Where(p => p.NestedOptIn && p.Path.Length > 0)
            .Select(p => new OptIn(
                p.Path,
                [
                    .. new[] { p.Path, p.OptInPath }
                        .Where(path => !string.IsNullOrWhiteSpace(path))
                        .SelectMany(path => new[] { Lexical(path!), Canonicalize(path!) })
                        .Distinct(StringComparer.OrdinalIgnoreCase),
                ]))
            .ToList();

        var optIns = optInPaths
            .SelectMany(o => o.Paths)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var found = new List<DiscoveredProject>();
        var diagnostics = new List<Diagnostic>();

        // Routes are deduplicated separately from identities. Two routes can lead to one directory —
        // a junction and its target — and dropping the second route would hide whatever only it can
        // reach, while emitting both would invent a second project for one place on disk.
        var routes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Every directory actually walked, in both forms, so an unreached opt-in can be told apart
        // from one that was reached under another of its names.
        var reached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

            // Lexical is the path as it reads on disk and drives policy and opt-in matching;
            // canonical resolves links and is the project's identity.
            var queue = new Queue<(string Lexical, string Path, int Depth, string? Owner, string? ExcludedLocation)>();
            queue.Enqueue((Lexical(root), Canonicalize(root), 0, null, null));

            while (queue.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var (lexical, path, depth, owner, excludedLocation) = queue.Dequeue();
                if (!routes.Add(lexical))
                {
                    continue;
                }

                reached.Add(lexical);
                reached.Add(path);

                if (++scanned > settings.MaxDirectoriesScanned || found.Count >= settings.MaxProjects)
                {
                    truncated = true;
                    break;
                }

                var markers = DetectMarkers(path, diagnostics);
                var owningProject = owner;

                if (markers.Count > 0)
                {
                    // One place on disk is one project, whichever route arrived at it first.
                    if (emitted.Add(path))
                    {
                        found.Add(new DiscoveredProject(
                            path,
                            Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)),
                            markers,
                            IsNested: owner is not null,
                            ParentProjectPath: owner,
                            ExcludedLocation: excludedLocation));
                    }

                    owningProject = path;
                }

                if (depth >= settings.MaxDiscoveryDepth)
                {
                    continue;
                }

                if (excludedLocation is not null)
                {
                    // Inside an excluded tree nothing is enumerated at all: the walk steps along
                    // the opted-in paths themselves, so a vendor directory with thousands of
                    // siblings costs one directory probe per opted-in segment.
                    foreach (var name in OptInDescendants(lexical, path, optIns))
                    {
                        queue.Enqueue((
                            Path.Combine(lexical, name),
                            CanonicalChild(path, name),
                            depth + 1,
                            owningProject,
                            excludedLocation));
                    }

                    continue;
                }

                foreach (var name in EnumerateChildNames(path, diagnostics))
                {
                    // Repository internals and scratch contents are indexed by their own adapters,
                    // never treated as places to look for further projects.
                    if (string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(name, ".scratch", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var childLexical = Path.Combine(lexical, name);
                    var childCanonical = CanonicalChild(path, name);

                    // The policy is about the name the owner sees on disk. Canonicalizing first
                    // would let a junction named 'node_modules' pointing elsewhere escape it.
                    if (!excluded.Contains(name))
                    {
                        queue.Enqueue((childLexical, childCanonical, depth + 1, owningProject, null));
                        continue;
                    }

                    // Either name can carry the opt-in: the route the owner wrote, or the place the
                    // junction actually points at.
                    if (LeadsToOptIn(childLexical, optIns) || LeadsToOptIn(childCanonical, optIns))
                    {
                        queue.Enqueue((childLexical, childCanonical, depth + 1, owningProject, childLexical));
                    }
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

        // An opt-in that discovery never reached would otherwise be silent: no project, no reason.
        if (!truncated)
        {
            foreach (var optIn in optInPaths.Where(o => !o.Paths.Any(reached.Contains)))
            {
                diagnostics.Add(Diagnostic.Warning(
                    DiagnosticCode.ProjectScanFailed,
                    $"The opted-in project '{optIn.Written}' was not reached: check that the path exists, sits beneath a configured root, and is within the discovery depth.",
                    Canonicalize(optIn.Written)));
            }
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

    /// <summary>
    /// The path as written, normalized but with links left alone — what the owner sees on disk and
    /// what discovery policy reads. <see cref="Canonicalize"/> is identity; this is navigation.
    /// </summary>
    public static string Lexical(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

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

    /// <summary>
    /// The names of the next directories below this one on the way to each opted-in project, taken
    /// from the opt-in paths themselves rather than from enumerating the directory. Segments match
    /// against either form of the current directory, because an opt-in may have been written as the
    /// route the owner sees or as the place a junction points at.
    /// </summary>
    private static IEnumerable<string> OptInDescendants(
        string lexical,
        string canonical,
        IReadOnlyList<string> optIns)
    {
        var next = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var prefix in new[] { lexical, canonical }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var withSeparator = prefix + Path.DirectorySeparatorChar;

            foreach (var optIn in optIns)
            {
                if (!optIn.StartsWith(withSeparator, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var remainder = optIn[withSeparator.Length..];
                var separator = remainder.IndexOf(Path.DirectorySeparatorChar);
                var segment = separator < 0 ? remainder : remainder[..separator];
                if (segment.Length > 0)
                {
                    next.Add(segment);
                }
            }
        }

        return next.Where(name => Directory.Exists(Path.Combine(lexical, name)));
    }

    /// <summary>
    /// The canonical path of a child, built from the canonical parent so that a junction anywhere
    /// above it is already resolved. Canonicalizing a whole path only resolves its final segment,
    /// which is how an alias would otherwise become a second identity for one directory.
    /// </summary>
    private static string CanonicalChild(string canonicalParent, string name) =>
        Canonicalize(Path.Combine(canonicalParent, name));

    /// <summary>
    /// Child directory names as they read on disk. Policy reads the name, and identity is composed
    /// from the canonical parent — never from canonicalizing the child's own full path.
    /// </summary>
    private static IEnumerable<string> EnumerateChildNames(
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

            yield return name;
        }
    }
}
