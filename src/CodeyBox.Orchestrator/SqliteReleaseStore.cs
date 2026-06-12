using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// SQLite-backed store for <see cref="Release"/> records. Shares the same
/// database file as <see cref="SqliteWorkItemStore"/> and other stores.
/// Must be constructed before <see cref="SqliteWorkItemStore"/> so the
/// <c>releases</c> table exists before <c>work_items.release_id</c> is added.
/// </summary>
public sealed class SqliteReleaseStore : IReleaseStore, IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly SqliteDatabaseWriteGate _writeLock;
    private int _disposed;

    public SqliteReleaseStore(string path)
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
                CREATE TABLE IF NOT EXISTS releases (
                    id                  TEXT PRIMARY KEY,
                    project_id          TEXT NOT NULL,
                    name                TEXT NOT NULL,
                    description         TEXT,
                    state               INTEGER NOT NULL,
                    base_commit_sha     TEXT,
                    branch_name         TEXT,
                    created_at          TEXT NOT NULL,
                    closed_at           TEXT,
                    review_started_at   TEXT,
                    released_at         TEXT,
                    failed_reason       TEXT,
                    target_tag          TEXT,
                    config_json         TEXT NOT NULL DEFAULT '{}'
                );
                CREATE UNIQUE INDEX IF NOT EXISTS idx_releases_project_name ON releases(project_id, name);
                CREATE INDEX IF NOT EXISTS idx_releases_state ON releases(state);

                CREATE TABLE IF NOT EXISTS release_audit_iterations (
                    id                          INTEGER PRIMARY KEY AUTOINCREMENT,
                    release_id                  TEXT NOT NULL,
                    iteration                   INTEGER NOT NULL,
                    max_iterations              INTEGER NOT NULL,
                    total_findings              INTEGER NOT NULL,
                    blocking_findings           INTEGER NOT NULL,
                    findings_json               TEXT NOT NULL,
                    remediation_work_item_id    TEXT,
                    created_at                  TEXT NOT NULL,
                    UNIQUE(release_id, iteration)
                );
                CREATE INDEX IF NOT EXISTS idx_release_audit_iter ON release_audit_iterations(release_id);
                """;
            cmd.ExecuteNonQuery();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task CreateAsync(Release release, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO releases (id, project_id, name, description, state, base_commit_sha,
                    branch_name, created_at, closed_at, review_started_at, released_at,
                    failed_reason, target_tag, config_json)
                VALUES ($id, $pid, $name, $desc, $state, $sha, $branch, $ca, $closed, $review, $released,
                    $failed, $tag, $cfg);
                """;
            Bind(cmd, release);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task UpdateAsync(Release release, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE releases SET
                    project_id = $pid, name = $name, description = $desc, state = $state,
                    base_commit_sha = $sha, branch_name = $branch,
                    closed_at = $closed, review_started_at = $review, released_at = $released,
                    failed_reason = $failed, target_tag = $tag, config_json = $cfg
                WHERE id = $id;
                """;
            Bind(cmd, release);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<Release?> GetAsync(ReleaseId id, CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM releases WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    public async Task<Release?> GetByNameAsync(ProjectId projectId, string name, CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM releases WHERE project_id = $pid AND name = $name;";
        cmd.Parameters.AddWithValue("$pid", projectId.Value);
        cmd.Parameters.AddWithValue("$name", name);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<Release>> ListAsync(
        ProjectId? projectId = null,
        ReleaseState? state = null,
        int? limit = null,
        int? offset = null,
        CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        var conditions = new List<string>();
        if (projectId.HasValue)
        {
            conditions.Add("project_id = $pid");
            cmd.Parameters.AddWithValue("$pid", projectId.Value.Value);
        }
        if (state.HasValue)
        {
            conditions.Add("state = $state");
            cmd.Parameters.AddWithValue("$state", (int)state.Value);
        }
        var where = conditions.Count > 0 ? " WHERE " + string.Join(" AND ", conditions) : "";
        var limitClause = "";
        if (limit.HasValue)
        {
            limitClause = " LIMIT $limit";
            cmd.Parameters.AddWithValue("$limit", limit.Value);
            if (offset.HasValue)
            {
                limitClause += " OFFSET $offset";
                cmd.Parameters.AddWithValue("$offset", offset.Value);
            }
        }
        // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- conditions/pagination built from hardcoded literals only; parameter values injected via AddWithValue
        cmd.CommandText = $"SELECT * FROM releases{where} ORDER BY created_at DESC{limitClause};";
        using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = new List<Release>();
        while (await reader.ReadAsync(ct))
            result.Add(Read(reader));
        return result;
    }

    /// <inheritdoc/>
    public async Task<bool> TrySetBranchAsync(ReleaseId id, string branchName, string baseCommitSha, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE releases SET branch_name = $branch, base_commit_sha = $sha
                WHERE id = $id AND branch_name IS NULL;
                """;
            cmd.Parameters.AddWithValue("$branch", branchName);
            cmd.Parameters.AddWithValue("$sha", baseCommitSha);
            cmd.Parameters.AddWithValue("$id", id.ToString());
            return await cmd.ExecuteNonQueryAsync(ct) > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> TryTransitionStateAsync(Release release, ReleaseState expectedCurrentState, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE releases SET
                    project_id = $pid, name = $name, description = $desc, state = $state,
                    base_commit_sha = $sha, branch_name = $branch,
                    closed_at = $closed, review_started_at = $review, released_at = $released,
                    failed_reason = $failed, target_tag = $tag, config_json = $cfg
                WHERE id = $id AND state = $expectedState;
                """;
            Bind(cmd, release);
            cmd.Parameters.AddWithValue("$expectedState", (int)expectedCurrentState);
            return await cmd.ExecuteNonQueryAsync(ct) > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task SaveAuditIterationAsync(ReleaseAuditIteration iteration, CancellationToken ct = default)
    {
        var findingsJson = JsonSerializer.Serialize(iteration.Findings, _findingsSerializerOptions);
        await _writeLock.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO release_audit_iterations
                    (release_id, iteration, max_iterations, total_findings, blocking_findings,
                     findings_json, remediation_work_item_id, created_at)
                VALUES ($rid, $iter, $max, $total, $blocking, $findings, $remId, $ca);
                """;
            cmd.Parameters.AddWithValue("$rid", iteration.ReleaseId.ToString());
            cmd.Parameters.AddWithValue("$iter", iteration.Iteration);
            cmd.Parameters.AddWithValue("$max", iteration.MaxIterations);
            cmd.Parameters.AddWithValue("$total", iteration.TotalFindings);
            cmd.Parameters.AddWithValue("$blocking", iteration.BlockingFindings);
            cmd.Parameters.AddWithValue("$findings", findingsJson);
            cmd.Parameters.AddWithValue("$remId", (object?)iteration.RemediationWorkItemId?.ToString() ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ca", iteration.CreatedAt.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<ReleaseAuditIteration>> ListAuditIterationsAsync(ReleaseId releaseId, CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM release_audit_iterations WHERE release_id = $rid ORDER BY iteration ASC;";
        cmd.Parameters.AddWithValue("$rid", releaseId.ToString());
        using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = new List<ReleaseAuditIteration>();
        while (await reader.ReadAsync(ct))
        {
            var findingsJson = reader.GetString(reader.GetOrdinal("findings_json"));
            var findings = JsonSerializer.Deserialize<List<AuditFindingRecord>>(findingsJson, _findingsSerializerOptions)
                ?? [];
            var remIdCol = reader.GetOrdinal("remediation_work_item_id");
            WorkItemId? remId = reader.IsDBNull(remIdCol) ? null
                : new WorkItemId(Guid.Parse(reader.GetString(remIdCol)));
            result.Add(new ReleaseAuditIteration
            {
                ReleaseId = ReleaseId.Parse(reader.GetString(reader.GetOrdinal("release_id"))),
                Iteration = reader.GetInt32(reader.GetOrdinal("iteration")),
                MaxIterations = reader.GetInt32(reader.GetOrdinal("max_iterations")),
                TotalFindings = reader.GetInt32(reader.GetOrdinal("total_findings")),
                BlockingFindings = reader.GetInt32(reader.GetOrdinal("blocking_findings")),
                Findings = findings.Select(f => new AuditFinding(f.AuditorName, f.Severity, f.Title, f.Description, f.Location)).ToList(),
                RemediationWorkItemId = remId,
                CreatedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at")), System.Globalization.CultureInfo.InvariantCulture),
            });
        }
        return result;
    }

    private static readonly JsonSerializerOptions _findingsSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed class AuditFindingRecord
    {
        public string AuditorName { get; set; } = "";
        public AuditSeverity Severity { get; set; }
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public string? Location { get; set; }
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

    private static void Bind(SqliteCommand cmd, Release r)
    {
        cmd.Parameters.AddWithValue("$id", r.Id.ToString());
        cmd.Parameters.AddWithValue("$pid", r.ProjectId.Value);
        cmd.Parameters.AddWithValue("$name", r.Name);
        cmd.Parameters.AddWithValue("$desc", (object?)r.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$state", (int)r.State);
        cmd.Parameters.AddWithValue("$sha", (object?)r.BaseCommitSha ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$branch", (object?)r.BranchName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ca", r.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$closed", (object?)r.ClosedAt?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$review", (object?)r.ReviewStartedAt?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$released", (object?)r.ReleasedAt?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$failed", (object?)r.FailedReason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tag", (object?)r.TargetTag ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cfg", r.ConfigJson);
    }

    private static Release Read(SqliteDataReader r) => new()
    {
        Id = ReleaseId.Parse(r.GetString(r.GetOrdinal("id"))),
        ProjectId = new ProjectId(r.GetString(r.GetOrdinal("project_id"))),
        Name = r.GetString(r.GetOrdinal("name")),
        Description = Nullable(r, "description"),
        State = (ReleaseState)r.GetInt32(r.GetOrdinal("state")),
        BaseCommitSha = Nullable(r, "base_commit_sha"),
        BranchName = Nullable(r, "branch_name"),
        CreatedAt = DateTimeOffset.Parse(r.GetString(r.GetOrdinal("created_at")), System.Globalization.CultureInfo.InvariantCulture),
        ClosedAt = NullableDateTimeOffset(r, "closed_at"),
        ReviewStartedAt = NullableDateTimeOffset(r, "review_started_at"),
        ReleasedAt = NullableDateTimeOffset(r, "released_at"),
        FailedReason = Nullable(r, "failed_reason"),
        TargetTag = Nullable(r, "target_tag"),
        ConfigJson = r.GetString(r.GetOrdinal("config_json")),
    };

    private static string? Nullable(SqliteDataReader r, string col)
    {
        var ord = r.GetOrdinal(col);
        return r.IsDBNull(ord) ? null : r.GetString(ord);
    }

    private static DateTimeOffset? NullableDateTimeOffset(SqliteDataReader r, string col)
    {
        var ord = r.GetOrdinal(col);
        return r.IsDBNull(ord) ? null
            : DateTimeOffset.Parse(r.GetString(ord), System.Globalization.CultureInfo.InvariantCulture);
    }
}
