using Microsoft.Data.Sqlite;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// SQLite-backed cost store. Writes to the same database file as
/// <see cref="SqliteWorkItemStore"/>; the work_item_costs table is created
/// here via an additive migration.
///
/// A prepared INSERT keeps the hot-path overhead well under 50 ms per call.
/// </summary>
public sealed class SqliteWorkItemCostStore : IWorkItemCostStore, IDisposable
{
    private readonly string _path;
    private readonly SqliteConnection _conn;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SqliteCommand _insertCmd;

    public SqliteWorkItemCostStore(string path)
    {
        _path = path;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _conn = new SqliteConnection($"Data Source={path}");
        _conn.Open();

        using (var pragmaCmd = _conn.CreateCommand())
        {
            pragmaCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
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
                model_id            TEXT,
                input_tokens        INTEGER NOT NULL,
                cached_input_tokens INTEGER NOT NULL DEFAULT 0,
                output_tokens       INTEGER NOT NULL,
                estimated_usd       REAL NOT NULL DEFAULT 0,
                started_at          TEXT NOT NULL,
                ended_at            TEXT NOT NULL,
                raw_metadata_json   TEXT NOT NULL DEFAULT '{}'
            );
            CREATE INDEX IF NOT EXISTS idx_costs_work_item
                ON work_item_costs(work_item_id, phase, iteration);
            CREATE INDEX IF NOT EXISTS idx_costs_project_time
                ON work_item_costs(work_item_id, started_at);
            """;
        createCmd.ExecuteNonQuery();

        _insertCmd = _conn.CreateCommand();
        _insertCmd.CommandText = """
            INSERT INTO work_item_costs
                (id, work_item_id, phase, iteration, agent_kind, model_id,
                 input_tokens, cached_input_tokens, output_tokens,
                 estimated_usd, started_at, ended_at, raw_metadata_json)
            VALUES
                ($id, $wi, $phase, $iter, $kind, $model,
                 $input, $cached, $output,
                 $usd, $started, $ended, $meta)
            """;
        _insertCmd.Parameters.Add("$id", SqliteType.Text);
        _insertCmd.Parameters.Add("$wi", SqliteType.Text);
        _insertCmd.Parameters.Add("$phase", SqliteType.Text);
        _insertCmd.Parameters.Add("$iter", SqliteType.Integer);
        _insertCmd.Parameters.Add("$kind", SqliteType.Text);
        _insertCmd.Parameters.Add("$model", SqliteType.Text);
        _insertCmd.Parameters.Add("$input", SqliteType.Integer);
        _insertCmd.Parameters.Add("$cached", SqliteType.Integer);
        _insertCmd.Parameters.Add("$output", SqliteType.Integer);
        _insertCmd.Parameters.Add("$usd", SqliteType.Real);
        _insertCmd.Parameters.Add("$started", SqliteType.Text);
        _insertCmd.Parameters.Add("$ended", SqliteType.Text);
        _insertCmd.Parameters.Add("$meta", SqliteType.Text);
        _insertCmd.Prepare();
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
            _insertCmd.Parameters["$model"].Value = cost.ModelId is not null ? (object)cost.ModelId : DBNull.Value;
            _insertCmd.Parameters["$input"].Value = cost.InputTokens;
            _insertCmd.Parameters["$cached"].Value = cost.CachedInputTokens;
            _insertCmd.Parameters["$output"].Value = cost.OutputTokens;
            _insertCmd.Parameters["$usd"].Value = cost.EstimatedUsd;
            _insertCmd.Parameters["$started"].Value = cost.StartedAt.ToString("O");
            _insertCmd.Parameters["$ended"].Value = cost.EndedAt.ToString("O");
            _insertCmd.Parameters["$meta"].Value = cost.RawMetadataJson;
            await _insertCmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeLock.Release();
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
            SELECT id, work_item_id, phase, iteration, agent_kind, model_id,
                   input_tokens, cached_input_tokens, output_tokens,
                   estimated_usd, started_at, ended_at, raw_metadata_json
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

    public async Task<IReadOnlyList<WorkItemCost>> GetByProjectAsync(
        string projectId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        // Read-only query: open a separate read connection to avoid holding the write lock
        // during what could be a multi-row scan.
        using var readConn = new SqliteConnection($"Data Source={_path};Mode=ReadOnly");
        readConn.Open();

        using var cmd = readConn.CreateCommand();
        cmd.CommandText = """
            SELECT c.id, c.work_item_id, c.phase, c.iteration, c.agent_kind, c.model_id,
                   c.input_tokens, c.cached_input_tokens, c.output_tokens,
                   c.estimated_usd, c.started_at, c.ended_at, c.raw_metadata_json
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
        ModelId = r.IsDBNull(5) ? null : r.GetString(5),
        InputTokens = r.GetInt32(6),
        CachedInputTokens = r.GetInt32(7),
        OutputTokens = r.GetInt32(8),
        EstimatedUsd = r.GetDouble(9),
        StartedAt = DateTimeOffset.Parse(r.GetString(10)),
        EndedAt = DateTimeOffset.Parse(r.GetString(11)),
        RawMetadataJson = r.GetString(12),
    };

    public void Dispose()
    {
        _insertCmd.Dispose();
        _conn.Dispose();
        _writeLock.Dispose();
    }
}
