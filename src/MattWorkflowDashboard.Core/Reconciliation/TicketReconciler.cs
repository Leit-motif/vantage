using MattWorkflowDashboard.Core.Observed;
using MattWorkflowDashboard.Core.Projection;
using MattWorkflowDashboard.Core.Workflow;

namespace MattWorkflowDashboard.Core.Reconciliation;

public sealed record ReconciliationResult(
    IReadOnlyList<WorkflowEffort> Efforts,
    IReadOnlyList<ConflictReport> Conflicts,
    IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>
/// Merges local workflow facts with GitHub evidence. Local files are primary; GitHub fills gaps
/// and supplies remote activity. Identity comes only from explicit links — title similarity is
/// never an identity signal, so similarly named work is never merged.
/// </summary>
public static class TicketReconciler
{
    /// <summary>The effort that holds GitHub issues with no local counterpart.</summary>
    public const string GitHubEffortId = "__github__";

    /// <summary>Labels that classify a GitHub issue as workflow-managed work.</summary>
    private static readonly Dictionary<string, WorkflowStatus> ClassifyingLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ready-for-agent"] = WorkflowStatus.Ready,
        ["ready-for-human"] = WorkflowStatus.Ready,
        ["needs-triage"] = WorkflowStatus.Open,
        ["needs-info"] = WorkflowStatus.Blocked,
        ["blocked"] = WorkflowStatus.Blocked,
        ["in-progress"] = WorkflowStatus.InProgress,
        ["wontfix"] = WorkflowStatus.Done,
    };

    public static ReconciliationResult Reconcile(
        IReadOnlyList<WorkflowEffort> localEfforts,
        IReadOnlyList<ObservedGitHubIssue> issues,
        GitHubOrigin? origin)
    {
        var conflicts = new List<ConflictReport>();
        var diagnostics = new List<Diagnostic>();
        var issuesByNumber = issues.ToDictionary(i => i.Number);
        var claimed = new HashSet<int>();

        var efforts = new List<WorkflowEffort>();
        foreach (var effort in localEfforts)
        {
            var tickets = new List<WorkflowTicket>();
            foreach (var ticket in effort.Tickets)
            {
                if (ticket.Link is null || !issuesByNumber.TryGetValue(ticket.Link.Number, out var issue))
                {
                    if (ticket.Link is not null && origin is not null)
                    {
                        diagnostics.Add(Diagnostic.Info(
                            DiagnosticCode.LinkTargetMissing,
                            $"'{ticket.Title}' links to {origin.Slug}#{ticket.Link.Number}, which was not returned by GitHub.",
                            ticket.SourcePath,
                            ticket.Provenance));
                    }

                    tickets.Add(ticket);
                    continue;
                }

                claimed.Add(issue.Number);
                tickets.Add(Merge(ticket, issue, conflicts));
            }

            efforts.Add(effort with { Tickets = tickets });
        }

        var unlinked = issues.Where(i => !claimed.Contains(i.Number)).ToList();
        if (unlinked.Count > 0)
        {
            efforts.Add(BuildGitHubEffort(unlinked, origin));
        }

        return new ReconciliationResult(efforts, conflicts, diagnostics);
    }

    /// <summary>
    /// Local facts win. Only genuine disagreements in title, state, workflow status, blockers, or
    /// effort are reported; anything the local file simply does not say is enrichment.
    /// </summary>
    private static WorkflowTicket Merge(WorkflowTicket local, ObservedGitHubIssue issue, ICollection<ConflictReport> conflicts)
    {
        void Report(ConflictField field, string localValue, string remoteValue) =>
            conflicts.Add(new ConflictReport(
                local.Id,
                field,
                localValue,
                remoteValue,
                "Local value kept: local workflow files are primary.",
                local.Provenance,
                issue.Provenance));

        if (!string.IsNullOrWhiteSpace(local.Title)
            && !string.IsNullOrWhiteSpace(issue.Title)
            && !TitlesAgree(local.Title, issue.Title))
        {
            Report(ConflictField.Title, local.Title, issue.Title);
        }

        var remoteComplete = issue.IsClosed;
        if (local.Status.Status != WorkflowStatus.Unknown && local.IsComplete != remoteComplete)
        {
            Report(ConflictField.State, local.IsComplete ? "closed" : "open", remoteComplete ? "closed" : "open");
        }

        var remoteStatus = ClassifyFromLabels(issue.Labels);
        if (remoteStatus is not null
            && local.Status.Status != WorkflowStatus.Unknown
            && remoteStatus != local.Status.Status)
        {
            Report(ConflictField.WorkflowStatus, local.Status.RawValue, remoteStatus.ToString()!);
        }

        var remoteBlockers = ParseBlockersFromLabels(issue.Labels);
        if (remoteBlockers.Count > 0 && local.Blockers.Count > 0 && !BlockersAgree(local.Blockers, remoteBlockers))
        {
            Report(
                ConflictField.Blockers,
                string.Join(", ", local.Blockers.Select(b => b.RawValue)),
                string.Join(", ", remoteBlockers));
        }

        // Enrichment: fill only what the local file did not say.
        return local with
        {
            Labels = local.Labels.Count > 0 ? local.Labels : issue.Labels,
            Assignees = local.Assignees.Count > 0 ? local.Assignees : issue.Assignees,
            EnrichmentProvenance = [.. local.EnrichmentProvenance, issue.Provenance],
        };
    }

    private static bool TitlesAgree(string local, string remote) =>
        string.Equals(local.Trim(), remote.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool BlockersAgree(IReadOnlyList<BlockerReference> local, IReadOnlyList<string> remote)
    {
        var localKeys = local.Select(b => b.NormalizedKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var remoteKeys = remote.Select(BlockerGraph.NormalizeReference).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return localKeys.SetEquals(remoteKeys);
    }

    private static WorkflowStatus? ClassifyFromLabels(IReadOnlyList<string> labels)
    {
        foreach (var label in labels)
        {
            if (ClassifyingLabels.TryGetValue(label, out var status))
            {
                return status;
            }
        }

        return null;
    }

    private static IReadOnlyList<string> ParseBlockersFromLabels(IReadOnlyList<string> labels) =>
        labels
            .Where(l => l.StartsWith("blocked-by:", StringComparison.OrdinalIgnoreCase))
            .Select(l => l["blocked-by:".Length..].Trim())
            .ToList();

    /// <summary>
    /// GitHub issues with no local counterpart. A classifying label makes an issue ordinary
    /// workflow work; without one it is remaining work that can never become a next action.
    /// </summary>
    private static WorkflowEffort BuildGitHubEffort(
        IReadOnlyList<ObservedGitHubIssue> issues,
        GitHubOrigin? origin)
    {
        var slug = origin?.Slug ?? "github";
        var tickets = issues.Select(issue =>
        {
            var labelStatus = ClassifyFromLabels(issue.Labels);
            var status = issue.IsClosed
                ? new StatusReading(WorkflowStatus.Done, issue.State, "closed")
                : labelStatus is { } known
                    ? new StatusReading(known, string.Join(", ", issue.Labels))
                    : new StatusReading(WorkflowStatus.Open, issue.State);

            return new WorkflowTicket
            {
                Id = $"gh#{issue.Number}",
                LocalKey = issue.Number.ToString(),
                EffortId = GitHubEffortId,
                Title = issue.Title,
                Status = status,
                Kind = WorkUnitKind.Implementation,
                Labels = issue.Labels,
                Assignees = issue.Assignees,
                Link = new GitHubLink(slug, issue.Number, issue.Url),
                SourcePath = issue.Url,
                SemanticHash = $"{issue.Number}:{issue.UpdatedAt:O}",
                Provenance = issue.Provenance,
                IsUnclassifiedGitHubIssue = !issue.IsClosed && labelStatus is null,
            };
        }).ToList();

        return new WorkflowEffort
        {
            Id = GitHubEffortId,
            Name = $"GitHub · {slug}",
            Path = origin is null ? "github" : $"https://github.com/{slug}/issues",
            HasMap = false,
            Tickets = tickets,
        };
    }
}
