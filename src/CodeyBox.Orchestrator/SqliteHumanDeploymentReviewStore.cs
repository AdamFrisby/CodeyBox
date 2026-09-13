using System.Globalization;
using Microsoft.Data.Sqlite;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// SQLite-backed store for parked human deployment reviews. Shares the state
/// database file (same WAL/busy-timeout conventions as
/// <see cref="SqliteWorkItemQuestionStore"/>) so reviews live alongside the
/// work items and questions they reference.
/// </summary>
public sealed class SqliteHumanDeploymentReviewStore : IHumanDeploymentReviewStore, IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly SqliteDatabaseWriteGate _writeLock;

    public SqliteHumanDeploymentReviewStore(
        string path,
        SqliteDatabaseWriteGateFactory? writeGateFactory = null)
    {
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
                pragmaCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=30000; PRAGMA foreign_keys=ON;";
                pragmaCmd.ExecuteNonQuery();
            }

            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS human_deployment_reviews (
                    work_item_id               TEXT NOT NULL,
                    iteration                  INTEGER NOT NULL,
                    deployment_id              TEXT NOT NULL,
                    endpoint_json              TEXT NOT NULL,
                    deadline                   TEXT NOT NULL,
                    requested_at               TEXT NOT NULL,
                    brief                      TEXT NOT NULL,
                    question_id                TEXT NOT NULL,
                    code_findings_json         TEXT NOT NULL DEFAULT '[]',
                    code_completed_json          TEXT NOT NULL DEFAULT '[]',
                    human_auditors_json        TEXT NOT NULL,
                    automated_findings_json    TEXT NOT NULL,
                    automated_completed_json   TEXT NOT NULL,
                    automated_incomplete_json  TEXT NOT NULL,
                    active_audit_agent_kind    TEXT,
                    declared_short_circuit     INTEGER NOT NULL DEFAULT 0,
                    incomplete_verdict         INTEGER NOT NULL DEFAULT 0,
                    status                     INTEGER NOT NULL DEFAULT 0,
                    notes                      TEXT,
                    decided_at                 TEXT,
                    decided_by                 TEXT,
                    consumed_at                TEXT,
                    PRIMARY KEY (work_item_id, iteration)
                );
                CREATE INDEX IF NOT EXISTS idx_human_reviews_pending
                    ON human_deployment_reviews(status, deadline);
                """;
            cmd.ExecuteNonQuery();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<HumanDeploymentReview> GetOrCreatePendingAsync(HumanDeploymentReview review, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(review);
        await _writeLock.WaitAsync(ct);
        try
        {
            var existing = await TryGetAsync(review.WorkItemId, review.Iteration, ct).ConfigureAwait(false);
            if (existing is not null && existing.ConsumedAt is null)
                return existing;

            if (existing is not null)
            {
                using var delete = _conn.CreateCommand();
                delete.CommandText = """
                    DELETE FROM human_deployment_reviews
                    WHERE work_item_id = $wid AND iteration = $iter;
                    """;
                delete.Parameters.AddWithValue("$wid", review.WorkItemId);
                delete.Parameters.AddWithValue("$iter", review.Iteration);
                await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO human_deployment_reviews
                    (work_item_id, iteration, deployment_id, endpoint_json, deadline,
                     requested_at, brief, question_id, code_findings_json, code_completed_json, human_auditors_json,
                     automated_findings_json, automated_completed_json, automated_incomplete_json,
                     active_audit_agent_kind, declared_short_circuit, incomplete_verdict,
                     status, notes, decided_at, decided_by, consumed_at)
                VALUES ($wid, $iter, $dep, $ep, $deadline, $requested, $brief, $qid,
                        $codefindings, $codecompleted, $humans, $findings, $completed, $incomplete, $agent,
                        $shortcircuit, $incompleteverdict, $status, $notes, $decidedat, $decidedby, $consumedat);
                """;
            Bind(cmd, review);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return review;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<HumanDeploymentReview?> TryGetAsync(string workItemId, int iteration, CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM human_deployment_reviews
            WHERE work_item_id = $wid AND iteration = $iter;
            """;
        cmd.Parameters.AddWithValue("$wid", workItemId);
        cmd.Parameters.AddWithValue("$iter", iteration);
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Read(reader) : null;
    }

    public async Task<HumanDeploymentReview?> GetActiveForWorkItemAsync(string workItemId, CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM human_deployment_reviews
            WHERE work_item_id = $wid AND consumed_at IS NULL
            ORDER BY iteration DESC
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$wid", workItemId);
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Read(reader) : null;
    }

    public async Task<bool> RecordVerdictAsync(
        string workItemId,
        int iteration,
        bool approved,
        string? notes,
        string? decidedBy,
        DateTimeOffset decidedAt,
        CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE human_deployment_reviews
                SET status = $status,
                    notes = $notes,
                    decided_at = $decidedat,
                    decided_by = $decidedby
                WHERE work_item_id = $wid AND iteration = $iter AND status = 0;
                """;
            cmd.Parameters.AddWithValue("$status", (int)(approved
                ? HumanDeploymentReviewStatus.Approved
                : HumanDeploymentReviewStatus.Rejected));
            cmd.Parameters.AddWithValue("$notes", (object?)notes ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$decidedat", decidedAt.ToString("O"));
            cmd.Parameters.AddWithValue("$decidedby", (object?)decidedBy ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$wid", workItemId);
            cmd.Parameters.AddWithValue("$iter", iteration);
            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> MarkExpiredAsync(string workItemId, int iteration, DateTimeOffset expiredAt, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE human_deployment_reviews
                SET status = 3,
                    decided_at = $decidedat
                WHERE work_item_id = $wid AND iteration = $iter AND status = 0;
                """;
            cmd.Parameters.AddWithValue("$decidedat", expiredAt.ToString("O"));
            cmd.Parameters.AddWithValue("$wid", workItemId);
            cmd.Parameters.AddWithValue("$iter", iteration);
            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task MarkConsumedAsync(string workItemId, int iteration, DateTimeOffset consumedAt, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE human_deployment_reviews
                SET consumed_at = $consumedat
                WHERE work_item_id = $wid AND iteration = $iter AND consumed_at IS NULL;
                """;
            cmd.Parameters.AddWithValue("$consumedat", consumedAt.ToString("O"));
            cmd.Parameters.AddWithValue("$wid", workItemId);
            cmd.Parameters.AddWithValue("$iter", iteration);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<HumanDeploymentReview>> ListExpiredPendingAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM human_deployment_reviews
            WHERE status = 0 AND deadline <= $now
            ORDER BY deadline ASC;
            """;
        cmd.Parameters.AddWithValue("$now", now.ToString("O"));
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var results = new List<HumanDeploymentReview>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            results.Add(Read(reader));
        return results;
    }

    public void Dispose()
    {
        _conn.Dispose();
        _writeLock.Dispose();
    }

    private static void Bind(SqliteCommand cmd, HumanDeploymentReview review)
    {
        cmd.Parameters.AddWithValue("$wid", review.WorkItemId);
        cmd.Parameters.AddWithValue("$iter", review.Iteration);
        cmd.Parameters.AddWithValue("$dep", review.DeploymentId);
        cmd.Parameters.AddWithValue("$ep", review.EndpointJson);
        cmd.Parameters.AddWithValue("$deadline", review.Deadline.ToString("O"));
        cmd.Parameters.AddWithValue("$requested", review.RequestedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$brief", review.Brief);
        cmd.Parameters.AddWithValue("$qid", review.QuestionId);
        cmd.Parameters.AddWithValue("$codefindings", review.CodeFindingsJson);
        cmd.Parameters.AddWithValue("$codecompleted", review.CodeCompletedJson);
        cmd.Parameters.AddWithValue("$humans", review.HumanAuditorsJson);
        cmd.Parameters.AddWithValue("$findings", review.AutomatedFindingsJson);
        cmd.Parameters.AddWithValue("$completed", review.AutomatedCompletedJson);
        cmd.Parameters.AddWithValue("$incomplete", review.AutomatedIncompleteJson);
        cmd.Parameters.AddWithValue("$agent", (object?)review.ActiveAuditAgentKind ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$shortcircuit", review.DeclaredShortCircuitBlocking ? 1 : 0);
        cmd.Parameters.AddWithValue("$incompleteverdict", review.IncompleteVerdict ? 1 : 0);
        cmd.Parameters.AddWithValue("$status", (int)review.Status);
        cmd.Parameters.AddWithValue("$notes", (object?)review.Notes ?? DBNull.Value);
        cmd.Parameters.AddWithValue(
            "$decidedat",
            review.DecidedAt is { } decided ? decided.ToString("O") : DBNull.Value);
        cmd.Parameters.AddWithValue("$decidedby", (object?)review.DecidedBy ?? DBNull.Value);
        cmd.Parameters.AddWithValue(
            "$consumedat",
            review.ConsumedAt is { } consumed ? consumed.ToString("O") : DBNull.Value);
    }

    private static HumanDeploymentReview Read(SqliteDataReader r) => new()
    {
        WorkItemId = r.GetString(r.GetOrdinal("work_item_id")),
        Iteration = r.GetInt32(r.GetOrdinal("iteration")),
        DeploymentId = r.GetString(r.GetOrdinal("deployment_id")),
        EndpointJson = r.GetString(r.GetOrdinal("endpoint_json")),
        Deadline = Parse(r, "deadline"),
        RequestedAt = Parse(r, "requested_at"),
        Brief = r.GetString(r.GetOrdinal("brief")),
        QuestionId = r.GetString(r.GetOrdinal("question_id")),
        CodeFindingsJson = Nullable(r, "code_findings_json") ?? "[]",
        CodeCompletedJson = Nullable(r, "code_completed_json") ?? "[]",
        HumanAuditorsJson = r.GetString(r.GetOrdinal("human_auditors_json")),
        AutomatedFindingsJson = r.GetString(r.GetOrdinal("automated_findings_json")),
        AutomatedCompletedJson = r.GetString(r.GetOrdinal("automated_completed_json")),
        AutomatedIncompleteJson = r.GetString(r.GetOrdinal("automated_incomplete_json")),
        ActiveAuditAgentKind = Nullable(r, "active_audit_agent_kind"),
        DeclaredShortCircuitBlocking = r.GetInt32(r.GetOrdinal("declared_short_circuit")) != 0,
        IncompleteVerdict = r.GetInt32(r.GetOrdinal("incomplete_verdict")) != 0,
        Status = (HumanDeploymentReviewStatus)r.GetInt32(r.GetOrdinal("status")),
        Notes = Nullable(r, "notes"),
        DecidedAt = NullableDate(r, "decided_at"),
        DecidedBy = Nullable(r, "decided_by"),
        ConsumedAt = NullableDate(r, "consumed_at"),
    };

    private static DateTimeOffset Parse(SqliteDataReader r, string column)
        => DateTimeOffset.Parse(r.GetString(r.GetOrdinal(column)), CultureInfo.InvariantCulture);

    private static string? Nullable(SqliteDataReader r, string column)
    {
        var ord = r.GetOrdinal(column);
        return r.IsDBNull(ord) ? null : r.GetString(ord);
    }

    private static DateTimeOffset? NullableDate(SqliteDataReader r, string column)
    {
        var ord = r.GetOrdinal(column);
        return r.IsDBNull(ord)
            ? null
            : DateTimeOffset.Parse(r.GetString(ord), CultureInfo.InvariantCulture);
    }
}
