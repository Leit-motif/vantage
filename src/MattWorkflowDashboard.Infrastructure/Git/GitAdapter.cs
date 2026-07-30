using System.Globalization;
using MattWorkflowDashboard.Core;
using MattWorkflowDashboard.Core.Observed;
using MattWorkflowDashboard.Core.Workflow;
using MattWorkflowDashboard.Infrastructure.Parsing;
using MattWorkflowDashboard.Infrastructure.Processes;

namespace MattWorkflowDashboard.Infrastructure.Git;

public sealed record GitReadResult(
    bool Available,
    GitHubOrigin? Origin,
    string? RawOriginUrl,
    IReadOnlyList<ObservedCommit> Commits,
    GitFileFacts FileFacts,
    IReadOnlyList<Diagnostic> Diagnostics)
{
    public static GitReadResult Unavailable(IReadOnlyList<Diagnostic> diagnostics) =>
        new(false, null, null, [], GitFileFacts.None, diagnostics);
}

/// <summary>
/// Reads local Git history. Only commits reachable from local branches are observed: history that
/// exists solely on a remote has not been seen locally and is not local work.
/// </summary>
public sealed class GitAdapter(IProcessRunner runner, int maxCommits = 2000)
{
    private const char RecordSeparator = (char)0x1e;
    private const char FieldSeparator = (char)0x1f;

    public async Task<GitReadResult> ReadAsync(string projectPath, CancellationToken cancellationToken)
    {
        var diagnostics = new List<Diagnostic>();

        var probe = await runner.RunAsync(
            "git",
            ["-C", projectPath, "rev-parse", "--is-inside-work-tree"],
            projectPath,
            cancellationToken).ConfigureAwait(false);

        if (!probe.Succeeded)
        {
            diagnostics.Add(Diagnostic.Info(
                DiagnosticCode.GitUnavailable,
                probe.NotFound
                    ? "git was not found on PATH; local history is unavailable."
                    : $"git could not read '{projectPath}': {Summarize(probe)}",
                projectPath));
            return GitReadResult.Unavailable(diagnostics);
        }

        var origin = await ReadOriginAsync(projectPath, cancellationToken).ConfigureAwait(false);
        var (commits, fileFacts) = await ReadLogAsync(projectPath, diagnostics, cancellationToken).ConfigureAwait(false);

        return new GitReadResult(
            true,
            GitHubOrigin.TryParse(origin),
            origin,
            commits,
            fileFacts,
            diagnostics);
    }

    private async Task<string?> ReadOriginAsync(string projectPath, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(
            "git",
            ["-C", projectPath, "remote", "get-url", "origin"],
            projectPath,
            cancellationToken).ConfigureAwait(false);

        return result.Succeeded ? result.StandardOutput.Trim() : null;
    }

    private async Task<(IReadOnlyList<ObservedCommit> Commits, GitFileFacts FileFacts)> ReadLogAsync(
        string projectPath,
        ICollection<Diagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(
            "git",
            [
                "-C", projectPath,
                "log",
                "--branches",
                "--no-color",
                $"-n{maxCommits}",
                "--name-only",
                $"--pretty=format:{RecordSeparator}%H{FieldSeparator}%cI{FieldSeparator}%an{FieldSeparator}%s",
            ],
            projectPath,
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            // A repository with no commits yet exits non-zero; that is not an error worth alarming about.
            if (!result.StandardError.Contains("does not have any commits", StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(Diagnostic.Info(
                    DiagnosticCode.GitUnavailable,
                    $"git log failed for '{projectPath}': {Summarize(result)}",
                    projectPath));
            }

            return ([], GitFileFacts.None);
        }

        var commits = new List<ObservedCommit>();
        var seenShas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lastCommitByPath = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);

        foreach (var record in result.StandardOutput.Split(RecordSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var lines = record.Split('\n');
            var header = lines[0].TrimEnd('\r').Split(FieldSeparator);
            if (header.Length < 4)
            {
                continue;
            }

            if (!DateTimeOffset.TryParse(header[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var committedAt))
            {
                continue;
            }

            var paths = lines
                .Skip(1)
                .Select(l => l.TrimEnd('\r').Trim())
                .Where(l => l.Length > 0)
                .ToList();

            foreach (var path in paths)
            {
                if (!lastCommitByPath.TryGetValue(path, out var existing) || existing < committedAt)
                {
                    lastCommitByPath[path] = committedAt;
                }
            }

            // The same commit is reachable from several local branches; it is one piece of activity.
            if (seenShas.Add(header[0]))
            {
                commits.Add(new ObservedCommit(header[0], committedAt, header[2], header[3], paths));
            }
        }

        return (commits, new GitFileFacts(lastCommitByPath, GitAvailable: true));
    }

    private static string Summarize(ProcessResult result)
    {
        if (result.TimedOut)
        {
            return "the command timed out";
        }

        var error = result.StandardError.Trim();
        return error.Length == 0 ? $"exit code {result.ExitCode}" : error.Split('\n')[0].Trim();
    }
}
