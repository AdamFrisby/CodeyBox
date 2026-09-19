using Microsoft.Data.Sqlite;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// SQLite-backed <see cref="ISecretLeaseStore"/>. Writes to the same state
/// database file as the other stores; the <c>secret_leases</c> table is
/// created here via an additive migration. Stores lease identity only —
/// there is deliberately no value column, so a database dump can never leak
/// a credential. Survives orchestrator restarts: the reconciliation sweep
/// re-reads outstanding handles from this table.
/// </summary>
public sealed class SqliteSecretLeaseStore : ISecretLeaseStore, IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly SqliteDatabaseWriteGate _writeLock;
    private bool _disposed;

    public SqliteSecretLeaseStore(
        string path,
        SqliteDatabaseWriteGateFactory? writeGateFactory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _conn = new SqliteConnection($"Data Source={path}");
        _writeLock = SqliteDatabaseWriteGateFactory.Resolve(writeGateFactory).ForPath(path);
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
            createCmd.CommandText = """
                CREATE TABLE IF NOT EXISTS secret_leases (
                    lease_id        TEXT PRIMARY KEY,
                    provider_id     TEXT NOT NULL,
                    work_item_id    TEXT NOT NULL,
                    secret_group    TEXT NOT NULL,
                    sandbox_env_var TEXT NOT NULL,
                    scope           TEXT NOT NULL,
                    expires_at_utc  TEXT NOT NULL,
                    created_at_utc  TEXT NOT NULL,
                    brokered        INTEGER NOT NULL DEFAULT 0,
                    endpoint        TEXT,
                    status          INTEGER NOT NULL DEFAULT 0,
                    attempt_count   INTEGER NOT NULL DEFAULT 0,
                    last_error      TEXT
                );
                CREATE INDEX IF NOT EXISTS idx_secret_leases_work_item
                    ON secret_leases(work_item_id);
                CREATE INDEX IF NOT EXISTS idx_secret_leases_status
                    ON secret_leases(status);
                """;
            createCmd.ExecuteNonQuery();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task UpsertAsync(SecretLeaseRecord lease, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();
        _writeLock.Wait();
        try
        {
            using var cmd = _conn.CreateCommand();
            // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- hardcoded DML with bound parameters
            cmd.CommandText = """
                INSERT INTO secret_leases
                    (lease_id, provider_id, work_item_id, secret_group, sandbox_env_var,
                     scope, expires_at_utc, created_at_utc, brokered, endpoint,
                     status, attempt_count, last_error)
                VALUES
                    ($id, $provider, $wi, $group, $env,
                     $scope, $expires, $created, $brokered, $endpoint,
                     $status, $attempts, $error)
                ON CONFLICT(lease_id) DO UPDATE SET
                    expires_at_utc = excluded.expires_at_utc,
                    status = excluded.status,
                    attempt_count = excluded.attempt_count,
                    last_error = excluded.last_error,
                    endpoint = excluded.endpoint
                """;
            Bind(cmd, lease);
            cmd.ExecuteNonQuery();
            return Task.CompletedTask;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task<SecretLeaseRecord?> GetAsync(string leaseId, CancellationToken ct = default)
    {
        SecretLeasePolicy.ValidateLeaseId(leaseId, nameof(leaseId));
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();
        using var cmd = _conn.CreateCommand();
        // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- hardcoded DML with bound parameters
        cmd.CommandText = "SELECT lease_id, provider_id, work_item_id, secret_group, sandbox_env_var, scope, expires_at_utc, created_at_utc, brokered, endpoint, status, attempt_count, last_error FROM secret_leases WHERE lease_id = $id;";
        cmd.Parameters.AddWithValue("$id", leaseId);
        using var reader = cmd.ExecuteReader();
        return Task.FromResult(reader.Read() ? ReadRecord(reader) : null);
    }

    public Task<IReadOnlyList<SecretLeaseRecord>> ListOutstandingAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();
        var result = new List<SecretLeaseRecord>();
        using var cmd = _conn.CreateCommand();
        // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- hardcoded DML with bound parameters
        cmd.CommandText = "SELECT lease_id, provider_id, work_item_id, secret_group, sandbox_env_var, scope, expires_at_utc, created_at_utc, brokered, endpoint, status, attempt_count, last_error FROM secret_leases WHERE status IN (0, 2) ORDER BY expires_at_utc;";
        cmd.Parameters.AddWithValue("$active", (int)SecretLeaseStatus.Active);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add(ReadRecord(reader));
        return Task.FromResult<IReadOnlyList<SecretLeaseRecord>>(result.AsReadOnly());
    }

    public Task<IReadOnlyList<SecretLeaseRecord>> ListForWorkItemAsync(Guid workItemId, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();
        var result = new List<SecretLeaseRecord>();
        using var cmd = _conn.CreateCommand();
        // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- hardcoded DML with bound parameters
        cmd.CommandText = "SELECT lease_id, provider_id, work_item_id, secret_group, sandbox_env_var, scope, expires_at_utc, created_at_utc, brokered, endpoint, status, attempt_count, last_error FROM secret_leases WHERE work_item_id = $wi ORDER BY created_at_utc;";
        cmd.Parameters.AddWithValue("$wi", workItemId.ToString("D"));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add(ReadRecord(reader));
        return Task.FromResult<IReadOnlyList<SecretLeaseRecord>>(result.AsReadOnly());
    }

    public Task<bool> TryUpdateExpiryAsync(string leaseId, DateTimeOffset expiresAt, CancellationToken ct = default)
    {
        SecretLeasePolicy.ValidateLeaseId(leaseId, nameof(leaseId));
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();
        _writeLock.Wait();
        try
        {
            using var cmd = _conn.CreateCommand();
            // Conditional write: only an Active lease moves forward, so a
            // concurrent revocation cannot be resurrected by a late renewal.
            // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- hardcoded DML with bound parameters
            cmd.CommandText = "UPDATE secret_leases SET expires_at_utc = $expires, last_error = NULL WHERE lease_id = $id AND status = 0;";
            cmd.Parameters.AddWithValue("$expires", expiresAt.ToUniversalTime().ToString("o"));
            cmd.Parameters.AddWithValue("$id", leaseId);
            return Task.FromResult(cmd.ExecuteNonQuery() == 1);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task MarkRevokedAsync(string leaseId, CancellationToken ct = default)
    {
        SecretLeasePolicy.ValidateLeaseId(leaseId, nameof(leaseId));
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();
        _writeLock.Wait();
        try
        {
            using var cmd = _conn.CreateCommand();
            // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- hardcoded DML with bound parameters
            cmd.CommandText = "UPDATE secret_leases SET status = 1, last_error = NULL WHERE lease_id = $id;";
            cmd.Parameters.AddWithValue("$id", leaseId);
            cmd.ExecuteNonQuery();
            return Task.CompletedTask;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task MarkRevocationFailedAsync(string leaseId, string error, CancellationToken ct = default)
    {
        SecretLeasePolicy.ValidateLeaseId(leaseId, nameof(leaseId));
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();
        _writeLock.Wait();
        try
        {
            using var cmd = _conn.CreateCommand();
            // Stays outstanding (status 2 is included in ListOutstandingAsync)
            // so the next sweep retries it instead of orphaning a live lease.
            // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- hardcoded DML with bound parameters
            cmd.CommandText = "UPDATE secret_leases SET status = 2, attempt_count = attempt_count + 1, last_error = $error WHERE lease_id = $id;";
            cmd.Parameters.AddWithValue("$id", leaseId);
            cmd.Parameters.AddWithValue("$error", error.Length > 2000 ? error[..2000] : error);
            cmd.ExecuteNonQuery();
            return Task.CompletedTask;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static void Bind(SqliteCommand cmd, SecretLeaseRecord lease)
    {
        cmd.Parameters.AddWithValue("$id", lease.LeaseId);
        cmd.Parameters.AddWithValue("$provider", lease.ProviderId);
        cmd.Parameters.AddWithValue("$wi", lease.WorkItemId.ToString("D"));
        cmd.Parameters.AddWithValue("$group", lease.Group);
        cmd.Parameters.AddWithValue("$env", lease.SandboxEnvVar);
        cmd.Parameters.AddWithValue("$scope", lease.Scope);
        cmd.Parameters.AddWithValue("$expires", lease.ExpiresAt.ToUniversalTime().ToString("o"));
        cmd.Parameters.AddWithValue("$created", lease.CreatedAt.ToUniversalTime().ToString("o"));
        cmd.Parameters.AddWithValue("$brokered", lease.Brokered ? 1 : 0);
        cmd.Parameters.AddWithValue("$endpoint", (object?)lease.Endpoint ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", (int)lease.Status);
        cmd.Parameters.AddWithValue("$attempts", lease.AttemptCount);
        cmd.Parameters.AddWithValue("$error", (object?)lease.LastError ?? DBNull.Value);
    }

    private static SecretLeaseRecord ReadRecord(SqliteDataReader reader)
    {
        return new SecretLeaseRecord
        {
            LeaseId = reader.GetString(0),
            ProviderId = reader.GetString(1),
            WorkItemId = Guid.Parse(reader.GetString(2)),
            Group = reader.GetString(3),
            SandboxEnvVar = reader.GetString(4),
            Scope = reader.GetString(5),
            ExpiresAt = DateTimeOffset.Parse(reader.GetString(6), null, System.Globalization.DateTimeStyles.RoundtripKind),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(7), null, System.Globalization.DateTimeStyles.RoundtripKind),
            Brokered = reader.GetInt64(8) != 0,
            Endpoint = reader.IsDBNull(9) ? null : reader.GetString(9),
            Status = (SecretLeaseStatus)reader.GetInt32(10),
            AttemptCount = reader.GetInt32(11),
            LastError = reader.IsDBNull(12) ? null : reader.GetString(12),
        };
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SqliteSecretLeaseStore));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _conn.Dispose();
    }
}
