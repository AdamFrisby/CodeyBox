using Microsoft.Data.Sqlite;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// SQLite-backed implementation of <see cref="IIdempotencyStore"/>. Shares the
/// state database file with <see cref="SqliteWorkItemStore"/> via a separate
/// connection; writes are coordinated by the per-database SQLite write gate.
/// </summary>
public sealed class SqliteIdempotencyStore : IIdempotencyStore, IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly SqliteDatabaseWriteGate _writeLock;

    public SqliteIdempotencyStore(string path)
    {
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

            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS idempotency_keys (
                    key                   TEXT PRIMARY KEY,
                    body_hash             TEXT NOT NULL,
                    response_status       INTEGER NOT NULL,
                    response_body         BLOB NOT NULL,
                    response_content_type TEXT NOT NULL DEFAULT 'application/json',
                    expires_at            TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_idempotency_expires_at ON idempotency_keys(expires_at);
                """;
            cmd.ExecuteNonQuery();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IdempotencyLookupResult> LookupAsync(
        string key,
        string bodyHash,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT key, body_hash, response_status, response_body, response_content_type, expires_at
            FROM idempotency_keys
            WHERE key = $key;
            """;
        cmd.Parameters.AddWithValue("$key", key);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return new IdempotencyLookupResult(IdempotencyLookupOutcome.Miss, null);

        var storedExpiresAt = DateTimeOffset.Parse(reader.GetString(5),
            System.Globalization.CultureInfo.InvariantCulture);
        if (storedExpiresAt <= now)
            return new IdempotencyLookupResult(IdempotencyLookupOutcome.Miss, null);

        var storedHash = reader.GetString(1);
        var entry = new IdempotencyEntry(
            reader.GetString(0),
            storedHash,
            reader.GetInt32(2),
            (byte[])reader.GetValue(3),
            reader.GetString(4),
            storedExpiresAt);

        return string.Equals(storedHash, bodyHash, StringComparison.Ordinal)
            ? new IdempotencyLookupResult(IdempotencyLookupOutcome.Hit, entry)
            : new IdempotencyLookupResult(IdempotencyLookupOutcome.Conflict, entry);
    }

    public async Task PutAsync(IdempotencyEntry entry, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            // Overwrite the row when the existing entry has already expired so
            // a replay after the 24-hour TTL re-caches a fresh response. Live
            // rows (expires_at > excluded.expires_at AT INSERT-TIME implies the
            // existing row was inserted later AND has not expired) are left
            // untouched — the first writer wins for a given key within the TTL
            // window, which preserves the spec's "same key + same body returns
            // the cached response" guarantee.
            //
            // The WHERE clause compares the EXISTING row's expires_at against
            // the new row's expires_at: when the existing row's TTL has elapsed
            // (i.e. it predates the new insert by >= TTL) we overwrite; when it
            // is still live, DO UPDATE is a no-op and the existing row stays.
            cmd.CommandText = """
                INSERT INTO idempotency_keys (key, body_hash, response_status, response_body, response_content_type, expires_at)
                VALUES ($key, $hash, $status, $body, $ct, $exp)
                ON CONFLICT(key) DO UPDATE SET
                    body_hash             = excluded.body_hash,
                    response_status       = excluded.response_status,
                    response_body         = excluded.response_body,
                    response_content_type = excluded.response_content_type,
                    expires_at            = excluded.expires_at
                WHERE idempotency_keys.expires_at <= $now;
                """;
            cmd.Parameters.AddWithValue("$key", entry.Key);
            cmd.Parameters.AddWithValue("$hash", entry.BodyHash);
            cmd.Parameters.AddWithValue("$status", entry.ResponseStatus);
            cmd.Parameters.AddWithValue("$body", entry.ResponseBody);
            cmd.Parameters.AddWithValue("$ct", entry.ResponseContentType);
            cmd.Parameters.AddWithValue("$exp", entry.ExpiresAt.ToString("O"));
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<int> DeleteExpiredAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM idempotency_keys WHERE expires_at <= $cutoff;";
            cmd.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));
            return await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Dispose()
    {
        _conn.Dispose();
        _writeLock.Dispose();
    }
}
