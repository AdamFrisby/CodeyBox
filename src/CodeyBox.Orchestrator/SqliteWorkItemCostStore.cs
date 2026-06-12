using Microsoft.Data.Sqlite;
using CodeyBox.Agents;
using CodeyBox.Core;
using System.Text.Json;

namespace CodeyBox.Orchestrator;

/// <summary>
/// SQLite-backed cost store. Writes to the same database file as
/// <see cref="SqliteWorkItemStore"/>; the work_item_costs table is created
/// here via an additive migration.
///
/// A prepared INSERT keeps the hot-path overhead well under 50 ms per call.
/// </summary>
public sealed class SqliteWorkItemCostStore : IWorkItemCostStore, IRecentCostsByAgentQueryable, IDisposable
{
    private const string LegacyElapsedFallbackMetadataSource = "extractor_null_elapsed_fallback";

    private readonly string _path;
    private readonly SqliteConnection _conn;
    private readonly SqliteDatabaseWriteGate _writeLock;
    private readonly SqliteCommand _insertCmd;
    private readonly AgentCostCalculator? _costCalculator;

    public SqliteWorkItemCostStore(string path, AgentCostCalculator? costCalculator = null)
    {
        _costCalculator = costCalculator;
        _path = path;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _conn = new SqliteConnection($"Data Source={path}");
        _writeLock = SqliteDatabaseWriteGate.ForPath(path);
        _writeLock.Wait();
        try
        {
            _conn.Open();

            using (var pragmaCmd = _conn.CreateCommand())
            {
                pragmaCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=30000; PRAGMA foreign_keys=ON;";
                pragmaCmd.ExecuteNonQuery();
            }

            using var createCmd = _conn.CreateCommand();
            // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- hardcoded DDL only
            createCmd.CommandText = """
                CREATE TABLE IF NOT EXISTS work_item_costs (
                    id                  TEXT PRIMARY KEY,
                    work_item_id        TEXT NOT NULL REFERENCES work_items(id) ON DELETE CASCADE,
                    phase               TEXT NOT NULL,
                    iteration           INTEGER,
                    agent_kind          TEXT NOT NULL,
                    agent_instance_id   TEXT,
                    model_id            TEXT,
                    input_tokens        INTEGER NOT NULL,
                    cached_input_tokens INTEGER NOT NULL DEFAULT 0,
                    output_tokens       INTEGER NOT NULL,
                    estimated_usd       REAL NOT NULL DEFAULT 0,
                    started_at          TEXT NOT NULL,
                    ended_at            TEXT NOT NULL,
                    raw_metadata_json   TEXT NOT NULL DEFAULT '{}',
                    has_extracted_token_usage INTEGER NOT NULL DEFAULT 1
                );
                CREATE INDEX IF NOT EXISTS idx_costs_work_item
                    ON work_item_costs(work_item_id, phase, iteration);
                CREATE INDEX IF NOT EXISTS idx_costs_project_time
                    ON work_item_costs(work_item_id, started_at);
                """;
            createCmd.ExecuteNonQuery();
            RunMigration("ALTER TABLE work_item_costs ADD COLUMN agent_instance_id TEXT;");

            AddWorkItemCostColumnIfMissing(
                "has_extracted_token_usage",
                "ALTER TABLE work_item_costs ADD COLUMN has_extracted_token_usage INTEGER NOT NULL DEFAULT 1;");
            MarkLegacyElapsedFallbackRows();

            // The pre-fix Claude/OpenCode extractors stored prompt-side totals
            // (i.e. input_tokens already included the cached subset) while
            // cached_input_tokens carried the cache_read portion. The new
            // contract is fresh-only in input_tokens, so the aggregator can sum
            // input + cached without double-counting. Run a one-time UPDATE on
            // legacy rows to subtract cached from input. usage_contract_version
            // = 1 means the row already uses the new contract; defaulting new
            // inserts to 1 below means the migration only ever touches rows
            // created before this column existed (i.e. default 0 backfill).
            AddWorkItemCostColumnIfMissing(
                "usage_contract_version",
                "ALTER TABLE work_item_costs ADD COLUMN usage_contract_version INTEGER NOT NULL DEFAULT 0;");
            MigrateLegacyInputTokensToFreshOnly();

            _insertCmd = _conn.CreateCommand();
            // usage_contract_version is hard-coded 1 here so the post-migration
            // idempotency check (re-runs on startup only touch version=0 legacy
            // rows) holds — see MigrateLegacyInputTokensToFreshOnly.
            _insertCmd.CommandText = """
                INSERT INTO work_item_costs
                    (id, work_item_id, phase, iteration, agent_kind, agent_instance_id, model_id,
                     input_tokens, cached_input_tokens, output_tokens,
                     estimated_usd, started_at, ended_at, raw_metadata_json,
                     has_extracted_token_usage, usage_contract_version)
                VALUES
                    ($id, $wi, $phase, $iter, $kind, $instance, $model,
                     $input, $cached, $output,
                     $usd, $started, $ended, $meta,
                     $hasExtracted, 1)
                """;
            _insertCmd.Parameters.Add("$id", SqliteType.Text);
            _insertCmd.Parameters.Add("$wi", SqliteType.Text);
            _insertCmd.Parameters.Add("$phase", SqliteType.Text);
            _insertCmd.Parameters.Add("$iter", SqliteType.Integer);
            _insertCmd.Parameters.Add("$kind", SqliteType.Text);
            _insertCmd.Parameters.Add("$instance", SqliteType.Text);
            _insertCmd.Parameters.Add("$model", SqliteType.Text);
            _insertCmd.Parameters.Add("$input", SqliteType.Integer);
            _insertCmd.Parameters.Add("$cached", SqliteType.Integer);
            _insertCmd.Parameters.Add("$output", SqliteType.Integer);
            _insertCmd.Parameters.Add("$usd", SqliteType.Real);
            _insertCmd.Parameters.Add("$started", SqliteType.Text);
            _insertCmd.Parameters.Add("$ended", SqliteType.Text);
            _insertCmd.Parameters.Add("$meta", SqliteType.Text);
            _insertCmd.Parameters.Add("$hasExtracted", SqliteType.Integer);
            _insertCmd.Prepare();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task RecordAsync(WorkItemCost cost, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            _insertCmd.Parameters["$id"].Value = cost.Id;
            _insertCmd.Parameters["$wi"].Value = cost.WorkItemId;
            _insertCmd.Parameters["$phase"].Value = cost.Phase;
            _insertCmd.Parameters["$iter"].Value = cost.Iteration.HasValue ? (object)cost.Iteration.Value : DBNull.Value;
            _insertCmd.Parameters["$kind"].Value = cost.AgentKind;
            _insertCmd.Parameters["$instance"].Value = cost.AgentInstanceId is not null ? cost.AgentInstanceId : DBNull.Value;
            _insertCmd.Parameters["$model"].Value = cost.ModelId is not null ? (object)cost.ModelId : DBNull.Value;
            _insertCmd.Parameters["$input"].Value = cost.InputTokens;
            _insertCmd.Parameters["$cached"].Value = cost.CachedInputTokens;
            _insertCmd.Parameters["$output"].Value = cost.OutputTokens;
            _insertCmd.Parameters["$usd"].Value = cost.EstimatedUsd;
            _insertCmd.Parameters["$started"].Value = cost.StartedAt.ToString("O");
            _insertCmd.Parameters["$ended"].Value = cost.EndedAt.ToString("O");
            _insertCmd.Parameters["$meta"].Value = cost.RawMetadataJson;
            _insertCmd.Parameters["$hasExtracted"].Value = cost.HasExtractedTokenUsage ? 1 : 0;
            await _insertCmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeLock.Release();
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

    public async Task<IReadOnlyList<WorkItemCost>> GetByWorkItemAsync(string workItemId, CancellationToken ct = default)
    {
        // Read-only query: open a separate read connection to avoid holding the write lock
        // during what could be a multi-row scan.
        using var readConn = new SqliteConnection($"Data Source={_path};Mode=ReadOnly");
        readConn.Open();

        using var cmd = readConn.CreateCommand();
        cmd.CommandText = """
            SELECT id, work_item_id, phase, iteration, agent_kind, agent_instance_id, model_id,
                   input_tokens, cached_input_tokens, output_tokens,
                   estimated_usd, started_at, ended_at, raw_metadata_json,
                   has_extracted_token_usage
            FROM work_item_costs
            WHERE work_item_id = $wi
            ORDER BY started_at
            """;
        cmd.Parameters.AddWithValue("$wi", workItemId);

        var results = new List<WorkItemCost>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(ReadRow(reader));
        return results;
    }

    /// <summary>
    /// Single-connection batched summarisation for the list endpoint. Replaces
    /// the default N+1 implementation (one connection per work item) with a
    /// single WHERE work_item_id IN (...) SELECT chunked into groups of
    /// <c>ChunkSize</c> rows to stay well under SQLite's 999 host-parameter
    /// limit. Reading rows for K items is O(1) connections regardless of K.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, WorkItemUsageSummary>> SummariseManyAsync(
        IReadOnlyCollection<string> workItemIds, CancellationToken ct = default)
    {
        if (workItemIds.Count == 0)
            return new Dictionary<string, WorkItemUsageSummary>(StringComparer.Ordinal);

        const int ChunkSize = 500;

        var rowsByItem = new Dictionary<string, List<WorkItemCost>>(StringComparer.Ordinal);

        using var readConn = new SqliteConnection($"Data Source={_path};Mode=ReadOnly");
        readConn.Open();

        var distinct = workItemIds.Distinct(StringComparer.Ordinal).ToList();
        for (var offset = 0; offset < distinct.Count; offset += ChunkSize)
        {
            var chunk = distinct.Skip(offset).Take(ChunkSize).ToList();
            using var cmd = readConn.CreateCommand();

            var placeholders = string.Join(",", chunk.Select((_, i) => $"$wi{i}"));
            // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- placeholders are $-prefixed indices, all values bound via parameters
            cmd.CommandText = $"""
                SELECT id, work_item_id, phase, iteration, agent_kind, agent_instance_id, model_id,
                       input_tokens, cached_input_tokens, output_tokens,
                       estimated_usd, started_at, ended_at, raw_metadata_json,
                       has_extracted_token_usage
                FROM work_item_costs
                WHERE work_item_id IN ({placeholders})
                ORDER BY work_item_id, started_at
                """;
            for (var i = 0; i < chunk.Count; i++)
                cmd.Parameters.AddWithValue($"$wi{i}", chunk[i]);

            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var row = ReadRow(reader);
                if (!rowsByItem.TryGetValue(row.WorkItemId, out var list))
                {
                    list = new List<WorkItemCost>();
                    rowsByItem[row.WorkItemId] = list;
                }
                list.Add(row);
            }
        }

        var summaries = new Dictionary<string, WorkItemUsageSummary>(rowsByItem.Count, StringComparer.Ordinal);
        foreach (var (id, rows) in rowsByItem)
        {
            var summary = WorkItemUsageAggregator.Summarise(rows);
            if (summary is not null) summaries[id] = summary;
        }
        return summaries;
    }

    public async Task<IReadOnlyList<WorkItemCost>> GetByProjectAsync(
        string projectId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        // Read-only query: open a separate read connection to avoid holding the write lock
        // during what could be a multi-row scan.
        using var readConn = new SqliteConnection($"Data Source={_path};Mode=ReadOnly");
        readConn.Open();

        using var cmd = readConn.CreateCommand();
        cmd.CommandText = """
            SELECT c.id, c.work_item_id, c.phase, c.iteration, c.agent_kind, c.agent_instance_id, c.model_id,
                   c.input_tokens, c.cached_input_tokens, c.output_tokens,
                   c.estimated_usd, c.started_at, c.ended_at, c.raw_metadata_json,
                   c.has_extracted_token_usage
            FROM work_item_costs c
            JOIN work_items w ON w.id = c.work_item_id
            WHERE w.project_id = $proj
              AND c.started_at >= $from
              AND c.started_at < $to
            ORDER BY c.started_at
            """;
        cmd.Parameters.AddWithValue("$proj", projectId);
        cmd.Parameters.AddWithValue("$from", from.ToString("O"));
        cmd.Parameters.AddWithValue("$to", to.ToString("O"));

        var results = new List<WorkItemCost>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(ReadRow(reader));
        return results;
    }

    public async Task<IReadOnlyList<(string ProjectId, double TotalUsd)>> GetFleetCostSummaryAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        using var readConn = new SqliteConnection($"Data Source={_path};Mode=ReadOnly");
        readConn.Open();

        using var cmd = readConn.CreateCommand();
        cmd.CommandText = """
            SELECT w.project_id, SUM(c.estimated_usd)
            FROM work_item_costs c
            JOIN work_items w ON w.id = c.work_item_id
            WHERE c.started_at >= $from
              AND c.started_at < $to
            GROUP BY w.project_id
            """;
        cmd.Parameters.AddWithValue("$from", from.ToString("O"));
        cmd.Parameters.AddWithValue("$to", to.ToString("O"));

        var results = new List<(string, double)>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add((reader.GetString(0), reader.GetDouble(1)));
        return results;
    }

