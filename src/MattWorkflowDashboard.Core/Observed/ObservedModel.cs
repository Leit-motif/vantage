namespace MattWorkflowDashboard.Core.Observed;

/// <summary>
/// The observed layer. Everything here is raw source evidence exactly as an adapter saw it.
/// Normalization and inference happen later and never overwrite these values.
/// </summary>
public sealed record ObservedProject(
    string CanonicalPath,
    string Name,
    IReadOnlyList<string> Markers,
    string? GitOriginUrl,
    bool IsGitRepository)
{
    public bool HasMarker(string marker) =>
        Markers.Any(m => string.Equals(m, marker, StringComparison.OrdinalIgnoreCase));
}

/// <summary>One <c>Key: value</c> or <c>**Key:** value</c> line as written in the source file.</summary>
public sealed record ObservedMetadataField(string Key, string RawValue, int LineNumber);

public enum EffortArtifactKind
{
    Map,
    Spec,
    Prd,
    Issue,
    Other,
}

/// <summary>A markdown artifact inside an effort directory, read but not interpreted.</summary>
public sealed record ObservedArtifact(
    string AbsolutePath,
    string RelativePath,
    EffortArtifactKind Kind,
    string? HeadingTitle,
    IReadOnlyList<ObservedMetadataField> Fields,
    string SemanticHash,
    bool TrackedInGit,
    Provenance Provenance)
{
    public ObservedMetadataField? Field(string key) =>
        Fields.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));
}

/// <summary>An immediate <c>.scratch</c> child that qualifies as an effort.</summary>
public sealed record ObservedEffort(
    string Id,
    string Name,
    string AbsolutePath,
    IReadOnlyList<ObservedArtifact> Artifacts);

/// <summary>A commit reachable from a local branch. Remote-only refs are never observed here.</summary>
public sealed record ObservedCommit(
    string Sha,
    DateTimeOffset CommittedAt,
    string Author,
    string Subject,
    IReadOnlyList<string> ChangedPaths);

/// <summary>An issue read through the authenticated <c>gh</c> CLI for an associated repository.</summary>
public sealed record ObservedGitHubIssue(
    int Number,
    string Title,
    string State,
    IReadOnlyList<string> Labels,
    IReadOnlyList<string> Assignees,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ClosedAt,
    int CommentCount,
    string Url,
    Provenance Provenance)
{
    public bool IsClosed => string.Equals(State, "CLOSED", StringComparison.OrdinalIgnoreCase)
        || string.Equals(State, "closed", StringComparison.Ordinal);
}

/// <summary>Everything one adapter pass observed for a single project.</summary>
public sealed record ObservedProjectEvidence(
    ObservedProject Project,
    IReadOnlyList<ObservedEffort> Efforts,
    IReadOnlyList<ObservedCommit> Commits,
    IReadOnlyList<ObservedGitHubIssue> GitHubIssues,
    IReadOnlyList<Diagnostic> Diagnostics,
    bool GitAvailable,
    bool GitHubAvailable);
