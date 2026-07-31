using MattWorkflowDashboard.Core.Workflow;

namespace MattWorkflowDashboard.Core.Projection;

public sealed record ProjectionOptions
{
    public required DateTimeOffset Now { get; init; }

    /// <summary>The Recent window. Defaults to a rolling 24 hours.</summary>
    public TimeSpan RecentWindow { get; init; } = TimeSpan.FromHours(24);

    /// <summary>Activity replayed from the cache, already filtered to this project.</summary>
    public IReadOnlyList<ActivityEvent> PersistedActivity { get; init; } = [];

    public IReadOnlyList<ConflictReport> Conflicts { get; init; } = [];

    public bool IsPinned { get; init; }

    public bool IsStale { get; init; }
}

/// <summary>
/// Turns normalized workflow facts into the derived dashboard view. Every derivation here is
/// explainable: state, progress, next action, and recency all carry the reason that produced them.
/// </summary>
public static class ProjectProjector
{
    public static ProjectView Project(NormalizedProject project, ProjectionOptions options)
    {
        var diagnostics = new List<Diagnostic>(project.Diagnostics);
        var effortViews = new List<EffortView>();
        var conflictsByTicket = ConflictsByTicket(options.Conflicts);

        var resolutions = new Dictionary<string, BlockerResolution>(StringComparer.OrdinalIgnoreCase);
        var actionable = new List<(WorkflowEffort Effort, WorkflowTicket Ticket, NextActionSource Source, string Reason)>();

        foreach (var effort in project.Efforts)
        {
            var effortResolutions = BlockerGraph.Resolve(effort, diagnostics);
            var ticketViews = new List<TicketView>();

            foreach (var ticket in effort.Tickets)
            {
                var resolution = effortResolutions[ticket.Id];
                resolutions[ticket.Id] = resolution;

                var candidacy = Candidacy(effort, ticket, resolution);
                if (candidacy is not null)
                {
                    actionable.Add((effort, ticket, candidacy.Value.Source, candidacy.Value.Reason));
                }

                ticketViews.Add(new TicketView(
                    ticket.Id,
                    ticket.Title,
                    ticket.EffortId,
                    ticket.Status,
                    ticket.Kind,
                    ticket.Stage,
                    candidacy is not null,
                    IsBlocked(ticket, resolution),
                    resolution.ResolvedBlockerIds,
                    ticket.Link,
                    ticket.SourcePath,
                    ticket.Provenance,
                    ticket.EnrichmentProvenance)
                {
                    Conflicts = conflictsByTicket.TryGetValue(ticket.Id, out var reports) ? reports : [],
                });
            }

            effortViews.Add(new EffortView(
                effort.Id,
                effort.Name,
                effort.Path,
                effort.IsWayfinderContext,
                Summarize(effort.Tickets),
                ticketViews));
        }

        var allTickets = project.AllTickets.ToList();
        var progress = Summarize(allTickets);
        var pipeline = Pipeline(allTickets);
        var nextAction = ChooseNextAction(project, actionable);
        var (state, reason) = DetermineState(project, resolutions, actionable, progress);

        var activity = CollectActivity(project, options).ToList();
        var recent = activity
            .Where(e => e.At >= options.Now - options.RecentWindow)
            .OrderByDescending(e => e.At)
            .ToList();

        return new ProjectView
        {
            Identity = project.Identity,
            Origin = project.Origin,
            State = state,
            StateReason = reason,
            Progress = progress,
            Pipeline = pipeline,
            NextAction = nextAction,
            LastActivityAt = activity.Count == 0 ? null : activity.Max(e => e.At),
            RecentActivity = recent,
            Efforts = effortViews,
            Conflicts = options.Conflicts,
            Diagnostics = diagnostics,
            IsPinned = options.IsPinned,
            IsStale = options.IsStale,
            GitAvailable = project.GitAvailable,
            GitHubAvailable = project.GitHubAvailable,
        };
    }