    /// <summary>
    /// Server-side aggregation for <see cref="IAgentBurnEstimator"/>: returns
    /// the avg per-item token total (input + output + cached) across the most
    /// recent <paramref name="limit"/> distinct <b>Done</b> work items that
    /// used <paramref name="agentKind"/>. "Most recent" is ordered by each
    /// work item's latest cost row. Items in non-Done states (in-flight,
    /// failed, cancelled) are excluded so partial cost rows don't bias the
    /// rolling average — the spec's Part B item 6 requires Done-only samples.
    /// </summary>
    public async Task<(long AvgTokens, int Samples)> GetAvgTokensPerItemAsync(
        string agentKind, int limit, CancellationToken ct = default)
    {
        if (limit <= 0) return (0, 0);

        // Read-only connection: this query is invoked from the rate-aware gate
        // on every dispatch tick. The store write connection `_conn` is not
        // safe to use for concurrent commands, so opening a dedicated read
        // connection avoids racing the writer (RecordAsync, DeleteByWorkItemAsync,
        // ReconcileFromAgentStreamSummaryAsync). Matches the pattern used by
        // GetByProjectAsync, GetFleetCostSummaryAsync, SummariseManyAsync.
        using var readConn = new SqliteConnection($"Data Source={_path};Mode=ReadOnly");
        readConn.Open();

        using var cmd = readConn.CreateCommand();
        cmd.CommandText = """
            SELECT AVG(total) AS avg_tokens, COUNT(*) AS n
            FROM (
                SELECT c.work_item_id,
                       SUM(c.input_tokens + c.output_tokens + c.cached_input_tokens) AS total,
                       MAX(c.started_at) AS latest
                FROM work_item_costs c
                JOIN work_items w ON w.id = c.work_item_id
                WHERE c.agent_kind = $kind
                  AND w.state = $done
                  AND c.has_extracted_token_usage = 1
                GROUP BY c.work_item_id
                ORDER BY latest DESC
                LIMIT $lim
            )
            """;
        cmd.Parameters.AddWithValue("$kind", agentKind);
        cmd.Parameters.AddWithValue("$done", (int)WorkItemState.Done);
        cmd.Parameters.AddWithValue("$lim", limit);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return (0, 0);
        if (reader.IsDBNull(0)) return (0, 0);
        var avg = reader.GetDouble(0);
        var n = reader.GetInt32(1);
        return ((long)Math.Round(avg), n);
    }

