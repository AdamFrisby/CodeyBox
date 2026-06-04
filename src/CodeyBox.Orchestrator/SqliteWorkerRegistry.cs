using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// SQLite-backed worker registry. Each worker slot writes its own row on
/// pickup and keeps <c>last_heartbeat_at</c> fresh via periodic updates.
/// The <see cref="ClaimDeadWorkersAsync"/> method atomically deletes stale
/// rows under a write lock so only the first caller performs recovery.
/// </summary>
public sealed class SqliteWorkerRegistry : IWorkerRegistry, IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly SqliteDatabaseWriteGate _writeLock;
    private readonly ILogger<SqliteWorkerRegistry>? _logger;
    private int _disposed;

    public SqliteWorkerRegistry(string path, ILogger<SqliteWorkerRegistry>? logger = null)
    {
        _logger = logger;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _conn = new SqliteConnection($"Data Source={path}");
        _writeLock = SqliteDatabaseWriteGate.ForPath(path);
        _writeLock.Wait();
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
                CREATE TABLE IF NOT EXISTS worker_registry (
                    worker_id            TEXT PRIMARY KEY,
                    host_name            TEXT NOT NULL,
                    process_id           INTEGER NOT NULL,
                    started_at           TEXT NOT NULL,
                    last_heartbeat_at    TEXT NOT NULL,
                    current_work_item_id TEXT
                );
                CREATE INDEX IF NOT EXISTS idx_worker_heartbeat ON worker_registry(last_heartbeat_at);
                """;
            cmd.ExecuteNonQuery();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task RegisterAsync(WorkerRegistration reg, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO worker_registry (worker_id, host_name, process_id, started_at, last_heartbeat_at, current_work_item_id)
                VALUES ($id, $host, $pid, $started, $hb, $item)
                ON CONFLICT(worker_id) DO UPDATE SET
                    host_name = excluded.host_name,
                    process_id = excluded.process_id,
                    started_at = excluded.started_at,
                    last_heartbeat_at = excluded.last_heartbeat_at,
                    current_work_item_id = excluded.current_work_item_id;
                """;
            Bind(cmd, reg);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Fail-soft: any storage exception is caught and logged as a warning;
    /// the caller retries on the next heartbeat interval. Only intentional
    /// cancellation (<see cref="OperationCanceledException"/>) propagates.
    /// </remarks>
    public async Task HeartbeatAsync(string workerId, string? currentWorkItemId, CancellationToken ct = default)
    {
        try
        {
            await _writeLock.WaitAsync(ct);
            try
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = """
                    UPDATE worker_registry
                    SET last_heartbeat_at = $hb, current_work_item_id = $item
                    WHERE worker_id = $id;
                    """;
                cmd.Parameters.AddWithValue("$hb", DateTimeOffset.UtcNow.ToString("O"));
                cmd.Parameters.AddWithValue("$item", (object?)currentWorkItemId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$id", workerId);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Heartbeat failed for worker {WorkerId}; will retry on next interval", workerId);
        }
    }

    public async Task DeregisterAsync(string workerId, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM worker_registry WHERE worker_id = $id;";
            cmd.Parameters.AddWithValue("$id", workerId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<WorkerRegistration>> ListAsync(CancellationToken ct = default)
    {
        // Microsoft.Data.Sqlite serializes commands per-connection: if another
        // caller (e.g. ClaimDeadWorkersAsync) is mid-BeginTransaction on _conn,
        // an unscoped ExecuteReaderAsync here throws "pending local transaction".
        // _writeLock is the single-writer guard that protects every mutation
        // and the only transactional read path; taking it for reads too closes
        // the race without forcing callers to coordinate externally.
        await _writeLock.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM worker_registry ORDER BY started_at;";
            using var reader = await cmd.ExecuteReaderAsync(ct);
            var results = new List<WorkerRegistration>();
            while (await reader.ReadAsync(ct))
                results.Add(Read(reader));
            return results;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Atomically selects and deletes all rows with <c>last_heartbeat_at &lt; cutoff</c>
    /// inside a single IMMEDIATE transaction. Only one concurrent caller can
    /// acquire the write lock; the loser sees an empty result.
    /// </summary>
    public async Task<IReadOnlyList<WorkerRegistration>> ClaimDeadWorkersAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var dead = new List<WorkerRegistration>();
            using var tx = _conn.BeginTransaction();

            using var sel = _conn.CreateCommand();
            sel.Transaction = tx;
            sel.CommandText = "SELECT * FROM worker_registry WHERE last_heartbeat_at < $cutoff;";
            sel.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));
            using (var reader = await sel.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                    dead.Add(Read(reader));
            }

            if (dead.Count > 0)
            {
                using var del = _conn.CreateCommand();
                del.Transaction = tx;
                del.CommandText = "DELETE FROM worker_registry WHERE last_heartbeat_at < $cutoff;";
                del.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));
                await del.ExecuteNonQueryAsync(ct);
            }

            tx.Commit();
            return dead;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Single-row atomic claim: SELECT-then-DELETE by primary key inside an
    /// IMMEDIATE transaction. Returns the deleted row, or null when no row
    /// matched (already claimed by another caller, or never existed).
    /// </summary>
    public async Task<WorkerRegistration?> TryClaimWorkerAsync(string workerId, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var tx = _conn.BeginTransaction();

            WorkerRegistration? claimed = null;
            using (var sel = _conn.CreateCommand())
            {
                sel.Transaction = tx;
                sel.CommandText = "SELECT * FROM worker_registry WHERE worker_id = $id;";
                sel.Parameters.AddWithValue("$id", workerId);
                using var reader = await sel.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                    claimed = Read(reader);
            }

            if (claimed is not null)
            {
                using var del = _conn.CreateCommand();
                del.Transaction = tx;
                del.CommandText = "DELETE FROM worker_registry WHERE worker_id = $id;";
                del.Parameters.AddWithValue("$id", workerId);
                await del.ExecuteNonQueryAsync(ct);
            }

            tx.Commit();
            return claimed;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _conn.Dispose();
        _writeLock.Dispose();
    }

    private static void Bind(SqliteCommand cmd, WorkerRegistration reg)
    {
        cmd.Parameters.AddWithValue("$id", reg.WorkerId);
        cmd.Parameters.AddWithValue("$host", reg.HostName);
        cmd.Parameters.AddWithValue("$pid", reg.ProcessId);
        cmd.Parameters.AddWithValue("$started", reg.StartedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$hb", reg.LastHeartbeatAt.ToString("O"));
        cmd.Parameters.AddWithValue("$item", (object?)reg.CurrentWorkItemId ?? DBNull.Value);
    }

    private static WorkerRegistration Read(SqliteDataReader r) => new()
    {
        WorkerId = r.GetString(r.GetOrdinal("worker_id")),
        HostName = r.GetString(r.GetOrdinal("host_name")),
        ProcessId = r.GetInt32(r.GetOrdinal("process_id")),
        StartedAt = DateTimeOffset.Parse(r.GetString(r.GetOrdinal("started_at")), System.Globalization.CultureInfo.InvariantCulture),
        LastHeartbeatAt = DateTimeOffset.Parse(r.GetString(r.GetOrdinal("last_heartbeat_at")), System.Globalization.CultureInfo.InvariantCulture),
        CurrentWorkItemId = r.IsDBNull(r.GetOrdinal("current_work_item_id")) ? null : r.GetString(r.GetOrdinal("current_work_item_id")),
    };
}
