using System.Runtime.CompilerServices;
using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Core;
using Microsoft.Data.Sqlite;

namespace CodeyBox.Orchestrator;

public interface IAgentStreamSummaryStore
{
    Task UpsertAsync(AgentStreamSummaryRow row, CancellationToken ct = default);
    Task<IReadOnlyList<AgentStreamSummaryRow>> GetByWorkItemAsync(WorkItemId workItemId, CancellationToken ct = default);
    IAsyncEnumerable<AgentStreamSummaryRow> StreamRecentCompletedAsync(int workItemLimit, CancellationToken ct = default);
    Task DeleteByWorkItemAsync(WorkItemId workItemId, CancellationToken ct = default);
}

public sealed class SqliteAgentStreamSummaryStore : IAgentStreamSummaryStore, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _path;
    private readonly SqliteConnection _conn;
    private readonly SqliteDatabaseWriteGate _writeLock;

    public SqliteAgentStreamSummaryStore(
        string path,
        SqliteDatabaseWriteGateFactory? writeGateFactory = null)
    {
        _path = path;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _conn = new SqliteConnection($"Data Source={path}");
        _writeLock = SqliteDatabaseWriteGateFactory.Resolve(writeGateFactory).ForPath(path);
        _writeLock.Wait();
        try
        {
            _conn.Open();

            using (var pragma = _conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=30000; PRAGMA foreign_keys=ON;";
                pragma.ExecuteNonQuery();
            }

            using var create = _conn.CreateCommand();
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS agent_stream_summaries (
                    work_item_id    TEXT NOT NULL REFERENCES work_items(id) ON DELETE CASCADE,
                    file_name       TEXT NOT NULL,
                    phase           TEXT NOT NULL,
                    iteration       INTEGER,
                    agent_kind      TEXT NOT NULL,
                    total_duration_ms INTEGER NOT NULL,
                    time_to_first_token_ms INTEGER,
                    input_tokens    INTEGER,
                    output_tokens   INTEGER,
                    cached_input_tokens INTEGER,
                    estimated_usd   REAL,
                    tool_calls_json TEXT NOT NULL,
                    stalls_json     TEXT NOT NULL,
                    final_assistant_message TEXT,
                    summarised_at   TEXT NOT NULL,
                    PRIMARY KEY (work_item_id, file_name)
                );
                CREATE INDEX IF NOT EXISTS idx_summaries_work_item ON agent_stream_summaries(work_item_id);
                """;
            create.ExecuteNonQuery();
            AddFinalAssistantMessageColumn();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task UpsertAsync(AgentStreamSummaryRow row, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO agent_stream_summaries
                    (work_item_id, file_name, phase, iteration, agent_kind, total_duration_ms,
                     time_to_first_token_ms, input_tokens, output_tokens, cached_input_tokens,
                     estimated_usd, tool_calls_json, stalls_json, final_assistant_message, summarised_at)
                VALUES
                    ($wid, $file, $phase, $iter, $kind, $total, $ttft, $input, $output, $cached,
                     $usd, $tools, $stalls, $final, $summarised)
                ON CONFLICT(work_item_id, file_name) DO UPDATE SET
                    phase = excluded.phase,
                    iteration = excluded.iteration,
                    agent_kind = excluded.agent_kind,
                    total_duration_ms = excluded.total_duration_ms,
                    time_to_first_token_ms = excluded.time_to_first_token_ms,
                    input_tokens = excluded.input_tokens,
                    output_tokens = excluded.output_tokens,
                    cached_input_tokens = excluded.cached_input_tokens,
                    estimated_usd = excluded.estimated_usd,
                    tool_calls_json = excluded.tool_calls_json,
                    stalls_json = excluded.stalls_json,
                    final_assistant_message = excluded.final_assistant_message,
                    summarised_at = excluded.summarised_at
                """;
            Bind(cmd, row);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<AgentStreamSummaryRow>> GetByWorkItemAsync(WorkItemId workItemId, CancellationToken ct = default)
    {
        using var readConn = new SqliteConnection($"Data Source={_path};Mode=ReadOnly");
        readConn.Open();
        using var cmd = readConn.CreateCommand();
        cmd.CommandText = """
            SELECT work_item_id, file_name, phase, iteration, agent_kind, total_duration_ms,
                   time_to_first_token_ms, input_tokens, output_tokens, cached_input_tokens,
                   estimated_usd, tool_calls_json, stalls_json, final_assistant_message, summarised_at
            FROM agent_stream_summaries
            WHERE work_item_id = $wid
            ORDER BY phase, iteration, file_name
            """;
        cmd.Parameters.AddWithValue("$wid", workItemId.ToString());

        var rows = new List<AgentStreamSummaryRow>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add(ReadRow(reader));
        return rows;
    }

    public async IAsyncEnumerable<AgentStreamSummaryRow> StreamRecentCompletedAsync(
        int workItemLimit,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var readConn = new SqliteConnection($"Data Source={_path};Mode=ReadOnly");
        readConn.Open();
        using var cmd = readConn.CreateCommand();
        cmd.CommandText = """
            SELECT s.work_item_id, s.file_name, s.phase, s.iteration, s.agent_kind, s.total_duration_ms,
                   s.time_to_first_token_ms, s.input_tokens, s.output_tokens, s.cached_input_tokens,
                   s.estimated_usd, s.tool_calls_json, s.stalls_json, s.final_assistant_message, s.summarised_at
            FROM agent_stream_summaries s
            JOIN (
                SELECT id FROM work_items
                WHERE state IN ($done, $failed, $auditFailed, $cancelled, $abandoned)
                ORDER BY updated_at DESC
                LIMIT $lim
            ) w ON w.id = s.work_item_id
            ORDER BY s.work_item_id, s.phase, s.iteration, s.file_name
            """;
        cmd.Parameters.AddWithValue("$lim", Math.Clamp(workItemLimit, 1, 500));
        cmd.Parameters.AddWithValue("$done", (int)WorkItemState.Done);
        cmd.Parameters.AddWithValue("$failed", (int)WorkItemState.Failed);
        cmd.Parameters.AddWithValue("$auditFailed", (int)WorkItemState.AuditFailed);
        cmd.Parameters.AddWithValue("$cancelled", (int)WorkItemState.Cancelled);
        cmd.Parameters.AddWithValue("$abandoned", (int)WorkItemState.AbandonedAfterRecoveryAttempts);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            yield return ReadRow(reader);
    }

    public async Task DeleteByWorkItemAsync(WorkItemId workItemId, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM agent_stream_summaries WHERE work_item_id = $wid";
            cmd.Parameters.AddWithValue("$wid", workItemId.ToString());
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void AddFinalAssistantMessageColumn()
    {
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "ALTER TABLE agent_stream_summaries ADD COLUMN final_assistant_message TEXT;";
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
        {
        }
    }

    private static void Bind(SqliteCommand cmd, AgentStreamSummaryRow row)
    {
        cmd.Parameters.AddWithValue("$wid", row.WorkItemId.ToString());
        cmd.Parameters.AddWithValue("$file", row.FileName);
        cmd.Parameters.AddWithValue("$phase", row.Phase);
        cmd.Parameters.AddWithValue("$iter", row.Iteration.HasValue ? row.Iteration.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$kind", row.AgentKind.Value);
        cmd.Parameters.AddWithValue("$total", ToMs(row.Summary.TotalDuration));
        cmd.Parameters.AddWithValue("$ttft", row.Summary.TimeToFirstToken.HasValue ? ToMs(row.Summary.TimeToFirstToken.Value) : DBNull.Value);
        cmd.Parameters.AddWithValue("$input", row.Summary.InputTokens);
        cmd.Parameters.AddWithValue("$output", row.Summary.OutputTokens);
        cmd.Parameters.AddWithValue("$cached", row.Summary.CachedInputTokens);
        cmd.Parameters.AddWithValue("$usd", row.Summary.EstimatedUsd.HasValue ? (double)row.Summary.EstimatedUsd.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$tools", JsonSerializer.Serialize(row.Summary.ToolCalls, JsonOptions));
        cmd.Parameters.AddWithValue("$stalls", JsonSerializer.Serialize(row.Summary.Stalls, JsonOptions));
        cmd.Parameters.AddWithValue("$final", row.Summary.FinalAssistantMessage is null
            ? DBNull.Value
            : row.Summary.FinalAssistantMessage);
        cmd.Parameters.AddWithValue("$summarised", row.SummarisedAt.ToString("O"));
    }

    private static AgentStreamSummaryRow ReadRow(SqliteDataReader r)
    {
        var tools = JsonSerializer.Deserialize<List<ToolCallInvocation>>(r.GetString(11), JsonOptions) ?? [];
        var stalls = JsonSerializer.Deserialize<List<StallEvent>>(r.GetString(12), JsonOptions) ?? [];
        var summary = new AgentStreamSummary(
            TimeSpan.FromMilliseconds(r.GetInt64(5)),
            r.IsDBNull(6) ? null : TimeSpan.FromMilliseconds(r.GetInt64(6)),
            r.IsDBNull(7) ? 0 : r.GetInt32(7),
            r.IsDBNull(8) ? 0 : r.GetInt32(8),
            r.IsDBNull(9) ? 0 : r.GetInt32(9),
            r.IsDBNull(10) ? null : Convert.ToDecimal(r.GetDouble(10)),
            tools,
            stalls,
            r.IsDBNull(13) ? null : r.GetString(13));

        return new AgentStreamSummaryRow(
            new WorkItemId(Guid.Parse(r.GetString(0))),
            r.GetString(1),
            r.GetString(2),
            r.IsDBNull(3) ? null : r.GetInt32(3),
            new AgentKind(r.GetString(4)),
            summary,
            DateTimeOffset.Parse(r.GetString(14)));
    }

    private static long ToMs(TimeSpan value) => Math.Max(0, (long)Math.Round(value.TotalMilliseconds));

    public void Dispose()
    {
        _conn.Dispose();
        _writeLock.Dispose();
    }
}
