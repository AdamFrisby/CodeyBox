using System.Globalization;
using Microsoft.Data.Sqlite;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// SQLite-backed <see cref="IAuditCheckPublicationStore"/>. Shares the state
/// database file with the other orchestrator stores; the
/// <c>audit_check_publications</c> table is created here via its own additive
/// migration so the store remains independently testable.
///
/// Concurrency: all writes go through the per-database write gate (atomic
/// per key). <see cref="TryCompleteAsync"/> additionally guards against stale
/// completions in SQL: a completion lands only when no newer
/// (iteration, attempt) row exists for the same publication scope.
/// </summary>
public sealed class SqliteAuditCheckPublicationStore : IAuditCheckPublicationStore, IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly SqliteDatabaseWriteGate _writeLock;

    public SqliteAuditCheckPublicationStore(
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

            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS audit_check_publications (
                    repository            TEXT NOT NULL,
                    head_sha              TEXT NOT NULL,
                    work_item_id          TEXT NOT NULL,
                    target                TEXT NOT NULL,
                    iteration             INTEGER NOT NULL,
                    attempt               INTEGER NOT NULL,
                    scope                 TEXT NOT NULL,
                    check_name            TEXT NOT NULL,
                    external_id           TEXT NOT NULL,
                    check_run_id          INTEGER NULL,
                    status                TEXT NULL,
                    conclusion            TEXT NULL,
                    annotations_published INTEGER NOT NULL DEFAULT 0,
                    batches_sent          INTEGER NOT NULL DEFAULT 0,
                    last_batch_uncertain  INTEGER NOT NULL DEFAULT 0,
                    state                 TEXT NOT NULL,
                    blocked_reason        TEXT NULL,
                    last_error            TEXT NULL,
                    transport_attempts    INTEGER NOT NULL DEFAULT 0,
                    next_retry_utc        TEXT NULL,
                    updated_utc           TEXT NOT NULL,
                    PRIMARY KEY (repository, head_sha, work_item_id, target, iteration, attempt, scope)
                );
                """;
                cmd.ExecuteNonQuery();
            }

            using var indexCmd = _conn.CreateCommand();
            indexCmd.CommandText = """
                CREATE INDEX IF NOT EXISTS idx_audit_check_pubs_retry
                    ON audit_check_publications(state, next_retry_utc);
                CREATE INDEX IF NOT EXISTS idx_audit_check_pubs_scope
                    ON audit_check_publications(repository, head_sha, work_item_id, target, scope, iteration, attempt);
                """;
            indexCmd.ExecuteNonQuery();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<AuditCheckPublicationRecord?> GetAsync(
        string repository,
        string headSha,
        string workItemId,
        string target,
        int iteration,
        int attempt,
        string scope,
        CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT repository, head_sha, work_item_id, target, iteration, attempt, scope,
                   check_name, external_id, check_run_id, status, conclusion,
                   annotations_published, batches_sent, last_batch_uncertain, state,
                   blocked_reason, last_error, transport_attempts, next_retry_utc, updated_utc
            FROM audit_check_publications
            WHERE repository = $repo AND head_sha = $sha AND work_item_id = $wi
              AND target = $target AND iteration = $iter AND attempt = $attempt AND scope = $scope
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$repo", repository);
        cmd.Parameters.AddWithValue("$sha", headSha);
        cmd.Parameters.AddWithValue("$wi", workItemId);
        cmd.Parameters.AddWithValue("$target", target);
        cmd.Parameters.AddWithValue("$iter", iteration);
        cmd.Parameters.AddWithValue("$attempt", attempt);
        cmd.Parameters.AddWithValue("$scope", scope);
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadRow(reader) : null;
    }

    public async Task UpsertAsync(AuditCheckPublicationRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            AuditCheckPublicationRecord? existing = null;
            using (var select = _conn.CreateCommand())
            {
                select.CommandText = """
                    SELECT repository, head_sha, work_item_id, target, iteration, attempt, scope,
                           check_name, external_id, check_run_id, status, conclusion,
                           annotations_published, batches_sent, last_batch_uncertain, state,
                           blocked_reason, last_error, transport_attempts, next_retry_utc, updated_utc
                    FROM audit_check_publications
                    WHERE repository = $repo AND head_sha = $sha AND work_item_id = $wi
                      AND target = $target AND iteration = $iter AND attempt = $attempt AND scope = $scope
                    LIMIT 1;
                    """;
                BindKey(select, record);
                using var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (await reader.ReadAsync(ct).ConfigureAwait(false))
                    existing = ReadRow(reader);
            }

            if (existing is not null)
            {
                // Never regress a completed publication back to a live state,
                // and never null out a proven forge check-run id: concurrent
                // delivery and restarts converge on stored identity.
                if (existing.State == AuditCheckPublicationState.Completed &&
                    record.State != AuditCheckPublicationState.Completed)
                    return;
                if (existing.CheckRunId.HasValue && !record.CheckRunId.HasValue)
                    record = record with { CheckRunId = existing.CheckRunId };
                if (record.TransportAttempts < existing.TransportAttempts)
                    record = record with { TransportAttempts = existing.TransportAttempts };
            }

            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO audit_check_publications (
                    repository, head_sha, work_item_id, target, iteration, attempt, scope,
                    check_name, external_id, check_run_id, status, conclusion,
                    annotations_published, batches_sent, last_batch_uncertain, state,
                    blocked_reason, last_error, transport_attempts, next_retry_utc, updated_utc)
                VALUES (
                    $repo, $sha, $wi, $target, $iter, $attempt, $scope,
                    $name, $ext, $runid, $status, $conclusion,
                    $pub, $batches, $uncertain, $state,
                    $blocked, $err, $tries, $retry, $updated)
                ON CONFLICT (repository, head_sha, work_item_id, target, iteration, attempt, scope)
                DO UPDATE SET
                    check_name = excluded.check_name,
                    external_id = excluded.external_id,
                    check_run_id = excluded.check_run_id,
                    status = excluded.status,
                    conclusion = excluded.conclusion,
                    annotations_published = excluded.annotations_published,
                    batches_sent = excluded.batches_sent,
                    last_batch_uncertain = excluded.last_batch_uncertain,
                    state = excluded.state,
                    blocked_reason = excluded.blocked_reason,
                    last_error = excluded.last_error,
                    transport_attempts = excluded.transport_attempts,
                    next_retry_utc = excluded.next_retry_utc,
                    updated_utc = excluded.updated_utc;
                """;
            BindKey(cmd, record);
            cmd.Parameters.AddWithValue("$name", record.CheckName);
            cmd.Parameters.AddWithValue("$ext", record.ExternalId);
            cmd.Parameters.AddWithValue("$runid", (object?)record.CheckRunId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$status", (object?)record.Status ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$conclusion", (object?)record.Conclusion ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$pub", record.AnnotationsPublished);
            cmd.Parameters.AddWithValue("$batches", record.BatchesSent);
            cmd.Parameters.AddWithValue("$uncertain", record.LastBatchUncertain ? 1 : 0);
            cmd.Parameters.AddWithValue("$state", record.State.ToString());
            cmd.Parameters.AddWithValue("$blocked", (object?)record.BlockedReason ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$err", (object?)record.LastError ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$tries", record.TransportAttempts);
            cmd.Parameters.AddWithValue("$retry", (object?)record.NextRetryUtc?.ToString("O") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$updated", record.UpdatedUtc.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> TryCompleteAsync(AuditCheckPublicationRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.State != AuditCheckPublicationState.Completed)
            throw new ArgumentException("TryComplete requires a Completed record.", nameof(record));

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cmd = _conn.CreateCommand();
            // A stale completion (older iteration/attempt) never overwrites a
            // newer run: the write lands only when no newer row exists for the
            // same publication scope. Same-key re-completion is idempotent.
            cmd.CommandText = """
                UPDATE audit_check_publications
                SET check_run_id = $runid,
                    status = $status,
                    conclusion = $conclusion,
                    annotations_published = $pub,
                    batches_sent = $batches,
                    last_batch_uncertain = $uncertain,
                    state = 'Completed',
                    blocked_reason = NULL,
                    last_error = NULL,
                    next_retry_utc = NULL,
                    updated_utc = $updated
                WHERE repository = $repo AND head_sha = $sha AND work_item_id = $wi
                  AND target = $target AND iteration = $iter AND attempt = $attempt AND scope = $scope
                  AND NOT EXISTS (
                      SELECT 1 FROM audit_check_publications newer
                      WHERE newer.repository = $repo AND newer.head_sha = $sha
                        AND newer.work_item_id = $wi AND newer.target = $target
                        AND newer.scope = $scope
                        AND (newer.iteration > $iter
                             OR (newer.iteration = $iter AND newer.attempt > $attempt)));
                """;
            BindKey(cmd, record);
            cmd.Parameters.AddWithValue("$runid", (object?)record.CheckRunId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$status", (object?)record.Status ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$conclusion", (object?)record.Conclusion ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$pub", record.AnnotationsPublished);
            cmd.Parameters.AddWithValue("$batches", record.BatchesSent);
            cmd.Parameters.AddWithValue("$uncertain", record.LastBatchUncertain ? 1 : 0);
            cmd.Parameters.AddWithValue("$updated", record.UpdatedUtc.ToString("O"));
            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<AuditCheckPublicationRecord>> ListDueForRetryAsync(
        DateTimeOffset nowUtc,
        int limit,
        CancellationToken ct = default)
    {
        if (limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit), "must be positive");
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT repository, head_sha, work_item_id, target, iteration, attempt, scope,
                   check_name, external_id, check_run_id, status, conclusion,
                   annotations_published, batches_sent, last_batch_uncertain, state,
                   blocked_reason, last_error, transport_attempts, next_retry_utc, updated_utc
            FROM audit_check_publications
            WHERE state = 'AwaitingRetry' AND next_retry_utc IS NOT NULL AND next_retry_utc <= $now
            ORDER BY next_retry_utc ASC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$now", nowUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var results = new List<AuditCheckPublicationRecord>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            results.Add(ReadRow(reader));
        return results;
    }

    public void Dispose()
    {
        _conn.Dispose();
        _writeLock.Dispose();
    }

    private static void BindKey(SqliteCommand cmd, AuditCheckPublicationRecord record)
    {
        cmd.Parameters.AddWithValue("$repo", record.Repository);
        cmd.Parameters.AddWithValue("$sha", record.HeadSha);
        cmd.Parameters.AddWithValue("$wi", record.WorkItemId);
        cmd.Parameters.AddWithValue("$target", record.Target);
        cmd.Parameters.AddWithValue("$iter", record.Iteration);
        cmd.Parameters.AddWithValue("$attempt", record.Attempt);
        cmd.Parameters.AddWithValue("$scope", record.Scope);
    }

    private static AuditCheckPublicationRecord ReadRow(SqliteDataReader reader)
    {
        static string? Text(SqliteDataReader r, string column)
        {
            var ordinal = r.GetOrdinal(column);
            return r.IsDBNull(ordinal) ? null : r.GetString(ordinal);
        }
        var retryText = Text(reader, "next_retry_utc");
        return new AuditCheckPublicationRecord
        {
            Repository = reader.GetString(reader.GetOrdinal("repository")),
            HeadSha = reader.GetString(reader.GetOrdinal("head_sha")),
            WorkItemId = reader.GetString(reader.GetOrdinal("work_item_id")),
            Target = reader.GetString(reader.GetOrdinal("target")),
            Iteration = reader.GetInt32(reader.GetOrdinal("iteration")),
            Attempt = reader.GetInt32(reader.GetOrdinal("attempt")),
            Scope = reader.GetString(reader.GetOrdinal("scope")),
            CheckName = reader.GetString(reader.GetOrdinal("check_name")),
            ExternalId = reader.GetString(reader.GetOrdinal("external_id")),
            CheckRunId = reader.IsDBNull(reader.GetOrdinal("check_run_id"))
                ? null : reader.GetInt64(reader.GetOrdinal("check_run_id")),
            Status = Text(reader, "status"),
            Conclusion = Text(reader, "conclusion"),
            AnnotationsPublished = reader.GetInt32(reader.GetOrdinal("annotations_published")),
            BatchesSent = reader.GetInt32(reader.GetOrdinal("batches_sent")),
            LastBatchUncertain = reader.GetInt32(reader.GetOrdinal("last_batch_uncertain")) != 0,
            State = Enum.Parse<AuditCheckPublicationState>(
                reader.GetString(reader.GetOrdinal("state")), ignoreCase: false),
            BlockedReason = Text(reader, "blocked_reason"),
            LastError = Text(reader, "last_error"),
            TransportAttempts = reader.GetInt32(reader.GetOrdinal("transport_attempts")),
            NextRetryUtc = string.IsNullOrWhiteSpace(retryText)
                ? null : DateTimeOffset.Parse(retryText, CultureInfo.InvariantCulture),
            UpdatedUtc = DateTimeOffset.Parse(
                reader.GetString(reader.GetOrdinal("updated_utc")), CultureInfo.InvariantCulture),
        };
    }
}
