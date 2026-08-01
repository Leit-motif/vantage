using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vantage.Core;
using Vantage.Core.Projection;

namespace Vantage.App.ViewModels;

/// <summary>One piece of stuck work, what is holding it, and where that was observed.</summary>
public sealed record BlockedItem(string Title, string HeldBy, string Provenance);

/// <summary>
/// One project row. Everything shown here is read straight from the snapshot, and every value
/// carries the provenance that produced it so any conclusion can be challenged.
/// </summary>
public sealed partial class ProjectItemViewModel : ObservableObject
{
    private readonly Action<ProjectItemViewModel>? _navigateToConflicts;

    public ProjectItemViewModel(ProjectView view, Action<ProjectItemViewModel>? navigateToConflicts = null)
    {
        View = view;
        _navigateToConflicts = navigateToConflicts;

        var titles = view.Efforts
            .SelectMany(e => e.Tickets)
            .ToDictionary(t => t.Id, t => t.Title, StringComparer.OrdinalIgnoreCase);

        Conflicts =
        [
            .. view.Conflicts.Select(c => new ConflictItemViewModel(
                c,
                titles.TryGetValue(c.TicketId, out var title) ? title : c.TicketId)),
        ];

        Efforts =
        [
            .. view.Efforts.Select(e => new EffortItemViewModel(
                e,
                [.. e.Tickets.Select(t => new TicketItemViewModel(t, InspectTicketConflictsCommand))])),
        ];
    }

    public ProjectView View { get; }

    public string Name => View.Identity.Name;

    public string Path => View.Identity.CanonicalPath;

    public ProjectState State => View.State;

    /// <summary>The state as a word, so meaning is never carried by colour alone.</summary>
    public string StateLabel => View.State switch
    {
        ProjectState.InProgress => "In progress",
        ProjectState.Ready => "Ready",
        ProjectState.Blocked => "Blocked",
        ProjectState.Complete => "Complete",
        _ => "Idle",
    };

    /// <summary>A Fluent outline glyph, paired with the label as a second non-colour cue.</summary>
    public string StateGlyph => Glyphs.ForState(View.State);

    public string StateReason => View.StateReason;

    public double ProgressFraction => View.Progress.Fraction;

    public string ProgressText => View.Progress.Total == 0
        ? "no work units"
        : $"{View.Progress.Completed}/{View.Progress.Total}";

    public string ProgressPercentText => $"{Math.Round(View.Progress.Fraction * 100)}%";

    public IReadOnlyList<PipelineSegment> Pipeline => View.Pipeline;

    public string NextActionText => View.NextAction?.Title ?? "—";

    public string NextActionDetail => View.NextAction is { } action
        ? $"{action.Reason} ({action.Source})"
        : "No explicitly actionable work.";

    public bool HasNextAction => View.NextAction is not null;

    /// <summary>Where the next action came from, so the recommendation can be challenged.</summary>
    public string NextActionProvenance => View.NextAction?.Provenance.Description ?? string.Empty;

    public string LastUpdateText => View.LastActivityAt is { } at
        ? Relative(at)
        : "no observed activity";

    public DateTimeOffset? LastActivityAt => View.LastActivityAt;

    public IReadOnlyList<ActivityEvent> RecentActivity => View.RecentActivity;

    public IReadOnlyList<EffortItemViewModel> Efforts { get; }

    /// <summary>Everything that cannot move, and what is holding it.</summary>
    public IReadOnlyList<BlockedItem> Blockers => View.Efforts
        .SelectMany(e => e.Tickets)
        .Where(t => t.IsBlocked)
        .Select(t => new BlockedItem(
            t.Title,
            t.BlockedBy.Count > 0
                ? "Waiting on " + string.Join(", ", t.BlockedBy)
                : "Marked blocked, with no dependency named",
            t.Provenance.Description))
        .ToList();

    public int BlockerCount => Blockers.Count;

    /// <summary>Every disagreement in this project, each with both sides and both provenances.</summary>
    public IReadOnlyList<ConflictItemViewModel> Conflicts { get; }

