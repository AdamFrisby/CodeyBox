using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// SQLite-backed worker registry. Each worker slot writes its own row on
/// pickup and keeps <c>last_heartbeat_at</c> fresh via periodic updates.
/// The <see cref="ClaimDeadWorkersAsync"/> method atomically deletes stale
/// rows under a write lock so only the first caller performs recovery.
/// Reads (<see cref="ListAsync"/>) run on dedicated connections and never
/// take the write gate: the database is in WAL mode, so readers proceed
/// concurrently with the single serialized writer. Writes execute
/// synchronously while holding the gate so a holder never retains it across
/// an awaited continuation — the hold covers only the statement itself.
/// </summary>
public sealed class SqliteWorkerRegistry : IWorkerRegistry, IDisposable
{
    private const int HeartbeatMaxAttempts = 5;
    private static readonly TimeSpan HeartbeatInitialRetryDelay = TimeSpan.FromMilliseconds(50);

    private readonly SqliteConnection _conn;
    private readonly string _connectionString;
    private readonly string _dbPath;
    private readonly SqliteDatabaseWriteGate _writeLock;
    private readonly SqliteDatabaseWriteGateFactory _writeGateFactory;
    private readonly ILogger<SqliteWorkerRegistry>? _logger;
    private readonly int _busyTimeoutMilliseconds;
    private readonly int _commandTimeoutSeconds;
    private int _disposed;

