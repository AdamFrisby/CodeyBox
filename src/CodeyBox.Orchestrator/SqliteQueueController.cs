using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// SQLite-backed <see cref="IQueueController"/>. Persists the queue state to
/// the same database file as the work-item store (separate connection, separate
/// table). On startup the state is loaded; if paused, a warning is emitted at
/// audit tier so operators don't forget they left the queue paused.
/// </summary>
public sealed class SqliteQueueController : IQueueController, IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ILogger<SqliteQueueController> _log;

    private QueueState _state;
    private DateTimeOffset? _pausedAt;
    private string? _pausedReason;

    public QueueState State => _state;
    public DateTimeOffset? PausedAt => _pausedAt;
    public string? PausedReason => _pausedReason;

    public SqliteQueueController(string dbPath, ILogger<SqliteQueueController> log)
    {
        _log = log;
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS queue_state (
                singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
                state     INTEGER NOT NULL DEFAULT 0,
                paused_at TEXT,
                paused_reason TEXT,
                updated_at TEXT NOT NULL
            );
            INSERT OR IGNORE INTO queue_state (singleton, state, updated_at)
            VALUES (1, 0, datetime('now'));
            """;
        cmd.ExecuteNonQuery();

        LoadState();

        if (_state == QueueState.Paused)
            AuditLog.QueueStartedWhilePaused();
    }

    public async Task PauseAsync(string reason, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow;
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE queue_state
                SET state = 1, paused_at = $at, paused_reason = $reason, updated_at = $ua
                WHERE singleton = 1;
                """;
            cmd.Parameters.AddWithValue("$at", now.ToString("O"));
            cmd.Parameters.AddWithValue("$reason", reason);
            cmd.Parameters.AddWithValue("$ua", now.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);

            _state = QueueState.Paused;
            _pausedAt = now;
            _pausedReason = reason;
            AuditLog.QueuePaused(reason);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ResumeAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow;
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE queue_state
                SET state = 0, paused_at = NULL, paused_reason = NULL, updated_at = $ua
                WHERE singleton = 1;
                """;
            cmd.Parameters.AddWithValue("$ua", now.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);

            _state = QueueState.Running;
            _pausedAt = null;
            _pausedReason = null;
            AuditLog.QueueResumed();
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        _conn.Dispose();
        _lock.Dispose();
    }

    private void LoadState()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT state, paused_at, paused_reason FROM queue_state WHERE singleton = 1;";
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return;

        _state = (QueueState)reader.GetInt32(reader.GetOrdinal("state"));
        var pausedAtOrd = reader.GetOrdinal("paused_at");
        _pausedAt = reader.IsDBNull(pausedAtOrd)
            ? null
            : DateTimeOffset.Parse(reader.GetString(pausedAtOrd), System.Globalization.CultureInfo.InvariantCulture);
        var reasonOrd = reader.GetOrdinal("paused_reason");
        _pausedReason = reader.IsDBNull(reasonOrd) ? null : reader.GetString(reasonOrd);
    }
}
