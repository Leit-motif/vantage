namespace Vantage.Core;

/// <summary>
/// Which adapter observed a fact.
/// <para>
/// <see cref="GitHubCli"/> has no producer since the remote source was removed, and is kept only
/// so an existing cache stays readable: activity rows store this by name and are read back with
/// <c>Enum.Parse</c>, so deleting the member would turn every row an earlier build wrote into a
/// failure to load that project's history. Nothing new is ever written with it.
/// </para>
/// </summary>
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

    /// <summary>Retained for cache compatibility only — see <see cref="EvidenceSource.GitHubCli"/>.</summary>
    GitHubApi,
    WatcherEvent,

    /// <summary>
    /// A semantic change the dashboard saw for itself by comparing this refresh against the
    /// previous one. It is real movement, but the timestamp is when it was noticed, not when
    /// it happened — which is why it is not labelled as a watcher event.
    /// </summary>
    ObservedChange,
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

    public static Provenance Watcher(string path, DateTimeOffset at, string refreshId) =>
        new(EvidenceSource.LocalFile, path, TimestampProvenance.WatcherEvent, at, refreshId);

    public static Provenance ObservedChange(string path, DateTimeOffset noticedAt, string refreshId) =>
        new(EvidenceSource.LocalFile, path, TimestampProvenance.ObservedChange, noticedAt, refreshId);

    /// <summary>
    /// True when the timestamp is trustworthy as evidence that something actually happened,
    /// as opposed to merely being when a file landed on disk.
    /// </summary>
    public bool IsActivityGradeTimestamp =>
        ObservedAt is not null &&
        TimestampKind is TimestampProvenance.GitCommit
            or TimestampProvenance.GitHubApi
            or TimestampProvenance.WatcherEvent
            or TimestampProvenance.ObservedChange;

    /// <summary>
    /// A short name for this observation, for the places something has to be named by where it
    /// came from rather than by a fixed role — a side of a disagreement being the case that
    /// motivated it. The subject is carried too, because two sides can share a kind and differ
    /// only in what was read.
    /// </summary>
    public string Origin
    {
        get
        {
            var kind = Source switch
            {
                EvidenceSource.LocalGit => "last commit",
                EvidenceSource.LocalFile => "working tree",
                EvidenceSource.Cache => "last known good",
                _ => Source.ToString(),
            };

            var subject = Subject(Locator);
            return subject.Length == 0 ? kind : $"{kind} · {subject}";
        }
    }

    /// <summary>
    /// The tail of a locator: the file and whatever anchors the observation inside it, without the
    /// directories above it. A side label names what was read; the full trail is in
    /// <see cref="Description"/> beside it.
    /// </summary>
    private static string Subject(string locator)
    {
        var cut = locator.LastIndexOfAny(['/', '\\']);
        return cut < 0 ? locator : locator[(cut + 1)..];
    }

    /// <summary>
    /// The whole trail in one line: source, locator, what kind of timestamp it is, when, and the
    /// refresh that produced it. This is what makes a displayed conclusion challengeable.
    /// </summary>
    public string Description =>
        $"{Source} · {Locator} · {TimestampKind}" +
        (ObservedAt is { } at ? $" · {at:u}" : string.Empty) +
        $" · refresh {RefreshId}";
}
