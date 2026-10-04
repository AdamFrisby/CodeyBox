using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// SQLite-backed <see cref="IBaseBrokenConditionStore"/>. Persists the
/// project-level "base branch does not build" conditions to the same
/// database file as the work-item store (separate connection, separate
/// table) so a dispatcher hold survives an orchestrator restart.
/// </summary>
public sealed class SqliteBaseBrokenConditionStore : IBaseBrokenConditionStore, IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnection _conn;
    private readonly SqliteDatabaseWriteGate _lock;

    public SqliteBaseBrokenConditionStore(
        string dbPath,
        ILogger<SqliteBaseBrokenConditionStore> log,
        SqliteDatabaseWriteGateFactory? writeGateFactory = null)
    {
        _dbPath = dbPath;
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _conn = new SqliteConnection($"Data Source={dbPath}");
        _lock = SqliteDatabaseWriteGateFactory.Resolve(writeGateFactory).ForPath(dbPath);
        var lockHeld = false;
        var initialized = false;
        try
        {
            _lock.Wait();
            lockHeld = true;
            _conn.Open();

            using (var walCmd = _conn.CreateCommand())
            {
                walCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=30000;";
                walCmd.ExecuteNonQuery();
            }

            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS base_broken_conditions (
                    project_id      TEXT NOT NULL,
                    base_branch     TEXT NOT NULL,
                    base_sha        TEXT NOT NULL,
                    repository_id   TEXT NOT NULL,
                    error_summary   TEXT,
                    fix_work_item_id TEXT,
                    detected_at     TEXT NOT NULL,
                    cleared_at      TEXT,
                    updated_at      TEXT NOT NULL,
                    PRIMARY KEY (project_id, base_sha)
                );
                CREATE INDEX IF NOT EXISTS idx_base_broken_conditions_active
                    ON base_broken_conditions(project_id)
                    WHERE cleared_at IS NULL;
                """;
            cmd.ExecuteNonQuery();
            initialized = true;
        }
        finally
        {
            if (lockHeld)
                _lock.Release();
            if (!initialized)
            {
                _conn.Dispose();
                _lock.Dispose();
            }
        }
    }

    public async Task UpsertAsync(BaseBrokenCondition condition, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(condition);
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            using var cmd = _conn.CreateCommand();
            // A cleared row stays cleared: the same SHA can never become
            // broken again (its content is immutable), so resurrecting it
            // would only resurrect a stale hold. Active rows refresh their
            // summary / repo pointer on repeat detection.
            cmd.CommandText = """
                INSERT INTO base_broken_conditions
                    (project_id, base_branch, base_sha, repository_id, error_summary, fix_work_item_id, detected_at, cleared_at, updated_at)
                VALUES
                    ($pid, $branch, $sha, $repo, $summary, $fix, $detected, NULL, $ua)
                ON CONFLICT(project_id, base_sha) DO UPDATE SET
                    repository_id = CASE WHEN cleared_at IS NULL THEN $repo ELSE repository_id END,
                    error_summary = CASE WHEN cleared_at IS NULL THEN $summary ELSE error_summary END,
                    updated_at    = $ua;
                """;
            cmd.Parameters.AddWithValue("$pid", condition.ProjectId.Value);
            cmd.Parameters.AddWithValue("$branch", condition.BaseBranch);
            cmd.Parameters.AddWithValue("$sha", condition.BaseSha);
            cmd.Parameters.AddWithValue("$repo", condition.RepositoryId);
            cmd.Parameters.AddWithValue("$summary", (object?)condition.ErrorSummary ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$fix", (object?)condition.FixWorkItemId?.ToString() ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$detected", condition.DetectedAt.ToString("O"));
            cmd.Parameters.AddWithValue("$ua", now.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task AttachFixItemAsync(
        ProjectId projectId,
        string baseSha,
        WorkItemId fixWorkItemId,
        string repositoryId,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE base_broken_conditions
                SET fix_work_item_id = $fix,
                    repository_id    = $repo,
                    updated_at       = $ua
                WHERE project_id = $pid
                  AND base_sha   = $sha
                  AND cleared_at IS NULL;
                """;
            cmd.Parameters.AddWithValue("$fix", fixWorkItemId.ToString());
            cmd.Parameters.AddWithValue("$repo", repositoryId);
            cmd.Parameters.AddWithValue("$ua", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$pid", projectId.Value);
            cmd.Parameters.AddWithValue("$sha", baseSha);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ClearAsync(
        ProjectId projectId,
        string baseSha,
        DateTimeOffset clearedAt,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE base_broken_conditions
                SET cleared_at = $cleared,
                    updated_at = $ua
                WHERE project_id = $pid
                  AND base_sha   = $sha
                  AND cleared_at IS NULL;
                """;
            cmd.Parameters.AddWithValue("$cleared", clearedAt.ToString("O"));
            cmd.Parameters.AddWithValue("$ua", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$pid", projectId.Value);
            cmd.Parameters.AddWithValue("$sha", baseSha);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<BaseBrokenCondition>> ListActiveAsync(CancellationToken ct = default)
    {
        using var rc = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly");
        rc.Open();
        using var cmd = rc.CreateCommand();
        cmd.CommandText = """
            SELECT project_id, base_branch, base_sha, repository_id, error_summary, fix_work_item_id, detected_at
            FROM base_broken_conditions
            WHERE cleared_at IS NULL;
            """;
        return await ReadConditionsAsync(cmd, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<BaseBrokenCondition>> ListActiveForProjectAsync(
        ProjectId projectId,
        CancellationToken ct = default)
    {
        using var rc = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly");
        rc.Open();
        using var cmd = rc.CreateCommand();
        cmd.CommandText = """
            SELECT project_id, base_branch, base_sha, repository_id, error_summary, fix_work_item_id, detected_at
            FROM base_broken_conditions
            WHERE cleared_at IS NULL
              AND project_id = $pid;
            """;
        cmd.Parameters.AddWithValue("$pid", projectId.Value);
        return await ReadConditionsAsync(cmd, ct).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<BaseBrokenCondition>> ReadConditionsAsync(
        SqliteCommand cmd,
        CancellationToken ct)
    {
        var results = new List<BaseBrokenCondition>();
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var fixRaw = reader.IsDBNull(5) ? null : reader.GetString(5);
            results.Add(new BaseBrokenCondition
            {
                ProjectId = new ProjectId(reader.GetString(0)),
                BaseBranch = reader.GetString(1),
                BaseSha = reader.GetString(2),
                RepositoryId = reader.GetString(3),
                ErrorSummary = reader.IsDBNull(4) ? null : reader.GetString(4),
                FixWorkItemId = Guid.TryParse(fixRaw, out var fixGuid) ? new WorkItemId(fixGuid) : null,
                DetectedAt = DateTimeOffset.Parse(
                    reader.GetString(6),
                    System.Globalization.CultureInfo.InvariantCulture),
            });
        }
        return results;
    }

    public void Dispose()
    {
        _conn.Dispose();
        _lock.Dispose();
    }
}
