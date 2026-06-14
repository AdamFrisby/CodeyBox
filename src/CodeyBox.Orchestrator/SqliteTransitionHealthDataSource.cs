using System.Text.Json;
using CodeyBox.Core;
using Microsoft.Data.Sqlite;

namespace CodeyBox.Orchestrator;

/// <summary>
/// SQLite-backed implementation of <see cref="ITransitionHealthDataSource"/>.
/// Reads from the shared state database (<c>state.db</c>) using a read-only
/// connection; WAL mode lets it run alongside the writer stores without
/// SQLITE_BUSY contention. No DDL is performed here — the source assumes the
/// three tables already exist (created by their owning stores). When a table
/// is absent (e.g. the orchestrator has not yet recorded an audit report), the
/// per-table query short-circuits with an empty list rather than throwing.
/// </summary>
public sealed class SqliteTransitionHealthDataSource : ITransitionHealthDataSource
{
    private readonly string _path;

    public SqliteTransitionHealthDataSource(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _path = path;
    }

    public async Task<TransitionDataSnapshot> LoadAsync(
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        int maxRowsPerSource,
        CancellationToken ct = default)
    {
        if (maxRowsPerSource <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxRowsPerSource));

        if (!File.Exists(_path))
        {
            return new TransitionDataSnapshot(
                Array.Empty<TransitionInvolvementRow>(),
                Array.Empty<TransitionAuditReportRow>(),
                Array.Empty<TransitionTerminalFailureRow>());
        }

        // Open one read-only connection and reuse it across the three reads.
        using var conn = new SqliteConnection($"Data Source={_path};Mode=ReadOnly");
        await conn.OpenAsync(ct);

        var involvements = await TryReadAsync(
            conn, "agent_involvement", ReadInvolvementsAsync,
            windowStart, windowEnd, maxRowsPerSource, ct);
        var auditReports = await TryReadAsync(
            conn, "audit_reports", ReadAuditReportsAsync,
            windowStart, windowEnd, maxRowsPerSource, ct);
        var terminals = await TryReadAsync(
            conn, "work_items", ReadTerminalFailuresAsync,
            windowStart, windowEnd, maxRowsPerSource, ct);

