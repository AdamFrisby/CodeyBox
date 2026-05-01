using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// SQLite-backed timing store. Writes to the same database file as
/// <see cref="SqliteWorkItemStore"/>; SQLite WAL mode allows concurrent
/// readers from both stores without SQLITE_BUSY contention.
///
/// Prepared statements for INSERT and UPDATE keep the hot-path overhead
/// well under 1 ms per Begin/End call.
/// </summary>
public sealed class SqliteTimingStore : ITimingStore, IDisposable
{
    private readonly string _path;
    private readonly SqliteConnection _conn;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SqliteCommand _insertCmd;
    private readonly SqliteCommand _updateCmd;

    public SqliteTimingStore(string path)
    {
        _path = path;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _conn = new SqliteConnection($"Data Source={path}");
        _conn.Open();

        using (var walCmd = _conn.CreateCommand())
        {
            walCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
            walCmd.ExecuteNonQuery();
        }

        using var createCmd = _conn.CreateCommand();
        // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- hardcoded DDL only
        createCmd.CommandText = """
            CREATE TABLE IF NOT EXISTS work_item_timings (
                id              TEXT PRIMARY KEY,
                work_item_id    TEXT NOT NULL REFERENCES work_items(id) ON DELETE CASCADE,
                phase           TEXT NOT NULL,
                iteration       INTEGER,
                step            TEXT NOT NULL,
                started_at      TEXT NOT NULL,
                ended_at        TEXT,
                duration_ms     INTEGER,
                metadata_json   TEXT NOT NULL DEFAULT '{}'
            );
            CREATE INDEX IF NOT EXISTS idx_timings_work_item_phase
                ON work_item_timings(work_item_id, phase, iteration, started_at);
            """;
        createCmd.ExecuteNonQuery();

        // Prepared INSERT — called on every Begin (hot path).
        _insertCmd = _conn.CreateCommand();
        _insertCmd.CommandText = """
            INSERT INTO work_item_timings
                (id, work_item_id, phase, iteration, step, started_at, ended_at, duration_ms, metadata_json)
            VALUES ($id, $wid, $phase, $iter, $step, $started, NULL, NULL, $meta)
            """;
        _insertCmd.Parameters.Add("$id", SqliteType.Text);
        _insertCmd.Parameters.Add("$wid", SqliteType.Text);
        _insertCmd.Parameters.Add("$phase", SqliteType.Text);
        _insertCmd.Parameters.Add("$iter", SqliteType.Integer);
        _insertCmd.Parameters.Add("$step", SqliteType.Text);
        _insertCmd.Parameters.Add("$started", SqliteType.Text);
        _insertCmd.Parameters.Add("$meta", SqliteType.Text);
        _insertCmd.Prepare();

        // Prepared UPDATE — called on every End (hot path).
        _updateCmd = _conn.CreateCommand();
        _updateCmd.CommandText = """
            UPDATE work_item_timings SET ended_at = $ended, duration_ms = $dur WHERE id = $id
            """;
        _updateCmd.Parameters.Add("$ended", SqliteType.Text);
        _updateCmd.Parameters.Add("$dur", SqliteType.Integer);
        _updateCmd.Parameters.Add("$id", SqliteType.Text);
        _updateCmd.Prepare();
    }

    public async Task BeginAsync(TimingRecord record, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            _insertCmd.Parameters["$id"].Value = record.Id;
            _insertCmd.Parameters["$wid"].Value = record.WorkItemId.ToString();
            _insertCmd.Parameters["$phase"].Value = record.Phase;
            _insertCmd.Parameters["$iter"].Value = record.Iteration.HasValue
                ? (object)record.Iteration.Value
                : DBNull.Value;
            _insertCmd.Parameters["$step"].Value = record.Step;
            _insertCmd.Parameters["$started"].Value = record.StartedAt.ToString("O");
            _insertCmd.Parameters["$meta"].Value = record.MetadataJson;
            await _insertCmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task EndAsync(string id, DateTimeOffset endedAt, long durationMs, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            _updateCmd.Parameters["$ended"].Value = endedAt.ToString("O");
            _updateCmd.Parameters["$dur"].Value = durationMs;
            _updateCmd.Parameters["$id"].Value = id;
            await _updateCmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<TimingRecord>> GetByWorkItemAsync(WorkItemId id, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, work_item_id, phase, iteration, step, started_at, ended_at, duration_ms, metadata_json
                FROM work_item_timings
                WHERE work_item_id = $wid
                ORDER BY started_at
                """;
            cmd.Parameters.AddWithValue("$wid", id.ToString());

            var results = new List<TimingRecord>();
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                results.Add(ReadRecord(reader));
            return results;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task DeleteByWorkItemAsync(WorkItemId id, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM work_item_timings WHERE work_item_id = $wid";
            cmd.Parameters.AddWithValue("$wid", id.ToString());
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async IAsyncEnumerable<TimingRecord> StreamCompletedAsync(
        int workItemLimit,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Open a separate read-only connection so we can yield rows incrementally
        // without holding _writeLock. WAL mode allows this reader to proceed
        // concurrently with the writer (_conn) without SQLITE_BUSY contention.
        using var readConn = new SqliteConnection($"Data Source={_path};Mode=ReadOnly");
        readConn.Open();

        using var cmd = readConn.CreateCommand();
        cmd.CommandText = """
            SELECT t.id, t.work_item_id, t.phase, t.iteration, t.step,
                   t.started_at, t.ended_at, t.duration_ms, t.metadata_json
            FROM work_item_timings t
            JOIN (
                SELECT id FROM work_items
                WHERE state = $doneState
                ORDER BY updated_at DESC
                LIMIT $lim
            ) w ON t.work_item_id = w.id
            WHERE t.duration_ms IS NOT NULL
            ORDER BY t.work_item_id, t.step, t.started_at
            """;
        cmd.Parameters.AddWithValue("$lim", workItemLimit);
        cmd.Parameters.AddWithValue("$doneState", (int)WorkItemState.Done);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            yield return ReadRecord(reader);
    }

    private static TimingRecord ReadRecord(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        WorkItemId = new WorkItemId(Guid.Parse(r.GetString(1))),
        Phase = r.GetString(2),
        Iteration = r.IsDBNull(3) ? null : r.GetInt32(3),
        Step = r.GetString(4),
        StartedAt = DateTimeOffset.Parse(r.GetString(5)),
        EndedAt = r.IsDBNull(6) ? null : DateTimeOffset.Parse(r.GetString(6)),
        DurationMs = r.IsDBNull(7) ? null : r.GetInt64(7),
        MetadataJson = r.GetString(8),
    };

    public void Dispose()
    {
        _insertCmd.Dispose();
        _updateCmd.Dispose();
        _conn.Dispose();
        _writeLock.Dispose();
    }
}
