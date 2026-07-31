using System.Text.Json;
using System.Text.Json.Serialization;
using Vantage.Core;
using Vantage.Core.Projection;
using Vantage.Core.Workflow;
using Microsoft.Data.Sqlite;

namespace Vantage.Infrastructure.Persistence;

/// <summary>What the cache remembered about a ticket at the end of the previous refresh.</summary>
public sealed record TicketSnapshot(
    string TicketId,
    string SemanticHash,
    string Title,
    string RawStatus,
    bool IsComplete,
    string SourcePath,
    string Labels,
    string Assignees,
    string Blockers,
    int CommentCount);

/// <summary>What the cache remembered about a map, spec, or PRD at the end of the previous refresh.</summary>
public sealed record ArtifactSnapshot(string Path, string Kind, string SemanticHash);

/// <summary>
/// The dashboard's disposable index. Nothing here is workflow truth: it is derived state that
/// exists to make the dashboard fast, explainable across restarts, and useful offline. A cache
/// that cannot be opened or migrated is rebuilt rather than allowed to block startup.
/// </summary>
public sealed class DashboardCache : IDisposable
{
    private const int SchemaVersion = 3;

    private static readonly JsonSerializerOptions SnapshotOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly SqliteConnection _connection;
    private readonly Lock _gate = new();

    private DashboardCache(SqliteConnection connection, IReadOnlyList<Diagnostic> diagnostics)
    {
        _connection = connection;
        Diagnostics = diagnostics;
    }

    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    public static DashboardCache Open(string databasePath)
    {
        var diagnostics = new List<Diagnostic>();

        try
        {
            return new DashboardCache(Connect(databasePath), diagnostics);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException)
        {
            // The index is disposable by design, so a corrupt or unreadable one is discarded
            // rather than allowed to prevent the dashboard from starting.
            diagnostics.Add(Diagnostic.Warning(
                DiagnosticCode.CacheRebuilt,
                $"The cache could not be opened ({ex.Message}) and was rebuilt. No workflow source was affected.",
                databasePath));

            SqliteConnection.ClearAllPools();
            TryDelete(databasePath);
            return new DashboardCache(Connect(databasePath), diagnostics);
        }
    }

    private static SqliteConnection Connect(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());

