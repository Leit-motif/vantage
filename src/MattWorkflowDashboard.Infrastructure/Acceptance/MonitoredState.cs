using System.Security.Cryptography;
using System.Text;
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
    string? GitHubLabelsDigest);

/// <summary>One thing that did not survive the run untouched.</summary>
public sealed record MonitoredStateChange(string ProjectId, string Subject, string Before, string After);

/// <summary>
/// Reads the monitored state of a set of projects, before and after, using only commands the
/// <see cref="ReadOnlyProcessRunner"/> will let through.
/// </summary>
public sealed class MonitoredStateReader(IProcessRunner runner, Func<string, string> identify, int maxIssues)
{
    /// <summary>
    /// The files this dashboard actually opens in a project. Everything else in a working tree is
    /// covered by the Git status and ref digests instead.
    /// </summary>
    private static readonly string[] MarkerFiles = ["AGENTS.md", Path.Combine("docs", "agents", "issue-tracker.md")];

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
        var (fileCount, contentDigest) = WorkflowDigest(projectPath);

        var isRepository = Directory.Exists(Path.Combine(projectPath, ".git"))
            || File.Exists(Path.Combine(projectPath, ".git"));

        string? head = null, status = null, config = null, refs = null, originSlug = null;

        if (isRepository)
        {
            head = await GitDigestAsync(projectPath, ["rev-parse", "HEAD"], cancellationToken).ConfigureAwait(false);
            status = await GitDigestAsync(projectPath, ["status", "--porcelain"], cancellationToken).ConfigureAwait(false);
            config = await GitDigestAsync(projectPath, ["config", "--list", "--local"], cancellationToken).ConfigureAwait(false);
            refs = await GitDigestAsync(projectPath, ["show-ref"], cancellationToken).ConfigureAwait(false);
            originSlug = await OriginSlugAsync(projectPath, cancellationToken).ConfigureAwait(false);
        }

        var issues = (Count: 0, Digest: (string?)null);
        var labels = (Count: 0, Digest: (string?)null);

        if (includeGitHub && originSlug is not null)
        {
            issues = await GitHubDigestAsync(
                ["issue", "list", "--repo", originSlug, "--state", "all", "--limit", maxIssues.ToString(),
                 "--json", "number,title,state,labels,assignees,body,closedAt"],
                cancellationToken).ConfigureAwait(false);

            labels = await GitHubDigestAsync(
                ["label", "list", "--repo", originSlug, "--json", "name,color,description"],
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
            labels.Digest);
    }

    /// <summary>
    /// A digest over every workflow file the dashboard reads: the markers that make a directory a
    /// project, and the whole <c>.scratch</c> tree. Names are included, so a rename is a change.
    /// </summary>
    private static (int Count, string Digest) WorkflowDigest(string projectPath)
    {
        var builder = new StringBuilder();
        var count = 0;

        foreach (var file in MonitoredFiles(projectPath).OrderBy(f => f, StringComparer.Ordinal))
        {
            try
            {
                var bytes = File.ReadAllBytes(file);
                builder.Append(Path.GetRelativePath(projectPath, file)).Append('|')
                    .Append(bytes.Length).Append('|')
                    .Append(Convert.ToHexStringLower(SHA256.HashData(bytes))).Append('\n');
                count++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file that cannot be read cannot be compared; record that fact rather than
                // silently dropping it, so an unreadable file is not mistaken for an absent one.
                builder.Append(Path.GetRelativePath(projectPath, file)).Append("|unreadable\n");
                count++;
            }
        }

        return (count, Digest(builder.ToString()));
    }

    private static IEnumerable<string> MonitoredFiles(string projectPath)
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

        IEnumerable<string> scratchFiles;
        try
        {
            scratchFiles = Directory.EnumerateFiles(scratch, "*", SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var file in scratchFiles)
        {
            yield return file;
        }
    }

    private async Task<string?> GitDigestAsync(
        string projectPath,
        IReadOnlyList<string> verb,
        CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(
            "git",
            ["-C", projectPath, .. verb],
            projectPath,
            cancellationToken).ConfigureAwait(false);

        return result.Succeeded ? Digest(result.StandardOutput) : null;
    }

    private async Task<string?> OriginSlugAsync(string projectPath, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(
            "git",
            ["-C", projectPath, "remote", "get-url", "origin"],
            projectPath,
            cancellationToken).ConfigureAwait(false);

        return result.Succeeded
            ? Core.Workflow.GitHubOrigin.TryParse(result.StandardOutput.Trim())?.Slug
            : null;
    }

    private async Task<(int Count, string? Digest)> GitHubDigestAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync("gh", arguments, null, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return (0, null);
        }

        var json = result.StandardOutput.Trim();
        var count = 0;

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                count = document.RootElement.GetArrayLength();
            }
        }
        catch (System.Text.Json.JsonException)
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

    private static string Digest(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
}
