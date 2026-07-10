using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// SQLite-backed <see cref="IQueueController"/>. Persists the global queue state
/// and per-project queue states to the same database file as the work-item store
/// (separate connection, separate tables). On startup the global state is loaded;
/// if paused, a warning is emitted so operators don't forget they left it paused.
/// </summary>
public sealed class SqliteQueueController : IQueueController, IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnection _conn;
    private readonly SqliteDatabaseWriteGate _lock;
    private readonly ILogger<SqliteQueueController> _log;
    private readonly object _auditSync = new();
    private Task _auditTail = Task.CompletedTask;

    // volatile so reads outside the lock see writes made inside the lock on ARM64.
    private volatile QueueState _state;
    // long cannot be volatile in C#; use Interlocked for atomic 64-bit reads/writes.
    // 0 is the null sentinel (the epoch DateTimeOffset has non-zero UtcTicks, so 0 is safe).
    private long _pausedAtUtcTicks;
    private volatile string? _pausedReason;

    public QueueState State => _state;
    public DateTimeOffset? PausedAt
    {
        get
        {
            var t = Interlocked.Read(ref _pausedAtUtcTicks);
            return t == 0 ? null : new DateTimeOffset(t, TimeSpan.Zero);
        }
    }
    public string? PausedReason => _pausedReason;

    public SqliteQueueController(
        string dbPath,
        ILogger<SqliteQueueController> log,
        SqliteDatabaseWriteGateFactory? writeGateFactory = null)
    {
        _dbPath = dbPath;
        _log = log;
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _conn = new SqliteConnection($"Data Source={dbPath}");
        _lock = SqliteDatabaseWriteGateFactory.Resolve(writeGateFactory).ForPath(dbPath);
        var lockHeld = false;
        var initialized = false;
        try
        {
            _lock.Wait();
            lockHeld = true;
            _conn.Open();

            // WAL mode allows concurrent readers; SqliteDatabaseWriteGate serializes
            // writers in-process. busy_timeout gives external lock holders a retry window.
            using (var walCmd = _conn.CreateCommand())
            {
                walCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=30000;";
                walCmd.ExecuteNonQuery();
            }

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
                CREATE TABLE IF NOT EXISTS project_queue_state (
                    project_id    TEXT PRIMARY KEY,
                    paused        INTEGER NOT NULL DEFAULT 0,
                    paused_at     TEXT,
                    paused_reason TEXT,
                    updated_at    TEXT NOT NULL
                );
                """;
            cmd.ExecuteNonQuery();

            LoadState();
            initialized = true;
        }
        finally
        {
            if (lockHeld)
                _lock.Release();
            if (!initialized)
            {
                _conn.Dispose();
                _lock.Dispose();
            }
        }

        if (_state == QueueState.Paused)
            AuditLog.QueueStartedWhilePaused();
    }

    public async Task PauseAsync(string reason, CancellationToken ct = default)
    {
        Task audit;
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
            Interlocked.Exchange(ref _pausedAtUtcTicks, now.UtcTicks);
            _pausedReason = reason;
            audit = EnqueueAudit(() => AuditLog.QueuePaused(reason));
        }
        finally
        {
            _lock.Release();
        }

        await audit.ConfigureAwait(false);
    }

    public async Task ResumeAsync(CancellationToken ct = default)
    {
        Task? audit = null;
        await _lock.WaitAsync(ct);
        try
        {
            // No-op if already running: prevents spurious audit entries on duplicate calls.
            if (_state == QueueState.Running) return;

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
            Interlocked.Exchange(ref _pausedAtUtcTicks, 0);
            _pausedReason = null;
            audit = EnqueueAudit(AuditLog.QueueResumed);
        }
        finally
        {
            _lock.Release();
        }

        if (audit is not null)
            await audit.ConfigureAwait(false);
    }

    public async Task PauseProjectAsync(ProjectId projectId, string reason, CancellationToken ct = default)
    {
        Task audit;
        await _lock.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow;
            using var cmd = _conn.CreateCommand();
            // COALESCE(paused_at, $at) keeps the original pause timestamp on repeat calls.
            cmd.CommandText = """
                INSERT INTO project_queue_state (project_id, paused, paused_at, paused_reason, updated_at)
                VALUES ($pid, 1, $at, $reason, $ua)
                ON CONFLICT(project_id) DO UPDATE SET
                    paused        = 1,
                    paused_at     = COALESCE(paused_at, $at),
                    paused_reason = $reason,
                    updated_at    = $ua;
                """;
            cmd.Parameters.AddWithValue("$pid", projectId.Value);
            cmd.Parameters.AddWithValue("$at", now.ToString("O"));
            cmd.Parameters.AddWithValue("$reason", reason);
            cmd.Parameters.AddWithValue("$ua", now.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
            audit = EnqueueAudit(() => AuditLog.ProjectQueuePaused(projectId, reason));
        }
        finally
        {
            _lock.Release();
        }

        await audit.ConfigureAwait(false);
    }

    public async Task ResumeProjectAsync(ProjectId projectId, CancellationToken ct = default)
    {
        Task audit;
        await _lock.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow;
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO project_queue_state (project_id, paused, paused_at, paused_reason, updated_at)
                VALUES ($pid, 0, NULL, NULL, $ua)
                ON CONFLICT(project_id) DO UPDATE SET
                    paused        = 0,
                    paused_at     = NULL,
                    paused_reason = NULL,
                    updated_at    = $ua;
                """;
            cmd.Parameters.AddWithValue("$pid", projectId.Value);
            cmd.Parameters.AddWithValue("$ua", now.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
            audit = EnqueueAudit(() => AuditLog.ProjectQueueResumed(projectId));
        }
        finally
        {
            _lock.Release();
        }

        await audit.ConfigureAwait(false);
    }

    public async Task<ProjectQueueState?> GetProjectStateAsync(ProjectId projectId, CancellationToken ct = default)
    {
        // Read-only connection so we don't block the write lock during the query.
        using var rc = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly");
        rc.Open();
        using var cmd = rc.CreateCommand();
        cmd.CommandText = """
            SELECT paused, paused_at, paused_reason
            FROM project_queue_state
            WHERE project_id = $pid;
            """;
        cmd.Parameters.AddWithValue("$pid", projectId.Value);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        var paused = reader.GetInt32(0) == 1;
        var pausedAtOrd = reader.GetOrdinal("paused_at");
        var pausedAt = reader.IsDBNull(pausedAtOrd)
            ? (DateTimeOffset?)null
            : DateTimeOffset.Parse(reader.GetString(pausedAtOrd), System.Globalization.CultureInfo.InvariantCulture);
        var reasonOrd = reader.GetOrdinal("paused_reason");
        var reason = reader.IsDBNull(reasonOrd) ? null : reader.GetString(reasonOrd);
        return new ProjectQueueState(projectId, paused, pausedAt, reason);
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

        var rawState = reader.GetInt32(reader.GetOrdinal("state"));
        if (!Enum.IsDefined(typeof(QueueState), rawState))
        {
            _log.LogWarning("Persisted queue state value {RawState} is not a valid QueueState; defaulting to Running", rawState);
            _state = QueueState.Running;
        }
        else
        {
            _state = (QueueState)rawState;
        }
        var pausedAtOrd = reader.GetOrdinal("paused_at");
        _pausedAtUtcTicks = reader.IsDBNull(pausedAtOrd)
            ? 0L
            : DateTimeOffset.Parse(reader.GetString(pausedAtOrd), System.Globalization.CultureInfo.InvariantCulture).UtcTicks;
        var reasonOrd = reader.GetOrdinal("paused_reason");
        _pausedReason = reader.IsDBNull(reasonOrd) ? null : reader.GetString(reasonOrd);
    }

    private Task EnqueueAudit(Action emit)
    {
        lock (_auditSync)
        {
            var next = RunAuditAfterAsync(_auditTail, emit);
            _auditTail = next;
            return next;
        }
    }

    private static async Task RunAuditAfterAsync(Task prior, Action emit)
    {
        try
        {
            await prior.ConfigureAwait(false);
        }
        catch
        {
        }

        emit();
    }
}