        try
        {
            connection.Open();
            Execute(connection, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;");
            Migrate(connection);
            return connection;
        }
        catch
        {
            // The handle has to go before the file can be replaced, or the rebuild fails too.
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Forward-only migrations keyed on <c>user_version</c>. Newer files are left alone.</summary>
    private static void Migrate(SqliteConnection connection)
    {
        var version = Convert.ToInt32(Scalar(connection, "PRAGMA user_version;") ?? 0);

        if (version > SchemaVersion)
        {
            throw new InvalidOperationException(
                $"Cache schema {version} is newer than this build understands ({SchemaVersion}).");
        }

        if (version < 1)
        {
            Execute(connection, """
                CREATE TABLE activity (
                    id             INTEGER PRIMARY KEY AUTOINCREMENT,
                    project_path   TEXT NOT NULL,
                    occurred_at    TEXT NOT NULL,
                    kind           TEXT NOT NULL,
                    summary        TEXT NOT NULL,
                    ticket_id      TEXT NULL,
                    source         TEXT NOT NULL,
                    locator        TEXT NOT NULL,
                    timestamp_kind TEXT NOT NULL,
                    refresh_id     TEXT NOT NULL,
                    UNIQUE (project_path, occurred_at, kind, locator, summary)
                );
                CREATE INDEX ix_activity_project_time ON activity (project_path, occurred_at);

                CREATE TABLE ticket_snapshot (
                    project_path  TEXT NOT NULL,
                    ticket_id     TEXT NOT NULL,
                    semantic_hash TEXT NOT NULL,
                    title         TEXT NOT NULL,
                    raw_status    TEXT NOT NULL,
                    is_complete   INTEGER NOT NULL,
                    source_path   TEXT NOT NULL,
                    PRIMARY KEY (project_path, ticket_id)
                );

                CREATE TABLE project_snapshot (
                    project_path TEXT PRIMARY KEY,
                    payload      TEXT NOT NULL,
                    captured_at  TEXT NOT NULL
                );
                """);
        }

        if (version < 2)
        {
            // Ticket facts beyond the file's own content, plus the planning artifacts whose
            // change is movement in its own right.
            Execute(connection, """
                ALTER TABLE ticket_snapshot ADD COLUMN labels TEXT NOT NULL DEFAULT '';
                ALTER TABLE ticket_snapshot ADD COLUMN assignees TEXT NOT NULL DEFAULT '';
                ALTER TABLE ticket_snapshot ADD COLUMN blockers TEXT NOT NULL DEFAULT '';
                ALTER TABLE ticket_snapshot ADD COLUMN comment_count INTEGER NOT NULL DEFAULT 0;

                CREATE TABLE artifact_snapshot (
                    project_path  TEXT NOT NULL,
                    path          TEXT NOT NULL,
                    kind          TEXT NOT NULL,
                    semantic_hash TEXT NOT NULL,
                    PRIMARY KEY (project_path, path)
                );
                """);
        }

        if (version < 3)
        {
            // The collection columns changed to an escaped encoding. Rewriting what is already
            // stored keeps every row this cache held and stops the change of encoding from being
            // read as movement on the next refresh — an upgrade is not something that happened to
            // the owner's work.
            Recanonicalize(connection);
        }

        if (version < SchemaVersion)
        {
            Execute(connection, $"PRAGMA user_version={SchemaVersion};");
        }
    }

    private static void Recanonicalize(SqliteConnection connection)
    {
        var rows = new List<(string Project, string Ticket, string Labels, string Assignees, string Blockers)>();

        using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT project_path, ticket_id, labels, assignees, blockers FROM ticket_snapshot;";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4)));
            }
        }

        // A stored value that contains the separator is exactly what the previous encoding could
        // not pin down: those bytes meant either one item spelling the separator or several items,
        // and nothing in the cache can say which. Re-reading them one way would make the other way
        // look like movement, which is the very mistake this ticket exists to stop. So the project
        // holding such a row loses its whole baseline and is observed afresh, which is the one
        // reading that cannot invent movement — the same rule a first scan already follows.
        var undecidable = rows
            .Where(r => IsAmbiguous(r.Labels) || IsAmbiguous(r.Assignees) || IsAmbiguous(r.Blockers))
            .Select(r => r.Project)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        using var transaction = connection.BeginTransaction();

        foreach (var project in undecidable)
        {
            using var forget = connection.CreateCommand();
            forget.Transaction = transaction;
            forget.CommandText = "DELETE FROM ticket_snapshot WHERE project_path = $path;";
            forget.Parameters.AddWithValue("$path", project);
            forget.ExecuteNonQuery();
        }

        using var write = connection.CreateCommand();
        write.Transaction = transaction;
        write.CommandText = """
            UPDATE ticket_snapshot SET labels = $labels, assignees = $assignees, blockers = $blockers
            WHERE project_path = $path AND ticket_id = $id;
            """;

        var labels = write.Parameters.Add("$labels", SqliteType.Text);
        var assignees = write.Parameters.Add("$assignees", SqliteType.Text);
        var blockers = write.Parameters.Add("$blockers", SqliteType.Text);
        var path = write.Parameters.Add("$path", SqliteType.Text);
        var id = write.Parameters.Add("$id", SqliteType.Text);

        foreach (var row in rows.Where(r => !undecidable.Contains(r.Project)))
        {
            labels.Value = Canonical(ReadUnescaped(row.Labels));
            assignees.Value = Canonical(ReadUnescaped(row.Assignees));
            blockers.Value = Canonical(ReadUnescaped(row.Blockers));
            path.Value = row.Project;
            id.Value = row.Ticket;
            write.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static bool IsAmbiguous(string stored) => stored.Contains(Separator, StringComparison.Ordinal);

    public IReadOnlyList<ActivityEvent> LoadActivity(string projectPath, DateTimeOffset since)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT occurred_at, kind, summary, ticket_id, source, locator, timestamp_kind, refresh_id
                FROM activity
                WHERE project_path = $path AND occurred_at >= $since
                ORDER BY occurred_at DESC;
                """;
            command.Parameters.AddWithValue("$path", projectPath);
            command.Parameters.AddWithValue("$since", since.ToString("O"));

            var events = new List<ActivityEvent>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                events.Add(new ActivityEvent(
                    DateTimeOffset.Parse(reader.GetString(0)),
                    Enum.Parse<ActivityKind>(reader.GetString(1)),
                    reader.GetString(2),
                    projectPath,
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    new Provenance(
                        Enum.Parse<EvidenceSource>(reader.GetString(4)),
                        reader.GetString(5),
                        Enum.Parse<TimestampProvenance>(reader.GetString(6)),
                        DateTimeOffset.Parse(reader.GetString(0)),
                        reader.GetString(7))));
            }

            return events;
        }
    }

    public void RecordActivity(IEnumerable<ActivityEvent> events)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO activity
                    (project_path, occurred_at, kind, summary, ticket_id, source, locator, timestamp_kind, refresh_id)
                VALUES ($path, $at, $kind, $summary, $ticket, $source, $locator, $timestampKind, $refresh);
                """;

            var path = command.Parameters.Add("$path", SqliteType.Text);
            var at = command.Parameters.Add("$at", SqliteType.Text);
            var kind = command.Parameters.Add("$kind", SqliteType.Text);
            var summary = command.Parameters.Add("$summary", SqliteType.Text);
            var ticket = command.Parameters.Add("$ticket", SqliteType.Text);
            var source = command.Parameters.Add("$source", SqliteType.Text);
            var locator = command.Parameters.Add("$locator", SqliteType.Text);
            var timestampKind = command.Parameters.Add("$timestampKind", SqliteType.Text);
            var refresh = command.Parameters.Add("$refresh", SqliteType.Text);

            foreach (var e in events)
            {
                path.Value = e.ProjectPath;
                at.Value = e.At.ToString("O");
                kind.Value = e.Kind.ToString();
                summary.Value = e.Summary;
                ticket.Value = (object?)e.TicketId ?? DBNull.Value;
                source.Value = e.Provenance.Source.ToString();
                locator.Value = e.Provenance.Locator;
                timestampKind.Value = e.Provenance.TimestampKind.ToString();
                refresh.Value = e.Provenance.RefreshId;
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    /// <summary>
    /// Drops activity older than the retention window. Current ticket snapshots are deliberately
    /// left alone: the dashboard must stay explainable after its history ages out.
    /// </summary>
    public int PruneActivity(DateTimeOffset cutoff)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM activity WHERE occurred_at < $cutoff;";
            command.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));
            return command.ExecuteNonQuery();
        }
    }

    public IReadOnlyDictionary<string, TicketSnapshot> LoadTicketSnapshots(string projectPath)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT ticket_id, semantic_hash, title, raw_status, is_complete, source_path,
                       labels, assignees, blockers, comment_count
                FROM ticket_snapshot WHERE project_path = $path;
                """;
            command.Parameters.AddWithValue("$path", projectPath);

            var snapshots = new Dictionary<string, TicketSnapshot>(StringComparer.OrdinalIgnoreCase);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                snapshots[reader.GetString(0)] = new TicketSnapshot(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetInt32(4) != 0,
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.GetString(7),
                    reader.GetString(8),
                    reader.GetInt32(9));
            }

            return snapshots;
        }
    }

    public IReadOnlyDictionary<string, ArtifactSnapshot> LoadArtifactSnapshots(string projectPath)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT path, kind, semantic_hash FROM artifact_snapshot WHERE project_path = $path;";
            command.Parameters.AddWithValue("$path", projectPath);

            var snapshots = new Dictionary<string, ArtifactSnapshot>(StringComparer.OrdinalIgnoreCase);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                snapshots[reader.GetString(0)] = new ArtifactSnapshot(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2));
            }

            return snapshots;
        }
    }

    public void SaveArtifactSnapshots(string projectPath, IEnumerable<PlanningArtifact> artifacts)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();

            using (var delete = _connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM artifact_snapshot WHERE project_path = $path;";
                delete.Parameters.AddWithValue("$path", projectPath);
                delete.ExecuteNonQuery();
            }

            using var insert = _connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR REPLACE INTO artifact_snapshot (project_path, path, kind, semantic_hash)
                VALUES ($path, $artifact, $kind, $hash);
                """;

            var path = insert.Parameters.Add("$path", SqliteType.Text);
            var artifactPath = insert.Parameters.Add("$artifact", SqliteType.Text);
            var kind = insert.Parameters.Add("$kind", SqliteType.Text);
            var hash = insert.Parameters.Add("$hash", SqliteType.Text);

            foreach (var artifact in artifacts)
            {
                path.Value = projectPath;
                artifactPath.Value = artifact.Path;
                kind.Value = artifact.Kind.ToString();
                hash.Value = artifact.SemanticHash;
                insert.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    public void SaveTicketSnapshots(string projectPath, IEnumerable<WorkflowTicket> tickets)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();

            using (var delete = _connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM ticket_snapshot WHERE project_path = $path;";
                delete.Parameters.AddWithValue("$path", projectPath);
                delete.ExecuteNonQuery();
            }

            using var insert = _connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR REPLACE INTO ticket_snapshot
                    (project_path, ticket_id, semantic_hash, title, raw_status, is_complete, source_path,
                     labels, assignees, blockers, comment_count)
                VALUES ($path, $id, $hash, $title, $status, $complete, $source,
                        $labels, $assignees, $blockers, $comments);
                """;

            var path = insert.Parameters.Add("$path", SqliteType.Text);
            var id = insert.Parameters.Add("$id", SqliteType.Text);
            var hash = insert.Parameters.Add("$hash", SqliteType.Text);
            var title = insert.Parameters.Add("$title", SqliteType.Text);
            var status = insert.Parameters.Add("$status", SqliteType.Text);
            var complete = insert.Parameters.Add("$complete", SqliteType.Integer);
            var source = insert.Parameters.Add("$source", SqliteType.Text);
            var labels = insert.Parameters.Add("$labels", SqliteType.Text);
            var assignees = insert.Parameters.Add("$assignees", SqliteType.Text);
            var blockers = insert.Parameters.Add("$blockers", SqliteType.Text);
            var comments = insert.Parameters.Add("$comments", SqliteType.Integer);

            foreach (var ticket in tickets)
            {
                path.Value = projectPath;
                id.Value = ticket.Id;
                hash.Value = ticket.SemanticHash;
                title.Value = ticket.Title;
                status.Value = ticket.Status.RawValue;
                complete.Value = ticket.IsComplete ? 1 : 0;
                source.Value = ticket.SourcePath;
                labels.Value = Canonical(ticket.Labels);
                assignees.Value = Canonical(ticket.Assignees);
                blockers.Value = Canonical(ticket.Blockers.Select(b => b.NormalizedKey));
                comments.Value = ticket.CommentCount;
                insert.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    /// <summary>Stores the last-known-good projection so an unavailable source still shows something true.</summary>
    public void SaveProjectSnapshot(ProjectView view, DateTimeOffset capturedAt)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO project_snapshot (project_path, payload, captured_at)
                VALUES ($path, $payload, $at)
                ON CONFLICT (project_path) DO UPDATE SET payload = excluded.payload, captured_at = excluded.captured_at;
                """;
            command.Parameters.AddWithValue("$path", view.Identity.CanonicalPath);
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(view, SnapshotOptions));
            command.Parameters.AddWithValue("$at", capturedAt.ToString("O"));
            command.ExecuteNonQuery();
        }
    }

    public (ProjectView? View, DateTimeOffset? CapturedAt) LoadProjectSnapshot(string projectPath)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT payload, captured_at FROM project_snapshot WHERE project_path = $path;";
            command.Parameters.AddWithValue("$path", projectPath);

            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return (null, null);
            }

            try
            {
                var stored = JsonSerializer.Deserialize<ProjectView>(reader.GetString(0), SnapshotOptions);

                return (
                    // A projection written by an earlier build carries the project's conflicts but
                    // nothing on its items, so the attachment is rebuilt rather than left missing
                    // until the next successful refresh.
                    stored is null ? null : ProjectProjector.AttachConflictsToItems(stored),
                    DateTimeOffset.Parse(reader.GetString(1)));
            }
            catch (JsonException)
            {
                return (null, null);
            }
        }
    }

    /// <summary>
    /// A canonical, collision-safe rendering of a set of values, and the only thing the dashboard
    /// compares one refresh's collections against the next.
    /// <para>
    /// Two properties matter and neither is free. It is <em>injective</em>: every value is escaped
    /// before the items are joined, so an item that itself contains the separator can never read
    /// back as several distinct items — one label written <c>alpha|beta</c> and the two labels
    /// <c>alpha</c> and <c>beta</c> are different strings, and moving between them is real
    /// movement. And it is <em>canonical</em>: the ordering is total, so the same items written in
    /// a different order — including entries that differ only in case — always render the same
    /// way, and reformatting is never reported as work moving.
    /// </para>
    /// </summary>
    public static string Canonical(IEnumerable<string> values) =>
        string.Join(
            Separator,
            values
                .Select(v => v.Trim())
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .ThenBy(v => v, StringComparer.Ordinal)
                .Select(Escape));

    private const char Separator = '|';

    private static string Escape(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("|", "\\|", StringComparison.Ordinal);

    /// <summary>
    /// Reads a collection stored before the encoding escaped anything. Values that contained the
    /// separator were already indistinguishable from several items back then; this reads them the
    /// way that schema did, which is the only meaning those bytes ever had.
    /// </summary>
    private static IEnumerable<string> ReadUnescaped(string stored) =>
        stored.Split(Separator, StringSplitOptions.RemoveEmptyEntries);

    public void ForgetProject(string projectPath)
    {
        lock (_gate)
        {
            foreach (var table in new[] { "activity", "ticket_snapshot", "project_snapshot", "artifact_snapshot" })
            {
                using var command = _connection.CreateCommand();
                command.CommandText = $"DELETE FROM {table} WHERE project_path = $path;";
                command.Parameters.AddWithValue("$path", projectPath);
                command.ExecuteNonQuery();
            }
        }
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void TryDelete(string path)
    {
        foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
        {
            try
            {
                if (File.Exists(candidate))
                {
                    File.Delete(candidate);
                }
            }
            catch (IOException)
            {
                // If it cannot be deleted the reconnect below will surface the real problem.
            }
        }
    }

    public void Dispose()
    {
        _connection.Dispose();
        SqliteConnection.ClearAllPools();
    }
}
