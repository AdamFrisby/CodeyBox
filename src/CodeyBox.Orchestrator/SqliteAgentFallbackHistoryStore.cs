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
    private readonly SemaphoreSlim _lock = new(1, 1);

    public SqliteAgentFallbackHistoryStore(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _conn = new SqliteConnection($"Data Source={path}");
        _conn.Open();

        using (var pragma = _conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
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
                from_model TEXT,
                to_agent TEXT,
                to_model TEXT,
                reason TEXT NOT NULL,
                occurred_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_agent_fallback_history_work_item
                ON agent_fallback_history(work_item_id, occurred_at);
            """;
        create.ExecuteNonQuery();
    }

    public async Task RecordAsync(AgentFallbackRecord record, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO agent_fallback_history
                    (id, work_item_id, phase, iteration, from_agent, from_model, to_agent, to_model, reason, occurred_at)
                VALUES
                    ($id, $wid, $phase, $iter, $fa, $fm, $ta, $tm, $reason, $at);
                """;
            cmd.Parameters.AddWithValue("$id", record.Id.ToString());
            cmd.Parameters.AddWithValue("$wid", record.WorkItemId.ToString());
            cmd.Parameters.AddWithValue("$phase", record.Phase);
            cmd.Parameters.AddWithValue("$iter", (object?)record.Iteration ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$fa", record.FromAgent.Value);
            cmd.Parameters.AddWithValue("$fm", (object?)record.FromModel ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ta", record.ToAgent is { } toa ? toa.Value : DBNull.Value);
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

    public async Task<IReadOnlyList<AgentFallbackRecord>> ListByWorkItemAsync(WorkItemId workItemId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, work_item_id, phase, iteration, from_agent, from_model, to_agent, to_model, reason, occurred_at
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
                    FromModel: reader.IsDBNull(5) ? null : reader.GetString(5),
                    ToAgent: reader.IsDBNull(6) ? null : new AgentKind(reader.GetString(6)),
                    ToModel: reader.IsDBNull(7) ? null : reader.GetString(7),
                    Reason: reader.GetString(8),
                    OccurredAt: DateTimeOffset.Parse(reader.GetString(9))));
            }
            return rows;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose() => _conn.Dispose();
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
