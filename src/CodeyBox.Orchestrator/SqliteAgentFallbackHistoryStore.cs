using Microsoft.Data.Sqlite;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// SQLite-backed implementation of <see cref="IAgentFallbackHistoryStore"/>.
/// Rows are append-only and never garbage-collected — fallback events are
/// low-volume (only fire on mid-iteration quota exhaustion) and operators
/// need them indefinitely for post-incident review.
/// </summary>
public sealed class SqliteAgentFallbackHistoryStore : IAgentFallbackHistoryStore, IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly SqliteDatabaseWriteGate _lock;

    public SqliteAgentFallbackHistoryStore(
        string path,
        SqliteDatabaseWriteGateFactory? writeGateFactory = null)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _conn = new SqliteConnection($"Data Source={path}");
        _lock = SqliteDatabaseWriteGateFactory.Resolve(writeGateFactory).ForPath(path);
        _lock.Wait();
        try
        {
            _conn.Open();

            using (var pragma = _conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=30000;";
                pragma.ExecuteNonQuery();
            }

            using var create = _conn.CreateCommand();
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS agent_fallback_history (
                    id TEXT PRIMARY KEY,
                    work_item_id TEXT NOT NULL,
                    phase TEXT NOT NULL,
                    iteration INTEGER,
                    from_agent TEXT NOT NULL,
                    from_instance_id TEXT,
                    from_model TEXT,
                    to_agent TEXT,
                    to_instance_id TEXT,
                    to_model TEXT,
                    reason TEXT NOT NULL,
                    occurred_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_agent_fallback_history_work_item
                    ON agent_fallback_history(work_item_id, occurred_at);
                """;
            create.ExecuteNonQuery();
            RunMigration("ALTER TABLE agent_fallback_history ADD COLUMN from_instance_id TEXT;");
            RunMigration("ALTER TABLE agent_fallback_history ADD COLUMN to_instance_id TEXT;");
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RecordAsync(AgentFallbackRecord record, CancellationToken ct = default)
    {
        await _connectionLock.WaitAsync(ct);
        try
        {
            await _lock.WaitAsync(ct);
            try
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO agent_fallback_history
                        (id, work_item_id, phase, iteration, from_agent, from_instance_id, from_model, to_agent, to_instance_id, to_model, reason, occurred_at)
                    VALUES
                        ($id, $wid, $phase, $iter, $fa, $fi, $fm, $ta, $ti, $tm, $reason, $at);
                    """;
                cmd.Parameters.AddWithValue("$id", record.Id.ToString());
                cmd.Parameters.AddWithValue("$wid", record.WorkItemId.ToString());
                cmd.Parameters.AddWithValue("$phase", record.Phase);
                cmd.Parameters.AddWithValue("$iter", (object?)record.Iteration ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$fa", record.FromAgent.Value);
                cmd.Parameters.AddWithValue("$fi", (object?)record.FromInstanceId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$fm", (object?)record.FromModel ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$ta", record.ToAgent is { } toa ? toa.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("$ti", (object?)record.ToInstanceId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$tm", (object?)record.ToModel ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$reason", record.Reason);
                cmd.Parameters.AddWithValue("$at", record.OccurredAt.ToUniversalTime().ToString("O"));
                await cmd.ExecuteNonQueryAsync(ct);
            }
            finally
            {
                _lock.Release();
            }
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private void RunMigration(string sql)
    {
        try
        {
            using var m = _conn.CreateCommand();
            // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- all callers pass hardcoded DDL literals
            m.CommandText = sql;
            m.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
        {
        }
    }

    public async Task<IReadOnlyList<AgentFallbackRecord>> ListByWorkItemAsync(WorkItemId workItemId, CancellationToken ct = default)
    {
        await _connectionLock.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, work_item_id, phase, iteration, from_agent, from_instance_id, from_model, to_agent, to_instance_id, to_model, reason, occurred_at
                FROM agent_fallback_history
                WHERE work_item_id = $wid
                ORDER BY occurred_at ASC;
                """;
            cmd.Parameters.AddWithValue("$wid", workItemId.ToString());

            var rows = new List<AgentFallbackRecord>();
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add(new AgentFallbackRecord(
                    Id: Guid.Parse(reader.GetString(0)),
                    WorkItemId: new WorkItemId(Guid.Parse(reader.GetString(1))),
                    Phase: reader.GetString(2),
                    Iteration: reader.IsDBNull(3) ? null : reader.GetInt32(3),
                    FromAgent: new AgentKind(reader.GetString(4)),
                    FromInstanceId: reader.IsDBNull(5) ? null : reader.GetString(5),
                    FromModel: reader.IsDBNull(6) ? null : reader.GetString(6),
                    ToAgent: reader.IsDBNull(7) ? null : new AgentKind(reader.GetString(7)),
                    ToInstanceId: reader.IsDBNull(8) ? null : reader.GetString(8),
                    ToModel: reader.IsDBNull(9) ? null : reader.GetString(9),
                    Reason: reader.GetString(10),
                    OccurredAt: DateTimeOffset.Parse(reader.GetString(11))));
            }
            return rows;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public void Dispose()
    {
        _conn.Dispose();
        _connectionLock.Dispose();
        _lock.Dispose();
    }
}

/// <summary>
/// No-op implementation used when fallback-history persistence is not wired
/// (tests, embedded-only deployments). Recording is silently dropped; lists
/// return empty.
/// </summary>
public sealed class NullAgentFallbackHistoryStore : IAgentFallbackHistoryStore
{
    public Task RecordAsync(AgentFallbackRecord record, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<AgentFallbackRecord>> ListByWorkItemAsync(WorkItemId workItemId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AgentFallbackRecord>>([]);
}

/// <summary>
/// In-memory implementation used by tests so they can assert that the
/// expected fallback events were recorded without hitting SQLite.
/// </summary>
public sealed class InMemoryAgentFallbackHistoryStore : IAgentFallbackHistoryStore
{
    private readonly List<AgentFallbackRecord> _records = new();
    private readonly object _gate = new();

    public Task RecordAsync(AgentFallbackRecord record, CancellationToken ct = default)
    {
        lock (_gate) _records.Add(record);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AgentFallbackRecord>> ListByWorkItemAsync(WorkItemId workItemId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<AgentFallbackRecord> snapshot = _records
                .Where(r => r.WorkItemId == workItemId)
                .OrderBy(r => r.OccurredAt)
                .ToList();
            return Task.FromResult(snapshot);
        }
    }
}
