using System.Text.Json;
using CodeyBox.Core.ExternalBuilds;
using Microsoft.Data.Sqlite;

namespace CodeyBox.Orchestrator.ExternalBuilds;

/// <summary>
/// SQLite-backed external-build store sharing the state database file.
/// The <c>external_builds</c> table is created here via its own additive
/// migration; pre-existing databases gain it on first open. Records carry a
/// schema version; unknown newer versions fail closed, older versions are
/// backward-read with defaults for added fields.
/// </summary>
public sealed class SqliteExternalBuildStore : IExternalBuildStore, IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly SqliteDatabaseWriteGate _writeLock;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public SqliteExternalBuildStore(string path, SqliteDatabaseWriteGateFactory? writeGateFactory = null)
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
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS external_builds (
                    id TEXT PRIMARY KEY,
                    schema_version INTEGER NOT NULL DEFAULT 1,
                    project_id TEXT NOT NULL,
                    work_item_id TEXT NOT NULL,
                    phase TEXT NOT NULL,
                    iteration INTEGER NOT NULL DEFAULT 0,
                    attempt INTEGER NOT NULL DEFAULT 0,
                    state TEXT NOT NULL,
                    target_json TEXT NOT NULL,
                    source_json TEXT NOT NULL,
                    config_digest TEXT NOT NULL,
                    idempotency_key TEXT,
                    idempotency_body_hash TEXT,
                    request_id TEXT NOT NULL DEFAULT '',
                    provider_run_id TEXT,
                    fence_owner TEXT,
                    fence_epoch INTEGER NOT NULL DEFAULT 0,
                    dispatch_attempts INTEGER NOT NULL DEFAULT 0,
                    poll_count INTEGER NOT NULL DEFAULT 0,
                    terminal_cause TEXT NOT NULL DEFAULT 'Unknown',
                    evidence_json TEXT,
                    failure_detail TEXT,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    completed_at TEXT,
                    expires_at TEXT
                );
                CREATE INDEX IF NOT EXISTS idx_extbuilds_project ON external_builds(project_id, created_at DESC);
                CREATE INDEX IF NOT EXISTS idx_extbuilds_idem ON external_builds(project_id, idempotency_key, idempotency_body_hash);
                CREATE INDEX IF NOT EXISTS idx_extbuilds_run ON external_builds(provider_run_id);
                CREATE TABLE IF NOT EXISTS external_build_parks (
                    build_id TEXT PRIMARY KEY,
                    work_item_id TEXT NOT NULL,
                    phase TEXT NOT NULL,
                    iteration INTEGER NOT NULL DEFAULT 0,
                    attempt INTEGER NOT NULL DEFAULT 0,
                    reason TEXT NOT NULL,
                    estimate_ticks INTEGER,
                    sample_count INTEGER NOT NULL DEFAULT 0,
                    parked_at TEXT NOT NULL,
                    checkpoint_id TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS external_build_history (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    target_json TEXT NOT NULL,
                    duration_ticks INTEGER NOT NULL,
                    completed_ok INTEGER NOT NULL DEFAULT 1,
                    completed_at TEXT NOT NULL,
                    cold_cache INTEGER NOT NULL DEFAULT 0
                );
                """;
            cmd.ExecuteNonQuery();
            using var migrate = _conn.CreateCommand();
            migrate.CommandText = "PRAGMA table_info(external_builds);";
            var columns = new HashSet<string>(StringComparer.Ordinal);
            using (var reader = migrate.ExecuteReader())
                while (reader.Read()) columns.Add(reader.GetString(1));
            if (!columns.Contains("delivery_acked"))
            {
                using var alter = _conn.CreateCommand();
                alter.CommandText = "ALTER TABLE external_builds ADD COLUMN delivery_acked INTEGER NOT NULL DEFAULT 0;";
                alter.ExecuteNonQuery();
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task CreateAsync(ExternalBuildRecord record, CancellationToken ct = default)
    {
        _writeLock.Wait(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO external_builds (id, schema_version, project_id, work_item_id, phase, iteration, attempt,
                    state, target_json, source_json, config_digest, idempotency_key, idempotency_body_hash,
                    request_id, provider_run_id, fence_owner, fence_epoch, dispatch_attempts, poll_count,
                    terminal_cause, evidence_json, failure_detail, created_at, updated_at, completed_at, expires_at, delivery_acked)
                VALUES ($id,$sv,$p,$w,$ph,$it,$at,$st,$tj,$sj,$cd,$ik,$ih,$rq,$pr,$fo,$fe,$da,$pc,$tc,$ev,$fd,$ca,$ua,$co,$ex,$dl);
                """;
            Bind(cmd, record);
            cmd.ExecuteNonQuery();
        }
        finally
        {
            _writeLock.Release();
        }
        return Task.CompletedTask;
    }

    public Task<ExternalBuildRecord?> GetAsync(string buildId, CancellationToken ct = default)
    {
        _writeLock.Wait(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT " + Columns + " FROM external_builds WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", buildId);
            using var reader = cmd.ExecuteReader();
            return Task.FromResult(reader.Read() ? Read(reader) : null);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task<ExternalBuildRecord?> GetByIdempotencyAsync(string projectId, string idempotencyKey, string bodyHash, CancellationToken ct = default)
    {
        _writeLock.Wait(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT " + Columns + " FROM external_builds WHERE project_id = $p AND idempotency_key = $k AND idempotency_body_hash = $h LIMIT 1;";
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

    public Task<ExternalBuildRecord?> GetByProviderRunAsync(string providerId, string providerRunId, CancellationToken ct = default)
    {
        _writeLock.Wait(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT " + Columns + " FROM external_builds WHERE provider_run_id = $r;";
            cmd.Parameters.AddWithValue("$r", providerRunId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var record = Read(reader);
                if (string.Equals(record.Target.ProviderId, providerId, StringComparison.Ordinal))
                    return Task.FromResult<ExternalBuildRecord?>(record);
            }
            return Task.FromResult<ExternalBuildRecord?>(null);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task<IReadOnlyList<ExternalBuildRecord>> ListActiveAsync(string projectId, CancellationToken ct = default)
    {
        _writeLock.Wait(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT " + Columns + " FROM external_builds WHERE project_id = $p AND state NOT IN ('Succeeded','Failed','Cancelled','ReconciliationBlocked');";
            cmd.Parameters.AddWithValue("$p", projectId);
            var list = new List<ExternalBuildRecord>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) list.Add(Read(reader));
            return Task.FromResult<IReadOnlyList<ExternalBuildRecord>>(list);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task<int> CountActiveAsync(string projectId, CancellationToken ct = default)
    {
        _writeLock.Wait(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM external_builds WHERE project_id = $p AND state NOT IN ('Succeeded','Failed','Cancelled','ReconciliationBlocked');";
            cmd.Parameters.AddWithValue("$p", projectId);
            return Task.FromResult(Convert.ToInt32(cmd.ExecuteScalar()));
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task<int> CountActiveForProviderAsync(string providerId, CancellationToken ct = default)
    {
        _writeLock.Wait(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT target_json FROM external_builds WHERE state NOT IN ('Succeeded','Failed','Cancelled','ReconciliationBlocked');";
            var count = 0;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var target = JsonSerializer.Deserialize<ExternalBuildTargetKey>(reader.GetString(0), JsonOpts);
                if (target is not null && string.Equals(target.ProviderId, providerId, StringComparison.Ordinal))
                    count++;
            }
            return Task.FromResult(count);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task<bool> TryClaimAsync(string buildId, ExternalBuildState expectedState, string? expectedFence, ExternalBuildRecord updated, CancellationToken ct = default)
    {
        _writeLock.Wait(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE external_builds SET state=$st, target_json=$tj, source_json=$sj, config_digest=$cd,
                    request_id=$rq, provider_run_id=$pr, fence_owner=$fo, fence_epoch=$fe,
                    dispatch_attempts=$da, poll_count=$pc, terminal_cause=$tc, evidence_json=$ev,
                    failure_detail=$fd, updated_at=$ua, completed_at=$co, expires_at=$ex, delivery_acked=$dl
                WHERE id=$id AND state=$expected AND ((fence_owner IS NULL AND $efo IS NULL) OR fence_owner = $efo);
                """;
            cmd.Parameters.AddWithValue("$id", buildId);
            cmd.Parameters.AddWithValue("$expected", expectedState.ToString());
            cmd.Parameters.AddWithValue("$efo", (object?)expectedFence ?? DBNull.Value);
            BindUpdate(cmd, updated);
            return Task.FromResult(cmd.ExecuteNonQuery() == 1);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task UpdateAsync(ExternalBuildRecord record, CancellationToken ct = default)
    {
        _writeLock.Wait(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE external_builds SET state=$st, target_json=$tj, source_json=$sj, config_digest=$cd,
                    request_id=$rq, provider_run_id=$pr, fence_owner=$fo, fence_epoch=$fe,
                    dispatch_attempts=$da, poll_count=$pc, terminal_cause=$tc, evidence_json=$ev,
                    failure_detail=$fd, updated_at=$ua, completed_at=$co, expires_at=$ex, delivery_acked=$dl
                WHERE id=$id;
                """;
            cmd.Parameters.AddWithValue("$id", record.Id);
            BindUpdate(cmd, record);
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
            cmd.CommandText = "DELETE FROM external_builds WHERE created_at < $c AND state IN ('Succeeded','Failed','Cancelled','ReconciliationBlocked');";
            cmd.Parameters.AddWithValue("$c", cutoff.ToString("O"));
            return Task.FromResult(cmd.ExecuteNonQuery());
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task<IReadOnlyList<ExternalBuildRecord>> ListUnackedTerminalsAsync(CancellationToken ct = default)
    {
        _writeLock.Wait(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT " + Columns + " FROM external_builds WHERE state IN ('Succeeded','Failed','Cancelled','ReconciliationBlocked') AND delivery_acked = 0;";
            var list = new List<ExternalBuildRecord>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) list.Add(Read(reader));
            return Task.FromResult<IReadOnlyList<ExternalBuildRecord>>(list);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task MarkDeliveredAsync(string buildId, CancellationToken ct = default)
    {
        _writeLock.Wait(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE external_builds SET delivery_acked = 1 WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", buildId);
            cmd.ExecuteNonQuery();
        }
        finally
        {
            _writeLock.Release();
        }
        return Task.CompletedTask;
    }

    public Task SaveParkAsync(ExternalBuildParkRecord park, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(park);
        _writeLock.Wait(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO external_build_parks (build_id, work_item_id, phase, iteration, attempt, reason, estimate_ticks, sample_count, parked_at, checkpoint_id)
                VALUES ($b,$w,$ph,$it,$at,$r,$e,$s,$p,$c)
                ON CONFLICT(build_id) DO UPDATE SET work_item_id=$w, phase=$ph, iteration=$it, attempt=$at,
                    reason=$r, estimate_ticks=$e, sample_count=$s, parked_at=$p, checkpoint_id=$c;
                """;
            cmd.Parameters.AddWithValue("$b", park.BuildId);
            cmd.Parameters.AddWithValue("$w", park.WorkItemId);
            cmd.Parameters.AddWithValue("$ph", park.Phase);
            cmd.Parameters.AddWithValue("$it", park.Iteration);
            cmd.Parameters.AddWithValue("$at", park.Attempt);
            cmd.Parameters.AddWithValue("$r", park.Reason);
            cmd.Parameters.AddWithValue("$e", park.EstimateTicks.HasValue ? (object)park.EstimateTicks.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("$s", park.SampleCount);
            cmd.Parameters.AddWithValue("$p", park.ParkedAt.ToString("O"));
            cmd.Parameters.AddWithValue("$c", park.CheckpointId);
            cmd.ExecuteNonQuery();
        }
        finally
        {
            _writeLock.Release();
        }
        return Task.CompletedTask;
    }

    public Task<ExternalBuildParkRecord?> GetParkAsync(string buildId, CancellationToken ct = default)
    {
        _writeLock.Wait(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT build_id, work_item_id, phase, iteration, attempt, reason, estimate_ticks, sample_count, parked_at, checkpoint_id FROM external_build_parks WHERE build_id = $b;";
            cmd.Parameters.AddWithValue("$b", buildId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return Task.FromResult<ExternalBuildParkRecord?>(null);
            return Task.FromResult<ExternalBuildParkRecord?>(new ExternalBuildParkRecord(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt32(3), reader.GetInt32(4), reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetInt64(6), reader.GetInt32(7),
                DateTimeOffset.Parse(reader.GetString(8)), reader.GetString(9)));
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task RemoveParkAsync(string buildId, CancellationToken ct = default)
    {
        _writeLock.Wait(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM external_build_parks WHERE build_id = $b;";
            cmd.Parameters.AddWithValue("$b", buildId);
            cmd.ExecuteNonQuery();
        }
        finally
        {
            _writeLock.Release();
        }
        return Task.CompletedTask;
    }

    public Task RecordSampleAsync(ExternalBuildDurationSample sample, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sample);
        _writeLock.Wait(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO external_build_history (target_json, duration_ticks, completed_ok, completed_at, cold_cache)
                VALUES ($t,$d,$o,$c,$cc);
                """;
            cmd.Parameters.AddWithValue("$t", JsonSerializer.Serialize(sample.Target, JsonOpts));
            cmd.Parameters.AddWithValue("$d", sample.Duration.Ticks);
            cmd.Parameters.AddWithValue("$o", sample.CompletedSuccessfully ? 1 : 0);
            cmd.Parameters.AddWithValue("$c", sample.CompletedAt.ToString("O"));
            cmd.Parameters.AddWithValue("$cc", sample.ColdCache ? 1 : 0);
            cmd.ExecuteNonQuery();
        }
        finally
        {
            _writeLock.Release();
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ExternalBuildDurationSample>> ListSamplesAsync(CancellationToken ct = default)
    {
        _writeLock.Wait(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT target_json, duration_ticks, completed_ok, completed_at, cold_cache FROM external_build_history ORDER BY completed_at DESC;";
            var list = new List<ExternalBuildDurationSample>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                list.Add(new ExternalBuildDurationSample(
                    JsonSerializer.Deserialize<ExternalBuildTargetKey>(reader.GetString(0), JsonOpts)!,
                    TimeSpan.FromTicks(reader.GetInt64(1)),
                    reader.GetInt32(2) != 0,
                    DateTimeOffset.Parse(reader.GetString(3)),
                    reader.GetInt32(4) != 0));
            return Task.FromResult<IReadOnlyList<ExternalBuildDurationSample>>(list);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Dispose() => _conn.Dispose();

    private const string Columns =
        "id, schema_version, project_id, work_item_id, phase, iteration, attempt, state," +
        " target_json, source_json, config_digest, idempotency_key, idempotency_body_hash," +
        " request_id, provider_run_id, fence_owner, fence_epoch, dispatch_attempts, poll_count," +
        " terminal_cause, evidence_json, failure_detail, created_at, updated_at, completed_at, expires_at," +
        " delivery_acked";

    private static void Bind(SqliteCommand cmd, ExternalBuildRecord r)
    {
        cmd.Parameters.AddWithValue("$id", r.Id);
        cmd.Parameters.AddWithValue("$sv", r.SchemaVersion);
        cmd.Parameters.AddWithValue("$p", r.ProjectId);
        cmd.Parameters.AddWithValue("$w", r.WorkItemId);
        cmd.Parameters.AddWithValue("$ph", r.Phase);
        cmd.Parameters.AddWithValue("$it", r.Iteration);
        cmd.Parameters.AddWithValue("$at", r.Attempt);
        BindUpdate(cmd, r);
        cmd.Parameters.AddWithValue("$ik", (object?)r.IdempotencyKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ih", (object?)r.IdempotencyBodyHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ca", r.CreatedAt.ToString("O"));
    }

    private static void BindUpdate(SqliteCommand cmd, ExternalBuildRecord r)
    {
        cmd.Parameters.AddWithValue("$st", r.State.ToString());
        cmd.Parameters.AddWithValue("$tj", JsonSerializer.Serialize(r.Target, JsonOpts));
        cmd.Parameters.AddWithValue("$sj", JsonSerializer.Serialize(r.Source, JsonOpts));
        cmd.Parameters.AddWithValue("$cd", r.ConfigDigest);
        cmd.Parameters.AddWithValue("$rq", r.RequestId);
        cmd.Parameters.AddWithValue("$pr", (object?)r.ProviderRunId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fo", (object?)r.FenceOwner ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fe", r.FenceEpoch);
        cmd.Parameters.AddWithValue("$da", r.DispatchAttempts);
        cmd.Parameters.AddWithValue("$pc", r.PollCount);
        cmd.Parameters.AddWithValue("$tc", r.TerminalCause.ToString());
        cmd.Parameters.AddWithValue("$ev", r.Evidence is null ? DBNull.Value : (object)JsonSerializer.Serialize(r.Evidence, JsonOpts));
        cmd.Parameters.AddWithValue("$fd", (object?)r.FailureDetail ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ua", r.UpdatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$co", r.CompletedAt?.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$ex", r.ExpiresAt?.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$dl", r.DeliveryAcked ? 1 : 0);
    }

    private static ExternalBuildRecord Read(SqliteDataReader reader)
    {
        string? Null(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
        var schemaVersion = reader.GetInt32(1);
        if (schemaVersion > ExternalBuildRecord.CurrentSchemaVersion)
            throw new InvalidOperationException($"Unsupported external_builds schema version {schemaVersion}.");
        return new ExternalBuildRecord
        {
            Id = reader.GetString(0),
            SchemaVersion = schemaVersion,
            ProjectId = reader.GetString(2),
            WorkItemId = reader.GetString(3),
            Phase = reader.GetString(4),
            Iteration = reader.GetInt32(5),
            Attempt = reader.GetInt32(6),
            State = Enum.Parse<ExternalBuildState>(reader.GetString(7)),
            Target = JsonSerializer.Deserialize<ExternalBuildTargetKey>(reader.GetString(8), JsonOpts)!,
            Source = JsonSerializer.Deserialize<ExternalBuildSourceIdentity>(reader.GetString(9), JsonOpts)!,
            ConfigDigest = reader.GetString(10),
            IdempotencyKey = Null(11),
            IdempotencyBodyHash = Null(12),
            RequestId = Null(13) ?? string.Empty,
            ProviderRunId = Null(14),
            FenceOwner = Null(15),
            FenceEpoch = reader.GetInt64(16),
            DispatchAttempts = reader.GetInt32(17),
            PollCount = reader.GetInt32(18),
            TerminalCause = Enum.TryParse<ExternalBuildTerminalCause>(Null(19), out var cause) ? cause : ExternalBuildTerminalCause.Unknown,
            Evidence = Null(20) is { } ev ? JsonSerializer.Deserialize<ExternalBuildEvidence>(ev, JsonOpts) : null,
            FailureDetail = Null(21),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(22)),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(23)),
            CompletedAt = Null(24) is { } c ? DateTimeOffset.Parse(c) : null,
            ExpiresAt = Null(25) is { } e ? DateTimeOffset.Parse(e) : null,
            DeliveryAcked = reader.FieldCount > 26 && !reader.IsDBNull(26) && reader.GetInt32(26) != 0,
        };
    }
}
