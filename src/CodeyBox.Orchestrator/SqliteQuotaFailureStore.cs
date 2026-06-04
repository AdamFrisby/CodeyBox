using Microsoft.Data.Sqlite;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

public sealed class SqliteQuotaFailureStore : IQuotaFailureStore, IDisposable
{
    private const int PruneBatchSize = 500;

    private readonly SqliteConnection _conn;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly SqliteDatabaseWriteGate _lock;

    public SqliteQuotaFailureStore(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _conn = new SqliteConnection($"Data Source={path}");
        _lock = SqliteDatabaseWriteGate.ForPath(path);
        _lock.Wait();
        try
        {
            _conn.Open();

            using (var pragma = _conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=30000;";
                pragma.ExecuteNonQuery();
            }

            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS quota_failures (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    agent TEXT NOT NULL,
                    model_id TEXT,
                    project_id TEXT,
                    failure_kind TEXT NOT NULL,
                    observed_at TEXT NOT NULL
                );
                """;
            cmd.ExecuteNonQuery();
            EnsureProjectIdColumn();

            using var indexCmd = _conn.CreateCommand();
            indexCmd.CommandText = """
                CREATE INDEX IF NOT EXISTS idx_quota_failures_agent_model_observed
                    ON quota_failures(agent, model_id, observed_at);
                CREATE INDEX IF NOT EXISTS idx_quota_failures_project_agent_model_observed
                    ON quota_failures(project_id, agent, model_id, observed_at);
                CREATE INDEX IF NOT EXISTS idx_quota_failures_observed
                    ON quota_failures(observed_at);
                """;
            indexCmd.ExecuteNonQuery();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RecordAsync(AgentKind agent, string? modelId, QuotaFailureKind kind, DateTimeOffset observedAt, CancellationToken ct = default)
        => await RecordCoreAsync(agent, modelId, projectId: null, kind, observedAt, ct);

    public async Task RecordForProjectAsync(AgentKind agent, string? modelId, ProjectId projectId, QuotaFailureKind kind, DateTimeOffset observedAt, CancellationToken ct = default)
        => await RecordCoreAsync(agent, modelId, projectId, kind, observedAt, ct);

    private async Task RecordCoreAsync(AgentKind agent, string? modelId, ProjectId? projectId, QuotaFailureKind kind, DateTimeOffset observedAt, CancellationToken ct)
    {
        await _connectionLock.WaitAsync(ct);
        try
        {
            await _lock.WaitAsync(ct);
            try
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO quota_failures (agent, model_id, project_id, failure_kind, observed_at)
                    VALUES ($agent, $model_id, $project_id, $failure_kind, $observed_at);
                    """;
                cmd.Parameters.AddWithValue("$agent", agent.Value);
                cmd.Parameters.AddWithValue("$model_id", modelId is null ? DBNull.Value : modelId);
                cmd.Parameters.AddWithValue("$project_id", projectId is null ? DBNull.Value : projectId.Value.Value);
                cmd.Parameters.AddWithValue("$failure_kind", kind.ToString());
                cmd.Parameters.AddWithValue("$observed_at", observedAt.ToUniversalTime().ToString("O"));
                await cmd.ExecuteNonQueryAsync(ct);
            }
            finally
            {
                _lock.Release();
            }
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task<bool> HasRecentAsync(AgentKind agent, string? modelId, TimeSpan window, DateTimeOffset now, CancellationToken ct = default)
        => await HasRecentCoreAsync(agent, modelId, window, now, ct);

    private async Task<bool> HasRecentCoreAsync(
        AgentKind agent,
        string? modelId,
        TimeSpan window,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await _connectionLock.WaitAsync(ct);
        try
        {
            var cutoff = now.ToUniversalTime() - window;
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT 1
                FROM quota_failures
                WHERE agent = $agent
                  AND (($model_id IS NULL AND model_id IS NULL) OR model_id = $model_id)
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
            _connectionLock.Release();
        }
    }

    public async Task<DateTimeOffset?> GetMostRecentAsync(AgentKind agent, string? modelId, TimeSpan window, DateTimeOffset now, CancellationToken ct = default)
    {
        await _connectionLock.WaitAsync(ct);
        try
        {
            var cutoff = now.ToUniversalTime() - window;
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT observed_at
                FROM quota_failures
                WHERE agent = $agent
                  AND (($model_id IS NULL AND model_id IS NULL) OR model_id = $model_id)
                  AND observed_at >= $cutoff
                ORDER BY observed_at DESC
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$agent", agent.Value);
            cmd.Parameters.AddWithValue("$model_id", modelId is null ? DBNull.Value : modelId);
            cmd.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));
            var result = await cmd.ExecuteScalarAsync(ct);
            if (result is null or DBNull) return null;
            return DateTimeOffset.TryParse(result.ToString(), out var parsed) ? parsed : null;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task<IReadOnlyList<QuotaFailureObservation>> ListRecentAsync(TimeSpan window, DateTimeOffset now, CancellationToken ct = default)
    {
        await _connectionLock.WaitAsync(ct);
        try
        {
            var cutoff = now.ToUniversalTime() - window;
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT agent, model_id, failure_kind, observed_at, project_id
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
                    observedAt,
                    reader.IsDBNull(4) ? null : new ProjectId(reader.GetString(4))));
            }

            return rows;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task PruneOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        var cutoffText = cutoff.ToUniversalTime().ToString("O");

        while (true)
        {
            await _connectionLock.WaitAsync(ct);
            try
            {
                await _lock.WaitAsync(ct);
                try
                {
                    using var cmd = _conn.CreateCommand();
                    cmd.CommandText = """
                        DELETE FROM quota_failures
                        WHERE rowid IN (
                            SELECT rowid
                            FROM quota_failures
                            WHERE observed_at < $cutoff
                            LIMIT $limit
                        );
                        """;
                    cmd.Parameters.AddWithValue("$cutoff", cutoffText);
                    cmd.Parameters.AddWithValue("$limit", PruneBatchSize);
                    var deleted = await cmd.ExecuteNonQueryAsync(ct);
                    if (deleted < PruneBatchSize)
                        return;
                }
                finally
                {
                    _lock.Release();
                }
            }
            finally
            {
                _connectionLock.Release();
            }

            await Task.Yield();
        }
    }

    public void Dispose()
    {
        _conn.Dispose();
        _connectionLock.Dispose();
        _lock.Dispose();
    }

    private void EnsureProjectIdColumn()
    {
        using var columns = _conn.CreateCommand();
        columns.CommandText = "PRAGMA table_info(quota_failures);";
        using var reader = columns.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), "project_id", StringComparison.OrdinalIgnoreCase))
                return;
        }

        using var alter = _conn.CreateCommand();
        alter.CommandText = "ALTER TABLE quota_failures ADD COLUMN project_id TEXT;";
        alter.ExecuteNonQuery();

        using var index = _conn.CreateCommand();
        index.CommandText = """
            CREATE INDEX IF NOT EXISTS idx_quota_failures_project_agent_model_observed
                ON quota_failures(project_id, agent, model_id, observed_at);
            """;
        index.ExecuteNonQuery();
    }
}
