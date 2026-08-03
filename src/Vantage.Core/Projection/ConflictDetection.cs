using Vantage.Core.Workflow;

namespace Vantage.Core.Projection;

/// <summary>
/// The disagreements a project's own files hold, found without reading anything the indexer has
/// not already read. Two producers live here, and each earns its place by naming something the
/// owner would act on rather than merely something that is untidy:
/// <list type="bullet">
/// <item>
/// A ticket that still presents itself as blocked when every edge it names is finished. It reads
/// as stuck and is free, so the frontier the owner is working from is wrong by one item.
/// </item>
/// <item>
/// A ticket that calls itself finished over its own unticked checkboxes. Progress counts it as
/// done; the file's own evidence says part of it is not.
/// </item>
/// </list>
/// <para>
/// A dangling <c>Blocked by:</c> is deliberately not one of these. It is already reported, once
/// per edge, as a <see cref="DiagnosticCode.BlockerMissing"/>, and reporting it a second time as a
/// disagreement would put hundreds of rows behind a badge that is only useful while it is rare.
/// </para>
/// </summary>
public static class ConflictDetection
{
    public static IEnumerable<ConflictReport> InEffort(
        WorkflowEffort effort,
        IReadOnlyDictionary<string, BlockerResolution> resolutions)
    {
        var byId = effort.Tickets.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var ticket in effort.Tickets)
        {
            var resolution = resolutions.TryGetValue(ticket.Id, out var found)
                ? found
                : new BlockerResolution(ticket.Id, [], false, false);

            var satisfied = SatisfiedEdge(ticket, resolution, byId);
            if (satisfied is not null)
            {
                yield return satisfied;
            }

            var overstated = OverstatedCompletion(ticket);
            if (overstated is not null)
            {
                yield return overstated;
            }
        }
    }

    /// <summary>
    /// A stated edge against the thing it names. The ticket's status line says it is waiting; the
    /// tickets it names each say they are finished, and those are two observations of one subject.
    /// </summary>
    private static ConflictReport? SatisfiedEdge(
        WorkflowTicket ticket,
        BlockerResolution resolution,
        IReadOnlyDictionary<string, WorkflowTicket> byId)
    {
        // Only a ticket that says so itself. An unblocked ticket the dashboard merely infers is
        // ready has not claimed anything to the contrary, so there is nothing to disagree with.
        if (ticket.Status.Status != WorkflowStatus.Blocked
            || ticket.Blockers.Count == 0
            || !resolution.CanMove)
        {
            return null;
        }

        var named = resolution.ResolvedBlockerIds
            .Select(id => byId.TryGetValue(id, out var blocker) ? blocker : null)
            .Where(b => b is not null)
            .Select(b => b!)
            .ToList();

        if (named.Count == 0)
        {
            return null;
        }

        var stated = string.Join(", ", ticket.Blockers.Select(b => b.RawValue));

        return new ConflictReport(
            ticket.Id,
            ConflictField.Blockers,
            new ObservedValue(
                $"{ticket.Status.RawValue}, waiting on {stated}",
                AsRead(ticket, $"L{ticket.StatusLine}")),
            new ObservedValue(
                string.Join("; ", named.Select(b => $"'{b.Title}' is {b.Status.RawValue}")),
                AsRead(named[0])),
            "The named work was kept: every edge this ticket declares is finished, so it can move. "
            + "Its own status line is what is out of date.");
    }

    /// <summary>
    /// A stated status against the ticket's own evidence. Completion is the one status that ends
    /// an item's life in every count the dashboard makes, so a file that claims it over its own
    /// open checkboxes is disagreeing with itself where it costs the most.
    /// </summary>
    private static ConflictReport? OverstatedCompletion(WorkflowTicket ticket)
    {
        if (!ticket.IsComplete || ticket.Checklist.Unticked == 0)
        {
            return null;
        }

        var boxes = ticket.Checklist;

        return new ConflictReport(
            ticket.Id,
            ConflictField.WorkflowStatus,
            new ObservedValue(ticket.Status.RawValue, AsRead(ticket, $"L{ticket.StatusLine}")),
            new ObservedValue(
                $"{boxes.Unticked} of {boxes.Total} checklist item(s) still unticked",
                AsRead(ticket, "checklist")),
            "The status line was kept, so this ticket counts as complete everywhere. Its own "
            + "checklist says part of it is not.");
    }

    /// <summary>
    /// Where a value was read from: the file as it sits on disk now, at whatever anchors the
    /// observation inside it. The ticket's own provenance is not reused, because a tracked file
    /// carries the commit that last touched it — true about the file, and not where this value
    /// was read.
    /// </summary>
    private static Provenance AsRead(WorkflowTicket ticket, string? anchor = null) =>
        new(EvidenceSource.LocalFile,
            anchor is null ? ticket.SourcePath : $"{ticket.SourcePath}#{anchor}",
            TimestampProvenance.FileSystem,
            null,
            ticket.Provenance.RefreshId);
}
