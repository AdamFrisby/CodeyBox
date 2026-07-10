using System.Text.Json;
using Microsoft.Data.Sqlite;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// SQLite-backed audit report store. Shares the same database file as
/// <see cref="SqliteWorkItemStore"/>; the audit_reports table is created here
/// via its own additive migration so the stores remain independently testable.
/// </summary>
public sealed class SqliteAuditReportStore : IAuditReportStore, IDisposable
{
    private const int DeleteBatchSize = 500;

    private readonly SqliteConnection _conn;
    private readonly SqliteDatabaseWriteGate _writeLock;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public SqliteAuditReportStore(
        string path,
        SqliteDatabaseWriteGateFactory? writeGateFactory = null)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _conn = new SqliteConnection($"Data Source={path}");
        _writeLock = (writeGateFactory ?? SqliteDatabaseWriteGateFactory.Default).ForPath(path);
        _writeLock.Wait();
        try
        {
            _conn.Open();

            using (var pragmaCmd = _conn.CreateCommand())
            {
                pragmaCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=30000; PRAGMA foreign_keys=ON;";
                pragmaCmd.ExecuteNonQuery();
            }

            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS audit_reports (
                    id              TEXT PRIMARY KEY,
                    work_item_id    TEXT NOT NULL REFERENCES work_items(id) ON DELETE CASCADE,
                    iteration       INTEGER NOT NULL,
                    auditor_name    TEXT NOT NULL,
                    auditor_kind    TEXT NOT NULL,
                    worst_severity  TEXT NOT NULL,
                    started_at      TEXT NOT NULL,
                    ended_at        TEXT NOT NULL,
                    duration_ms     INTEGER NOT NULL,
                    findings_json   TEXT NOT NULL,
                    raw_output      TEXT
                );
                CREATE INDEX IF NOT EXISTS idx_audit_reports_workitem_iter
                    ON audit_reports(work_item_id, iteration, auditor_name);
                """;
            cmd.ExecuteNonQuery();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task CreateAsync(AuditReport report, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO audit_reports (id, work_item_id, iteration, auditor_name, auditor_kind,
                    worst_severity, started_at, ended_at, duration_ms, findings_json, raw_output)
                VALUES ($id, $wi, $iter, $name, $kind, $sev, $started, $ended, $dur, $findings, $raw);
                """;
            cmd.Parameters.AddWithValue("$id", report.Id);
            cmd.Parameters.AddWithValue("$wi", report.WorkItemId);
            cmd.Parameters.AddWithValue("$iter", report.Iteration);
            cmd.Parameters.AddWithValue("$name", report.AuditorName);
            cmd.Parameters.AddWithValue("$kind", report.AuditorKind);
            cmd.Parameters.AddWithValue("$sev", report.WorstSeverity);
            cmd.Parameters.AddWithValue("$started", report.StartedAt.ToString("O"));
            cmd.Parameters.AddWithValue("$ended", report.EndedAt.ToString("O"));
            cmd.Parameters.AddWithValue("$dur", report.DurationMs);
            cmd.Parameters.AddWithValue("$findings", JsonSerializer.Serialize(report.Findings, JsonOpts));
            cmd.Parameters.AddWithValue("$raw", (object?)report.RawOutput ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _writeLock.Release(); }
    }

    public async Task<IReadOnlyList<AuditReport>> GetByWorkItemAsync(string workItemId, CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, work_item_id, iteration, auditor_name, auditor_kind, worst_severity,
                   started_at, ended_at, duration_ms, findings_json, raw_output
            FROM audit_reports
            WHERE work_item_id = $wi
            ORDER BY iteration ASC, auditor_name ASC;
            """;
        cmd.Parameters.AddWithValue("$wi", workItemId);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        var results = new List<AuditReport>();
        while (await reader.ReadAsync(ct))
            results.Add(ReadRow(reader));
        return results;
    }

    public async Task<string?> GetRawOutputAsync(string workItemId, int iteration, string auditorName, CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT raw_output FROM audit_reports
            WHERE work_item_id = $wi AND iteration = $iter AND auditor_name = $name
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$wi", workItemId);
        cmd.Parameters.AddWithValue("$iter", iteration);
        cmd.Parameters.AddWithValue("$name", auditorName);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is string s ? s : null;
    }

    public async Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        var deleted = 0;
        var cutoffText = cutoff.ToString("O");

        while (true)
        {
            await _writeLock.WaitAsync(ct);
            try
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = """
                    DELETE FROM audit_reports
                    WHERE rowid IN (
                        SELECT rowid
                        FROM audit_reports
                        WHERE started_at < $cutoff
                        LIMIT $limit
                    );
                    """;
                cmd.Parameters.AddWithValue("$cutoff", cutoffText);
                cmd.Parameters.AddWithValue("$limit", DeleteBatchSize);
                var batchDeleted = await cmd.ExecuteNonQueryAsync(ct);
                deleted += batchDeleted;
                if (batchDeleted < DeleteBatchSize)
                    return deleted;
            }
            finally
            {
                _writeLock.Release();
            }

            await Task.Yield();
        }
    }

    public void Dispose()
    {
        _conn.Dispose();
        _writeLock.Dispose();
    }

    private static AuditReport ReadRow(SqliteDataReader r)
    {
        var findingsJson = r.GetString(r.GetOrdinal("findings_json"));
        var findings = DeserializeFindings(findingsJson);
        var rawOrd = r.GetOrdinal("raw_output");
        return new AuditReport
        {
            Id = r.GetString(r.GetOrdinal("id")),
            WorkItemId = r.GetString(r.GetOrdinal("work_item_id")),
            Iteration = r.GetInt32(r.GetOrdinal("iteration")),
            AuditorName = r.GetString(r.GetOrdinal("auditor_name")),
            AuditorKind = r.GetString(r.GetOrdinal("auditor_kind")),
            WorstSeverity = r.GetString(r.GetOrdinal("worst_severity")),
            StartedAt = DateTimeOffset.Parse(r.GetString(r.GetOrdinal("started_at")),
                System.Globalization.CultureInfo.InvariantCulture),
            EndedAt = DateTimeOffset.Parse(r.GetString(r.GetOrdinal("ended_at")),
                System.Globalization.CultureInfo.InvariantCulture),
            DurationMs = r.GetInt64(r.GetOrdinal("duration_ms")),
            Findings = findings,
            RawOutput = r.IsDBNull(rawOrd) ? null : r.GetString(rawOrd),
        };
    }

    private static IReadOnlyList<AuditReportFinding> DeserializeFindings(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<AuditReportFinding>>(json, JsonOpts) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