    public SqliteWorkerRegistry(
        string path,
        ILogger<SqliteWorkerRegistry>? logger = null,
        int busyTimeoutMilliseconds = SqliteDefaults.BusyTimeoutMilliseconds,
        SqliteDatabaseWriteGateFactory? writeGateFactory = null)
    {
        // busy_timeout is per-connection SQLite state with a default of 0
        // (fail immediately on lock contention). Zero is not a valid choice
        // here: under WAL concurrency a heartbeat racing a writer would
        // surface routine contention as SQLITE_BUSY instead of waiting out
        // the brief lock hold. Fail fast on misconfiguration rather than
        // silently running with lock retries disabled.
        if (busyTimeoutMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(busyTimeoutMilliseconds),
                "SQLite busy_timeout must be positive; 0 disables lock-wait retries and turns routine WAL contention into immediate SQLITE_BUSY failures.");

        _logger = logger;
        _busyTimeoutMilliseconds = busyTimeoutMilliseconds;
        _commandTimeoutSeconds = Math.Max(1, (int)Math.Ceiling(busyTimeoutMilliseconds / 1000.0));
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _dbPath = Path.GetFullPath(path);
        _connectionString = $"Data Source={path}";
        _conn = new SqliteConnection(_connectionString);
        _writeGateFactory = SqliteDatabaseWriteGateFactory.Resolve(writeGateFactory);
        _writeLock = _writeGateFactory.ForPath(path);
        var initialized = false;
        _writeLock.Wait();
        try
        {
            _conn.Open();

            using (var pragma = _conn.CreateCommand())
            {
                pragma.CommandText = $"PRAGMA journal_mode=WAL; PRAGMA busy_timeout={busyTimeoutMilliseconds};";
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
                    current_work_item_id TEXT,
                    executor_host_id     TEXT,
                    max_concurrent_sandboxes INTEGER,
                    executor_network_profiles TEXT,
                    executor_credentials TEXT,
                    cordoned             INTEGER NOT NULL DEFAULT 0,
                    healthy              INTEGER NOT NULL DEFAULT 1
                );
                CREATE INDEX IF NOT EXISTS idx_worker_heartbeat ON worker_registry(last_heartbeat_at);
                """;
            cmd.ExecuteNonQuery();
            EnsureExecutorColumns(_conn);
            initialized = true;
        }
        finally
        {
            _writeLock.Release();
            if (!initialized)
            {
                _conn.Dispose();
                _writeLock.Dispose();
            }
        }
    }

    public async Task RegisterAsync(WorkerRegistration reg, CancellationToken ct = default)
    {
        // Synchronous execution under the gate: the hold covers only the
        // statement, never an awaited continuation whose scheduling delay
        // would extend the global hold under load.
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO worker_registry (worker_id, host_name, process_id, started_at, last_heartbeat_at, current_work_item_id, executor_host_id, max_concurrent_sandboxes, executor_network_profiles, executor_credentials, cordoned, healthy)
                VALUES ($id, $host, $pid, $started, $hb, $item, $exhost, $cap, $profiles, $creds, $cordoned, $healthy)
                ON CONFLICT(worker_id) DO UPDATE SET
                    host_name = excluded.host_name,
                    process_id = excluded.process_id,
                    started_at = excluded.started_at,
                    last_heartbeat_at = excluded.last_heartbeat_at,
                    current_work_item_id = excluded.current_work_item_id,
                    executor_host_id = excluded.executor_host_id,
                    max_concurrent_sandboxes = excluded.max_concurrent_sandboxes,
                    executor_network_profiles = excluded.executor_network_profiles,
                    executor_credentials = excluded.executor_credentials,
                    cordoned = excluded.cordoned,
                    healthy = excluded.healthy;
                """;
            Bind(cmd, reg);
            cmd.ExecuteNonQuery();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Fail-soft only for transient SQLite writer contention. The caller retries
    /// on the next heartbeat interval, while non-transient storage failures still
    /// propagate to avoid reporting success when the row could not be persisted.
    /// </remarks>
    public async Task HeartbeatAsync(string workerId, string? currentWorkItemId, CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // Synchronous statement under the gate (see RegisterAsync):
                // the retry backoff below runs after the gate is released.
                await _writeLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    using var cmd = _conn.CreateCommand();
                    cmd.CommandTimeout = _commandTimeoutSeconds;
                    cmd.CommandText = """
                        UPDATE worker_registry
                        SET last_heartbeat_at = $hb, current_work_item_id = $item
                        WHERE worker_id = $id;
                        """;
                    cmd.Parameters.AddWithValue("$hb", DateTimeOffset.UtcNow.ToString("O"));
                    cmd.Parameters.AddWithValue("$item", (object?)currentWorkItemId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$id", workerId);
                    cmd.ExecuteNonQuery();
                    return;
                }
                finally
                {
                    _writeLock.Release();
                }
            }
            catch (Exception ex) when (IsTransientHeartbeatStorageFailure(ex) && attempt < HeartbeatMaxAttempts)
            {
                var delay = TimeSpan.FromMilliseconds(HeartbeatInitialRetryDelay.TotalMilliseconds * (1 << (attempt - 1)));
                _logger?.LogWarning(ex, "Heartbeat failed for worker {WorkerId}; retrying attempt {Attempt}/{MaxAttempts}", workerId, attempt + 1, HeartbeatMaxAttempts);
                await Task.Delay(delay, ct);
            }
            catch (Exception ex) when (IsTransientHeartbeatStorageFailure(ex))
            {
                // Fail-soft: transient writer contention is expected under load.
                // The heartbeat is a best-effort, idempotent liveness signal; the
                // caller re-issues it on the next interval, and the dead-worker
                // threshold provides the safety net. Swallowing here (after
                // bounded, backed-off retries) avoids surfacing routine SQLITE_BUSY
                // as a hard failure. Non-transient storage errors still propagate.
                _logger?.LogWarning(ex, "Heartbeat failed for worker {WorkerId} after {Attempts} attempts; will retry on next interval", workerId, attempt);
                return;
            }
        }
    }

