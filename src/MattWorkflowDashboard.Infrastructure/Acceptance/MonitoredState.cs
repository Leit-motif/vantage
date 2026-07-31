using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MattWorkflowDashboard.Infrastructure.Processes;

namespace MattWorkflowDashboard.Infrastructure.Acceptance;

/// <summary>
/// Everything about one monitored project that the dashboard must leave exactly as it found it:
/// the workflow files it reads, the Git state it reads them alongside, the repository's own
/// configuration, and the GitHub issues and labels it enriches them with.
/// <para>
/// Every field is a digest rather than a value. The comparison only ever asks whether something
/// changed, so the evidence never has to carry the contents of a private workspace to answer.
/// </para>
/// <para>
/// <see cref="Unavailable"/> is what keeps a blind spot from reading as a clean result. A source
/// that could not be read has no digest, and two absent digests compare equal — so the sources that
/// were never observed are named rather than left to look unchanged.
/// </para>
/// </summary>
public sealed record MonitoredProjectState(
    string ProjectId,
    bool IsGitRepository,
    int WorkflowFileCount,
    string WorkflowContentDigest,
    string? GitHeadDigest,
    string? GitStatusDigest,
    string? GitConfigDigest,
    string? GitRefsDigest,
    string? OriginId,
    int GitHubIssueCount,
    string? GitHubIssuesDigest,
    int GitHubLabelCount,
    string? GitHubLabelsDigest,
    IReadOnlyList<string> Unavailable);

/// <summary>One thing that did not survive the run untouched.</summary>
public sealed record MonitoredStateChange(string ProjectId, string Subject, string Before, string After);

/// <summary>A source the comparison could not see, and so cannot vouch for either way.</summary>
public sealed record FingerprintGap(string ProjectId, string Subject);

/// <summary>
/// What the registry says about a project's repository. The distinction that matters is not whether
/// the registry has an entry but whether that entry carries a <em>confirmed</em> origin: an entry
/// written before any remote was seen has nothing to contradict, and the dashboard's own rule for
/// that case is to adopt the first remote it observes.
/// </summary>
public sealed record ProjectAssociation(string? ConfirmedSlug)
{
    public static readonly ProjectAssociation None = new((string?)null);

    /// <summary>
    /// True when a repository has been confirmed for this project, and therefore when it is the only
    /// one that may be queried — a remote that has since changed is pending the owner's confirmation.
    /// </summary>
    public bool IsConfirmed => ConfirmedSlug is not null;
}

