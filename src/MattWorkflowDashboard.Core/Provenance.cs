namespace MattWorkflowDashboard.Core;

/// <summary>Which adapter observed a fact.</summary>
public enum EvidenceSource
{
    LocalFile,
    LocalGit,
    GitHubCli,
    Cache,
}

/// <summary>
/// How a timestamp was obtained. The dashboard must never present a filesystem
/// modification time as evidence of activity on a first scan.
/// </summary>
public enum TimestampProvenance
{
    Unknown,
    GitCommit,
    GitHubApi,
    WatcherEvent,
    FileSystem,
}

/// <summary>
/// The trail behind a single observed fact. Provenance is attached at observation and
/// carried through normalization and projection so every displayed conclusion stays
/// traceable to a source, a raw value, a timestamp kind, and the refresh that produced it.
/// </summary>
public sealed record Provenance(
    EvidenceSource Source,
    string Locator,
    TimestampProvenance TimestampKind,
    DateTimeOffset? ObservedAt,
    string RefreshId)
{
    public static Provenance File(string path, string refreshId) =>
        new(EvidenceSource.LocalFile, path, TimestampProvenance.FileSystem, null, refreshId);

    public static Provenance Git(string locator, DateTimeOffset at, string refreshId) =>
        new(EvidenceSource.LocalGit, locator, TimestampProvenance.GitCommit, at, refreshId);

    public static Provenance GitHub(string locator, DateTimeOffset at, string refreshId) =>
        new(EvidenceSource.GitHubCli, locator, TimestampProvenance.GitHubApi, at, refreshId);

    public static Provenance Watcher(string path, DateTimeOffset at, string refreshId) =>
        new(EvidenceSource.LocalFile, path, TimestampProvenance.WatcherEvent, at, refreshId);

    /// <summary>
    /// True when the timestamp is trustworthy as evidence that something actually happened,
    /// as opposed to merely being when a file landed on disk.
    /// </summary>
    public bool IsActivityGradeTimestamp =>
        ObservedAt is not null &&
        TimestampKind is TimestampProvenance.GitCommit
            or TimestampProvenance.GitHubApi
            or TimestampProvenance.WatcherEvent;

    public string Describe() =>
        $"{Source} · {Locator} · {TimestampKind}" +
        (ObservedAt is { } at ? $" · {at:u}" : string.Empty) +
        $" · refresh {RefreshId}";
}
