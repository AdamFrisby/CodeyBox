using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// SQLite-backed work item store. Durable across orchestrator restarts.
/// Schema is created on first use; intentionally minimal — most fields are
/// stored as columns so the orchestrator can query by state at startup.
/// </summary>
public sealed class SqliteWorkItemStore : IWorkItemStore, IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public SqliteWorkItemStore(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _conn = new SqliteConnection($"Data Source={path}");
        _conn.Open();

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS work_items (
                id TEXT PRIMARY KEY,
                project_id TEXT NOT NULL,
                title TEXT NOT NULL,
                prompt TEXT NOT NULL,
                base_branch TEXT,
                work_branch TEXT,
                agent TEXT,
                work_timeout_ticks INTEGER NOT NULL,
                merge_timeout_ticks INTEGER NOT NULL,
                push_upstream INTEGER NOT NULL,
                state INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                last_error TEXT,
                upstream_push_attempts INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_work_items_state ON work_items(state);
            CREATE INDEX IF NOT EXISTS idx_work_items_project ON work_items(project_id);
            """;
        cmd.ExecuteNonQuery();

        // Additive migration: add depends_on_json column if it doesn't exist yet.
        // Existing rows get the default '[]' so behaviour is unchanged.
        RunMigration("ALTER TABLE work_items ADD COLUMN depends_on_json TEXT NOT NULL DEFAULT '[]';");

        // Additive migration: add agent_class_id column for quota-aware routing.
        RunMigration("ALTER TABLE work_items ADD COLUMN agent_class_id TEXT;");

        // Additive migration: add queue_position for admin-dashboard reorder support.
        // Default 0 = "no explicit position" → store treats as sort-last (behind timestamp-based positions).
        RunMigration("ALTER TABLE work_items ADD COLUMN queue_position INTEGER NOT NULL DEFAULT 0;");
    }

    private void RunMigration(string sql)
    {
        try
        {
            using var m = _conn.CreateCommand();
            m.CommandText = sql; // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli
            m.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
        {
            // Column already exists from a previous startup — nothing to do.
        }
    }

    public async Task CreateAsync(WorkItem item, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO work_items (id, project_id, title, prompt, base_branch, work_branch, agent,
                    work_timeout_ticks, merge_timeout_ticks, push_upstream, state, created_at, updated_at,
                    last_error, upstream_push_attempts, depends_on_json, agent_class_id, queue_position)
                VALUES ($id, $project_id, $title, $prompt, $base, $work, $agent, $wt, $mt, $pu, $state, $ca, $ua, $err, $att, $deps, $class_id, $qpos);
                """;
            Bind(cmd, item);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task UpdateAsync(WorkItem item, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE work_items SET
                    project_id = $project_id, title = $title, prompt = $prompt,
                    base_branch = $base, work_branch = $work, agent = $agent,
                    work_timeout_ticks = $wt, merge_timeout_ticks = $mt, push_upstream = $pu,
                    state = $state, updated_at = $ua, last_error = $err,
                    upstream_push_attempts = $att, depends_on_json = $deps,
                    agent_class_id = $class_id, queue_position = $qpos
                WHERE id = $id;
                """;
            Bind(cmd, item);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> TryUpdateIfStateAsync(WorkItem item, WorkItemState onlyIfState, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE work_items SET
                    project_id = $project_id, title = $title, prompt = $prompt,
                    base_branch = $base, work_branch = $work, agent = $agent,
                    work_timeout_ticks = $wt, merge_timeout_ticks = $mt, push_upstream = $pu,
                    state = $state, updated_at = $ua, last_error = $err,
                    upstream_push_attempts = $att, depends_on_json = $deps,
                    agent_class_id = $class_id, queue_position = $qpos
                WHERE id = $id AND state = $only_if_state;
                """;
            Bind(cmd, item);
            cmd.Parameters.AddWithValue("$only_if_state", (int)onlyIfState);
            return await cmd.ExecuteNonQueryAsync(ct) > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<WorkItem?> GetAsync(WorkItemId id, CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM work_items WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    public async IAsyncEnumerable<WorkItem> ListAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM work_items ORDER BY created_at DESC;";
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            yield return Read(reader);
    }

    public async IAsyncEnumerable<WorkItem> ListByStateAsync(WorkItemState state, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        // Queued items: honour explicit queue_position (1, 2, 3 …) first; items with
        // queue_position = 0 (pre-migration or newly created without explicit pos) sort
        // last via the CASE sentinel, then by creation time for stable tie-breaking.
        // Other states: simple creation-time ordering.
        if (state == WorkItemState.Queued)
        {
            cmd.CommandText = """
                SELECT * FROM work_items WHERE state = $state
                ORDER BY
                    CASE WHEN queue_position > 0 THEN queue_position ELSE 9223372036854775807 END ASC,
                    created_at ASC;
                """;
        }
        else
        {
            cmd.CommandText = "SELECT * FROM work_items WHERE state = $state ORDER BY created_at;";
        }
        cmd.Parameters.AddWithValue("$state", (int)state);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            yield return Read(reader);
    }

    public async Task ReorderAsync(IReadOnlyList<WorkItemId> orderedIds, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var tx = _conn.BeginTransaction();
            for (var i = 0; i < orderedIds.Count; i++)
            {
                using var cmd = _conn.CreateCommand();
                cmd.Transaction = tx;
                // Only update rows still in Queued state. Items that raced to a different
                // state between the validation and the write are silently skipped.
                cmd.CommandText = """
                    UPDATE work_items SET queue_position = $pos
                    WHERE id = $id AND state = $queued;
                    """;
                cmd.Parameters.AddWithValue("$pos", (long)(i + 1));
                cmd.Parameters.AddWithValue("$id", orderedIds[i].ToString());
                cmd.Parameters.AddWithValue("$queued", (int)WorkItemState.Queued);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            tx.Commit();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Dispose()
    {
        _conn.Dispose();
        _writeLock.Dispose();
    }

    private static void Bind(SqliteCommand cmd, WorkItem item)
    {
        cmd.Parameters.AddWithValue("$id", item.Id.ToString());
        cmd.Parameters.AddWithValue("$project_id", item.ProjectId.Value);
        cmd.Parameters.AddWithValue("$title", item.Title);
        cmd.Parameters.AddWithValue("$prompt", item.Prompt);
        cmd.Parameters.AddWithValue("$base", (object?)item.BaseBranch ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$work", (object?)item.WorkBranch ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$agent", (object?)item.Agent?.Value ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$wt", item.WorkTimeout.Ticks);
        cmd.Parameters.AddWithValue("$mt", item.MergeTimeout.Ticks);
        cmd.Parameters.AddWithValue("$pu", item.PushUpstream ? 1 : 0);
        cmd.Parameters.AddWithValue("$state", (int)item.State);
        cmd.Parameters.AddWithValue("$ca", item.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$ua", item.UpdatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$err", (object?)item.LastError ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$att", item.UpstreamPushAttempts);
        cmd.Parameters.AddWithValue("$deps",
            JsonSerializer.Serialize(item.DependsOn.Select(id => id.ToString()).ToList()));
        cmd.Parameters.AddWithValue("$class_id", (object?)item.AgentClassId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$qpos", item.QueuePosition);
    }

    private static WorkItem Read(SqliteDataReader r) => new()
    {
        Id = WorkItemId.Parse(r.GetString(r.GetOrdinal("id"))),
        ProjectId = new ProjectId(r.GetString(r.GetOrdinal("project_id"))),
        Title = r.GetString(r.GetOrdinal("title")),
        Prompt = r.GetString(r.GetOrdinal("prompt")),
        BaseBranch = r.IsDBNull(r.GetOrdinal("base_branch")) ? null : r.GetString(r.GetOrdinal("base_branch")),
        WorkBranch = r.IsDBNull(r.GetOrdinal("work_branch")) ? null : r.GetString(r.GetOrdinal("work_branch")),
        Agent = r.IsDBNull(r.GetOrdinal("agent")) ? null : new AgentKind(r.GetString(r.GetOrdinal("agent"))),
        WorkTimeout = new TimeSpan(r.GetInt64(r.GetOrdinal("work_timeout_ticks"))),
        MergeTimeout = new TimeSpan(r.GetInt64(r.GetOrdinal("merge_timeout_ticks"))),
        PushUpstream = r.GetInt32(r.GetOrdinal("push_upstream")) != 0,
        State = (WorkItemState)r.GetInt32(r.GetOrdinal("state")),
        CreatedAt = DateTimeOffset.Parse(r.GetString(r.GetOrdinal("created_at")), System.Globalization.CultureInfo.InvariantCulture),
        UpdatedAt = DateTimeOffset.Parse(r.GetString(r.GetOrdinal("updated_at")), System.Globalization.CultureInfo.InvariantCulture),
        LastError = r.IsDBNull(r.GetOrdinal("last_error")) ? null : r.GetString(r.GetOrdinal("last_error")),
        UpstreamPushAttempts = r.GetInt32(r.GetOrdinal("upstream_push_attempts")),
        DependsOn = ReadDependsOn(r),
        AgentClassId = r.IsDBNull(r.GetOrdinal("agent_class_id")) ? null : r.GetString(r.GetOrdinal("agent_class_id")),
        QueuePosition = r.GetInt64(r.GetOrdinal("queue_position")),
    };

    private static IReadOnlyList<WorkItemId> ReadDependsOn(SqliteDataReader r)
    {
        var ordinal = r.GetOrdinal("depends_on_json");
        if (r.IsDBNull(ordinal)) return [];
        var json = r.GetString(ordinal);
        var ids = JsonSerializer.Deserialize<string[]>(json);
        if (ids is null || ids.Length == 0) return [];
        // Guard against malformed GUIDs from DB corruption: skip bad entries
        // rather than crashing all callers (including ReplayPendingAsync at boot).
        var result = new List<WorkItemId>(ids.Length);
        foreach (var raw in ids)
        {
            if (Guid.TryParse(raw, out var g))
                result.Add(new WorkItemId(g));
        }
        return result;
    }
}
