using Vantage.Core;
using Vantage.Core.Projection;
using Vantage.Core.Workflow;
using Vantage.Infrastructure.Parsing;
using Vantage.Infrastructure.Processes;

namespace Vantage.Infrastructure.Git;

public sealed record WorkingTreeComparison(
    IReadOnlyList<ConflictReport> Conflicts,
    IReadOnlyList<Diagnostic> Diagnostics)
{
    public static readonly WorkingTreeComparison None = new([], []);
}

/// <summary>
/// Compares a ticket file as it sits on disk against the version the last commit recorded.
/// <para>
/// A ticket whose file is modified is a conclusion drawn from evidence nobody else can see yet:
/// another worktree, another agent, and CI all still read the committed value. That is worth
/// telling the owner only where the two actually say different things, so the committed copy is
/// read and compared field by field rather than the whole file being reported as dirty — an edit
/// to a ticket's prose is work in progress, not a disagreement.
/// </para>
/// </summary>
public sealed class WorkingTreeAdapter(IProcessRunner runner, int maxFiles = 50, int maxBlobBytes = 1_048_576)
{
    public async Task<WorkingTreeComparison> CompareAsync(
        string projectPath,
        IReadOnlyList<WorkflowTicket> tickets,
        string refreshId,
        CancellationToken cancellationToken)
    {
        // No planning files means nothing to compare, and no reason to start a process at all.
        if (tickets.Count == 0)
        {
            return WorkingTreeComparison.None;
        }

        var byRelativePath = new Dictionary<string, WorkflowTicket>(StringComparer.OrdinalIgnoreCase);
        foreach (var ticket in tickets)
        {
            byRelativePath[Relative(projectPath, ticket.SourcePath)] = ticket;
        }

        var status = await runner.RunAsync(
            "git",
            ["-C", projectPath, "status", "--porcelain", "--untracked-files=no", "--", ".scratch"],
            projectPath,
            cancellationToken).ConfigureAwait(false);

        if (!status.Succeeded)
        {
            // Reported rather than swallowed. The history read can succeed while this one fails —
            // a corrupt index is enough — and silently dropping the comparison would present
            // evidence that is missing a producer as though it were complete.
            return new WorkingTreeComparison(
                [],
                [
                    Diagnostic.Warning(
                        DiagnosticCode.GitUnavailable,
                        "git could not report the working tree here, so uncommitted planning edits "
                        + $"are not compared against the last commit: {Summarize(status)}",
                        projectPath),
                ]);
        }

        var modified = ModifiedPaths(status.StandardOutput)
            .Where(byRelativePath.ContainsKey)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        var diagnostics = new List<Diagnostic>();
        if (modified.Count > maxFiles)
        {
            diagnostics.Add(Diagnostic.Info(
                DiagnosticCode.ConflictScanTruncated,
                $"{modified.Count} planning files are uncommitted; only the first {maxFiles} were "
                + "compared against the last commit.",
                projectPath));

            modified = modified.Take(maxFiles).ToList();
        }

        var conflicts = new List<ConflictReport>();

        foreach (var relative in modified)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The file count bounds how many processes start; it does not bound how many bytes
            // one of them hands back. A ticket that is small now can have a very large blob at
            // HEAD, and `cat-file -p` would stream all of it into memory, so the size is asked
            // for first — the same limit the indexer applies to the file on disk.
            var size = await runner.RunAsync(
                "git",
                ["-C", projectPath, "cat-file", "-s", $"HEAD:{relative}"],
                projectPath,
                cancellationToken).ConfigureAwait(false);

            // A file added but not yet committed has no other side to disagree with, and an
            // unborn HEAD fails the same way. Neither is an error worth reporting.
            if (!size.Succeeded || !long.TryParse(size.StandardOutput.Trim(), out var bytes))
            {
                continue;
            }

            if (bytes > maxBlobBytes)
            {
                diagnostics.Add(Diagnostic.Info(
                    DiagnosticCode.ConflictScanTruncated,
                    $"The committed copy of '{relative}' is larger than the {maxBlobBytes / 1024} KB "
                    + "read limit and was not compared against the working tree.",
                    Path.Combine(projectPath, relative)));
                continue;
            }

            var committed = await runner.RunAsync(
                "git",
                ["-C", projectPath, "cat-file", "-p", $"HEAD:{relative}"],
                projectPath,
                cancellationToken).ConfigureAwait(false);

            if (!committed.Succeeded)
            {
                continue;
            }

            conflicts.AddRange(Compare(
                byRelativePath[relative],
                relative,
                committed.StandardOutput,
                refreshId));
        }

