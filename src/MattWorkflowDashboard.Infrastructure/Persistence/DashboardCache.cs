using System.Text.Json;
using System.Text.Json.Serialization;
using MattWorkflowDashboard.Core;
using MattWorkflowDashboard.Core.Projection;
using MattWorkflowDashboard.Core.Workflow;
using Microsoft.Data.Sqlite;

namespace MattWorkflowDashboard.Infrastructure.Persistence;

/// <summary>What the cache remembered about a ticket at the end of the previous refresh.</summary>
public sealed record TicketSnapshot(string TicketId, string SemanticHash, string Title, string RawStatus, bool IsComplete, string SourcePath);

/// <summary>
/// The dashboard's disposable index. Nothing here is workflow truth: it is derived state that
/// exists to make the dashboard fast, explainable across restarts, and useful offline. A cache
/// that cannot be opened or migrated is rebuilt rather than allowed to block startup.
/// </summary>
public sealed class DashboardCache : IDisposable
{
    private const int SchemaVersion = 1;

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

            Execute(connection, $"PRAGMA user_version={SchemaVersion};");
        }
    }

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
                SELECT ticket_id, semantic_hash, title, raw_status, is_complete, source_path
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
                    reader.GetString(5));
            }

            return snapshots;
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
                INSERT INTO ticket_snapshot
                    (project_path, ticket_id, semantic_hash, title, raw_status, is_complete, source_path)
                VALUES ($path, $id, $hash, $title, $status, $complete, $source);
                """;

            var path = insert.Parameters.Add("$path", SqliteType.Text);
            var id = insert.Parameters.Add("$id", SqliteType.Text);
            var hash = insert.Parameters.Add("$hash", SqliteType.Text);
            var title = insert.Parameters.Add("$title", SqliteType.Text);
            var status = insert.Parameters.Add("$status", SqliteType.Text);
            var complete = insert.Parameters.Add("$complete", SqliteType.Integer);
            var source = insert.Parameters.Add("$source", SqliteType.Text);

            foreach (var ticket in tickets)
            {
                path.Value = projectPath;
                id.Value = ticket.Id;
                hash.Value = ticket.SemanticHash;
                title.Value = ticket.Title;
                status.Value = ticket.Status.RawValue;
                complete.Value = ticket.IsComplete ? 1 : 0;
                source.Value = ticket.SourcePath;
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
                return (
                    JsonSerializer.Deserialize<ProjectView>(reader.GetString(0), SnapshotOptions),
                    DateTimeOffset.Parse(reader.GetString(1)));
            }
            catch (JsonException)
            {
                return (null, null);
            }
        }
    }

    public void ForgetProject(string projectPath)
    {
        lock (_gate)
        {
            foreach (var table in new[] { "activity", "ticket_snapshot", "project_snapshot" })
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
