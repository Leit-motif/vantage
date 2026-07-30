using MattWorkflowDashboard.Core;
using MattWorkflowDashboard.Core.Projection;

namespace MattWorkflowDashboard.App.ViewModels;

/// <summary>
/// One project row. Everything shown here is read straight from the snapshot, and every value
/// carries the provenance that produced it so any conclusion can be challenged.
/// </summary>
public sealed class ProjectItemViewModel(ProjectView view)
{
    public ProjectView View { get; } = view;

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

    public string LastUpdateText => View.LastActivityAt is { } at
        ? Relative(at)
        : "no observed activity";

    public DateTimeOffset? LastActivityAt => View.LastActivityAt;

    public IReadOnlyList<ActivityEvent> RecentActivity => View.RecentActivity;

    public IReadOnlyList<EffortView> Efforts => View.Efforts;

    public IReadOnlyList<ConflictReport> Conflicts => View.Conflicts;

    public IReadOnlyList<Diagnostic> Diagnostics => View.Diagnostics;

    public int ConflictCount => View.Conflicts.Count;

    public int DiagnosticCount => View.Diagnostics.Count;

    public bool HasConflicts => View.Conflicts.Count > 0;

    public bool HasDiagnostics => View.Diagnostics.Count > 0;

    public bool IsPinned => View.IsPinned;

    public bool IsStale => View.IsStale;

    public string OriginText => View.Origin?.Slug ?? "local only";

    /// <summary>The freshness sentence shown when evidence came from a cached snapshot.</summary>
    public string FreshnessText => View.IsStale
        ? "Showing last known good — a source was unavailable."
        : View.GitHubAvailable ? "Live" : "Local evidence only";

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