    /// <summary>
    /// A disagreement travels with the work it is about, so the aggregate warning on a project and
    /// the control on the affected item are two routes to one piece of evidence.
    /// </summary>
    private static Dictionary<string, IReadOnlyList<ConflictReport>> ConflictsByTicket(
        IEnumerable<ConflictReport> conflicts) =>
        conflicts
            .GroupBy(c => c.TicketId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, IReadOnlyList<ConflictReport> (g) => [.. g], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Restores that attachment on a view that arrived without it. A projection stored by an
    /// earlier build carries a project's conflicts but nothing on its items, and a last-known-good
    /// view is still a view the owner has to be able to inspect — so the attachment is rebuilt on
    /// the way out of the cache rather than left to the next successful refresh.
    /// </summary>
    public static ProjectView AttachConflictsToItems(ProjectView view)
    {
        if (view.Conflicts.Count == 0
            || view.Efforts.SelectMany(e => e.Tickets).Any(t => t.Conflicts.Count > 0))
        {
            return view;
        }

        var byTicket = ConflictsByTicket(view.Conflicts);

        return view with
        {
            Efforts =
            [
                .. view.Efforts.Select(effort => effort with
                {
                    Tickets =
                    [
                        .. effort.Tickets.Select(ticket => ticket with
                        {
                            Conflicts = byTicket.TryGetValue(ticket.Id, out var reports) ? reports : [],
                        }),
                    ],
                }),
            ],
        };
    }

    /// <summary>
    /// Whether a ticket is explicitly actionable, and why. Openness alone is not readiness:
    /// an ordinary open ticket qualifies only inside an explicit Wayfinder context.
    /// </summary>
    private static (NextActionSource Source, string Reason)? Candidacy(
        WorkflowEffort effort,
        WorkflowTicket ticket,
        BlockerResolution resolution)
    {
        if (ticket.IsComplete || ticket.IsUnclassifiedGitHubIssue || !ticket.CountsTowardProgress)
        {
            return null;
        }

        if (!resolution.CanMove || ticket.Status.Status == WorkflowStatus.Blocked)
        {
            return null;
        }

        // The frontier drops claimed work: someone already has it.
        if (ticket.IsClaimed)
        {
            return null;
        }

        var readyByLabel = ticket.Labels.Any(l => string.Equals(l, "ready-for-agent", StringComparison.OrdinalIgnoreCase));

        if (ticket.Status.Status == WorkflowStatus.Ready || readyByLabel)
        {
            return effort.IsWayfinderContext
                ? (NextActionSource.WayfinderFrontier, readyByLabel
                    ? "Explicitly labelled ready-for-agent, unblocked, and on the map."
                    : "Explicitly Ready, unblocked, and on the map.")
                : (NextActionSource.ReadyFallback, readyByLabel
                    ? "Explicitly labelled ready-for-agent and unblocked."
                    : "Explicitly Ready and unblocked.");
        }

        if (ticket.Status.Status == WorkflowStatus.Open && effort.IsWayfinderContext)
        {
            return (NextActionSource.WayfinderFrontier, "Open, unblocked, and on the map.");
        }

        return null;
    }

    private static bool IsBlocked(WorkflowTicket ticket, BlockerResolution resolution) =>
        !ticket.IsComplete && (resolution.HasOpenBlocker
            || resolution.HasInvalidBlocker
            || ticket.Status.Status == WorkflowStatus.Blocked);

    /// <summary>
    /// Equal-weight totals. Containers and unrecognized artifacts never enter the denominator,
    /// and each ticket contributes exactly once at completion.
    /// </summary>
    private static ProgressSummary Summarize(IEnumerable<WorkflowTicket> tickets)
    {
        var counted = 0;
        var completed = 0;
        var excluded = 0;

        foreach (var ticket in tickets)
        {
            if (ticket.Kind == WorkUnitKind.Unrecognized)
            {
                excluded++;
                continue;
            }

            if (ticket.Kind == WorkUnitKind.Container)
            {
                continue;
            }

            counted++;
            if (ticket.IsComplete)
            {
                completed++;
            }
        }

        return new ProgressSummary(completed, counted, excluded);
    }

    private static IReadOnlyList<PipelineSegment> Pipeline(IEnumerable<WorkflowTicket> tickets)
    {
        var order = new[]
        {
            WorkUnitKind.Planning,
            WorkUnitKind.Research,
            WorkUnitKind.Grilling,
            WorkUnitKind.Prototype,
            WorkUnitKind.Implementation,
            WorkUnitKind.Review,
            WorkUnitKind.Release,
        };

        var grouped = tickets
            .Where(t => t.CountsTowardProgress)
            .GroupBy(t => t.Kind)
            .ToDictionary(g => g.Key, g => (Total: g.Count(), Completed: g.Count(t => t.IsComplete)));

        return order
            .Where(grouped.ContainsKey)
            .Select(kind => new PipelineSegment(kind, grouped[kind].Completed, grouped[kind].Total))
            .ToList();
    }

    /// <summary>
    /// Next action precedence: the explicit Wayfinder frontier first, then explicitly Ready or
    /// ready-for-agent work. Unclassified GitHub issues are remaining work but never a next action.
    /// </summary>
    private static NextAction? ChooseNextAction(
        NormalizedProject project,
        IReadOnlyList<(WorkflowEffort Effort, WorkflowTicket Ticket, NextActionSource Source, string Reason)> candidates)
    {
        foreach (var effort in project.Efforts.Where(e => e.IsWayfinderContext).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            var inEffort = candidates
                .Where(c => string.Equals(c.Effort.Id, effort.Id, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (inEffort.Count == 0)
            {
                continue;
            }

            // Map order is direction: the first candidate the map lists wins.
            var ordered = effort.MapOrder
                .Select(id => inEffort.FirstOrDefault(c => string.Equals(c.Ticket.Id, id, StringComparison.OrdinalIgnoreCase)))
                .Where(c => c.Ticket is not null)
                .Concat(inEffort.Where(c => !effort.MapOrder.Contains(c.Ticket.Id, StringComparer.OrdinalIgnoreCase))
                    .OrderBy(c => c.Ticket.Id, StringComparer.OrdinalIgnoreCase))
                .ToList();

            if (ordered.Count > 0)
            {
                var winner = ordered[0];
                return ToNextAction(winner.Ticket, NextActionSource.WayfinderFrontier, winner.Reason);
            }
        }

        var fallback = candidates
            .Where(c => c.Source == NextActionSource.ReadyFallback)
            .OrderBy(c => c.Effort.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Ticket.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        return fallback.Ticket is null
            ? null
            : ToNextAction(fallback.Ticket, NextActionSource.ReadyFallback, fallback.Reason);
    }

    private static NextAction ToNextAction(WorkflowTicket ticket, NextActionSource source, string reason) =>
        new(ticket.Id, ticket.Title, ticket.EffortId, source, reason, ticket.Provenance);

    /// <summary>
    /// State precedence: claimed work wins, then explicit readiness, then a fully stuck project,
    /// then unclear readiness, and only an empty remainder counts as complete.
    /// </summary>
    private static (ProjectState State, string Reason) DetermineState(
        NormalizedProject project,
        IReadOnlyDictionary<string, BlockerResolution> resolutions,
        IReadOnlyList<(WorkflowEffort Effort, WorkflowTicket Ticket, NextActionSource Source, string Reason)> candidates,
        ProgressSummary progress)
    {
        var remaining = project.AllTickets
            .Where(t => t.CountsTowardProgress && !t.IsComplete)
            .ToList();

        var claimed = remaining.FirstOrDefault(t => t.IsClaimed);
        if (claimed is not null)
        {
            return (ProjectState.InProgress, $"'{claimed.Title}' is claimed.");
        }

        if (candidates.Count > 0)
        {
            return (ProjectState.Ready, $"{candidates.Count} explicitly actionable unblocked item(s).");
        }

        if (remaining.Count == 0)
        {
            // Completion means work was tracked and finished. A project with no work units at all
            // has told us nothing, and reporting that as Complete would be a false conclusion.
            if (!progress.HasWorkUnits)
            {
                return (ProjectState.Idle, progress.Excluded > 0
                    ? $"No recognized work units; {progress.Excluded} artifact(s) could not be interpreted — see diagnostics."
                    : "No workflow artifacts were found for this project.");
            }

            return (ProjectState.Complete, progress.Excluded > 0
                ? $"No work remains; {progress.Excluded} artifact(s) excluded from totals — see diagnostics."
                : "No work remains.");
        }

        // Blocked is reserved for a project where nothing can move; one blocked ticket among
        // several must never misclassify an otherwise live project.
        var everythingIsBlocked = remaining.All(t =>
            t.Status.Status == WorkflowStatus.Blocked
            || (resolutions.TryGetValue(t.Id, out var resolution) && !resolution.CanMove));

        if (everythingIsBlocked)
        {
            return (ProjectState.Blocked, $"All {remaining.Count} remaining item(s) are blocked.");
        }

        return (ProjectState.Idle, $"{remaining.Count} item(s) remain, none explicitly actionable.");
    }

    /// <summary>
    /// Activity that qualifies as movement. Closed tickets and closure events never qualify, and
    /// filesystem modification times are not activity-grade evidence.
    /// </summary>
    private static IEnumerable<ActivityEvent> CollectActivity(NormalizedProject project, ProjectionOptions options)
    {
        var path = project.Identity.CanonicalPath;

        foreach (var commit in project.Commits)
        {
            yield return new ActivityEvent(
                commit.CommittedAt,
                ActivityKind.LocalCommit,
                commit.Subject,
                path,
                null,
                Provenance.Git($"{path}#{commit.Sha}", commit.CommittedAt, "observed"));
        }

        foreach (var issue in project.GitHubIssues.Where(i => !i.IsClosed))
        {
            yield return new ActivityEvent(
                issue.UpdatedAt,
                ActivityKind.TicketChanged,
                $"#{issue.Number} {issue.Title}",
                path,
                $"gh#{issue.Number}",
                issue.Provenance);
        }

        foreach (var persisted in options.PersistedActivity.Where(e => e.Provenance.IsActivityGradeTimestamp))
        {
            yield return persisted;
        }
    }
}
