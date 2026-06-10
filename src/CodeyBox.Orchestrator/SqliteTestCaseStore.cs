using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// SQLite-backed store for test cases.
/// Shares the same database file as <see cref="SqliteWorkItemStore"/>;
/// the test_cases table is created here via its own additive migration.
/// </summary>
public sealed class SqliteTestCaseStore : ITestCaseStore, IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly SqliteDatabaseWriteGate _writeLock;
    private int _disposed;

    public SqliteTestCaseStore(string path)
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
                CREATE TABLE IF NOT EXISTS test_cases (
                    id                        TEXT PRIMARY KEY,
                    name                      TEXT NOT NULL,
                    description               TEXT NOT NULL,
                    source_work_item_id       TEXT NOT NULL REFERENCES work_items(id) ON DELETE CASCADE,
                    created_at                TEXT NOT NULL,
                    updated_at                TEXT NOT NULL,
                    is_archived               INTEGER NOT NULL DEFAULT 0,
                    automation_kind           TEXT,
                    executable_artifact_json  TEXT,
                    conformance_json          TEXT,
                    label                     TEXT,
                    last_run_passed           INTEGER,
                    last_run_at               TEXT,
                    last_run_result           TEXT
                );
                CREATE INDEX IF NOT EXISTS idx_test_cases_work_item ON test_cases(source_work_item_id);
                CREATE INDEX IF NOT EXISTS idx_test_cases_label ON test_cases(label) WHERE label IS NOT NULL;
                CREATE INDEX IF NOT EXISTS idx_test_cases_archived ON test_cases(is_archived);
                """;
            cmd.ExecuteNonQuery();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task CreateAsync(TestCase testCase, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO test_cases (
                    id, name, description, source_work_item_id, created_at, updated_at,
                    is_archived, automation_kind, executable_artifact_json, conformance_json,
                    label, last_run_passed, last_run_at, last_run_result
                ) VALUES (
                    $id, $name, $desc, $wid, $ca, $ua, $archived, $kind, $exec, $conf,
                    $label, $passed, $run_at, $result
                );
                """;
            Bind(cmd, testCase);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task BulkCreateAsync(IReadOnlyList<TestCase> testCases, CancellationToken ct = default)
    {
        if (testCases.Count == 0) return;

        await _writeLock.WaitAsync(ct);
        try
        {
            // Execute all insertions inside a single transaction for atomicity and speed.
            using var transaction = _conn.BeginTransaction();
            try
            {
                foreach (var tc in testCases)
                {
                    using var cmd = _conn.CreateCommand();
                    cmd.Transaction = transaction;
                    cmd.CommandText = """
                        INSERT INTO test_cases (
                            id, name, description, source_work_item_id, created_at, updated_at,
                            is_archived, automation_kind, executable_artifact_json, conformance_json,
                            label, last_run_passed, last_run_at, last_run_result
                        ) VALUES (
                            $id, $name, $desc, $wid, $ca, $ua, $archived, $kind, $exec, $conf,
                            $label, $passed, $run_at, $result
                        );
                        """;
                    Bind(cmd, tc);
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                await transaction.CommitAsync(ct);
            }
            catch
            {
                await transaction.RollbackAsync(ct);
                throw;
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task UpdateAsync(TestCase testCase, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE test_cases SET
                    name = $name,
                    description = $desc,
                    source_work_item_id = $wid,
                    updated_at = $ua,
                    is_archived = $archived,
                    automation_kind = $kind,
                    executable_artifact_json = $exec,
                    conformance_json = $conf,
                    label = $label,
                    last_run_passed = $passed,
                    last_run_at = $run_at,
                    last_run_result = $result
                WHERE id = $id;
                """;
            Bind(cmd, testCase);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<TestCase?> GetAsync(string id, CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM test_cases WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    public async IAsyncEnumerable<TestCase> ListAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM test_cases ORDER BY created_at ASC;";
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            yield return Read(reader);
        }
    }

    public async IAsyncEnumerable<TestCase> ListByWorkItemAsync(string workItemId, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM test_cases WHERE source_work_item_id = $wid ORDER BY created_at ASC;";
        cmd.Parameters.AddWithValue("$wid", workItemId);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            yield return Read(reader);
        }
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM test_cases WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct);
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
            _conn.Dispose();
        }
        catch (NullReferenceException)
        {
        }
        finally
        {
            _writeLock.Dispose();
        }
    }

    private static void Bind(SqliteCommand cmd, TestCase tc)
    {
        cmd.Parameters.AddWithValue("$id", tc.Id);
        cmd.Parameters.AddWithValue("$name", tc.Name);
        cmd.Parameters.AddWithValue("$desc", tc.Description);
        cmd.Parameters.AddWithValue("$wid", tc.SourceWorkItemId);
        cmd.Parameters.AddWithValue("$ca", tc.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$ua", tc.UpdatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$archived", tc.IsArchived ? 1 : 0);
        cmd.Parameters.AddWithValue("$kind", (object?)tc.AutomationKind?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$exec", (object?)tc.ExecutableArtifactJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$conf", (object?)tc.ConformanceJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$label", (object?)tc.Label ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$passed", tc.LastRunPassed.HasValue ? (tc.LastRunPassed.Value ? 1 : 0) : DBNull.Value);
        cmd.Parameters.AddWithValue("$run_at", (object?)tc.LastRunAt?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$result", (object?)tc.LastRunResult ?? DBNull.Value);
    }

    private static TestCase Read(SqliteDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")),
        Name = r.GetString(r.GetOrdinal("name")),
        Description = r.GetString(r.GetOrdinal("description")),
        SourceWorkItemId = r.GetString(r.GetOrdinal("source_work_item_id")),
        CreatedAt = DateTimeOffset.Parse(r.GetString(r.GetOrdinal("created_at")), System.Globalization.CultureInfo.InvariantCulture),
        UpdatedAt = DateTimeOffset.Parse(r.GetString(r.GetOrdinal("updated_at")), System.Globalization.CultureInfo.InvariantCulture),
        IsArchived = r.GetInt32(r.GetOrdinal("is_archived")) != 0,
        AutomationKind = r.IsDBNull(r.GetOrdinal("automation_kind"))
            ? null
            : Enum.Parse<AutomationKind>(r.GetString(r.GetOrdinal("automation_kind"))),
        ExecutableArtifactJson = r.IsDBNull(r.GetOrdinal("executable_artifact_json")) ? null : r.GetString(r.GetOrdinal("executable_artifact_json")),
        ConformanceJson = r.IsDBNull(r.GetOrdinal("conformance_json")) ? null : r.GetString(r.GetOrdinal("conformance_json")),
        Label = r.IsDBNull(r.GetOrdinal("label")) ? null : r.GetString(r.GetOrdinal("label")),
        LastRunPassed = r.IsDBNull(r.GetOrdinal("last_run_passed")) ? null : r.GetInt32(r.GetOrdinal("last_run_passed")) != 0,
        LastRunAt = r.IsDBNull(r.GetOrdinal("last_run_at"))
            ? null
            : DateTimeOffset.Parse(r.GetString(r.GetOrdinal("last_run_at")), System.Globalization.CultureInfo.InvariantCulture),
        LastRunResult = r.IsDBNull(r.GetOrdinal("last_run_result")) ? null : r.GetString(r.GetOrdinal("last_run_result")),
    };
}
