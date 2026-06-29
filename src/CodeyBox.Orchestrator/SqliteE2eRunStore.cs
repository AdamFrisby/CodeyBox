using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// SQLite-backed store for E2E replay runs. Shares the orchestrator state
/// database with <see cref="SqliteWorkItemStore"/> and
/// <see cref="SqliteTestCaseStore"/>; the e2e_runs table is created via an
/// additive migration on construction.
///
/// <para>The <c>FOREIGN KEY ... ON DELETE CASCADE</c> on test_cases.id keeps the
/// run history's lifetime tied to its test case: archiving a work item
/// cascades through test_cases → e2e_runs without orphans.</para>
/// </summary>
public sealed class SqliteE2eRunStore : IE2eRunStore, IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly SqliteDatabaseWriteGate _writeLock;
    private int _disposed;

    public SqliteE2eRunStore(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _conn = new SqliteConnection($"Data Source={path}");
        _writeLock = SqliteDatabaseWriteGate.ForPath(path);
        _writeLock.Wait();
        try
        {
            _conn.Open();

            using (var walCmd = _conn.CreateCommand())
            {
                walCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=30000; PRAGMA foreign_keys=ON;";
                walCmd.ExecuteNonQuery();
            }

            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS e2e_runs (
                    id            TEXT PRIMARY KEY,
                    test_case_id  TEXT NOT NULL REFERENCES test_cases(id) ON DELETE CASCADE,
                    status        TEXT NOT NULL,
                    created_at    TEXT NOT NULL,
                    started_at    TEXT,
                    finished_at   TEXT,
                    result        TEXT,
                    sandbox_id    TEXT,
                    batch_id      TEXT
                );
                CREATE INDEX IF NOT EXISTS idx_e2e_runs_test_case ON e2e_runs(test_case_id);
                CREATE INDEX IF NOT EXISTS idx_e2e_runs_batch ON e2e_runs(batch_id) WHERE batch_id IS NOT NULL;
                CREATE INDEX IF NOT EXISTS idx_e2e_runs_status ON e2e_runs(status);
                CREATE INDEX IF NOT EXISTS idx_e2e_runs_queue ON e2e_runs(status, created_at);
                """;
            cmd.ExecuteNonQuery();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task CreateAsync(E2eRun run, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO e2e_runs (id, test_case_id, status, created_at, started_at, finished_at, result, sandbox_id, batch_id)
                VALUES ($id, $tc, $st, $ca, $sa, $fa, $res, $sb, $bt);
                """;
            Bind(cmd, run);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<E2eRun?> GetAsync(string id, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM e2e_runs WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            using var reader = await cmd.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct) ? Read(reader) : null;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async IAsyncEnumerable<E2eRun> ListAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        var rows = new List<E2eRun>();
        await _writeLock.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM e2e_runs ORDER BY created_at DESC;";
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add(Read(reader));
            }
        }
        finally
        {
            _writeLock.Release();
        }
        foreach (var row in rows) yield return row;
    }

    public async IAsyncEnumerable<E2eRun> ListByTestCaseAsync(string testCaseId, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var rows = new List<E2eRun>();
        await _writeLock.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM e2e_runs WHERE test_case_id = $tc ORDER BY created_at DESC;";
            cmd.Parameters.AddWithValue("$tc", testCaseId);
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add(Read(reader));
            }
        }
        finally
        {
            _writeLock.Release();
        }
        foreach (var row in rows) yield return row;
    }

    public async IAsyncEnumerable<E2eRun> ListByBatchAsync(string batchId, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var rows = new List<E2eRun>();
        await _writeLock.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM e2e_runs WHERE batch_id = $bt ORDER BY created_at ASC;";
            cmd.Parameters.AddWithValue("$bt", batchId);
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add(Read(reader));
            }
        }
        finally
        {
            _writeLock.Release();
        }
        foreach (var row in rows) yield return row;
    }

    public async Task<bool> HasQueuedAsync(CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM e2e_runs WHERE status = 'Queued' LIMIT 1;";
            var value = await cmd.ExecuteScalarAsync(ct);
            return value is not null && value is not DBNull;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<E2eRun?> ClaimNextQueuedAsync(string sandboxId, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var tx = _conn.BeginTransaction();
            try
            {
                string? claimedId = null;
                using (var sel = _conn.CreateCommand())
                {
                    sel.Transaction = tx;
                    sel.CommandText = """
                        SELECT id FROM e2e_runs
                        WHERE status = 'Queued'
                        ORDER BY created_at ASC
                        LIMIT 1;
                        """;
                    using var reader = await sel.ExecuteReaderAsync(ct);
                    if (await reader.ReadAsync(ct))
                    {
                        claimedId = reader.GetString(0);
                    }
                }

                if (claimedId is null)
                {
                    await tx.CommitAsync(ct);
                    return null;
                }

                var startedAt = DateTimeOffset.UtcNow;
                using (var upd = _conn.CreateCommand())
                {
                    upd.Transaction = tx;
                    upd.CommandText = """
                        UPDATE e2e_runs
                        SET status = 'Running', started_at = $sa, sandbox_id = $sb
                        WHERE id = $id AND status = 'Queued';
                        """;
                    upd.Parameters.AddWithValue("$sa", startedAt.ToString("O"));
                    upd.Parameters.AddWithValue("$sb", sandboxId);
                    upd.Parameters.AddWithValue("$id", claimedId);
                    var rows = await upd.ExecuteNonQueryAsync(ct);
                    if (rows == 0)
                    {
                        // Lost the race to another dispatcher; commit empty and retry.
                        await tx.CommitAsync(ct);
                        return null;
                    }
                }

                E2eRun? claimed = null;
                using (var refetch = _conn.CreateCommand())
                {
                    refetch.Transaction = tx;
                    refetch.CommandText = "SELECT * FROM e2e_runs WHERE id = $id;";
                    refetch.Parameters.AddWithValue("$id", claimedId);
                    using var reader = await refetch.ExecuteReaderAsync(ct);
                    if (await reader.ReadAsync(ct))
                    {
                        claimed = Read(reader);
                    }
                }

                await tx.CommitAsync(ct);
                return claimed;
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> UpdateStatusAsync(string id, E2eRunStatus status, DateTimeOffset? startedAt, DateTimeOffset? finishedAt, string? result, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE e2e_runs
                SET status = $st,
                    started_at = COALESCE($sa, started_at),
                    finished_at = COALESCE($fa, finished_at),
                    result = COALESCE($res, result)
                WHERE id = $id
                  AND status NOT IN ('Passed', 'Failed', 'Error', 'Canceled');
                """;
            cmd.Parameters.AddWithValue("$st", status.ToString());
            cmd.Parameters.AddWithValue("$sa", (object?)startedAt?.ToString("O") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$fa", (object?)finishedAt?.ToString("O") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$res", (object?)result ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$id", id);
            var rows = await cmd.ExecuteNonQueryAsync(ct);
            return rows > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> CancelAsync(string id, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE e2e_runs
                SET status = 'Canceled', finished_at = $fa
                WHERE id = $id AND status IN ('Queued', 'Running');
                """;
            cmd.Parameters.AddWithValue("$fa", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$id", id);
            var rows = await cmd.ExecuteNonQueryAsync(ct);
            return rows > 0;
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

        try
        {
            SqliteConnectionDisposal.DisposeTolerantOfTeardownRace(_conn);
        }
        finally
        {
            _writeLock.Dispose();
        }
    }

    private static void Bind(SqliteCommand cmd, E2eRun r)
    {
        cmd.Parameters.AddWithValue("$id", r.Id);
        cmd.Parameters.AddWithValue("$tc", r.TestCaseId);
        cmd.Parameters.AddWithValue("$st", r.Status.ToString());
        cmd.Parameters.AddWithValue("$ca", r.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$sa", (object?)r.StartedAt?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fa", (object?)r.FinishedAt?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$res", (object?)r.Result ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sb", (object?)r.SandboxId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$bt", (object?)r.BatchId ?? DBNull.Value);
    }

    private static E2eRun Read(SqliteDataReader r)
    {
        E2eRunStatus status = E2eRunStatus.Queued;
        var statusStr = r.GetString(r.GetOrdinal("status"));
        // TryParse — forward/backward compatible: an unknown status string
        // surfaces as Queued and is corrected by the dispatcher's next sweep
        // rather than poisoning the row.
        Enum.TryParse(statusStr, ignoreCase: true, out status);

        return new E2eRun
        {
            Id = r.GetString(r.GetOrdinal("id")),
            TestCaseId = r.GetString(r.GetOrdinal("test_case_id")),
            Status = status,
            CreatedAt = DateTimeOffset.Parse(r.GetString(r.GetOrdinal("created_at")), System.Globalization.CultureInfo.InvariantCulture),
            StartedAt = r.IsDBNull(r.GetOrdinal("started_at")) ? null : DateTimeOffset.Parse(r.GetString(r.GetOrdinal("started_at")), System.Globalization.CultureInfo.InvariantCulture),
            FinishedAt = r.IsDBNull(r.GetOrdinal("finished_at")) ? null : DateTimeOffset.Parse(r.GetString(r.GetOrdinal("finished_at")), System.Globalization.CultureInfo.InvariantCulture),
            Result = r.IsDBNull(r.GetOrdinal("result")) ? null : r.GetString(r.GetOrdinal("result")),
            SandboxId = r.IsDBNull(r.GetOrdinal("sandbox_id")) ? null : r.GetString(r.GetOrdinal("sandbox_id")),
            BatchId = r.IsDBNull(r.GetOrdinal("batch_id")) ? null : r.GetString(r.GetOrdinal("batch_id")),
        };
    }
}