        return new WorkingTreeComparison(conflicts, diagnostics);
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

    /// <summary>
    /// The tracked paths whose content differs from what is committed. Untracked files are
    /// excluded by the command; deletions are excluded here, because a file that is gone has no
    /// value to put on the working-tree side.
    /// </summary>
    private static IEnumerable<string> ModifiedPaths(string porcelain)
    {
        foreach (var line in porcelain.Split('\n'))
        {
            var entry = line.TrimEnd('\r');
            if (entry.Length < 4)
            {
                continue;
            }

            var index = entry[0];
            var worktree = entry[1];
            if (index == 'D' || worktree == 'D' || index == '?')
            {
                continue;
            }

            var path = entry[3..].Trim();

            // A rename is reported as `old -> new`; the new name is the one that exists.
            var arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0)
            {
                path = path[(arrow + 4)..];
            }

            // git quotes paths holding characters it cannot print verbatim. Unquoting them is its
            // own escaping problem, and a planning file with such a name is not worth guessing at.
            if (path.StartsWith('"') || path.Length == 0)
            {
                continue;
            }

            yield return path.Replace('\\', '/');
        }
    }

    /// <summary>
    /// The fields where the two copies of one ticket actually say different things. Ordering and
    /// letter case in a status are not disagreements: neither changes what the file claims.
    /// </summary>
    private static IEnumerable<ConflictReport> Compare(
        WorkflowTicket ticket,
        string relative,
        string committedContent,
        string refreshId)
    {
        var (heading, fields) = MetadataGrammar.Parse(committedContent);

        ObservedValue Working(string value) =>
            new(value, Provenance.File(ticket.SourcePath, refreshId));

        // No timestamp is claimed for this side. The obvious one to hand — the last commit that
        // touched this path — is computed across every local branch, and the content here was read
        // from HEAD specifically, so on a repository with more than one branch the two can be
        // about different commits. A locator naming the revision is honest; a borrowed time is not.
        ObservedValue Committed(string value) =>
            new(value, new Provenance(
                EvidenceSource.LocalGit,
                $"{relative}@HEAD",
                TimestampProvenance.Unknown,
                null,
                refreshId));

        const string Kept =
            "The working tree was kept: it is what the file says now. The last commit still "
            + "records the other value, which is what every other checkout reads.";

        string? Field(string key) => fields
            .FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase))?.RawValue;

        var committedTitle = Field(MetadataGrammar.Keys.Title)
            ?? heading
            ?? Path.GetFileNameWithoutExtension(relative);

        if (!string.Equals(ticket.Title, committedTitle, StringComparison.Ordinal))
        {
            yield return new ConflictReport(
                ticket.Id,
                ConflictField.Title,
                Working(ticket.Title),
                Committed(committedTitle),
                Kept);
        }

        var committedStatus = Field(MetadataGrammar.Keys.Status) ?? string.Empty;

        if (!string.Equals(ticket.Status.RawValue, committedStatus, StringComparison.OrdinalIgnoreCase))
        {
            yield return new ConflictReport(
                ticket.Id,
                ConflictField.WorkflowStatus,
                Working(Stated(ticket.Status.RawValue)),
                Committed(Stated(committedStatus)),
                Kept);
        }

        var working = Edges(ticket.Blockers.Select(b => b.NormalizedKey));
        var wasCommitted = Edges(MetadataGrammar
            .SplitList(Field(MetadataGrammar.Keys.BlockedBy))
            .Select(Core.Projection.BlockerGraph.NormalizeReference));

        if (!string.Equals(working, wasCommitted, StringComparison.Ordinal))
        {
            yield return new ConflictReport(
                ticket.Id,
                ConflictField.Blockers,
                Working(Stated(working)),
                Committed(Stated(wasCommitted)),
                Kept);
        }
    }

    private static string Edges(IEnumerable<string> references) =>
        string.Join(", ", references.OrderBy(r => r, StringComparer.Ordinal));

    /// <summary>A value the file does not state at all is said in words, never shown as a blank.</summary>
    private static string Stated(string value) =>
        string.IsNullOrWhiteSpace(value) ? "(not stated)" : value;

    private static string Relative(string projectPath, string absolute) =>
        Path.GetRelativePath(projectPath, absolute).Replace('\\', '/');
}
