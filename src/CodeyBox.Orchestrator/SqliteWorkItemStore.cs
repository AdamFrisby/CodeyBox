using System.Runtime.CompilerServices;
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
                    last_error, upstream_push_attempts)
                VALUES ($id, $project_id, $title, $prompt, $base, $work, $agent, $wt, $mt, $pu, $state, $ca, $ua, $err, $att);
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
                    upstream_push_attempts = $att
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
        cmd.CommandText = "SELECT * FROM work_items WHERE state = $state ORDER BY created_at;";
        cmd.Parameters.AddWithValue("$state", (int)state);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            yield return Read(reader);
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
    };
}
