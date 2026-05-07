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

        // WAL mode allows concurrent readers + SqliteQueueController writes without SQLITE_BUSY.
        // busy_timeout is per-connection and provides a retry window for the rare lock collision.
        // foreign_keys enables ON DELETE CASCADE from work_items → work_item_timings.
        using (var walCmd = _conn.CreateCommand())
        {
            walCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
            walCmd.ExecuteNonQuery();
        }

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

        // Additive migration: track automatic re-queues from stuck-agent detection.
        RunMigration("ALTER TABLE work_items ADD COLUMN stuck_retries INTEGER NOT NULL DEFAULT 0;");

        // Additive migration: record when an item was first picked up by a worker.
        // Used for per-project hourly/daily budget cap queries.
        RunMigration("ALTER TABLE work_items ADD COLUMN started_at TEXT;");

        // Index for cheap per-project budget window queries.
        RunMigration("CREATE INDEX IF NOT EXISTS idx_work_items_project_started ON work_items(project_id, started_at);");

        // Additive migration: caller-supplied external identifier, unique per project.
        RunMigration("ALTER TABLE work_items ADD COLUMN external_id TEXT;");

        // Partial unique index: enforces per-project uniqueness while allowing NULL coexistence.
        RunMigration("CREATE UNIQUE INDEX IF NOT EXISTS idx_work_items_external_id_per_project ON work_items(project_id, external_id) WHERE external_id IS NOT NULL;");

        // Additive migration: link a replay to its source work item.
        // Cleared (set to NULL) when the source is cancelled so replays keep running.
        RunMigration("ALTER TABLE work_items ADD COLUMN replay_of_work_item_id TEXT;");

        // Index for cheap replay-listing queries.
        RunMigration("CREATE INDEX IF NOT EXISTS idx_work_items_replay_of ON work_items(replay_of_work_item_id) WHERE replay_of_work_item_id IS NOT NULL;");

        // Additive migration: store the SHA of the merge commit produced during the merge phase.
        RunMigration("ALTER TABLE work_items ADD COLUMN merge_sha TEXT;");
        // Additive migration: minimum quality-score floor for routing.
        // Default 95 preserves existing semantics (frontier-adjacent fallback allowed).
        RunMigration("ALTER TABLE work_items ADD COLUMN min_model_score INTEGER NOT NULL DEFAULT 95;");

        // Composite indexes for fleet summary aggregation queries.
        RunMigration("CREATE INDEX IF NOT EXISTS idx_work_items_project_state ON work_items(project_id, state);");
        RunMigration("CREATE INDEX IF NOT EXISTS idx_work_items_project_updated ON work_items(project_id, updated_at);");
        // Additive migration: why the item was cancelled (OperatorRequested, ParentCascaded, HostShutdown).
        // NULL means legacy row or non-cancelled item.
        RunMigration("ALTER TABLE work_items ADD COLUMN cancellation_reason TEXT;");

        // Additive migration: how many times the recovery loop / dead-worker reaper
        // has reset this item. Default 0 = never recovered. Capped at MaxRecoveryAttempts.
        RunMigration("ALTER TABLE work_items ADD COLUMN recovery_attempts INTEGER NOT NULL DEFAULT 0;");

        // Additive migration: link work items to a release. NULL = legacy / merge-to-main behaviour.
        RunMigration("ALTER TABLE work_items ADD COLUMN release_id TEXT;");

        // Index for release state machine queries (all items for a release).
        RunMigration("CREATE INDEX IF NOT EXISTS idx_work_items_release ON work_items(release_id) WHERE release_id IS NOT NULL;");

        // Additive migration: graceful-shutdown preemption metadata. Nullable so
        // existing rows are treated as not preempted.
        RunMigration("ALTER TABLE work_items ADD COLUMN preempted_at TEXT;");
        RunMigration("ALTER TABLE work_items ADD COLUMN preempt_checkpoint TEXT;");
    }

    private void RunMigration(string sql)
    {
        try
        {
            using var m = _conn.CreateCommand();
            // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- all callers pass hardcoded DDL literals; no user-supplied input reaches this method
            m.CommandText = sql;
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
                    last_error, upstream_push_attempts, depends_on_json, agent_class_id, queue_position,
                    stuck_retries, started_at, external_id, replay_of_work_item_id, merge_sha,
                    min_model_score, cancellation_reason, recovery_attempts, release_id, preempted_at, preempt_checkpoint)
                VALUES ($id, $project_id, $title, $prompt, $base, $work, $agent, $wt, $mt, $pu, $state, $ca, $ua, $err, $att, $deps, $class_id, $qpos,
                    $sretries, $started_at, $external_id, $replay_of, $merge_sha,
                    $min_model_score, $cancellation_reason, $recovery_attempts, $release_id, $preempted_at, $preempt_checkpoint);
                """;
            Bind(cmd, item);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (SqliteException sqlex) when (sqlex.SqliteExtendedErrorCode == 2067) // SQLITE_CONSTRAINT_UNIQUE
        {
            // A concurrent request snuck past the application-level pre-check and
            // hit the UNIQUE index on (project_id, external_id).
            throw new WorkItemExternalIdConflictException();
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
                    agent_class_id = $class_id, queue_position = $qpos,
                    stuck_retries = $sretries, started_at = $started_at, external_id = $external_id,
                    replay_of_work_item_id = $replay_of, merge_sha = $merge_sha,
                    min_model_score = $min_model_score,
                    cancellation_reason = $cancellation_reason,
                    recovery_attempts = $recovery_attempts,
                    release_id = $release_id,
                    preempted_at = $preempted_at,
                    preempt_checkpoint = $preempt_checkpoint
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
                    agent_class_id = $class_id, queue_position = $qpos,
                    stuck_retries = $sretries, started_at = $started_at, external_id = $external_id,
                    replay_of_work_item_id = $replay_of, merge_sha = $merge_sha,
                    min_model_score = $min_model_score,
                    cancellation_reason = $cancellation_reason,
                    recovery_attempts = $recovery_attempts,
                    release_id = $release_id,
                    preempted_at = $preempted_at,
                    preempt_checkpoint = $preempt_checkpoint
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

    public async Task<int> CountStartedInWindowAsync(ProjectId projectId, DateTimeOffset since, CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM work_items
            WHERE project_id = $pid
              AND started_at IS NOT NULL
              AND started_at >= $since;
            """;
        cmd.Parameters.AddWithValue("$pid", projectId.Value);
        cmd.Parameters.AddWithValue("$since", since.ToString("O"));
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long l ? (int)l : 0;
    }

    public async Task<int> CountInFlightAsync(ProjectId projectId, CancellationToken ct = default)
    {
        // Use started_at IS NOT NULL as the concurrent proxy instead of state.
        // State transitions from Queued→Working happen *outside* the per-project
        // budget lock, so a worker that just wrote StartedAt (inside the lock) still
        // appears as state=Queued to the next worker's state-based query. Querying
        // on started_at IS NOT NULL makes the write inside the lock immediately
        // visible, preventing the concurrent cap from being exceeded.
        // Terminal states excluded; use cast enum values so renumbering is caught at compile time.
        using var cmd = _conn.CreateCommand();
        // NeedsOperatorInput items are parked (pipeline not running); exclude them so they
        // don't consume a concurrent slot while operators are offline for hours/days.
        cmd.CommandText = $"""
            SELECT COUNT(*) FROM work_items
            WHERE project_id = $pid
              AND started_at IS NOT NULL
              AND preempt_checkpoint IS NULL
              AND state NOT IN ({(int)WorkItemState.Done}, {(int)WorkItemState.Failed}, {(int)WorkItemState.Cancelled}, {(int)WorkItemState.AuditFailed}, {(int)WorkItemState.NeedsOperatorInput}, {(int)WorkItemState.AbandonedAfterRecoveryAttempts});
            """;
        cmd.Parameters.AddWithValue("$pid", projectId.Value);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long l ? (int)l : 0;
    }

    public async Task<WorkItem?> GetByExternalIdAsync(ProjectId projectId, string externalId, CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM work_items WHERE project_id = $pid AND external_id = $eid;";
        cmd.Parameters.AddWithValue("$pid", projectId.Value);
        cmd.Parameters.AddWithValue("$eid", externalId);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<(string ProjectId, int State, int Count, string MaxUpdatedAt)>> GetFleetStateCountsAsync(CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT project_id, state, COUNT(*) AS cnt, MAX(updated_at) AS max_updated_at
            FROM work_items
            GROUP BY project_id, state;
            """;
        using var reader = await cmd.ExecuteReaderAsync(ct);
        var results = new List<(string, int, int, string)>();
        while (await reader.ReadAsync(ct))
            results.Add((reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetString(3)));
        return results;
    }

    public async Task<IReadOnlyList<(string ProjectId, int State)>> GetFleetRecentOutcomesAsync(int perProject = 5, CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"""
            WITH ranked AS (
                SELECT project_id, state,
                       ROW_NUMBER() OVER (PARTITION BY project_id ORDER BY updated_at DESC) AS rn
                FROM work_items
                WHERE state IN ({(int)WorkItemState.Done}, {(int)WorkItemState.Failed}, {(int)WorkItemState.AuditFailed}, {(int)WorkItemState.Cancelled})
            )
            SELECT project_id, state FROM ranked WHERE rn <= $per_project
            ORDER BY project_id, rn;
            """;
        cmd.Parameters.AddWithValue("$per_project", perProject);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        var results = new List<(string, int)>();
        while (await reader.ReadAsync(ct))
            results.Add((reader.GetString(0), reader.GetInt32(1)));
        return results;
    }

    public async Task<IReadOnlyDictionary<string, bool>> GetFleetPauseStatesAsync(CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT project_id, is_paused FROM project_queue_state";
        try
        {
            using var reader = await cmd.ExecuteReaderAsync(ct);
            var results = new Dictionary<string, bool>();
            while (await reader.ReadAsync(ct))
                results[reader.GetString(0)] = reader.GetInt32(1) != 0;
            return results;
        }
        catch (SqliteException ex) when (ex.Message.Contains("no such table"))
        {
            return new Dictionary<string, bool>();
        }
    }

    public async IAsyncEnumerable<WorkItem> ListByReplaySourceAsync(
        WorkItemId sourceId, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM work_items
            WHERE replay_of_work_item_id = $source_id
            ORDER BY created_at ASC;
            """;
        cmd.Parameters.AddWithValue("$source_id", sourceId.ToString());
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            yield return Read(reader);
    }

    public async Task OrphanReplaysAsync(WorkItemId sourceId, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE work_items
                SET replay_of_work_item_id = NULL,
                    updated_at = $now
                WHERE replay_of_work_item_id = $source_id;
                """;
            cmd.Parameters.AddWithValue("$source_id", sourceId.ToString());
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async IAsyncEnumerable<WorkItem> ListByReleaseAsync(
        CodeyBox.Core.ReleaseId releaseId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM work_items WHERE release_id = $rid ORDER BY created_at;";
        cmd.Parameters.AddWithValue("$rid", releaseId.ToString());
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
        cmd.Parameters.AddWithValue("$deps",
            JsonSerializer.Serialize(item.DependsOn.Select(id => id.ToString()).ToList()));
        cmd.Parameters.AddWithValue("$class_id", (object?)item.AgentClassId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$qpos", item.QueuePosition);
        cmd.Parameters.AddWithValue("$sretries", item.StuckRetries);
        cmd.Parameters.AddWithValue("$started_at", (object?)item.StartedAt?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$external_id", (object?)item.ExternalId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$replay_of", (object?)item.ReplayOfWorkItemId?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$merge_sha", (object?)item.MergeSha ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$min_model_score", item.MinModelScore);
        cmd.Parameters.AddWithValue("$cancellation_reason",
            item.CancellationReason.HasValue ? (object)item.CancellationReason.Value.ToString() : DBNull.Value);
        cmd.Parameters.AddWithValue("$recovery_attempts", item.RecoveryAttempts);
        cmd.Parameters.AddWithValue("$release_id", (object?)item.ReleaseId?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$preempted_at", (object?)item.PreemptedAt?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$preempt_checkpoint", (object?)item.PreemptCheckpoint ?? DBNull.Value);
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
        StuckRetries = r.GetInt32(r.GetOrdinal("stuck_retries")),
        StartedAt = ReadNullableDateTimeOffset(r, "started_at"),
        ExternalId = r.IsDBNull(r.GetOrdinal("external_id")) ? null : r.GetString(r.GetOrdinal("external_id")),
        ReplayOfWorkItemId = ReadNullableWorkItemId(r, "replay_of_work_item_id"),
        MergeSha = r.IsDBNull(r.GetOrdinal("merge_sha")) ? null : r.GetString(r.GetOrdinal("merge_sha")),
        MinModelScore = ReadInt32OrDefault(r, "min_model_score", defaultValue: 95),
        CancellationReason = ReadCancellationReason(r),
        RecoveryAttempts = ReadInt32OrDefault(r, "recovery_attempts", defaultValue: 0),
        ReleaseId = ReadNullableReleaseId(r, "release_id"),
        PreemptedAt = ReadNullableDateTimeOffset(r, "preempted_at"),
        PreemptCheckpoint = r.IsDBNull(r.GetOrdinal("preempt_checkpoint")) ? null : r.GetString(r.GetOrdinal("preempt_checkpoint")),
    };

    private static WorkItemCancellationReason? ReadCancellationReason(SqliteDataReader r)
    {
        var ord = r.GetOrdinal("cancellation_reason");
        if (r.IsDBNull(ord)) return null;
        var raw = r.GetString(ord);
        return Enum.TryParse<WorkItemCancellationReason>(raw, out var val) ? val : null;
    }

    private static int ReadInt32OrDefault(SqliteDataReader r, string column, int defaultValue)
    {
        var ord = r.GetOrdinal(column);
        return r.IsDBNull(ord) ? defaultValue : r.GetInt32(ord);
    }

    private static DateTimeOffset? ReadNullableDateTimeOffset(SqliteDataReader r, string column)
    {
        var ord = r.GetOrdinal(column);
        return r.IsDBNull(ord)
            ? null
            : DateTimeOffset.Parse(r.GetString(ord), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static WorkItemId? ReadNullableWorkItemId(SqliteDataReader r, string column)
    {
        var ord = r.GetOrdinal(column);
        if (r.IsDBNull(ord)) return null;
        var raw = r.GetString(ord);
        return Guid.TryParse(raw, out var g) ? new WorkItemId(g) : null;
    }

    private static CodeyBox.Core.ReleaseId? ReadNullableReleaseId(SqliteDataReader r, string col)
    {
        var ord = r.GetOrdinal(col);
        if (r.IsDBNull(ord)) return null;
        return CodeyBox.Core.ReleaseId.TryParse(r.GetString(ord), out var rid) ? rid : null;
    }

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