    public async Task DeregisterAsync(string workerId, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM worker_registry WHERE worker_id = $id;";
            cmd.Parameters.AddWithValue("$id", workerId);
            cmd.ExecuteNonQuery();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<WorkerRegistration>> ListAsync(CancellationToken ct = default)
    {
        // Read-only: runs on a dedicated connection without the write gate.
        // WAL mode lets this reader proceed concurrently with a writer, and a
        // separate connection avoids the per-connection "pending local
        // transaction" race that forced reads through the gate in the first
        // place.
        using var readSlot = await _writeGateFactory.AcquireReadConnectionSlotAsync(_dbPath, ct).ConfigureAwait(false);
        using var readConn = await OpenReadConnectionAsync(ct).ConfigureAwait(false);
        using var cmd = readConn.CreateCommand();
        cmd.CommandText = "SELECT * FROM worker_registry ORDER BY started_at;";
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var results = new List<WorkerRegistration>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            results.Add(Read(reader));
        return results;
    }

    /// <summary>
    /// Atomically selects and deletes all rows with <c>last_heartbeat_at &lt; cutoff</c>
    /// inside a single IMMEDIATE transaction. Only one concurrent caller can
    /// acquire the write lock; the loser sees an empty result.
    /// </summary>
    public async Task<IReadOnlyList<WorkerRegistration>> ClaimDeadWorkersAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var dead = new List<WorkerRegistration>();
            using var tx = _conn.BeginTransaction();

            using var sel = _conn.CreateCommand();
            sel.Transaction = tx;
            sel.CommandText = "SELECT * FROM worker_registry WHERE last_heartbeat_at < $cutoff;";
            sel.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));
            using (var reader = sel.ExecuteReader())
            {
                while (reader.Read())
                    dead.Add(Read(reader));
            }

            if (dead.Count > 0)
            {
                using var del = _conn.CreateCommand();
                del.Transaction = tx;
                del.CommandText = "DELETE FROM worker_registry WHERE last_heartbeat_at < $cutoff;";
                del.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));
                del.ExecuteNonQuery();
            }

            tx.Commit();
            return dead;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<WorkerRegistration?> TryClaimDeadWorkerAsync(
        string workerId,
        DateTimeOffset cutoff,
        CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var tx = _conn.BeginTransaction();
            WorkerRegistration? claimed = null;
            using (var select = _conn.CreateCommand())
            {
                select.Transaction = tx;
                select.CommandText = """
                    SELECT *
                    FROM worker_registry
                    WHERE worker_id = $id
                      AND last_heartbeat_at < $cutoff;
                    """;
                select.Parameters.AddWithValue("$id", workerId);
                select.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));
                using var reader = select.ExecuteReader();
                if (reader.Read())
                    claimed = Read(reader);
            }

            if (claimed is null)
            {
                tx.Commit();
                return null;
            }

            using var delete = _conn.CreateCommand();
            delete.Transaction = tx;
            delete.CommandText = """
                DELETE FROM worker_registry
                WHERE worker_id = $id
                  AND last_heartbeat_at < $cutoff;
                """;
            delete.Parameters.AddWithValue("$id", workerId);
            delete.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));
            var deleted = delete.ExecuteNonQuery();
            tx.Commit();
            return deleted == 1 ? claimed : null;
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
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var tx = _conn.BeginTransaction();

            WorkerRegistration? claimed = null;
            using (var sel = _conn.CreateCommand())
            {
                sel.Transaction = tx;
                sel.CommandText = "SELECT * FROM worker_registry WHERE worker_id = $id;";
                sel.Parameters.AddWithValue("$id", workerId);
                using var reader = sel.ExecuteReader();
                if (reader.Read())
                    claimed = Read(reader);
            }

            if (claimed is not null)
            {
                using var del = _conn.CreateCommand();
                del.Transaction = tx;
                del.CommandText = "DELETE FROM worker_registry WHERE worker_id = $id;";
                del.Parameters.AddWithValue("$id", workerId);
                del.ExecuteNonQuery();
            }

            tx.Commit();
            return claimed;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task<SqliteConnection> OpenReadConnectionAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        using var pragma = conn.CreateCommand();
        // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- busy_timeout takes no parameters; the interpolated value is a validated positive ctor argument, not caller input
        pragma.CommandText = $"PRAGMA busy_timeout={_busyTimeoutMilliseconds};";
        await pragma.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return conn;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            SqliteConnectionDisposal.DisposeTolerantOfTeardownRace(_conn);
        }
        finally
        {
            _writeLock.Dispose();
        }
    }

    private static void Bind(SqliteCommand cmd, WorkerRegistration reg)
    {
        cmd.Parameters.AddWithValue("$id", reg.WorkerId);
        cmd.Parameters.AddWithValue("$host", reg.HostName);
        cmd.Parameters.AddWithValue("$pid", reg.ProcessId);
        cmd.Parameters.AddWithValue("$started", reg.StartedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$hb", reg.LastHeartbeatAt.ToString("O"));
        cmd.Parameters.AddWithValue("$item", (object?)reg.CurrentWorkItemId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$exhost", (object?)reg.ExecutorHostId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cap", (object?)reg.MaxConcurrentSandboxes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$profiles", (object?)SerializeStringList(reg.ExecutorNetworkProfiles) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$creds", (object?)SerializeStringList(reg.ExecutorCredentials) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cordoned", reg.Cordoned ? 1 : 0);
        cmd.Parameters.AddWithValue("$healthy", reg.Healthy ? 1 : 0);
    }

    private static WorkerRegistration Read(SqliteDataReader r) => new()
    {
        WorkerId = r.GetString(r.GetOrdinal("worker_id")),
        HostName = r.GetString(r.GetOrdinal("host_name")),
        ProcessId = r.GetInt32(r.GetOrdinal("process_id")),
        StartedAt = DateTimeOffset.Parse(r.GetString(r.GetOrdinal("started_at")), System.Globalization.CultureInfo.InvariantCulture),
        LastHeartbeatAt = DateTimeOffset.Parse(r.GetString(r.GetOrdinal("last_heartbeat_at")), System.Globalization.CultureInfo.InvariantCulture),
        CurrentWorkItemId = r.IsDBNull(r.GetOrdinal("current_work_item_id")) ? null : r.GetString(r.GetOrdinal("current_work_item_id")),
        ExecutorHostId = r.IsDBNull(r.GetOrdinal("executor_host_id")) ? null : r.GetString(r.GetOrdinal("executor_host_id")),
        MaxConcurrentSandboxes = r.IsDBNull(r.GetOrdinal("max_concurrent_sandboxes")) ? null : r.GetInt32(r.GetOrdinal("max_concurrent_sandboxes")),
        ExecutorNetworkProfiles = r.IsDBNull(r.GetOrdinal("executor_network_profiles")) ? null : DeserializeStringList(r.GetString(r.GetOrdinal("executor_network_profiles"))),
        ExecutorCredentials = r.IsDBNull(r.GetOrdinal("executor_credentials")) ? null : DeserializeStringList(r.GetString(r.GetOrdinal("executor_credentials"))),
        Cordoned = r.GetInt32(r.GetOrdinal("cordoned")) != 0,
        Healthy = r.GetInt32(r.GetOrdinal("healthy")) != 0,
    };

    /// <summary>
    /// Adds the executor-attribute columns to a <c>worker_registry</c> table
    /// created by an older build. Fresh databases already carry the columns
    /// via <c>CREATE TABLE</c>; this keeps pre-existing state files loading
    /// instead of failing on the wider reads. Column definitions are source
    /// literals, never caller input.
    /// </summary>
    private static void EnsureExecutorColumns(SqliteConnection conn)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA table_info(worker_registry);";
            using var reader = pragma.ExecuteReader();
            while (reader.Read())
                existing.Add(reader.GetString(1));
        }

        foreach (var (column, definition) in ExecutorColumnDefinitions)
        {
            if (existing.Contains(column))
                continue;
            using var alter = conn.CreateCommand();
            // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- both fragments come from the source-literal ExecutorColumnDefinitions table, not caller input
            alter.CommandText = $"ALTER TABLE worker_registry ADD COLUMN {definition};";
            alter.ExecuteNonQuery();
        }
    }

    private static readonly (string Column, string Definition)[] ExecutorColumnDefinitions =
    [
        ("executor_host_id", "executor_host_id TEXT"),
        ("max_concurrent_sandboxes", "max_concurrent_sandboxes INTEGER"),
        ("executor_network_profiles", "executor_network_profiles TEXT"),
        ("executor_credentials", "executor_credentials TEXT"),
        ("cordoned", "cordoned INTEGER NOT NULL DEFAULT 0"),
        ("healthy", "healthy INTEGER NOT NULL DEFAULT 1"),
    ];

    private static string? SerializeStringList(IReadOnlyList<string>? values) =>
        values is null ? null : JsonSerializer.Serialize(values);

    private static IReadOnlyList<string>? DeserializeStringList(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsTransientHeartbeatStorageFailure(Exception ex) =>
        ex is SqliteWriteGateAcquisitionTimeoutException
        || ex is SqliteException { SqliteErrorCode: SqliteDefaults.SqliteBusy or SqliteDefaults.SqliteLocked };
}
