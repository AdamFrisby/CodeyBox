using Microsoft.Data.Sqlite;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// SQLite-backed implementation of <see cref="IAgentInvolvementStore"/>. Rows
/// are append-only (plus a one-time completion stamp); they are never deleted —
/// the per-phase agent trail is low-volume and operators need it indefinitely
/// for post-incident review and cost attribution.
/// </summary>
public sealed class SqliteAgentInvolvementStore : IAgentInvolvementStore, IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public SqliteAgentInvolvementStore(string path)
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
            CREATE TABLE IF NOT EXISTS agent_involvement (
                id TEXT PRIMARY KEY,
                work_item_id TEXT NOT NULL,
                agent_kind TEXT NOT NULL,
                model_id TEXT,
                phase TEXT NOT NULL,
                started_at TEXT NOT NULL,
                ended_at TEXT,
                iteration INTEGER,
                outcome TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_agent_involvement_work_item
                ON agent_involvement(work_item_id, started_at);
            """;
        create.ExecuteNonQuery();
    }

    public async Task RecordStartAsync(AgentInvolvement entry, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO agent_involvement
                    (id, work_item_id, agent_kind, model_id, phase, started_at, ended_at, iteration, outcome)
                VALUES
                    ($id, $wid, $agent, $model, $phase, $started, $ended, $iter, $outcome);
                """;
            cmd.Parameters.AddWithValue("$id", entry.Id.ToString());
            cmd.Parameters.AddWithValue("$wid", entry.WorkItemId.ToString());
            cmd.Parameters.AddWithValue("$agent", entry.AgentKind.Value);
            cmd.Parameters.AddWithValue("$model", (object?)entry.ModelId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$phase", entry.Phase);
            cmd.Parameters.AddWithValue("$started", entry.StartedAt.ToUniversalTime().ToString("O"));
            cmd.Parameters.AddWithValue("$ended", entry.EndedAt is { } e ? e.ToUniversalTime().ToString("O") : DBNull.Value);
            cmd.Parameters.AddWithValue("$iter", (object?)entry.Iteration ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$outcome", (object?)entry.Outcome ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task FinalizeAsync(Guid id, DateTimeOffset endedAt, string outcome, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            // Only stamp completion once: the WHERE ended_at IS NULL guard keeps
            // a redundant or racing finalize from rewriting an already-closed
            // entry, preserving the immutable-identity invariant.
            cmd.CommandText = """
                UPDATE agent_involvement
                SET ended_at = $ended, outcome = $outcome
                WHERE id = $id AND ended_at IS NULL;
                """;
            cmd.Parameters.AddWithValue("$id", id.ToString());
            cmd.Parameters.AddWithValue("$ended", endedAt.ToUniversalTime().ToString("O"));
            cmd.Parameters.AddWithValue("$outcome", outcome);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<AgentInvolvement>> ListByWorkItemAsync(WorkItemId workItemId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, work_item_id, agent_kind, model_id, phase, started_at, ended_at, iteration, outcome
                FROM agent_involvement
                WHERE work_item_id = $wid
                ORDER BY started_at ASC, rowid ASC;
                """;
            cmd.Parameters.AddWithValue("$wid", workItemId.ToString());

            var rows = new List<AgentInvolvement>();
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add(new AgentInvolvement(
                    Id: Guid.Parse(reader.GetString(0)),
                    WorkItemId: new WorkItemId(Guid.Parse(reader.GetString(1))),
                    AgentKind: new AgentKind(reader.GetString(2)),
                    ModelId: reader.IsDBNull(3) ? null : reader.GetString(3),
                    Phase: reader.GetString(4),
                    StartedAt: DateTimeOffset.Parse(reader.GetString(5)),
                    EndedAt: reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6)),
                    Iteration: reader.IsDBNull(7) ? null : reader.GetInt32(7),
                    Outcome: reader.IsDBNull(8) ? null : reader.GetString(8)));
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
/// No-op implementation used when involvement persistence is not wired (tests,
/// embedded-only deployments). Recording is silently dropped; lists return empty.
/// </summary>
public sealed class NullAgentInvolvementStore : IAgentInvolvementStore
{
    public Task RecordStartAsync(AgentInvolvement entry, CancellationToken ct = default) => Task.CompletedTask;
    public Task FinalizeAsync(Guid id, DateTimeOffset endedAt, string outcome, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<AgentInvolvement>> ListByWorkItemAsync(WorkItemId workItemId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AgentInvolvement>>([]);
}

/// <summary>
/// In-memory implementation used by tests so they can assert the expected
/// involvement trail was recorded without hitting SQLite.
/// </summary>
public sealed class InMemoryAgentInvolvementStore : IAgentInvolvementStore
{
    private readonly List<AgentInvolvement> _records = new();
    private readonly object _gate = new();

    public Task RecordStartAsync(AgentInvolvement entry, CancellationToken ct = default)
    {
        lock (_gate) _records.Add(entry);
        return Task.CompletedTask;
    }

    public Task FinalizeAsync(Guid id, DateTimeOffset endedAt, string outcome, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var idx = _records.FindIndex(r => r.Id == id);
            // Only stamp once; mirrors the SQLite "ended_at IS NULL" guard.
            if (idx >= 0 && _records[idx].EndedAt is null)
                _records[idx] = _records[idx] with { EndedAt = endedAt, Outcome = outcome };
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AgentInvolvement>> ListByWorkItemAsync(WorkItemId workItemId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<AgentInvolvement> snapshot = _records
                .Where(r => r.WorkItemId == workItemId)
                .OrderBy(r => r.StartedAt)
                .ToList();
            return Task.FromResult(snapshot);
        }
    }
}
