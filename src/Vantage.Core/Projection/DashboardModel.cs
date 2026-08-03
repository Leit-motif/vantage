using Vantage.Core.Workflow;

namespace Vantage.Core.Projection;

public enum ProjectState
{
    /// <summary>Work remains but readiness is unclear.</summary>
    Idle,

    /// <summary>Explicit actionable unblocked work exists.</summary>
    Ready,

    /// <summary>Work is claimed.</summary>
    InProgress,

    /// <summary>Work remains and nothing can move.</summary>
    Blocked,

    /// <summary>No work remains.</summary>
    Complete,
}

public enum NextActionSource
{
    /// <summary>Chosen by explicit map ordering and dependency rules.</summary>
    WayfinderFrontier,

    /// <summary>Chosen because the work is explicitly Ready or labelled ready-for-agent and unblocked.</summary>
    ReadyFallback,
}

public sealed record NextAction(
    string TicketId,
    string Title,
    string EffortId,
    NextActionSource Source,
    string Reason,
    Provenance Provenance);

public enum ActivityKind
{
    LocalCommit,
    TicketChanged,
    SpecChanged,
    MapChanged,
    WorkflowFileChanged,
    CommentAdded,
    LabelChanged,
    AssignmentChanged,
    BlockerChanged,
}

/// <summary>
/// A qualifying movement. Closure events are never activity — finishing work must not displace
/// actionable work in the Recent projection.
/// </summary>
public sealed record ActivityEvent(
    DateTimeOffset At,
    ActivityKind Kind,
    string Summary,
    string ProjectPath,
    string? TicketId,
    Provenance Provenance);

/// <summary>What two observations disagree about. Only what a producer can actually report.</summary>
public enum ConflictField
{
    Title,
    WorkflowStatus,
    Blockers,
}

/// <summary>
/// One side of a disagreement: a value as it was observed, and where it was observed. Neither side
/// of a <see cref="ConflictReport"/> is privileged, so a side is named by its own provenance rather
/// than by a role the model assigns it.
/// </summary>
public sealed record ObservedValue(string Value, Provenance Provenance);

/// <summary>
/// A disagreement between two observations of one subject, each carrying where it was seen, plus
/// which side was kept and why. Missing information and timestamp differences are enrichment, not
/// conflict.
/// <para>
/// The sides used to be called local and remote, which described the one producer the model
/// happened to be built for rather than what it is. Removing the remote source removed a producer;
/// the shape — two values for one subject, provenance on each, a resolution — is unchanged, and
/// the producers that replace it are a working tree against the last commit, a stated edge against
/// the ticket it names, and a stated status against the ticket's own checklist.
/// </para>
/// </summary>
public sealed record ConflictReport(
    string TicketId,
    ConflictField Field,
    ObservedValue First,
    ObservedValue Second,
    string Resolution);

/// <summary>Equal-weight work-unit totals. A ticket counts once, at completion.</summary>
public sealed record ProgressSummary(int Completed, int Total, int Excluded)
{
    /// <summary>Nothing counted means nothing is known, which is not the same as finished.</summary>
    public double Fraction => Total == 0 ? 0d : (double)Completed / Total;

    public int Remaining => Total - Completed;

    public bool HasWorkUnits => Total > 0;
}

/// <summary>Completion counts per pipeline stage, so progress reflects the whole effort.</summary>
public sealed record PipelineSegment(WorkUnitKind Kind, int Completed, int Total)
{
    public int Remaining => Total - Completed;

    public string Label => Kind switch
    {
        WorkUnitKind.Planning => "Plan",
        WorkUnitKind.Research => "Research",
        WorkUnitKind.Grilling => "Grill",
        WorkUnitKind.Prototype => "Prototype",
        WorkUnitKind.Implementation => "Build",
        WorkUnitKind.Review => "Review",
        WorkUnitKind.Release => "Release",
        _ => Kind.ToString(),
    };
}

public sealed record TicketView(
    string Id,
    string Title,
    string EffortId,
    StatusReading Status,
    WorkUnitKind Kind,
    string? Stage,
    bool IsActionable,
    bool IsBlocked,
    IReadOnlyList<string> BlockedBy,
    GitHubLink? Link,
    string SourcePath,
    Provenance Provenance,
    IReadOnlyList<Provenance> EnrichmentProvenance)
{
    /// <summary>
    /// The disagreements this item is the subject of. A conflict belongs on the work it is about,
    /// so it can be inspected where it arose rather than only in an aggregate list.
    /// </summary>
    public IReadOnlyList<ConflictReport> Conflicts { get; init; } = [];
}

public sealed record EffortView(
    string Id,
    string Name,
    string Path,
    bool IsWayfinderContext,
    ProgressSummary Progress,
    IReadOnlyList<TicketView> Tickets);

public sealed record ProjectView
{
    public required ProjectIdentity Identity { get; init; }

    public required ProjectState State { get; init; }

    public required string StateReason { get; init; }

    public required ProgressSummary Progress { get; init; }

    public IReadOnlyList<PipelineSegment> Pipeline { get; init; } = [];

    public NextAction? NextAction { get; init; }

    public DateTimeOffset? LastActivityAt { get; init; }

    public IReadOnlyList<ActivityEvent> RecentActivity { get; init; } = [];

    public IReadOnlyList<EffortView> Efforts { get; init; } = [];

    public IReadOnlyList<ConflictReport> Conflicts { get; init; } = [];

    public IReadOnlyList<Diagnostic> Diagnostics { get; init; } = [];

    public bool IsPinned { get; init; }

    /// <summary>True when this project's evidence came from a last-known-good snapshot.</summary>
    public bool IsStale { get; init; }

    public bool GitAvailable { get; init; } = true;

    public bool HasRemainingWork => Progress.Remaining > 0;
}

public sealed record SnapshotCounters(int Active, int Ready, int Blocked, int Diagnostics);

/// <summary>
/// One complete, immutable dashboard projection. Everything the UI shows comes from a snapshot,
/// which is the product boundary the primary tests assert against.
/// </summary>
public sealed record DashboardSnapshot
{
    public required string RefreshId { get; init; }

    public required DateTimeOffset GeneratedAt { get; init; }

    public IReadOnlyList<ProjectView> Projects { get; init; } = [];

    public IReadOnlyList<Diagnostic> Diagnostics { get; init; } = [];

    public required SnapshotCounters Counters { get; init; }

    /// <summary>True when at least one source was unavailable and cached evidence was shown instead.</summary>
    public bool IsStale { get; init; }

    /// <summary>True when a bounded scan stopped early; paired with a SCAN_TRUNCATED diagnostic.</summary>
    public bool ScanTruncated { get; init; }

    public static DashboardSnapshot Empty(string refreshId, DateTimeOffset at) => new()
    {
        RefreshId = refreshId,
        GeneratedAt = at,
        Counters = new SnapshotCounters(0, 0, 0, 0),
    };
}