        return new TransitionDataSnapshot(involvements, auditReports, terminals);
    }

    private static async Task<IReadOnlyList<T>> TryReadAsync<T>(
        SqliteConnection conn,
        string tableName,
        Func<SqliteConnection, DateTimeOffset, DateTimeOffset, int, CancellationToken, Task<IReadOnlyList<T>>> read,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        int maxRows,
        CancellationToken ct)
    {
        if (!await TableExistsAsync(conn, tableName, ct))
            return Array.Empty<T>();
        return await read(conn, windowStart, windowEnd, maxRows, ct);
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection conn, string name, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$n LIMIT 1;";
        cmd.Parameters.AddWithValue("$n", name);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is not null;
    }

    private static async Task<IReadOnlyList<TransitionInvolvementRow>> ReadInvolvementsAsync(
        SqliteConnection conn, DateTimeOffset windowStart, DateTimeOffset windowEnd,
        int maxRows, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT work_item_id, phase, iteration, outcome, ended_at
            FROM agent_involvement
            WHERE ended_at IS NOT NULL
              AND ended_at >= $start
              AND ended_at <= $end
            ORDER BY ended_at DESC
            LIMIT $lim;
            """;
        cmd.Parameters.AddWithValue("$start", windowStart.ToUniversalTime().ToString("O"));
        cmd.Parameters.AddWithValue("$end", windowEnd.ToUniversalTime().ToString("O"));
        cmd.Parameters.AddWithValue("$lim", maxRows);

        var results = new List<TransitionInvolvementRow>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new TransitionInvolvementRow(
                WorkItemId: reader.GetString(0),
                Phase: reader.GetString(1),
                Iteration: reader.IsDBNull(2) ? null : reader.GetInt32(2),
                Outcome: reader.IsDBNull(3) ? null : reader.GetString(3),
                EndedAt: DateTimeOffset.Parse(reader.GetString(4),
                    System.Globalization.CultureInfo.InvariantCulture)));
        }
        return results;
    }

    private static async Task<IReadOnlyList<TransitionAuditReportRow>> ReadAuditReportsAsync(
        SqliteConnection conn, DateTimeOffset windowStart, DateTimeOffset windowEnd,
        int maxRows, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT work_item_id, iteration, auditor_name, worst_severity, ended_at, findings_json
            FROM audit_reports
            WHERE ended_at >= $start
              AND ended_at <= $end
            ORDER BY ended_at DESC
            LIMIT $lim;
            """;
        cmd.Parameters.AddWithValue("$start", windowStart.ToUniversalTime().ToString("O"));
        cmd.Parameters.AddWithValue("$end", windowEnd.ToUniversalTime().ToString("O"));
        cmd.Parameters.AddWithValue("$lim", maxRows);

        var results = new List<TransitionAuditReportRow>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var titles = ExtractFindingTitles(reader.IsDBNull(5) ? null : reader.GetString(5));
            results.Add(new TransitionAuditReportRow(
                WorkItemId: reader.GetString(0),
                Iteration: reader.GetInt32(1),
                AuditorName: reader.GetString(2),
                WorstSeverity: reader.GetString(3),
                EndedAt: DateTimeOffset.Parse(reader.GetString(4),
                    System.Globalization.CultureInfo.InvariantCulture),
                FindingTitles: titles));
        }
        return results;
    }

    private static async Task<IReadOnlyList<TransitionTerminalFailureRow>> ReadTerminalFailuresAsync(
        SqliteConnection conn, DateTimeOffset windowStart, DateTimeOffset windowEnd,
        int maxRows, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        // Terminal infra states the classifier scores:
        //   100 = Failed, 103 = AbandonedAfterRecoveryAttempts,
        //   104 = MergeConflictResolutionFailed.
        // Done / Cancelled / AuditFailed are intentionally excluded — Done is
        // throughput (not a health signal), Cancelled is operator intent, and
        // AuditFailed (rework-cap hit) is a work-quality outcome whose
        // preceding audit_report rows already contribute to the audit-stage
        // score. See docs/transition-health.md.
        var failedState = (int)WorkItemState.Failed;
        var abandonedState = (int)WorkItemState.AbandonedAfterRecoveryAttempts;
        var mcrfState = (int)WorkItemState.MergeConflictResolutionFailed;
        cmd.CommandText = $"""
            SELECT id, state, failure_kind, updated_at
            FROM work_items
            WHERE state IN ({failedState}, {abandonedState}, {mcrfState})
              AND updated_at >= $start
              AND updated_at <= $end
            ORDER BY updated_at DESC
            LIMIT $lim;
            """;
        cmd.Parameters.AddWithValue("$start", windowStart.ToUniversalTime().ToString("O"));
        cmd.Parameters.AddWithValue("$end", windowEnd.ToUniversalTime().ToString("O"));
        cmd.Parameters.AddWithValue("$lim", maxRows);

        var results = new List<TransitionTerminalFailureRow>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new TransitionTerminalFailureRow(
                WorkItemId: reader.GetString(0),
                State: reader.GetInt32(1),
                FailureKind: reader.IsDBNull(2) ? null : reader.GetString(2),
                UpdatedAt: DateTimeOffset.Parse(reader.GetString(3),
                    System.Globalization.CultureInfo.InvariantCulture)));
        }
        return results;
    }

    /// <summary>
    /// Pulls just the <c>title</c> field out of each finding object in the
    /// stored JSON. We do not need the full <see cref="Core.AuditReportFinding"/>
    /// records here, and parsing only the titles keeps the classifier hot path
    /// fast on snapshots with hundreds of audit reports.
    /// </summary>
    internal static IReadOnlyList<string> ExtractFindingTitles(string? findingsJson)
    {
        if (string.IsNullOrWhiteSpace(findingsJson))
            return Array.Empty<string>();
        try
        {
            using var doc = JsonDocument.Parse(findingsJson, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
            });
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return Array.Empty<string>();
            var titles = new List<string>(doc.RootElement.GetArrayLength());
            foreach (var f in doc.RootElement.EnumerateArray())
            {
                if (f.ValueKind != JsonValueKind.Object) continue;
                if (TryReadPropertyCaseInsensitive(f, "title", out var titleProp)
                    && titleProp.ValueKind == JsonValueKind.String
                    && titleProp.GetString() is { } t)
                {
                    titles.Add(t);
                }
            }
            return titles;
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    private static bool TryReadPropertyCaseInsensitive(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var p in obj.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = p.Value;
                return true;
            }
        }
        value = default;
        return false;
    }
}
