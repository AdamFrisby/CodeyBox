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

        // Additive migration: capture failure details for auto-retry logic.
        RunMigration("ALTER TABLE work_items ADD COLUMN failure_kind TEXT;");
        RunMigration("ALTER TABLE work_items ADD COLUMN quota_reset_at TEXT;");
        RunMigration("ALTER TABLE work_items ADD COLUMN next_quota_retry_at TEXT;");
        RunMigration("ALTER TABLE work_items ADD COLUMN quota_retry_attempts INTEGER NOT NULL DEFAULT 0;");
        RunMigration("ALTER TABLE work_items ADD COLUMN quota_retry_from TEXT;");

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

        // Additive migration: VM-suspend recovery metadata (R8-core). Records the
        // name of the suspended multipass VM that holds this item's in-progress
        // sandbox state across an orchestrator restart. Nullable: only set
        // between the suspend-on-shutdown handler and the startup resume
        // handler. The leak reaper skips VMs named here so the suspended VM is
        // not auto-disposed during the restart window.
        RunMigration("ALTER TABLE work_items ADD COLUMN suspended_vm_name TEXT;");
        RunMigration("ALTER TABLE work_items ADD COLUMN suspended_at TEXT;");
        RunMigration("CREATE INDEX IF NOT EXISTS idx_work_items_suspended_vm ON work_items(suspended_vm_name) WHERE suspended_vm_name IS NOT NULL;");

        // Additive migration: optional per-work-item audit profile override.
        // NULL means use the project's default audit profile.
        RunMigration("ALTER TABLE work_items ADD COLUMN auditor_profile TEXT;");

        // Additive migration: dispatch priority. Default 0 preserves FIFO behaviour
        // for existing rows. Higher values pick up first; ties break by created_at ASC.
        RunMigration("ALTER TABLE work_items ADD COLUMN priority INTEGER NOT NULL DEFAULT 0;");

        // Index for the priority-aware pickup query: state filter first, then priority,
        // then created_at. Speeds up the dispatch loop's per-tick "next eligible item" lookup.
        RunMigration("CREATE INDEX IF NOT EXISTS idx_work_items_state_priority ON work_items(state, priority DESC, created_at ASC);");

        // Additive migration: capture the first contributor that cancelled a
        // pipeline phase so the operator can distinguish a configured timeout
        // ("timeout:work") from a transient host-side cancellation ("unknown").
        // NULL means legacy row or never-cancelled item.
        RunMigration("ALTER TABLE work_items ADD COLUMN cancellation_source TEXT;");

        // Additive migration: counts auto-retries triggered by transient
        // (unattributed) host-side cancellations. Default 0 keeps existing rows
        // eligible for the auto-retry path on their next transient failure.
        RunMigration("ALTER TABLE work_items ADD COLUMN transient_cancel_retries INTEGER NOT NULL DEFAULT 0;");

        // Additive migration: monotonic prompt-generation counter. Default 1 so
        // legacy rows behave as "never edited"; the PUT /workitems/{id}/prompt
        // endpoint increments this on every successful write.
        RunMigration("ALTER TABLE work_items ADD COLUMN prompt_revision INTEGER NOT NULL DEFAULT 1;");

        // Additive migration: counts conflict-rework iterations executed on
        // this work item. Capped at 1 per merge attempt; the pipeline parks at
        // MergeConflictResolutionFailed past the cap rather than re-engaging
        // the original work agent a second time.
        RunMigration("ALTER TABLE work_items ADD COLUMN conflict_rework_attempts INTEGER NOT NULL DEFAULT 0;");

        // Per-iteration dispatch record. One row per (work_item_id, iteration);
        // most-recent-dispatch-wins — a re-dispatch (e.g. orchestrator
        // restart-recovery for the same iteration) overwrites the row via
        // ON CONFLICT DO UPDATE in RecordIterationDispatchAsync. The work
        // item's current prompt_revision is snapshotted into
        // prompt_revision_at_dispatch so a prompt edit landing mid-iteration
        // cannot be misattributed to the already-running iteration. Cascade-
        // delete with the parent work item.
        using (var iterCmd = _conn.CreateCommand())
        {
            iterCmd.CommandText = """
                CREATE TABLE IF NOT EXISTS work_item_iterations (
                    work_item_id TEXT NOT NULL REFERENCES work_items(id) ON DELETE CASCADE,
                    iteration INTEGER NOT NULL,
                    prompt_revision_at_dispatch INTEGER NOT NULL,
                    dispatched_at TEXT NOT NULL,
                    PRIMARY KEY (work_item_id, iteration)
                );
                """;
            iterCmd.ExecuteNonQuery();
        }

        // Namespaced external-ID side table. Each work item may carry an ID per
        // namespace (jobtrack, github, linear, …). The legacy single-value
        // external_id column on work_items is preserved as a denormalised
        // projection of the 'legacy' namespace for the deprecation window;
        // back-fill happens once on the migration below. Cascade-delete with
        // the parent work item so cancelled/deleted items don't leak rows.
        using (var extCmd = _conn.CreateCommand())
        {
            extCmd.CommandText = """
                CREATE TABLE IF NOT EXISTS work_item_external_ids (
                    work_item_id TEXT NOT NULL REFERENCES work_items(id) ON DELETE CASCADE,
                    project_id   TEXT NOT NULL,
                    namespace    TEXT NOT NULL,
                    external_id  TEXT NOT NULL,
                    PRIMARY KEY (work_item_id, namespace)
                );
                CREATE UNIQUE INDEX IF NOT EXISTS idx_work_item_external_ids_per_project
                    ON work_item_external_ids(project_id, namespace, external_id);
                CREATE INDEX IF NOT EXISTS idx_work_item_external_ids_lookup
                    ON work_item_external_ids(project_id, external_id);
                """;
            extCmd.ExecuteNonQuery();
        }

        // One-shot back-fill: copy any non-null work_items.external_id into the
        // side table under namespace 'legacy'. INSERT OR IGNORE so re-running
        // the migration is a no-op (rows already populated take precedence).
        using (var backfill = _conn.CreateCommand())
        {
            backfill.CommandText = """
                INSERT OR IGNORE INTO work_item_external_ids (work_item_id, project_id, namespace, external_id)
                SELECT id, project_id, 'legacy', external_id
                FROM work_items
                WHERE external_id IS NOT NULL;
                """;
            backfill.ExecuteNonQuery();
        }
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

    // SQLite primary error code for SQLITE_FULL. Matched on the primary
    // code (SqliteErrorCode) rather than the extended variant so any
    // SQLITE_FULL_*  refinement still routes through the disk-full path.
    internal const int SQLITE_FULL = 13;

    public async Task CreateAsync(WorkItem item, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var tx = _conn.BeginTransaction();
            using (var cmd = _conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO work_items (id, project_id, title, prompt, base_branch, work_branch, agent,
                        work_timeout_ticks, merge_timeout_ticks, push_upstream, state, created_at, updated_at,
                        last_error, upstream_push_attempts, depends_on_json, agent_class_id, queue_position,
                        stuck_retries, started_at, external_id, replay_of_work_item_id, merge_sha,
                        min_model_score, cancellation_reason, recovery_attempts, release_id, preempted_at, preempt_checkpoint,
                        suspended_vm_name, suspended_at,
                        failure_kind, quota_reset_at, next_quota_retry_at, quota_retry_attempts, quota_retry_from, auditor_profile, priority,
                        cancellation_source, transient_cancel_retries, prompt_revision, conflict_rework_attempts)
                    VALUES ($id, $project_id, $title, $prompt, $base, $work, $agent, $wt, $mt, $pu, $state, $ca, $ua, $err, $att, $deps, $class_id, $qpos,
                        $sretries, $started_at, $external_id, $replay_of, $merge_sha,
                        $min_model_score, $cancellation_reason, $recovery_attempts, $release_id, $preempted_at, $preempt_checkpoint,
                        $suspended_vm_name, $suspended_at,
                        $failure_kind, $quota_reset_at, $next_quota_retry_at, $quota_retry_attempts, $quota_retry_from, $auditor_profile, $priority,
                        $cancellation_source, $transient_cancel_retries, $prompt_revision, $conflict_rework_attempts);
                    """;
                Bind(cmd, item);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await WriteExternalIdsAsync(tx, item.Id, item.ProjectId, item.ExternalIds, ct);
            tx.Commit();
        }
        catch (SqliteException sqlex) when (sqlex.SqliteExtendedErrorCode == 2067) // SQLITE_CONSTRAINT_UNIQUE
        {
            // A concurrent request snuck past the application-level pre-check and
            // hit either the legacy work_items.external_id UNIQUE index or the
            // new work_item_external_ids UNIQUE index on (project_id, namespace, external_id).
            throw new WorkItemExternalIdConflictException();
        }
        catch (SqliteException sqlex) when (sqlex.SqliteErrorCode == SQLITE_FULL)
        {
            throw HandleDiskFull("CreateAsync", sqlex);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Inserts the side-table rows for an item's namespaced external IDs.
    /// Caller owns the transaction; <see cref="WriteExternalIdsAsync"/> only
    /// issues the INSERTs. No-op when the dict is empty.
    /// </summary>
    private async Task WriteExternalIdsAsync(
        SqliteTransaction tx,
        WorkItemId workItemId,
        ProjectId projectId,
        IReadOnlyDictionary<string, string> externalIds,
        CancellationToken ct)
    {
        if (externalIds.Count == 0) return;
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO work_item_external_ids (work_item_id, project_id, namespace, external_id)
            VALUES ($wid, $pid, $ns, $eid);
            """;
        var widParam = cmd.Parameters.Add("$wid", Microsoft.Data.Sqlite.SqliteType.Text);
        var pidParam = cmd.Parameters.Add("$pid", Microsoft.Data.Sqlite.SqliteType.Text);
        var nsParam = cmd.Parameters.Add("$ns", Microsoft.Data.Sqlite.SqliteType.Text);
        var eidParam = cmd.Parameters.Add("$eid", Microsoft.Data.Sqlite.SqliteType.Text);
        foreach (var (ns, value) in externalIds)
        {
            widParam.Value = workItemId.ToString();
            pidParam.Value = projectId.Value;
            nsParam.Value = ns;
            eidParam.Value = value;
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task UpdateAsync(WorkItem item, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            // prompt / prompt_revision / priority / external_id(s) are excluded
            // from this UPDATE. Callers commonly pass a STALE in-memory WorkItem
            // snapshot taken at pickup time; writing those columns from the
            // snapshot would clobber a concurrent PUT /workitems/{id}/prompt,
            // POST /workitems/{id}/priority, or PATCH /workitems/{id}/external-ids
            // that landed mid-pipeline. Use TryReplacePromptAsync /
            // UpdatePriorityAsync / ReplaceExternalIdsAsync to mutate them safely;
            // routine state transitions leave them alone.
            cmd.CommandText = """
                UPDATE work_items SET
                    project_id = $project_id, title = $title,
                    base_branch = $base, work_branch = $work, agent = $agent,
                    work_timeout_ticks = $wt, merge_timeout_ticks = $mt, push_upstream = $pu,
                    state = $state, updated_at = $ua, last_error = $err,
                    upstream_push_attempts = $att, depends_on_json = $deps,
                    agent_class_id = $class_id, queue_position = $qpos,
                    stuck_retries = $sretries, started_at = $started_at,
                    replay_of_work_item_id = $replay_of, merge_sha = $merge_sha,
                    min_model_score = $min_model_score,
                    cancellation_reason = $cancellation_reason,
                    recovery_attempts = $recovery_attempts,
                    release_id = $release_id,
                    preempted_at = $preempted_at,
                    preempt_checkpoint = $preempt_checkpoint,
                    suspended_vm_name = $suspended_vm_name,
                    suspended_at = $suspended_at,
                    failure_kind = $failure_kind,
                    quota_reset_at = $quota_reset_at,
                    next_quota_retry_at = $next_quota_retry_at,
                    quota_retry_attempts = $quota_retry_attempts,
                    quota_retry_from = $quota_retry_from,
                    auditor_profile = $auditor_profile,
                    cancellation_source = $cancellation_source,
                    transient_cancel_retries = $transient_cancel_retries,
                    conflict_rework_attempts = $conflict_rework_attempts
                WHERE id = $id;
                """;
            Bind(cmd, item);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (SqliteException sqlex) when (sqlex.SqliteErrorCode == SQLITE_FULL)
        {
            throw HandleDiskFull("UpdateAsync", sqlex);
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
            // See UpdateAsync — prompt / prompt_revision / priority / external_id(s)
            // are excluded from the full-row UPDATE to avoid stale-snapshot clobber.
            cmd.CommandText = """
                UPDATE work_items SET
                    project_id = $project_id, title = $title,
                    base_branch = $base, work_branch = $work, agent = $agent,
                    work_timeout_ticks = $wt, merge_timeout_ticks = $mt, push_upstream = $pu,
                    state = $state, updated_at = $ua, last_error = $err,
                    upstream_push_attempts = $att, depends_on_json = $deps,
                    agent_class_id = $class_id, queue_position = $qpos,
                    stuck_retries = $sretries, started_at = $started_at,
                    replay_of_work_item_id = $replay_of, merge_sha = $merge_sha,
                    min_model_score = $min_model_score,
                    cancellation_reason = $cancellation_reason,
                    recovery_attempts = $recovery_attempts,
                    release_id = $release_id,
                    preempted_at = $preempted_at,
                    preempt_checkpoint = $preempt_checkpoint,
                    suspended_vm_name = $suspended_vm_name,
                    suspended_at = $suspended_at,
                    failure_kind = $failure_kind,
                    quota_reset_at = $quota_reset_at,
                    next_quota_retry_at = $next_quota_retry_at,
                    quota_retry_attempts = $quota_retry_attempts,
                    quota_retry_from = $quota_retry_from,
                    auditor_profile = $auditor_profile,
                    cancellation_source = $cancellation_source,
                    transient_cancel_retries = $transient_cancel_retries,
                    conflict_rework_attempts = $conflict_rework_attempts
                WHERE id = $id AND state = $only_if_state;
                """;
            Bind(cmd, item);
            cmd.Parameters.AddWithValue("$only_if_state", (int)onlyIfState);
            return await cmd.ExecuteNonQueryAsync(ct) > 0;
        }
        catch (SqliteException sqlex) when (sqlex.SqliteErrorCode == SQLITE_FULL)
        {
            throw HandleDiskFull("TryUpdateIfStateAsync", sqlex);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Translates a <c>SQLITE_FULL</c> error into a typed exception the
    /// orchestrator can recognise. Emits an audit event at Fatal level so
    /// the operator dashboards / alerts fire before the host degrades
    /// further. Returned (not thrown) so the call site retains the
    /// <c>throw</c> for analysis flow.
    /// </summary>
    private static WorkItemStoreDiskFullException HandleDiskFull(string operation, SqliteException sqlex)
    {
        AuditLog.StoreDiskFull(operation);
        return new WorkItemStoreDiskFullException(operation, sqlex);
    }

    /// <summary>
    /// Test hook: clamps the underlying database's <c>max_page_count</c> on
    /// the same connection the store uses, so subsequent writes through
    /// <see cref="CreateAsync"/> / <see cref="UpdateAsync"/> deterministically
    /// fail with <c>SQLITE_FULL</c>. Production code never calls this.
    /// </summary>
    internal void ForceMaxPageCountForTesting(long pages)
    {
        using var cmd = _conn.CreateCommand();
        // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- 'pages' is an int64 from a test caller; PRAGMA does not accept parameters so inlining is required and safe
        cmd.CommandText = $"PRAGMA max_page_count = {pages};";
        cmd.ExecuteNonQuery();
    }

    public async Task<WorkItem?> GetAsync(WorkItemId id, CancellationToken ct = default)
    {
        WorkItem? row;
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "SELECT * FROM work_items WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id.ToString());
            using var reader = await cmd.ExecuteReaderAsync(ct);
            row = await reader.ReadAsync(ct) ? Read(reader) : null;
        }
        return await EnrichOneAsync(row, ct);
    }

    public async Task<PriorityUpdateResult> UpdatePriorityAsync(
        WorkItemId id,
        int priority,
        DateTimeOffset updatedAt,
        CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            // Read current row under the write lock so a concurrent worker can't
            // transition the row between the read and the partial UPDATE below.
            WorkItem? current;
            using (var read = _conn.CreateCommand())
            {
                read.CommandText = "SELECT * FROM work_items WHERE id = $id;";
                read.Parameters.AddWithValue("$id", id.ToString());
                using var reader = await read.ExecuteReaderAsync(ct);
                current = await reader.ReadAsync(ct) ? Read(reader) : null;
            }

            if (current is null)
                return new PriorityUpdateResult(PriorityUpdateOutcome.NotFound, null, null);

            current = current with { ExternalIds = await LoadExternalIdsForAsync(current.Id, tx: null, ct) };

            if (WorkItemDependencies.TerminalStates.Contains(current.State))
                return new PriorityUpdateResult(PriorityUpdateOutcome.TerminalState, current, current.Priority);

            // Partial UPDATE: touch only priority + updated_at. Crucially does NOT
            // write state/started_at/recovery_attempts/etc, so a worker that picks
            // the item up concurrently isn't stomped.
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE work_items SET priority = $priority, updated_at = $updated_at
                WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$priority", priority);
            cmd.Parameters.AddWithValue("$updated_at", updatedAt.ToString("O"));
            cmd.Parameters.AddWithValue("$id", id.ToString());
            await cmd.ExecuteNonQueryAsync(ct);

            var updated = current with { Priority = priority, UpdatedAt = updatedAt };
            return new PriorityUpdateResult(PriorityUpdateOutcome.Updated, updated, current.Priority);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async IAsyncEnumerable<WorkItem> ListAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        var rows = new List<WorkItem>();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "SELECT * FROM work_items ORDER BY created_at DESC;";
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) rows.Add(Read(reader));
        }
        var extByItem = await LoadExternalIdsBatchAsync(rows.Select(r => r.Id).ToList(), ct);
        foreach (var item in rows)
            yield return item with { ExternalIds = extByItem.GetValueOrDefault(item.Id, EmptyExternalIds) };
    }

    public async IAsyncEnumerable<WorkItem> ListByStateAsync(WorkItemState state, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var rows = new List<WorkItem>();
        using (var cmd = _conn.CreateCommand())
        {
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
            while (await reader.ReadAsync(ct)) rows.Add(Read(reader));
        }
        var extByItem = await LoadExternalIdsBatchAsync(rows.Select(r => r.Id).ToList(), ct);
        foreach (var item in rows)
            yield return item with { ExternalIds = extByItem.GetValueOrDefault(item.Id, EmptyExternalIds) };
    }

    public async Task<int> CountByStateAsync(WorkItemState state, CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM work_items WHERE state = $state;";
        cmd.Parameters.AddWithValue("$state", (int)state);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long l ? (int)l : 0;
    }

    public async IAsyncEnumerable<WorkItem> ListDispatchEligibleByPriorityAsync(
        IReadOnlySet<WorkItemId> skipIds,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var rows = new List<WorkItem>();
        using (var cmd = _conn.CreateCommand())
        {
            // Exclude terminal states and parked NeedsOperatorInput. The remaining set
            // mirrors what the FIFO dispatcher used to process via the channel:
            // Queued plus the mid-pipeline resumable states (Working, WorkComplete,
            // Auditing, Reworking, AuditPassed, Merging, Merged, UpstreamPushing).
            cmd.CommandText = $"""
                SELECT * FROM work_items
                WHERE state NOT IN (
                    {(int)WorkItemState.Done},
                    {(int)WorkItemState.Failed},
                    {(int)WorkItemState.Cancelled},
                    {(int)WorkItemState.AuditFailed},
                    {(int)WorkItemState.MergeConflictResolutionFailed},
                    {(int)WorkItemState.AbandonedAfterRecoveryAttempts},
                    {(int)WorkItemState.NeedsOperatorInput}
                )
                ORDER BY priority DESC, created_at ASC;
                """;
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var item = Read(reader);
                if (skipIds.Contains(item.Id)) continue;
                rows.Add(item);
            }
        }
        var extByItem = await LoadExternalIdsBatchAsync(rows.Select(r => r.Id).ToList(), ct);
        foreach (var item in rows)
            yield return item with { ExternalIds = extByItem.GetValueOrDefault(item.Id, EmptyExternalIds) };
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
              AND state NOT IN ({(int)WorkItemState.Done}, {(int)WorkItemState.Failed}, {(int)WorkItemState.Cancelled}, {(int)WorkItemState.AuditFailed}, {(int)WorkItemState.MergeConflictResolutionFailed}, {(int)WorkItemState.NeedsOperatorInput}, {(int)WorkItemState.WaitingForQuotaReset}, {(int)WorkItemState.AbandonedAfterRecoveryAttempts});
            """;
        cmd.Parameters.AddWithValue("$pid", projectId.Value);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long l ? (int)l : 0;
    }

    public async Task<WorkItem?> GetByExternalIdAsync(ProjectId projectId, string externalId, CancellationToken ct = default)
    {
        // Bare lookup: match against every namespace in the project. The
        // namespace dimension was added when work items got an external-IDs
        // dict; this entry point used to scan only work_items.external_id, but
        // a namespaced PATCH can produce an item discoverable only via the
        // side table. Disambiguation matters because two namespaces in the
        // same project may legitimately share a value (e.g.
        // github:PROJ-42 + linear:PROJ-42 on the same item is allowed; a
        // collision *across distinct items* shows up here too).
        var matches = new List<(WorkItemId Id, string Namespace)>();
        using (var lookup = _conn.CreateCommand())
        {
            lookup.CommandText = """
                SELECT work_item_id, namespace
                FROM work_item_external_ids
                WHERE project_id = $pid AND external_id = $eid;
                """;
            lookup.Parameters.AddWithValue("$pid", projectId.Value);
            lookup.Parameters.AddWithValue("$eid", externalId);
            using var reader = await lookup.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (Guid.TryParse(reader.GetString(0), out var g))
                    matches.Add((new WorkItemId(g), reader.GetString(1)));
            }
        }

        if (matches.Count == 0) return null;
        var distinctItems = matches.Select(m => m.Id).Distinct().ToList();
        if (distinctItems.Count > 1)
        {
            // Same value resolved to multiple WORK ITEMS via different namespaces.
            // Refuse the bare lookup; the caller must disambiguate.
            throw new AmbiguousExternalIdException(externalId, matches.Select(m => m.Namespace).Distinct().ToList());
        }
        return await GetAsync(distinctItems[0], ct);
    }

    public async Task<WorkItem?> GetByNamespacedExternalIdAsync(
        ProjectId projectId,
        string @namespace,
        string externalId,
        CancellationToken ct = default)
    {
        WorkItemId? matched = null;
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT work_item_id
                FROM work_item_external_ids
                WHERE project_id = $pid AND namespace = $ns AND external_id = $eid
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$pid", projectId.Value);
            cmd.Parameters.AddWithValue("$ns", @namespace);
            cmd.Parameters.AddWithValue("$eid", externalId);
            using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct) && Guid.TryParse(reader.GetString(0), out var g))
                matched = new WorkItemId(g);
        }
        return matched is null ? null : await GetAsync(matched.Value, ct);
    }

    public async Task<WorkItem?> ReplaceExternalIdsAsync(
        WorkItemId id,
        IReadOnlyDictionary<string, string> externalIds,
        DateTimeOffset updatedAt,
        CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            // Re-read inside the lock so we can detect "row vanished" cleanly and
            // so the caller's snapshot can't race a concurrent cancel-delete.
            WorkItem? current;
            using (var read = _conn.CreateCommand())
            {
                read.CommandText = "SELECT * FROM work_items WHERE id = $id;";
                read.Parameters.AddWithValue("$id", id.ToString());
                using var reader = await read.ExecuteReaderAsync(ct);
                current = await reader.ReadAsync(ct) ? Read(reader) : null;
            }
            if (current is null) return null;

            using var tx = _conn.BeginTransaction();
            using (var del = _conn.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM work_item_external_ids WHERE work_item_id = $id;";
                del.Parameters.AddWithValue("$id", id.ToString());
                await del.ExecuteNonQueryAsync(ct);
            }
            try
            {
                await WriteExternalIdsAsync(tx, id, current.ProjectId, externalIds, ct);
            }
            catch (SqliteException sqlex) when (sqlex.SqliteExtendedErrorCode == 2067)
            {
                // Side-table UNIQUE collision: another item in the project already
                // holds one of the namespaced values being assigned.
                tx.Rollback();
                throw new WorkItemExternalIdConflictException();
            }

            // Mirror the legacy projection so the deprecated work_items.external_id
            // column still reflects the 'legacy' namespace value (or NULL when
            // absent). Keeps the legacy UNIQUE index honest.
            using (var legacyUpdate = _conn.CreateCommand())
            {
                legacyUpdate.Transaction = tx;
                legacyUpdate.CommandText = "UPDATE work_items SET external_id = $eid, updated_at = $ua WHERE id = $id;";
                var legacy = externalIds.TryGetValue("legacy", out var v) ? v : null;
                legacyUpdate.Parameters.AddWithValue("$eid", (object?)legacy ?? DBNull.Value);
                legacyUpdate.Parameters.AddWithValue("$ua", updatedAt.ToString("O"));
                legacyUpdate.Parameters.AddWithValue("$id", id.ToString());
                try
                {
                    await legacyUpdate.ExecuteNonQueryAsync(ct);
                }
                catch (SqliteException sqlex) when (sqlex.SqliteExtendedErrorCode == 2067)
                {
                    // Legacy work_items.external_id UNIQUE-per-project index collision.
                    tx.Rollback();
                    throw new WorkItemExternalIdConflictException();
                }
            }

            tx.Commit();

            return current with
            {
                ExternalIds = externalIds.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase),
                UpdatedAt = updatedAt,
            };
        }
        finally
        {
            _writeLock.Release();
        }
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
                WHERE state IN ({(int)WorkItemState.Done}, {(int)WorkItemState.Failed}, {(int)WorkItemState.AuditFailed}, {(int)WorkItemState.MergeConflictResolutionFailed}, {(int)WorkItemState.Cancelled})
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
        var rows = new List<WorkItem>();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT * FROM work_items
                WHERE replay_of_work_item_id = $source_id
                ORDER BY created_at ASC;
                """;
            cmd.Parameters.AddWithValue("$source_id", sourceId.ToString());
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) rows.Add(Read(reader));
        }
        var extByItem = await LoadExternalIdsBatchAsync(rows.Select(r => r.Id).ToList(), ct);
        foreach (var item in rows)
            yield return item with { ExternalIds = extByItem.GetValueOrDefault(item.Id, EmptyExternalIds) };
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
        var rows = new List<WorkItem>();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "SELECT * FROM work_items WHERE release_id = $rid ORDER BY created_at;";
            cmd.Parameters.AddWithValue("$rid", releaseId.ToString());
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) rows.Add(Read(reader));
        }
        var extByItem = await LoadExternalIdsBatchAsync(rows.Select(r => r.Id).ToList(), ct);
        foreach (var item in rows)
            yield return item with { ExternalIds = extByItem.GetValueOrDefault(item.Id, EmptyExternalIds) };
    }


    public async Task<PromptReplaceResult> TryReplacePromptAsync(
        WorkItemId id,
        string newPrompt,
        DateTimeOffset updatedAt,
        CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            WorkItem? current;
            using (var read = _conn.CreateCommand())
            {
                read.CommandText = "SELECT * FROM work_items WHERE id = $id;";
                read.Parameters.AddWithValue("$id", id.ToString());
                using var reader = await read.ExecuteReaderAsync(ct);
                current = await reader.ReadAsync(ct) ? Read(reader) : null;
            }

            if (current is null)
                return new PromptReplaceResult(PromptReplaceOutcome.NotFound, null);
            if (WorkItemDependencies.TerminalStates.Contains(current.State))
                return new PromptReplaceResult(PromptReplaceOutcome.TerminalState, null);

            var newRevision = current.PromptRevision + 1;
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE work_items
                SET prompt = $prompt, prompt_revision = $rev, updated_at = $ua
                WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$prompt", newPrompt);
            cmd.Parameters.AddWithValue("$rev", newRevision);
            cmd.Parameters.AddWithValue("$ua", updatedAt.ToString("O"));
            cmd.Parameters.AddWithValue("$id", id.ToString());
            await cmd.ExecuteNonQueryAsync(ct);
            return new PromptReplaceResult(PromptReplaceOutcome.Updated, newRevision);
        }
        catch (SqliteException sqlex) when (sqlex.SqliteErrorCode == SQLITE_FULL)
        {
            throw HandleDiskFull("TryReplacePromptAsync", sqlex);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task RecordIterationDispatchAsync(
        WorkItemId workItemId,
        int iteration,
        int promptRevisionAtDispatch,
        DateTimeOffset dispatchedAt,
        CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO work_item_iterations (work_item_id, iteration, prompt_revision_at_dispatch, dispatched_at)
                VALUES ($wi, $iter, $rev, $at)
                ON CONFLICT(work_item_id, iteration) DO UPDATE SET
                    prompt_revision_at_dispatch = excluded.prompt_revision_at_dispatch,
                    dispatched_at = excluded.dispatched_at;
                """;
            cmd.Parameters.AddWithValue("$wi", workItemId.ToString());
            cmd.Parameters.AddWithValue("$iter", iteration);
            cmd.Parameters.AddWithValue("$rev", promptRevisionAtDispatch);
            cmd.Parameters.AddWithValue("$at", dispatchedAt.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (SqliteException sqlex) when (sqlex.SqliteErrorCode == SQLITE_FULL)
        {
            throw HandleDiskFull("RecordIterationDispatchAsync", sqlex);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<WorkItemIteration>> GetIterationsAsync(
        WorkItemId workItemId,
        CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT iteration, prompt_revision_at_dispatch, dispatched_at
            FROM work_item_iterations
            WHERE work_item_id = $wi
            ORDER BY iteration ASC;
            """;
        cmd.Parameters.AddWithValue("$wi", workItemId.ToString());
        var results = new List<WorkItemIteration>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new WorkItemIteration(
                workItemId,
                reader.GetInt32(0),
                reader.GetInt32(1),
                DateTimeOffset.Parse(reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture)));
        }
        return results;
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
        // external_id (singular column) is the denormalised projection of namespace 'legacy'.
        // It exists for the deprecation window so the legacy unique index and any read
        // paths that still query the column keep working without code-flow changes.
        // After the deprecation window the column is dropped and lookups go entirely
        // through the work_item_external_ids side table.
        var legacyValue = item.ExternalIds.TryGetValue("legacy", out var legacy) ? legacy : null;
        cmd.Parameters.AddWithValue("$external_id", (object?)legacyValue ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$replay_of", (object?)item.ReplayOfWorkItemId?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$merge_sha", (object?)item.MergeSha ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$min_model_score", item.MinModelScore);
        cmd.Parameters.AddWithValue("$cancellation_reason",
            item.CancellationReason.HasValue ? (object)item.CancellationReason.Value.ToString() : DBNull.Value);
        cmd.Parameters.AddWithValue("$recovery_attempts", item.RecoveryAttempts);
        cmd.Parameters.AddWithValue("$release_id", (object?)item.ReleaseId?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$preempted_at", (object?)item.PreemptedAt?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$preempt_checkpoint", (object?)item.PreemptCheckpoint ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$suspended_vm_name", (object?)item.SuspendedVmName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$suspended_at", (object?)item.SuspendedAt?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$failure_kind", (object?)item.FailureKind ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$quota_reset_at", (object?)item.QuotaResetAt?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$next_quota_retry_at", (object?)item.NextQuotaRetryAt?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$quota_retry_attempts", item.QuotaRetryAttempts);
        cmd.Parameters.AddWithValue("$quota_retry_from", (object?)item.QuotaRetryFrom ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$auditor_profile", (object?)item.AuditorProfile ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$priority", item.Priority);
        cmd.Parameters.AddWithValue("$cancellation_source", (object?)item.CancellationSource ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$transient_cancel_retries", item.TransientCancelRetries);
        cmd.Parameters.AddWithValue("$prompt_revision", item.PromptRevision);
        cmd.Parameters.AddWithValue("$conflict_rework_attempts", item.ConflictReworkAttempts);
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
        // ExternalIds is populated by a follow-up batch / per-id load from
        // work_item_external_ids; rows that fall straight out of Read() carry
        // an empty dict and rely on the wrapping method to enrich them. The
        // legacy work_items.external_id column is no longer parsed here — the
        // side table is the canonical source.
        ReplayOfWorkItemId = ReadNullableWorkItemId(r, "replay_of_work_item_id"),
        MergeSha = r.IsDBNull(r.GetOrdinal("merge_sha")) ? null : r.GetString(r.GetOrdinal("merge_sha")),
        MinModelScore = ReadInt32OrDefault(r, "min_model_score", defaultValue: 95),
        CancellationReason = ReadCancellationReason(r),
        RecoveryAttempts = ReadInt32OrDefault(r, "recovery_attempts", defaultValue: 0),
        ReleaseId = ReadNullableReleaseId(r, "release_id"),
        PreemptedAt = ReadNullableDateTimeOffset(r, "preempted_at"),
        PreemptCheckpoint = r.IsDBNull(r.GetOrdinal("preempt_checkpoint")) ? null : r.GetString(r.GetOrdinal("preempt_checkpoint")),
        SuspendedVmName = ReadNullableString(r, "suspended_vm_name"),
        SuspendedAt = ReadNullableDateTimeOffset(r, "suspended_at"),
        FailureKind = r.IsDBNull(r.GetOrdinal("failure_kind")) ? null : r.GetString(r.GetOrdinal("failure_kind")),
        QuotaResetAt = ReadNullableDateTimeOffset(r, "quota_reset_at"),
        NextQuotaRetryAt = ReadNullableDateTimeOffset(r, "next_quota_retry_at"),
        QuotaRetryAttempts = ReadInt32OrDefault(r, "quota_retry_attempts", defaultValue: 0),
        QuotaRetryFrom = ReadNullableString(r, "quota_retry_from"),
        AuditorProfile = r.IsDBNull(r.GetOrdinal("auditor_profile")) ? null : r.GetString(r.GetOrdinal("auditor_profile")),
        Priority = ReadInt32OrDefault(r, "priority", defaultValue: 0),
        CancellationSource = ReadNullableString(r, "cancellation_source"),
        TransientCancelRetries = ReadInt32OrDefault(r, "transient_cancel_retries", defaultValue: 0),
        PromptRevision = ReadInt32OrDefault(r, "prompt_revision", defaultValue: 1),
        ConflictReworkAttempts = ReadInt32OrDefault(r, "conflict_rework_attempts", defaultValue: 0),
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

    private static string? ReadNullableString(SqliteDataReader r, string column)
    {
        var ord = r.GetOrdinal(column);
        return r.IsDBNull(ord) ? null : r.GetString(ord);
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

    private static readonly IReadOnlyDictionary<string, string> EmptyExternalIds
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Loads the namespaced external IDs for a single work item. Returns an
    /// empty dictionary when the item has none. Caller-supplied
    /// <paramref name="tx"/> is reused so reads see writes from the same
    /// transaction; pass null for a no-transaction read.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> LoadExternalIdsForAsync(
        WorkItemId id,
        SqliteTransaction? tx,
        CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        if (tx is not null) cmd.Transaction = tx;
        cmd.CommandText = "SELECT namespace, external_id FROM work_item_external_ids WHERE work_item_id = $id;";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        using var reader = await cmd.ExecuteReaderAsync(ct);
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(ct))
            dict[reader.GetString(0)] = reader.GetString(1);
        return dict;
    }

    /// <summary>
    /// Batch-loads external IDs for a set of work items in a single round-trip.
    /// For batches larger than a small threshold falls back to a full-table
    /// scan filtered in memory to avoid blowing past SQLite's parameter cap.
    /// </summary>
    private async Task<Dictionary<WorkItemId, IReadOnlyDictionary<string, string>>> LoadExternalIdsBatchAsync(
        IReadOnlyCollection<WorkItemId> ids,
        CancellationToken ct)
    {
        var result = new Dictionary<WorkItemId, IReadOnlyDictionary<string, string>>();
        if (ids.Count == 0) return result;

        var idSet = ids as HashSet<WorkItemId> ?? new HashSet<WorkItemId>(ids);
        using var cmd = _conn.CreateCommand();
        if (ids.Count > 256)
        {
            cmd.CommandText = "SELECT work_item_id, namespace, external_id FROM work_item_external_ids;";
        }
        else
        {
            var paramNames = new List<string>(ids.Count);
            var i = 0;
            foreach (var id in ids)
            {
                var p = $"$id{i++}";
                paramNames.Add(p);
                cmd.Parameters.AddWithValue(p, id.ToString());
            }
            // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- paramNames is composed of literal '$idN' tokens generated from a counter; no user input reaches the SQL string
            cmd.CommandText = $"SELECT work_item_id, namespace, external_id FROM work_item_external_ids WHERE work_item_id IN ({string.Join(",", paramNames)});";
        }

        using var reader = await cmd.ExecuteReaderAsync(ct);
        var accum = new Dictionary<WorkItemId, Dictionary<string, string>>();
        while (await reader.ReadAsync(ct))
        {
            if (!Guid.TryParse(reader.GetString(0), out var g)) continue;
            var wid = new WorkItemId(g);
            if (!idSet.Contains(wid)) continue;
            if (!accum.TryGetValue(wid, out var inner))
                accum[wid] = inner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            inner[reader.GetString(1)] = reader.GetString(2);
        }
        foreach (var kv in accum)
            result[kv.Key] = kv.Value;
        return result;
    }

    /// <summary>
    /// Enriches a single in-memory item with its external IDs loaded from the
    /// side table. No-op when the item is null.
    /// </summary>
    private async Task<WorkItem?> EnrichOneAsync(WorkItem? item, CancellationToken ct)
    {
        if (item is null) return null;
        var extIds = await LoadExternalIdsForAsync(item.Id, tx: null, ct);
        return item with { ExternalIds = extIds };
    }
}
