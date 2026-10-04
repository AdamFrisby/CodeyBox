using System.Text.Json;
using Microsoft.Data.Sqlite;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// SQLite-backed standalone audit-run store. Shares the state database file;
/// the <c>audit_runs</c> table is created here via its own additive
/// migration. Artifacts live in a sibling store; only digests persist here.
/// </summary>
public sealed class SqliteAuditRunStore : IAuditRunStore, IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly SqliteDatabaseWriteGate _writeLock;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public SqliteAuditRunStore(string path, SqliteDatabaseWriteGateFactory? writeGateFactory = null)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _conn = new SqliteConnection($"Data Source={path}");
        _writeLock = SqliteDatabaseWriteGateFactory.Resolve(writeGateFactory).ForPath(path);
        _writeLock.Wait();
        try
        {
            _conn.Open();
            using (var pragma = _conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=30000; PRAGMA foreign_keys=ON;";
                pragma.ExecuteNonQuery();
            }
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS audit_runs (
                    id TEXT PRIMARY KEY,
                    project_id TEXT NOT NULL,
                    state TEXT NOT NULL,
                    aggregate TEXT NOT NULL,
                    provenance_json TEXT NOT NULL,
                    results_json TEXT NOT NULL,
                    artifacts_json TEXT NOT NULL,
                    idempotency_key TEXT,
                    idempotency_body_hash TEXT,
                    attempts INTEGER NOT NULL DEFAULT 0,
                    failure_detail TEXT,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    completed_at TEXT
                );
                CREATE INDEX IF NOT EXISTS idx_audit_runs_project_created
                    ON audit_runs(project_id, created_at DESC);
                CREATE INDEX IF NOT EXISTS idx_audit_runs_idempotency
                    ON audit_runs(project_id, idempotency_key, idempotency_body_hash);
                """;
                cmd.ExecuteNonQuery();
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task CreateAsync(AuditRunRecord run, CancellationToken ct = default)
    {
        _writeLock.Wait(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO audit_runs (id, project_id, state, aggregate, provenance_json, results_json, artifacts_json,
                    idempotency_key, idempotency_body_hash, attempts, failure_detail, created_at, updated_at, completed_at)
                VALUES ($id, $project, $state, $agg, $prov, $results, $artifacts, $ikey, $ihash, $attempts, $fail, $created, $updated, $completed);
                """;
            Bind(cmd, run);
            cmd.ExecuteNonQuery();
        }
        finally
        {
            _writeLock.Release();
        }
        return Task.CompletedTask;
    }

    public Task<AuditRunRecord?> GetAsync(string runId, CancellationToken ct = default)
    {
        _writeLock.Wait(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT " + Columns + " FROM audit_runs WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", runId);
            using var reader = cmd.ExecuteReader();
            return Task.FromResult(reader.Read() ? Read(reader) : null);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task<AuditRunRecord?> GetByIdempotencyAsync(string projectId, string idempotencyKey, string bodyHash, CancellationToken ct = default)
    {
        _writeLock.Wait(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT " + Columns + " FROM audit_runs WHERE project_id = $p AND idempotency_key = $k AND idempotency_body_hash = $h LIMIT 1;";
            cmd.Parameters.AddWithValue("$p", projectId);
            cmd.Parameters.AddWithValue("$k", idempotencyKey);
            cmd.Parameters.AddWithValue("$h", bodyHash);
            using var reader = cmd.ExecuteReader();
            return Task.FromResult(reader.Read() ? Read(reader) : null);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task<AuditRunRecord?> GetByIdempotencyKeyAsync(string projectId, string idempotencyKey, CancellationToken ct = default)
    {
        _writeLock.Wait(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT " + Columns + " FROM audit_runs WHERE project_id = $p AND idempotency_key = $k LIMIT 1;";
            cmd.Parameters.AddWithValue("$p", projectId);
            cmd.Parameters.AddWithValue("$k", idempotencyKey);
            using var reader = cmd.ExecuteReader();
            return Task.FromResult(reader.Read() ? Read(reader) : null);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task<IReadOnlyList<AuditRunRecord>> ListAsync(string? projectId, int limit, CancellationToken ct = default)
    {
        _writeLock.Wait(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            if (string.IsNullOrWhiteSpace(projectId))
            {
                cmd.CommandText = "SELECT " + Columns + " FROM audit_runs ORDER BY created_at DESC LIMIT $n;";
                cmd.Parameters.AddWithValue("$n", Math.Max(1, limit));
            }
            else
            {
                cmd.CommandText = "SELECT " + Columns + " FROM audit_runs WHERE project_id = $p ORDER BY created_at DESC LIMIT $n;";
                cmd.Parameters.AddWithValue("$p", projectId);
                cmd.Parameters.AddWithValue("$n", Math.Max(1, limit));
            }
            var list = new List<AuditRunRecord>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) list.Add(Read(reader));
            return Task.FromResult<IReadOnlyList<AuditRunRecord>>(list);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task<bool> TryUpdateStateAsync(string runId, AuditRunState expected, AuditRunRecord updated, CancellationToken ct = default)
    {
        _writeLock.Wait(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE audit_runs SET project_id = $project, state = $state, aggregate = $agg,
                    provenance_json = $prov, results_json = $results, artifacts_json = $artifacts,
                    idempotency_key = $ikey, idempotency_body_hash = $ihash, attempts = $attempts,
                    failure_detail = $fail, created_at = $created, updated_at = $updated, completed_at = $completed
                WHERE id = $id AND state = $expected;
                """;
            Bind(cmd, updated);
            cmd.Parameters.AddWithValue("$expected", expected.ToString());
            return Task.FromResult(cmd.ExecuteNonQuery() == 1);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task UpdateAsync(AuditRunRecord run, CancellationToken ct = default)
    {
        _writeLock.Wait(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE audit_runs SET project_id = $project, state = $state, aggregate = $agg,
                    provenance_json = $prov, results_json = $results, artifacts_json = $artifacts,
                    idempotency_key = $ikey, idempotency_body_hash = $ihash, attempts = $attempts,
                    failure_detail = $fail, created_at = $created, updated_at = $updated, completed_at = $completed
                WHERE id = $id;
                """;
            Bind(cmd, run);
            cmd.ExecuteNonQuery();
        }
        finally
        {
            _writeLock.Release();
        }
        return Task.CompletedTask;
    }

    public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        _writeLock.Wait(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM audit_runs WHERE created_at < $c AND state IN ('Passed','Findings','Cancelled','Failed');";
            cmd.Parameters.AddWithValue("$c", cutoff.ToString("O"));
            return Task.FromResult(cmd.ExecuteNonQuery());
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task<int> CountActiveAsync(CancellationToken ct = default)
    {
        _writeLock.Wait(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM audit_runs WHERE state IN ('Queued','Provisioning','Running','Collecting');";
            return Task.FromResult(Convert.ToInt32(cmd.ExecuteScalar()));
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Dispose() => _conn.Dispose();

    private const string Columns =
        "id, project_id, state, aggregate, provenance_json, results_json, artifacts_json," +
        " idempotency_key, idempotency_body_hash, attempts, failure_detail, created_at, updated_at, completed_at";

    private static void Bind(SqliteCommand cmd, AuditRunRecord run)
    {
        cmd.Parameters.AddWithValue("$id", run.Id);
        cmd.Parameters.AddWithValue("$project", run.ProjectId);
        cmd.Parameters.AddWithValue("$state", run.State.ToString());
        cmd.Parameters.AddWithValue("$agg", run.AggregateOutcome.ToString());
        cmd.Parameters.AddWithValue("$prov", JsonSerializer.Serialize(run.Provenance, JsonOpts));
        cmd.Parameters.AddWithValue("$results", JsonSerializer.Serialize(run.AuditorResults, JsonOpts));
        cmd.Parameters.AddWithValue("$artifacts", JsonSerializer.Serialize(run.Artifacts, JsonOpts));
        cmd.Parameters.AddWithValue("$ikey", (object?)run.IdempotencyKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ihash", (object?)run.IdempotencyBodyHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$attempts", run.Attempts);
        cmd.Parameters.AddWithValue("$fail", (object?)run.FailureDetail ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created", run.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$updated", run.UpdatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$completed", run.CompletedAt?.ToString("O") ?? (object)DBNull.Value);
    }

    private static AuditRunRecord Read(SqliteDataReader reader)
    {
        string? Null(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
        return new AuditRunRecord
        {
            Id = reader.GetString(0),
            ProjectId = reader.GetString(1),
            State = Enum.Parse<AuditRunState>(reader.GetString(2)),
            AggregateOutcome = Enum.Parse<AuditRunAggregateOutcome>(reader.GetString(3)),
            Provenance = JsonSerializer.Deserialize<AuditRunProvenance>(reader.GetString(4), JsonOpts)!,
            AuditorResults = JsonSerializer.Deserialize<List<AuditRunAuditorResult>>(reader.GetString(5), JsonOpts)!,
            Artifacts = JsonSerializer.Deserialize<List<AuditRunArtifactRef>>(reader.GetString(6), JsonOpts)!,
            IdempotencyKey = Null(7),
            IdempotencyBodyHash = Null(8),
            Attempts = reader.GetInt32(9),
            FailureDetail = Null(10),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(11)),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(12)),
            CompletedAt = Null(13) is { } c ? DateTimeOffset.Parse(c) : null,
        };
    }
}

/// <summary>
/// File-backed artifact store: content-addressed by run id + artifact name
/// under a dedicated root. Bounds enforced at the sink before buffering.
/// </summary>
public sealed class FileAuditRunArtifactStore : IAuditRunArtifactStore
{
    private readonly string _root;
    private readonly long _maxBytes;

    public FileAuditRunArtifactStore(string root, long maxBytes)
    {
        _root = root;
        _maxBytes = maxBytes;
    }

    public async Task PutAsync(string runId, string name, string mediaType, byte[] content, CancellationToken ct = default)
    {
        if (content.LongLength > _maxBytes)
            throw new AuditRunArtifactTooLargeException(name, content.LongLength, _maxBytes);
        var safeName = Sanitize(name);
        var dir = Path.Combine(_root, Sanitize(runId));
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, safeName + ".tmp");
        await File.WriteAllBytesAsync(tmp, content, ct).ConfigureAwait(false);
        File.Move(tmp, Path.Combine(dir, safeName), overwrite: true);
        await File.WriteAllTextAsync(Path.Combine(dir, safeName + ".media"), mediaType, ct).ConfigureAwait(false);
    }

    public async Task<(byte[] Content, string MediaType)?> GetAsync(string runId, string name, CancellationToken ct = default)
    {
        var path = Path.Combine(_root, Sanitize(runId), Sanitize(name));
        if (!File.Exists(path))
            return null;
        var mediaPath = path + ".media";
        var media = File.Exists(mediaPath) ? await File.ReadAllTextAsync(mediaPath, ct).ConfigureAwait(false) : "application/octet-stream";
        return (await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false), media);
    }

    public Task<IReadOnlyList<string>> ListAsync(string runId, CancellationToken ct = default)
    {
        var dir = Path.Combine(_root, Sanitize(runId));
        if (!Directory.Exists(dir))
            return Task.FromResult<IReadOnlyList<string>>([]);
        var list = Directory.GetFiles(dir)
            .Select(Path.GetFileName)
            .Where(n => n is not null && !n.EndsWith(".tmp", StringComparison.Ordinal) && !n.EndsWith(".media", StringComparison.Ordinal))
            .Select(n => n!)
            .ToList();
        return Task.FromResult<IReadOnlyList<string>>(list);
    }

    public Task DeleteRunAsync(string runId, CancellationToken ct = default)
    {
        var dir = Path.Combine(_root, Sanitize(runId));
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
        return Task.CompletedTask;
    }

    private static string Sanitize(string value)
    {
        var clean = new string(value.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray());
        return string.IsNullOrWhiteSpace(clean) ? "_" : clean;
    }
}
