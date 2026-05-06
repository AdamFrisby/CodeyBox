using Microsoft.Data.Sqlite;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

public sealed class SqliteQuotaFailureStore : IQuotaFailureStore, IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public SqliteQuotaFailureStore(string path)
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

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS quota_failures (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                agent TEXT NOT NULL,
                model_id TEXT,
                failure_kind TEXT NOT NULL,
                observed_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_quota_failures_agent_model_observed
                ON quota_failures(agent, model_id, observed_at);
            CREATE INDEX IF NOT EXISTS idx_quota_failures_observed
                ON quota_failures(observed_at);
            """;
        cmd.ExecuteNonQuery();
    }

    public async Task RecordAsync(AgentKind agent, string? modelId, QuotaFailureKind kind, DateTimeOffset observedAt, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO quota_failures (agent, model_id, failure_kind, observed_at)
                VALUES ($agent, $model_id, $failure_kind, $observed_at);
                """;
            cmd.Parameters.AddWithValue("$agent", agent.Value);
            cmd.Parameters.AddWithValue("$model_id", modelId is null ? DBNull.Value : modelId);
            cmd.Parameters.AddWithValue("$failure_kind", kind.ToString());
            cmd.Parameters.AddWithValue("$observed_at", observedAt.ToUniversalTime().ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> HasRecentAsync(AgentKind agent, string? modelId, TimeSpan window, DateTimeOffset now, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var cutoff = now.ToUniversalTime() - window;
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT 1
                FROM quota_failures
                WHERE agent = $agent
                  AND ($model_id IS NULL OR model_id = $model_id)
                  AND observed_at >= $cutoff
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$agent", agent.Value);
            cmd.Parameters.AddWithValue("$model_id", modelId is null ? DBNull.Value : modelId);
            cmd.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));
            var result = await cmd.ExecuteScalarAsync(ct);
            return result is not null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<QuotaFailureObservation>> ListRecentAsync(TimeSpan window, DateTimeOffset now, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var cutoff = now.ToUniversalTime() - window;
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT agent, model_id, failure_kind, observed_at
                FROM quota_failures
                WHERE observed_at >= $cutoff
                ORDER BY observed_at DESC;
                """;
            cmd.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));

            var rows = new List<QuotaFailureObservation>();
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var kind = Enum.TryParse<QuotaFailureKind>(reader.GetString(2), out var parsed)
                    ? parsed
                    : QuotaFailureKind.LimitReached;
                var observedAt = DateTimeOffset.TryParse(reader.GetString(3), out var parsedAt)
                    ? parsedAt
                    : DateTimeOffset.UnixEpoch;
                rows.Add(new QuotaFailureObservation(
                    new AgentKind(reader.GetString(0)),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    kind,
                    observedAt));
            }

            return rows;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task PruneOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM quota_failures WHERE observed_at < $cutoff;";
            cmd.Parameters.AddWithValue("$cutoff", cutoff.ToUniversalTime().ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose() => _conn.Dispose();
}
