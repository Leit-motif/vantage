namespace Vantage.Core.Observed;

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

/// <summary>
/// The markdown checkboxes a file states about itself. Counted, never interpreted: what a box
/// means is the file's business, and only the disagreement between a completed status and an
/// unticked box is the dashboard's.
/// </summary>
public sealed record ChecklistTally(int Ticked, int Unticked)
{
    public static readonly ChecklistTally None = new(0, 0);

    public int Total => Ticked + Unticked;
}

/// <summary>A markdown artifact inside an effort directory, read but not interpreted.</summary>
public sealed record ObservedArtifact(
    string AbsolutePath,
    string RelativePath,
    EffortArtifactKind Kind,
    string? HeadingTitle,
    IReadOnlyList<ObservedMetadataField> Fields,
    ChecklistTally Checklist,
    string SemanticHash,
    bool TrackedInGit,
    Provenance Provenance)
{
    public ObservedMetadataField? Field(string key) =>
        Fields.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A commit reachable from a local branch. Remote-only refs are never observed here.</summary>
public sealed record ObservedCommit(
    string Sha,
    DateTimeOffset CommittedAt,
    string Author,
    string Subject,
    IReadOnlyList<string> ChangedPaths);