    public async Task DeleteByWorkItemAsync(string workItemId, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM work_item_costs WHERE work_item_id = $wi";
            cmd.Parameters.AddWithValue("$wi", workItemId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task ReconcileFromAgentStreamSummaryAsync(AgentStreamSummaryRow row, CancellationToken ct = default)
    {
        if (row.Summary.EstimatedUsd is null && !HasExtractedTokens(row.Summary))
            return;

        await _writeLock.WaitAsync(ct);
        try
        {
            var target = await FindReconcileTargetAsync(row, ct).ConfigureAwait(false);
            var usd = ResolveReconciledEstimatedUsd(row, target?.ModelId);

            if (target is not null)
            {
                using var update = _conn.CreateCommand();
                update.CommandText = """
                    UPDATE work_item_costs
                    SET input_tokens = $input,
                        cached_input_tokens = $cached,
                        output_tokens = $output,
                        estimated_usd = $usd,
                        raw_metadata_json = $meta,
                        has_extracted_token_usage = 1,
                        usage_contract_version = 1
                    WHERE id = $target
                    """;
                BindReconcile(update, row, usd);
                update.Parameters.AddWithValue("$target", target.Id);
                var changed = await update.ExecuteNonQueryAsync(ct);
                if (changed > 0)
                    return;
            }

            using var insert = _conn.CreateCommand();
            insert.CommandText = """
                INSERT INTO work_item_costs
                    (id, work_item_id, phase, iteration, agent_kind, agent_instance_id, model_id,
                     input_tokens, cached_input_tokens, output_tokens,
                     estimated_usd, started_at, ended_at, raw_metadata_json,
                     has_extracted_token_usage, usage_contract_version)
                VALUES
                    ($id, $wi, $phase, $iter, $kind, NULL, NULL,
                     $input, $cached, $output, $usd, $started, $ended, $meta,
                     1, 1)
                """;
            BindReconcile(insert, row, usd);
            insert.Parameters.AddWithValue("$id", $"stream-{row.WorkItemId}-{row.FileName}");
            var ended = row.SummarisedAt;
            var started = ended - row.Summary.TotalDuration;
            insert.Parameters.AddWithValue("$started", started.ToString("O"));
            insert.Parameters.AddWithValue("$ended", ended.ToString("O"));
            await insert.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task<ReconcileTarget?> FindReconcileTargetAsync(AgentStreamSummaryRow row, CancellationToken ct)
    {
        using var select = _conn.CreateCommand();
        select.CommandText = """
            SELECT id, model_id
            FROM work_item_costs
            WHERE work_item_id = $wi
              AND phase IN ($phase, $canonicalPhase)
              AND (
                  ($iter IS NULL AND iteration IS NULL)
                  OR iteration = $iter
                  OR ($iter <= 1 AND iteration IS NULL)
              )
              AND agent_kind = $kind
            ORDER BY CASE WHEN phase = $canonicalPhase THEN 0 ELSE 1 END,
                     started_at DESC
            LIMIT 1
            """;
        BindReconcileIdentity(select, row);
        using var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;

        return new ReconcileTarget(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    private decimal ResolveReconciledEstimatedUsd(AgentStreamSummaryRow row, string? modelId)
    {
        if (row.Summary.EstimatedUsd is { } streamUsd)
            return Math.Max(0m, streamUsd);

        if (_costCalculator is null || !HasExtractedTokens(row.Summary))
            return 0m;

        return Math.Max(0m, _costCalculator.Calculate(new AgentCostSnapshot(
            row.Summary.InputTokens,
            row.Summary.CachedInputTokens,
            row.Summary.OutputTokens,
            modelId), row.AgentKind));
    }

    public async Task<decimal> SumEstimatedUsdAsync(
        string projectId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        using var readConn = new SqliteConnection($"Data Source={_path};Mode=ReadOnly");
        readConn.Open();

        using var cmd = readConn.CreateCommand();
        // Single aggregation query; idx_costs_project_time covers (work_item_id, started_at)
        // and the join to work_items on project_id is fast with that index.
        cmd.CommandText = """
            SELECT COALESCE(SUM(c.estimated_usd), 0.0)
            FROM work_item_costs c
            JOIN work_items w ON w.id = c.work_item_id
            WHERE w.project_id = $proj
              AND c.started_at >= $from
              AND c.started_at < $to
            """;
        cmd.Parameters.AddWithValue("$proj", projectId);
        cmd.Parameters.AddWithValue("$from", from.ToString("O"));
        cmd.Parameters.AddWithValue("$to", to.ToString("O"));

        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToDecimal(result ?? 0.0);
    }

    private static WorkItemCost ReadRow(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        WorkItemId = r.GetString(1),
        Phase = r.GetString(2),
        Iteration = r.IsDBNull(3) ? null : r.GetInt32(3),
        AgentKind = r.GetString(4),
        AgentInstanceId = r.IsDBNull(5) ? null : r.GetString(5),
        ModelId = r.IsDBNull(6) ? null : r.GetString(6),
        InputTokens = r.GetInt32(7),
        CachedInputTokens = r.GetInt32(8),
        OutputTokens = r.GetInt32(9),
        EstimatedUsd = r.GetDouble(10),
        StartedAt = DateTimeOffset.Parse(r.GetString(11)),
        EndedAt = DateTimeOffset.Parse(r.GetString(12)),
        RawMetadataJson = r.GetString(13),
        HasExtractedTokenUsage = r.GetInt32(14) != 0,
    };

    private void AddWorkItemCostColumnIfMissing(string columnName, string sql)
    {
        if (WorkItemCostColumnExists(columnName))
            return;

        using var cmd = _conn.CreateCommand();
        // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- all callers pass hardcoded DDL literals; no user-supplied input reaches this method
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private bool WorkItemCostColumnExists(string columnName)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(work_item_costs);";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private void MarkLegacyElapsedFallbackRows()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            UPDATE work_item_costs
            SET has_extracted_token_usage = 0
            WHERE raw_metadata_json LIKE $legacy
            """;
        cmd.Parameters.AddWithValue("$legacy", $"%\"{LegacyElapsedFallbackMetadataSource}\"%");
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// One-shot data migration from the pre-fix usage contract to the new
    /// fresh-only contract. The new aggregator computes
    /// total = input + cached, so any legacy row whose input column already
    /// included the cached subset must have cached subtracted to avoid
    /// double-counting on every public reporting surface.
    ///
    /// Pre-fix shapes:
    ///   - Claude rows: input = TOTAL prompt (fresh + cache_creation +
    ///     cache_read), cached = cache_read. Always input >= cached.
    ///     Subtraction is correct.
    ///   - OpenCode rows extracted from the OpenAI shape: input =
    ///     prompt_tokens (TOTAL, includes cached), cached =
    ///     prompt_tokens_details.cached_tokens. Always input >= cached.
    ///     Subtraction is correct.
    ///   - OpenCode rows extracted from the Anthropic shape: input =
    ///     input_tokens (ALREADY FRESH-ONLY per Anthropic's spec),
    ///     cached = cache_read_input_tokens. May have cached > input on
    ///     warm sessions. Subtraction would corrupt these rows.
    ///   - Codex rows: cached was always 0 pre-fix, so subtraction is a no-op.
    ///
    /// We cannot distinguish OpenCode OpenAI-shape from Anthropic-shape on a
    /// legacy row (raw_metadata_json didn't capture the shape pre-fix), so we
    /// take the conservative route: skip ALL opencode rows from the
    /// subtraction. Pre-fix OpenAI-shape OpenCode rows will over-report
    /// tokens until re-extracted, but that shape was not observed in
    /// practice; corrupting genuine Anthropic-shape rows would be
    /// irreversible. We also skip any row where cached > input as a
    /// belt-and-braces guard — under the pre-fix TOTAL-includes-cached
    /// contract that is impossible, so such rows must already be fresh-only.
    ///
    /// Skipped rows are still stamped at usage_contract_version = 1 so the
    /// migration is one-shot — they are now (correctly or imperfectly)
    /// considered to be on the new contract.
    /// </summary>
    private void MigrateLegacyInputTokensToFreshOnly()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            UPDATE work_item_costs
            SET input_tokens = CASE
                    WHEN agent_kind = 'opencode' THEN input_tokens
                    WHEN input_tokens < cached_input_tokens THEN input_tokens
                    ELSE input_tokens - cached_input_tokens
                END,
                usage_contract_version = 1
            WHERE usage_contract_version = 0
            """;
        cmd.ExecuteNonQuery();
    }

    private static void BindReconcile(SqliteCommand cmd, AgentStreamSummaryRow row, decimal estimatedUsd)
    {
        BindReconcileIdentity(cmd, row);
        cmd.Parameters.AddWithValue("$input", row.Summary.InputTokens);
        cmd.Parameters.AddWithValue("$cached", row.Summary.CachedInputTokens);
        cmd.Parameters.AddWithValue("$output", row.Summary.OutputTokens);
        cmd.Parameters.AddWithValue("$usd", (double)estimatedUsd);
        cmd.Parameters.AddWithValue("$meta", $$"""{"source":"agent_stream_analyser","fileName":{{JsonSerializer.Serialize(row.FileName)}},"streamPhase":{{JsonSerializer.Serialize(row.Phase)}}}""");
    }

    private static void BindReconcileIdentity(SqliteCommand cmd, AgentStreamSummaryRow row)
    {
        cmd.Parameters.AddWithValue("$wi", row.WorkItemId.ToString());
        cmd.Parameters.AddWithValue("$phase", row.Phase);
        cmd.Parameters.AddWithValue("$canonicalPhase", CanonicalCostPhase(row.Phase));
        cmd.Parameters.AddWithValue("$iter", row.Iteration.HasValue ? row.Iteration.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$kind", row.AgentKind.Value);
    }

    private sealed record ReconcileTarget(string Id, string? ModelId);

    private static string CanonicalCostPhase(string phase) =>
        phase.StartsWith("audit-llm-", StringComparison.OrdinalIgnoreCase)
            ? "audit"
            : phase;

    private static bool HasExtractedTokens(AgentStreamSummary summary) =>
        summary.InputTokens > 0
        || summary.CachedInputTokens > 0
        || summary.OutputTokens > 0;

    public void Dispose()
    {
        _insertCmd.Dispose();
        _conn.Dispose();
        _writeLock.Dispose();
    }
}
