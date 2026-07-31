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

/// <summary>An explicit link between a local ticket and a GitHub issue. Never inferred from titles.</summary>
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

    /// <summary>Comments observed on the linked issue. A new one is movement worth showing.</summary>
    public int CommentCount { get; init; }

    public required string SourcePath { get; init; }

    public required string SemanticHash { get; init; }

    public required Provenance Provenance { get; init; }

    /// <summary>Additional provenance accumulated by enrichment, kept alongside the primary trail.</summary>
    public IReadOnlyList<Provenance> EnrichmentProvenance { get; init; } = [];

    /// <summary>True when the ticket exists only on GitHub and carries no recognized workflow status.</summary>
    public bool IsUnclassifiedGitHubIssue { get; init; }

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

/// <summary>A GitHub origin normalized to <c>owner/repo</c>, associated with a local identity.</summary>
public sealed record GitHubOrigin(string Owner, string Repository)
{
    public string Slug => $"{Owner}/{Repository}";

    public static GitHubOrigin? TryParse(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
        {
            return null;
        }

        var value = remoteUrl.Trim();
        if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^4];
        }

        // git@github.com:owner/repo
        var scpIndex = value.IndexOf("github.com:", StringComparison.OrdinalIgnoreCase);
        if (scpIndex >= 0)
        {
            return FromPath(value[(scpIndex + "github.com:".Length)..]);
        }

        // https://github.com/owner/repo or ssh://git@github.com/owner/repo
        var hostIndex = value.IndexOf("github.com/", StringComparison.OrdinalIgnoreCase);
        return hostIndex >= 0 ? FromPath(value[(hostIndex + "github.com/".Length)..]) : null;
    }

    private static GitHubOrigin? FromPath(string path)
    {
        var parts = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? new GitHubOrigin(parts[0], parts[1]) : null;
    }
}

/// <summary>The normalized facts for one project, before any dashboard inference.</summary>
public sealed record NormalizedProject
{
    public required ProjectIdentity Identity { get; init; }

    public GitHubOrigin? Origin { get; init; }

    public IReadOnlyList<WorkflowEffort> Efforts { get; init; } = [];

    public IReadOnlyList<ObservedCommit> Commits { get; init; } = [];

    public IReadOnlyList<ObservedGitHubIssue> GitHubIssues { get; init; } = [];

    public IReadOnlyList<Diagnostic> Diagnostics { get; init; } = [];

    public bool GitAvailable { get; init; } = true;

    public bool GitHubAvailable { get; init; } = true;

    public IEnumerable<WorkflowTicket> AllTickets => Efforts.SelectMany(e => e.Tickets);
}