/// <summary>
/// Reads the monitored state of a set of projects, before and after, using only commands the
/// <see cref="ReadOnlyProcessRunner"/> will let through.
/// </summary>
/// <param name="associationOf">
/// Which repository a project is allowed to be asked about. For a project the registry already
/// knows, that is its *confirmed* association and never the remote as it currently reads — a
/// changed remote is held pending the owner's confirmation, and querying it would reach a
/// repository they never approved. For a project the registry has never seen there is nothing
/// pending: the first remote observed is what becomes its association, so reading the remote is
/// both correct and what the dashboard itself would do.
/// </param>
public sealed class MonitoredStateReader(
    IProcessRunner runner,
    Func<string, string> identify,
    int maxIssues,
    Func<string, ProjectAssociation>? associationOf = null)
{
    /// <summary>
    /// The files this dashboard actually opens in a project. Everything else in a working tree is
    /// covered by the Git status and ref digests instead.
    /// </summary>
    private static readonly string[] MarkerFiles = ["AGENTS.md", Path.Combine("docs", "agents", "issue-tracker.md")];

    /// <summary>
    /// Bounds on the fingerprint itself. The acceptance command has to complete as reliably as the
    /// scan it measures: an accidentally huge <c>.scratch</c> tree must truncate visibly rather than
    /// exhaust memory before the measured refresh has even started.
    /// </summary>
    private const int MaxWorkflowFiles = 20_000;

    private const long MaxWorkflowFileBytes = 8L * 1024 * 1024;

    public async Task<IReadOnlyList<MonitoredProjectState>> ReadAsync(
        IEnumerable<string> projectPaths,
        bool includeGitHub,
        CancellationToken cancellationToken)
    {
        var states = new List<MonitoredProjectState>();

        foreach (var path in projectPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            states.Add(await ReadOneAsync(path, includeGitHub, cancellationToken).ConfigureAwait(false));
        }

        return states;
    }

    private async Task<MonitoredProjectState> ReadOneAsync(
        string projectPath,
        bool includeGitHub,
        CancellationToken cancellationToken)
    {
        var unavailable = new List<string>();
        var (fileCount, contentDigest) = WorkflowDigest(projectPath, unavailable, cancellationToken);

        var isRepository = Directory.Exists(Path.Combine(projectPath, ".git"))
            || File.Exists(Path.Combine(projectPath, ".git"));

        string? head = null, status = null, config = null, refs = null;

        if (isRepository)
        {
            // Status first: whether it worked is what tells a repository that has nothing to show
            // apart from one that could not be read.
            status = await GitDigestAsync(projectPath, ["status", "--porcelain"], "git status", unavailable, cancellationToken).ConfigureAwait(false);
            config = await GitDigestAsync(projectPath, ["config", "--list", "--local"], "git config", unavailable, cancellationToken).ConfigureAwait(false);

            var readable = status is not null;

            // A repository with no commits has no HEAD and no refs, and both commands fail saying so.
            // That is the true state of it, not a blind spot — and it is a state that changes the
            // moment anything is committed, so it is recorded rather than reported as unseen.
            head = await GitDigestAsync(projectPath, ["rev-parse", "HEAD"], "git HEAD", unavailable, cancellationToken, emptyWhenReadable: readable).ConfigureAwait(false);
            refs = await GitDigestAsync(projectPath, ["show-ref"], "git refs", unavailable, cancellationToken, emptyWhenReadable: readable).ConfigureAwait(false);
        }

        var association = associationOf?.Invoke(projectPath) ?? ProjectAssociation.None;

        // A project with a confirmed repository is asked about only under that one, never under a
        // remote that has since changed and is waiting on the owner. A project without one is asked
        // about under the remote it actually has, which is exactly what the dashboard would adopt
        // for it — and what keeps the two fingerprints symmetric. Skipping it instead would mean a
        // project whose origin is confirmed between the two readings shows up as the workspace
        // changing, which is what the first run with this restriction in place reported.
        var originSlug = association.IsConfirmed
            ? association.ConfirmedSlug
            : isRepository
                ? await ObservedRemoteAsync(projectPath, unavailable, cancellationToken).ConfigureAwait(false)
                : null;

        var issues = (Count: 0, Digest: (string?)null);
        var labels = (Count: 0, Digest: (string?)null);

        if (includeGitHub && originSlug is not null)
        {
            issues = await GitHubDigestAsync(
                ["issue", "list", "--repo", originSlug, "--state", "all", "--limit", maxIssues.ToString(),
                 "--json", "number,title,state,labels,assignees,body,closedAt,updatedAt,comments"],
                "github issues",
                unavailable,
                cancellationToken).ConfigureAwait(false);

            // gh returns thirty labels unasked; a repository with more would have its later labels
            // silently outside the comparison.
            labels = await GitHubDigestAsync(
                ["label", "list", "--repo", originSlug, "--limit", "500", "--json", "name,color,description"],
                "github labels",
                unavailable,
                cancellationToken).ConfigureAwait(false);
        }

        return new MonitoredProjectState(
            identify(projectPath),
            isRepository,
            fileCount,
            contentDigest,
            head,
            status,
            config,
            refs,
            originSlug is null ? null : identify(originSlug),
            issues.Count,
            issues.Digest,
            labels.Count,
            labels.Digest,
            unavailable);
    }

    /// <summary>
    /// A digest over every workflow file the dashboard reads: the markers that make a directory a
    /// project, and the whole <c>.scratch</c> tree. Names are included, so a rename is a change.
    /// </summary>
    private static (int Count, string Digest) WorkflowDigest(
        string projectPath,
        ICollection<string> unavailable,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var count = 0;

        foreach (var file in MonitoredFiles(projectPath, unavailable).OrderBy(f => f, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (count >= MaxWorkflowFiles)
            {
                unavailable.Add("workflow files beyond the fingerprint bound");
                break;
            }

            try
            {
                var info = new FileInfo(file);
                if (info.Length > MaxWorkflowFileBytes)
                {
                    // Too big to hold, but its size and name still change when it does.
                    builder.Append(Path.GetRelativePath(projectPath, file)).Append('|')
                        .Append(info.Length).Append("|oversized\n");
                    unavailable.Add("content of a file past the size bound");
                    count++;
                    continue;
                }

                var bytes = File.ReadAllBytes(file);
                builder.Append(Path.GetRelativePath(projectPath, file)).Append('|')
                    .Append(bytes.Length).Append('|')
                    .Append(Convert.ToHexStringLower(SHA256.HashData(bytes))).Append('\n');
                count++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                builder.Append(Path.GetRelativePath(projectPath, file)).Append("|unreadable\n");
                unavailable.Add("an unreadable workflow file");
                count++;
            }
        }

        return (count, Digest(builder.ToString()));
    }

    private static IEnumerable<string> MonitoredFiles(string projectPath, ICollection<string> unavailable)
    {
        foreach (var marker in MarkerFiles)
        {
            var path = Path.Combine(projectPath, marker);
            if (File.Exists(path))
            {
                yield return path;
            }
        }

        var scratch = Path.Combine(projectPath, ".scratch");
        if (!Directory.Exists(scratch))
        {
            yield break;
        }

        IEnumerator<string> walk;
        try
        {
            walk = Directory.EnumerateFiles(scratch, "*", SearchOption.AllDirectories).GetEnumerator();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            unavailable.Add("the .scratch tree");
            yield break;
        }

        // Enumerated one at a time rather than materialized: the walk itself can fail part-way
        // through on a tree the owner has no rights to, and that is a gap to report, not a crash.
        using (walk)
        {
            while (true)
            {
                try
                {
                    if (!walk.MoveNext())
                    {
                        break;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    unavailable.Add("part of the .scratch tree");
                    break;
                }

                yield return walk.Current;
            }
        }
    }

    private async Task<string?> ObservedRemoteAsync(
        string projectPath,
        ICollection<string> unavailable,
        CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(
            "git",
            ["-C", projectPath, "remote", "get-url", "origin"],
            projectPath,
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            // A repository with no origin at all is not a gap: there is nothing to enrich it with.
            return null;
        }

        var slug = Core.Workflow.GitHubOrigin.TryParse(result.StandardOutput.Trim())?.Slug;
        if (slug is null && result.StandardOutput.Trim().Length > 0)
        {
            unavailable.Add("a remote that is not a GitHub origin");
        }

        return slug;
    }

    /// <param name="emptyWhenReadable">
    /// When the repository is known to be readable, a command that fails with nothing to say is
    /// reporting emptiness rather than refusing to answer. That is a real state worth recording, and
    /// one that stops being empty as soon as the repository has anything in it.
    /// </param>
    private async Task<string?> GitDigestAsync(
        string projectPath,
        IReadOnlyList<string> verb,
        string subject,
        ICollection<string> unavailable,
        CancellationToken cancellationToken,
        bool emptyWhenReadable = false)
    {
        var result = await runner.RunAsync(
            "git",
            ["-C", projectPath, .. verb],
            projectPath,
            cancellationToken).ConfigureAwait(false);

        if (result.Succeeded)
        {
            return Digest(result.StandardOutput);
        }

        // Not conditioned on empty output: `git rev-parse HEAD` against an unborn HEAD echoes the
        // argument back on stdout and reports the failure on stderr, so a test for silence would
        // miss exactly the case this is for. A repository whose status could be read is readable,
        // and a read that then fails has nothing to return rather than being unable to return it.
        if (emptyWhenReadable && !result.TimedOut && !result.NotFound)
        {
            return Digest("<nothing yet>");
        }

        unavailable.Add(subject);
        return null;
    }

    private async Task<(int Count, string? Digest)> GitHubDigestAsync(
        IReadOnlyList<string> arguments,
        string subject,
        ICollection<string> unavailable,
        CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync("gh", arguments, null, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            unavailable.Add(subject);
            return (0, null);
        }

        var json = result.StandardOutput.Trim();
        var count = 0;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                count = document.RootElement.GetArrayLength();
            }
        }
        catch (JsonException)
        {
            // The digest still compares; only the count is unavailable.
        }

        return (count, Digest(json));
    }

    /// <summary>
    /// Everything that differs between two readings of the same projects. A project that appears in
    /// only one of them is itself a change: the run must not have made a project come or go.
    /// </summary>
    public static IReadOnlyList<MonitoredStateChange> Diff(
        IReadOnlyList<MonitoredProjectState> before,
        IReadOnlyList<MonitoredProjectState> after)
    {
        var changes = new List<MonitoredStateChange>();
        var afterById = after.ToDictionary(s => s.ProjectId, StringComparer.Ordinal);

        foreach (var was in before)
        {
            if (!afterById.TryGetValue(was.ProjectId, out var now))
            {
                changes.Add(new MonitoredStateChange(was.ProjectId, "project", "present", "absent"));
                continue;
            }

            Compare("workflow files", was.WorkflowFileCount.ToString(), now.WorkflowFileCount.ToString());
            Compare("workflow content", was.WorkflowContentDigest, now.WorkflowContentDigest);
            Compare("git HEAD", was.GitHeadDigest, now.GitHeadDigest);
            Compare("git status", was.GitStatusDigest, now.GitStatusDigest);
            Compare("git config", was.GitConfigDigest, now.GitConfigDigest);
            Compare("git refs", was.GitRefsDigest, now.GitRefsDigest);
            Compare("github issues", was.GitHubIssuesDigest, now.GitHubIssuesDigest);
            Compare("github labels", was.GitHubLabelsDigest, now.GitHubLabelsDigest);

            void Compare(string subject, string? first, string? second)
            {
                if (!string.Equals(first, second, StringComparison.Ordinal))
                {
                    changes.Add(new MonitoredStateChange(
                        was.ProjectId,
                        subject,
                        first ?? "unavailable",
                        second ?? "unavailable"));
                }
            }
        }

        var beforeIds = before.Select(s => s.ProjectId).ToHashSet(StringComparer.Ordinal);
        changes.AddRange(after
            .Where(s => !beforeIds.Contains(s.ProjectId))
            .Select(s => new MonitoredStateChange(s.ProjectId, "project", "absent", "present")));

        return changes;
    }

    /// <summary>
    /// Every source that could not be read on either side. Two absent digests compare equal, so
    /// without this a source that was never observed would be indistinguishable in the report from
    /// one that was observed twice and found identical.
    /// </summary>
    public static IReadOnlyList<FingerprintGap> Gaps(
        IReadOnlyList<MonitoredProjectState> before,
        IReadOnlyList<MonitoredProjectState> after) =>
        [.. before.Concat(after)
            .SelectMany(state => state.Unavailable.Select(subject => new FingerprintGap(state.ProjectId, subject)))
            .DistinctBy(gap => (gap.ProjectId, gap.Subject))];

    private static string Digest(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
}
