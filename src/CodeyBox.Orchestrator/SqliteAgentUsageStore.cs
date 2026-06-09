using Microsoft.Data.Sqlite;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// SQLite-backed <see cref="IAgentUsageStore"/>. Writes to the same database
/// file as the other stores; the <c>agent_usage_events</c> table is created here
/// via an additive migration. Independent of <c>work_item_costs</c> (no FK) so
/// budget accounting survives work-item deletion.
///
/// A prepared INSERT keeps the hot-path overhead small. Reads open dedicated
/// read-only connections so they never race the store's write connection.
/// </summary>
public sealed class SqliteAgentUsageStore : IAgentUsageStore, IDisposable
{
    private readonly string _path;
    private readonly SqliteConnection _conn;
    private readonly SqliteDatabaseWriteGate _writeLock;
    private readonly SqliteCommand _insertCmd;
    private const int PruneBatchSize = 500;

    public SqliteAgentUsageStore(string path)
    {
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
                pragmaCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=30000;";
                pragmaCmd.ExecuteNonQuery();
            }

            using var createCmd = _conn.CreateCommand();
            // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- hardcoded DDL only
            // The agent_instance_id-bearing index lives in the post-ALTER block below;
            // creating it here would crash startup against a pre-pooling DB whose
            // agent_usage_events table is missing the column (SQLite resolves index
            // expressions at CREATE INDEX time, ignoring IF NOT EXISTS).
            createCmd.CommandText = """
                CREATE TABLE IF NOT EXISTS agent_usage_events (
                    id                  TEXT PRIMARY KEY,
                    time_utc            TEXT NOT NULL,
                    agent_kind          TEXT NOT NULL,
                    agent_instance_id   TEXT,
                    model_id            TEXT,
                    phase               TEXT,
                    started_utc         TEXT,
                    ended_utc           TEXT,
                    elapsed_ms          INTEGER NOT NULL DEFAULT 0,
                    input_tokens        INTEGER NOT NULL,
                    cached_input_tokens INTEGER NOT NULL DEFAULT 0,
                    output_tokens       INTEGER NOT NULL,
                    cost_microcents     INTEGER NOT NULL DEFAULT 0,
                    work_item_id        TEXT
                );
                CREATE INDEX IF NOT EXISTS idx_usage_agent_model_time
                    ON agent_usage_events(agent_kind, model_id, time_utc);
                CREATE INDEX IF NOT EXISTS idx_usage_time
                    ON agent_usage_events(time_utc);
                """;
            createCmd.ExecuteNonQuery();
            AddUsageColumnIfMissing("agent_instance_id", "ALTER TABLE agent_usage_events ADD COLUMN agent_instance_id TEXT;");

            using (var indexCmd = _conn.CreateCommand())
            {
                indexCmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_usage_instance_model_time ON agent_usage_events(agent_instance_id, model_id, time_utc) WHERE agent_instance_id IS NOT NULL;";
                indexCmd.ExecuteNonQuery();
            }

            AddUsageColumnIfMissing("phase", "ALTER TABLE agent_usage_events ADD COLUMN phase TEXT;");
            AddUsageColumnIfMissing("started_utc", "ALTER TABLE agent_usage_events ADD COLUMN started_utc TEXT;");
            AddUsageColumnIfMissing("ended_utc", "ALTER TABLE agent_usage_events ADD COLUMN ended_utc TEXT;");
            AddUsageColumnIfMissing("elapsed_ms", "ALTER TABLE agent_usage_events ADD COLUMN elapsed_ms INTEGER NOT NULL DEFAULT 0;");