    /// <summary>
    /// The item whose disagreement is being inspected, or null while the whole project's
    /// conflicts are listed.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InspectedConflicts))]
    [NotifyPropertyChangedFor(nameof(IsConflictInspectionNarrowed))]
    [NotifyPropertyChangedFor(nameof(ConflictHeading))]
    private string? _conflictFocusTicketId;

    /// <summary>What the conflicts region shows: everything, or one item's disagreement.</summary>
    public IReadOnlyList<ConflictItemViewModel> InspectedConflicts => ConflictFocusTicketId is { } id
        ? [.. Conflicts.Where(c => string.Equals(c.TicketId, id, StringComparison.OrdinalIgnoreCase))]
        : Conflicts;

    public bool IsConflictInspectionNarrowed => ConflictFocusTicketId is not null;

    public string ConflictHeading => ConflictFocusTicketId is null
        ? "CONFLICTS"
        : $"CONFLICTS · {InspectedConflicts.FirstOrDefault()?.TicketTitle ?? ConflictFocusTicketId}";

    public IReadOnlyList<Diagnostic> Diagnostics => View.Diagnostics;

    public int ConflictCount => View.Conflicts.Count;

    public int DiagnosticCount => View.Diagnostics.Count;

    public bool HasConflicts => View.Conflicts.Count > 0;

    public bool HasDiagnostics => View.Diagnostics.Count > 0;

    /// <summary>The aggregate warning as a command's name, so the badge is announced as a control.</summary>
    public string ConflictBadgeName => $"Inspect {ConflictCount} conflict(s) in project {Name}";

    /// <summary>
    /// How many times the conflicts region has been asked for. The view scrolls it into view on
    /// each request, because a pane taller than its viewport can otherwise leave the evidence
    /// below the fold — selected and expanded, but not actually reached.
    /// </summary>
    [ObservableProperty]
    private int _conflictInspectionRequests;

    /// <summary>
    /// The aggregate badge leads to the evidence: it opens this project's conflicts, all of them,
    /// wherever the owner clicked from.
    /// </summary>
    [RelayCommand]
    public void InspectConflicts()
    {
        ConflictFocusTicketId = null;
        Reach();
    }

    /// <summary>The control on an affected item narrows the same region to that item.</summary>
    [RelayCommand]
    public void InspectTicketConflicts(string? ticketId)
    {
        ConflictFocusTicketId = ticketId;
        Reach();
    }

    private void Reach()
    {
        _navigateToConflicts?.Invoke(this);
        ConflictInspectionRequests++;
    }

    [RelayCommand]
    public void ShowAllConflicts() => ConflictFocusTicketId = null;

    public bool IsPinned => View.IsPinned;

    public bool IsStale => View.IsStale;

    /// <summary>The freshness sentence shown when evidence came from a cached snapshot.</summary>
    public string FreshnessText => View.IsStale
        ? "Showing last known good — this project could not be read."
        : "Live";

    /// <summary>Everything the row asserts, in one sentence, for screen readers.</summary>
    public string AccessibleSummary =>
        $"{Name}. {StateLabel}. {StateReason} Progress {ProgressText}. " +
        $"Next action: {NextActionText}. Last update {LastUpdateText}." +
        (HasConflicts ? $" {ConflictCount} conflict(s)." : string.Empty) +
        (HasDiagnostics ? $" {DiagnosticCount} diagnostic(s)." : string.Empty);

    public static string Relative(DateTimeOffset at)
    {
        var delta = DateTimeOffset.UtcNow - at;
        return delta switch
        {
            { TotalSeconds: < 90 } => "just now",
            { TotalMinutes: < 60 } => $"{(int)delta.TotalMinutes}m ago",
            { TotalHours: < 24 } => $"{(int)delta.TotalHours}h ago",
            { TotalDays: < 30 } => $"{(int)delta.TotalDays}d ago",
            _ => at.ToLocalTime().ToString("d MMM yyyy"),
        };
    }
}
