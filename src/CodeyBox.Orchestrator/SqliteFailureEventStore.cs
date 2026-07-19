using Microsoft.Data.Sqlite;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// SQLite-backed append-only failure/park event log. Writes to the same
/// database file as <see cref="SqliteWorkItemStore"/>; WAL mode allows
/// concurrent readers, and the shared <see cref="SqliteDatabaseWriteGate"/>
/// serialises writers across the stores that point at this file. Mirrors the
/// shape of <see cref="SqliteTimingStore"/>.
/// </summary>
public sealed class SqliteFailureEventStore : IFailureEventStore, IDisposable
{
    /// <summary>
    /// Upper bound on stored error text. Bounds an unbounded/attacker-shaped
    /// error string before it is buffered into the row.
    /// </summary>
    internal const int MaxErrorMessageLength = 2000;

    /// <summary>Hard cap on rows returned by a single query, enforced at the sink.</summary>
    internal const int MaxQueryLimit = 2000;

    private readonly SqliteConnection _conn;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly SqliteDatabaseWriteGate _writeLock;
    private readonly SqliteCommand _insertCmd;

    public SqliteFailureEventStore(
        string path,
        SqliteDatabaseWriteGateFactory? writeGateFactory = null)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _conn = new SqliteConnection($"Data Source={path}");
        _writeLock = SqliteDatabaseWriteGateFactory.Resolve(writeGateFactory).ForPath(path);
        _writeLock.Wait();
        try
        {
            _conn.Open();

            using (var walCmd = _conn.CreateCommand())
            {
                walCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=30000; PRAGMA foreign_keys=ON;";
                walCmd.ExecuteNonQuery();
            }

            using var createCmd = _conn.CreateCommand();
            // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- hardcoded DDL only
            createCmd.CommandText = """
                CREATE TABLE IF NOT EXISTS failure_events (
                    id              TEXT PRIMARY KEY,
                    work_item_id    TEXT NOT NULL REFERENCES work_items(id) ON DELETE CASCADE,
                    agent           TEXT,
                    phase           TEXT NOT NULL,
                    iteration       INTEGER,
                    failure_kind    TEXT,
                    error_message   TEXT,
                    sandbox_name    TEXT,
                    provider        TEXT,
                    occurred_at     TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_failure_events_occurred_at
                    ON failure_events(occurred_at);
                CREATE INDEX IF NOT EXISTS idx_failure_events_kind_occurred
                    ON failure_events(failure_kind, occurred_at);
                """;
            createCmd.ExecuteNonQuery();

            _insertCmd = _conn.CreateCommand();
            _insertCmd.CommandText = """
                INSERT INTO failure_events
                    (id, work_item_id, agent, phase, iteration, failure_kind, error_message, sandbox_name, provider, occurred_at)
                VALUES ($id, $wid, $agent, $phase, $iter, $kind, $err, $sandbox, $provider, $occurred)
                """;
            _insertCmd.Parameters.Add("$id", SqliteType.Text);
            _insertCmd.Parameters.Add("$wid", SqliteType.Text);
            _insertCmd.Parameters.Add("$agent", SqliteType.Text);
            _insertCmd.Parameters.Add("$phase", SqliteType.Text);
            _insertCmd.Parameters.Add("$iter", SqliteType.Integer);
            _insertCmd.Parameters.Add("$kind", SqliteType.Text);
            _insertCmd.Parameters.Add("$err", SqliteType.Text);
            _insertCmd.Parameters.Add("$sandbox", SqliteType.Text);
            _insertCmd.Parameters.Add("$provider", SqliteType.Text);
            _insertCmd.Parameters.Add("$occurred", SqliteType.Text);
            _insertCmd.Prepare();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task AppendAsync(FailureEventRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var error = Truncate(record.ErrorMessage, MaxErrorMessageLength);

        await _connectionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                _insertCmd.Parameters["$id"].Value = record.Id;
                _insertCmd.Parameters["$wid"].Value = record.WorkItemId.ToString();
                _insertCmd.Parameters["$agent"].Value = (object?)record.Agent ?? DBNull.Value;
                _insertCmd.Parameters["$phase"].Value = record.Phase;
                _insertCmd.Parameters["$iter"].Value = record.Iteration.HasValue
                    ? record.Iteration.Value
                    : DBNull.Value;
                _insertCmd.Parameters["$kind"].Value = (object?)record.FailureKind ?? DBNull.Value;
                _insertCmd.Parameters["$err"].Value = (object?)error ?? DBNull.Value;
                _insertCmd.Parameters["$sandbox"].Value = (object?)record.SandboxName ?? DBNull.Value;
                _insertCmd.Parameters["$provider"].Value = (object?)record.Provider ?? DBNull.Value;
                // Normalise to UTC so the stored ISO-8601 text sorts and range-filters
                // lexicographically regardless of the caller's offset.
                _insertCmd.Parameters["$occurred"].Value = record.OccurredAt.ToUniversalTime().ToString("O");
                await _insertCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task<IReadOnlyList<FailureEventRecord>> QueryAsync(
        DateTimeOffset? since,
        string? kind,
        int limit,
        CancellationToken ct = default)
    {
        var boundedLimit = Math.Clamp(limit, 1, MaxQueryLimit);

        await _connectionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cmd = _conn.CreateCommand();
            // Static, fully-parameterised SQL: null filters are neutralised by the
            // `$p IS NULL OR col = $p` guards rather than by concatenating clauses.
            cmd.CommandText = """
                SELECT id, work_item_id, agent, phase, iteration, failure_kind, error_message, sandbox_name, provider, occurred_at
                FROM failure_events
                WHERE ($since IS NULL OR occurred_at >= $since)
                  AND ($kind IS NULL OR failure_kind = $kind)
                ORDER BY occurred_at DESC, rowid DESC
                LIMIT $limit
                """;
            cmd.Parameters.AddWithValue(
                "$since",
                since.HasValue ? since.Value.ToUniversalTime().ToString("O") : DBNull.Value);
            cmd.Parameters.AddWithValue("$kind", (object?)kind ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$limit", boundedLimit);

            var results = new List<FailureEventRecord>();
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                results.Add(ReadRecord(reader));
            return results;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private static string? Truncate(string? value, int maxLength) =>
        value is { Length: > 0 } && value.Length > maxLength
            ? value[..maxLength]
            : value;

    private static FailureEventRecord ReadRecord(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        WorkItemId = WorkItemId.Parse(r.GetString(1)),
        Agent = r.IsDBNull(2) ? null : r.GetString(2),
        Phase = r.GetString(3),
        Iteration = r.IsDBNull(4) ? null : r.GetInt32(4),
        FailureKind = r.IsDBNull(5) ? null : r.GetString(5),
        ErrorMessage = r.IsDBNull(6) ? null : r.GetString(6),
        SandboxName = r.IsDBNull(7) ? null : r.GetString(7),
        Provider = r.IsDBNull(8) ? null : r.GetString(8),
        OccurredAt = DateTimeOffset.Parse(r.GetString(9), System.Globalization.CultureInfo.InvariantCulture),
    };

    public void Dispose()
    {
        _insertCmd.Dispose();
        _conn.Dispose();
        _connectionLock.Dispose();
        _writeLock.Dispose();
    }
}