            _insertCmd = _conn.CreateCommand();
            _insertCmd.CommandText = """
                INSERT INTO agent_usage_events
                    (id, time_utc, agent_kind, agent_instance_id, model_id,
                     phase, started_utc, ended_utc, elapsed_ms,
                     input_tokens, cached_input_tokens, output_tokens,
                     cost_microcents, work_item_id)
                VALUES
                    ($id, $time, $kind, $instance, $model,
                     $phase, $started, $ended, $elapsed,
                     $input, $cached, $output,
                     $cost, $wi)
                """;
            _insertCmd.Parameters.Add("$id", SqliteType.Text);
            _insertCmd.Parameters.Add("$time", SqliteType.Text);
            _insertCmd.Parameters.Add("$kind", SqliteType.Text);
            _insertCmd.Parameters.Add("$instance", SqliteType.Text);
            _insertCmd.Parameters.Add("$model", SqliteType.Text);
            _insertCmd.Parameters.Add("$phase", SqliteType.Text);
            _insertCmd.Parameters.Add("$started", SqliteType.Text);
            _insertCmd.Parameters.Add("$ended", SqliteType.Text);
            _insertCmd.Parameters.Add("$elapsed", SqliteType.Integer);
            _insertCmd.Parameters.Add("$input", SqliteType.Integer);
            _insertCmd.Parameters.Add("$cached", SqliteType.Integer);
            _insertCmd.Parameters.Add("$output", SqliteType.Integer);
            _insertCmd.Parameters.Add("$cost", SqliteType.Integer);
            _insertCmd.Parameters.Add("$wi", SqliteType.Text);
            _insertCmd.Prepare();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task RecordAsync(AgentUsageEvent usage, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            _insertCmd.Parameters["$id"].Value = usage.Id;
            _insertCmd.Parameters["$time"].Value = usage.TimeUtc.ToUniversalTime().ToString("O");
            _insertCmd.Parameters["$kind"].Value = usage.AgentKind;
            _insertCmd.Parameters["$instance"].Value = usage.AgentInstanceId is not null ? usage.AgentInstanceId : DBNull.Value;
            _insertCmd.Parameters["$model"].Value = usage.ModelId is not null ? usage.ModelId : DBNull.Value;
            _insertCmd.Parameters["$phase"].Value = usage.Phase is not null ? usage.Phase : DBNull.Value;
            _insertCmd.Parameters["$started"].Value = usage.StartedUtc is { } started
                ? started.ToUniversalTime().ToString("O")
                : DBNull.Value;
            _insertCmd.Parameters["$ended"].Value = usage.EndedUtc is { } ended
                ? ended.ToUniversalTime().ToString("O")
                : DBNull.Value;
            _insertCmd.Parameters["$elapsed"].Value = Math.Max(0, usage.ElapsedMs);
            _insertCmd.Parameters["$input"].Value = usage.InputTokens;
            _insertCmd.Parameters["$cached"].Value = usage.CachedInputTokens;
            _insertCmd.Parameters["$output"].Value = usage.OutputTokens;
            _insertCmd.Parameters["$cost"].Value = usage.CostMicroCents;
            _insertCmd.Parameters["$wi"].Value = usage.WorkItemId is not null ? usage.WorkItemId : DBNull.Value;
            await _insertCmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void AddUsageColumnIfMissing(string columnName, string sql)
    {
        if (UsageColumnExists(columnName))
            return;

        using var cmd = _conn.CreateCommand();
        // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- all callers pass hardcoded DDL literals; no user-supplied input reaches this method
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private bool UsageColumnExists(string columnName)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(agent_usage_events);";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public async Task<AgentUsageWindowAggregate> SumWindowAsync(
        string agentKind, string? modelId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
    {
        using var readConn = new SqliteConnection($"Data Source={_path};Mode=ReadOnly");
        readConn.Open();

        using var cmd = readConn.CreateCommand();
        // The ($model IS NULL AND model_id IS NULL) OR model_id = $model branch
        // matches a null-model budget only against null-model events, and a
        // specific-model budget only against that exact model's events. Both the
        // agent_kind and model_id comparisons are COLLATE NOCASE because budget
        // config keys resolve case-insensitively (OrdinalIgnoreCase). The
        // dispatch gate queries with the canonical lowercase AgentKind.Value
        // while the /quota visibility summary iterates config dictionary keys
        // verbatim (e.g. "OpenCode"); without NOCASE that casing difference would
        // sum to zero and overstate remaining budget for the visibility path.
        cmd.CommandText = """
            SELECT COALESCE(SUM(cost_microcents), 0), MIN(time_utc), COUNT(*)
            FROM agent_usage_events
            WHERE agent_kind = $kind COLLATE NOCASE
              AND (($model IS NULL AND model_id IS NULL) OR model_id = $model COLLATE NOCASE)
              AND time_utc >= $from
              AND time_utc < $to
            """;
        cmd.Parameters.AddWithValue("$kind", agentKind);
        cmd.Parameters.AddWithValue("$model", modelId is not null ? modelId : DBNull.Value);
        cmd.Parameters.AddWithValue("$from", fromUtc.ToUniversalTime().ToString("O"));
        cmd.Parameters.AddWithValue("$to", toUtc.ToUniversalTime().ToString("O"));

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return new AgentUsageWindowAggregate(0, null, 0);

        var sum = reader.GetInt64(0);
        DateTimeOffset? earliest = reader.IsDBNull(1)
            ? null
            : DateTimeOffset.Parse(reader.GetString(1)).ToUniversalTime();
        var count = reader.GetInt32(2);
        return new AgentUsageWindowAggregate(sum, earliest, count);
    }

    public async Task<int> PruneAsync(DateTimeOffset cutoffUtc, CancellationToken ct = default)
    {
        var deleted = 0;
        var cutoff = cutoffUtc.ToUniversalTime().ToString("O");
        while (true)
        {
            await _writeLock.WaitAsync(ct);
            try
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = """
                    DELETE FROM agent_usage_events
                    WHERE rowid IN (
                        SELECT rowid
                        FROM agent_usage_events
                        WHERE time_utc < $cutoff
                        LIMIT $limit
                    );
                    """;
                cmd.Parameters.AddWithValue("$cutoff", cutoff);
                cmd.Parameters.AddWithValue("$limit", PruneBatchSize);
                var batchDeleted = await cmd.ExecuteNonQueryAsync(ct);
                deleted += batchDeleted;
                if (batchDeleted < PruneBatchSize)
                    return deleted;
            }
            finally
            {
                _writeLock.Release();
            }

            await Task.Yield();
        }
    }

    public void Dispose()
    {
        _insertCmd.Dispose();
        _conn.Dispose();
        _writeLock.Dispose();
    }
}
