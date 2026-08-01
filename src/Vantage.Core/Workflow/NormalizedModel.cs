using Vantage.Core.Observed;

namespace Vantage.Core.Workflow;

// EffortArtifactKind lives in the observed layer; the normalized layer reuses the same vocabulary
// rather than inventing a parallel one.

/// <summary>
/// Where a work unit sits in the full pipeline. Progress counts the whole effort, not coding
/// alone, so planning, research, grilling, prototypes, review, and release all count as work.
/// </summary>
public enum WorkUnitKind
{
    Planning,
    Research,
    Grilling,
    Prototype,
    Implementation,
    Review,
    Release,

    /// <summary>Maps and parent artifacts: structure, not a work unit.</summary>
    Container,

    /// <summary>Recognized as an artifact but not interpretable as a work unit.</summary>
    Unrecognized,
}

/// <summary>
/// An explicit link a ticket file writes about itself, as the <c>.scratch</c> grammar's
/// <c>GitHub:</c> line states it. It is a local observation, never a remote read: nothing resolves
/// it, and the dashboard only ever uses the number as one more explicit identifier a map or a
/// blocker may refer to the ticket by.
/// </summary>
public sealed record GitHubLink(string Repository, int Number, string RawValue);

/// <summary>A reference to another ticket within the same effort.</summary>
public sealed record BlockerReference(string RawValue, string NormalizedKey);

/// <summary>
/// A normalized work unit. Ticket files own ticket facts; a map may order them but may never
/// override them.
/// </summary>
public sealed record WorkflowTicket
{
    /// <summary>Unique within the project, so two efforts can both hold an <c>001.md</c>.</summary>
    public required string Id { get; init; }

    /// <summary>
    /// How the ticket refers to itself inside its own effort. Blocker references resolve against
    /// this, which is why they can never reach across efforts.
    /// </summary>
    public required string LocalKey { get; init; }

    public required string EffortId { get; init; }

    public required string Title { get; init; }

    public required StatusReading Status { get; init; }

    public required WorkUnitKind Kind { get; init; }

    /// <summary>The internal stage of the ticket, projected separately from completion.</summary>
    public string? Stage { get; init; }

    public IReadOnlyList<BlockerReference> Blockers { get; init; } = [];

    public GitHubLink? Link { get; init; }

    public IReadOnlyList<string> Labels { get; init; } = [];

    public IReadOnlyList<string> Assignees { get; init; } = [];

    public required string SourcePath { get; init; }

    public required string SemanticHash { get; init; }

    public required Provenance Provenance { get; init; }

    /// <summary>Additional provenance accumulated by enrichment, kept alongside the primary trail.</summary>
    public IReadOnlyList<Provenance> EnrichmentProvenance { get; init; } = [];

    public bool IsComplete => Status.IsComplete;

    public bool CountsTowardProgress => Kind is not (WorkUnitKind.Container or WorkUnitKind.Unrecognized);

    public bool IsClaimed => Status.Status == WorkflowStatus.InProgress || Assignees.Count > 0;
}

/// <summary>
/// An immediate <c>.scratch</c> child holding a map, spec, PRD, or markdown issues. Blockers
/// resolve only inside the effort that declares them.
/// </summary>
/// <summary>
/// A map, spec, or PRD: the effort's direction and scope. Not a work unit, but a change to one
/// is real movement, so its content identity is tracked.
/// </summary>
public sealed record PlanningArtifact(EffortArtifactKind Kind, string Path, string SemanticHash);

public sealed record WorkflowEffort
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Path { get; init; }

    public IReadOnlyList<PlanningArtifact> Artifacts { get; init; } = [];

    public bool HasMap => Artifacts.Any(a => a.Kind == EffortArtifactKind.Map);

    public bool HasSpec => Artifacts.Any(a => a.Kind == EffortArtifactKind.Spec);

    public bool HasPrd => Artifacts.Any(a => a.Kind == EffortArtifactKind.Prd);

    public IReadOnlyList<WorkflowTicket> Tickets { get; init; } = [];

    /// <summary>Ticket ids in map order. Empty when the effort has no map.</summary>
    public IReadOnlyList<string> MapOrder { get; init; } = [];

    /// <summary>A map makes ordinary open tickets actionable; without one, openness is not readiness.</summary>
    public bool IsWayfinderContext => HasMap;
}

/// <summary>Stable identity for a project: the normalized canonical local path.</summary>
public sealed record ProjectIdentity(string CanonicalPath, string Name)
{
    public bool Equals(ProjectIdentity? other) =>
        other is not null && string.Equals(CanonicalPath, other.CanonicalPath, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(CanonicalPath);
}

/// <summary>The normalized facts for one project, before any dashboard inference.</summary>
public sealed record NormalizedProject
{
    public required ProjectIdentity Identity { get; init; }

    public IReadOnlyList<WorkflowEffort> Efforts { get; init; } = [];

    public IReadOnlyList<ObservedCommit> Commits { get; init; } = [];

    public IReadOnlyList<Diagnostic> Diagnostics { get; init; } = [];

    public bool GitAvailable { get; init; } = true;

    public IEnumerable<WorkflowTicket> AllTickets => Efforts.SelectMany(e => e.Tickets);
}
