using System.Globalization;
using System.Text.Json;
using CodeyBox.Core;
using Microsoft.Data.Sqlite;

namespace CodeyBox.StatisticsPlugin;

/// <summary>
/// SQLite-backed time-series store for per-agent quota snapshots. Owns its own
/// database file (independent of the orchestrator's <c>state.db</c>) so the
/// stats workload never competes for the hot-path write gate and so the file
/// can be archived / shipped to a separate analysis box without touching live
/// pipeline state.
///
/// <para>Two tables: <c>quota_sample</c> for the normalised rows the operator
/// queries (overall + per-window + per-model expansions, one row each) and
/// <c>quota_raw</c> for the full <see cref="AgentQuotaSnapshot"/> serialised
/// as JSON. The raw row is anchored to a probe call by <c>snapshot_id</c> so
/// a normalised row can be joined back to its source snapshot.</para>
/// </summary>
public sealed class QuotaTimeSeriesSqliteStore : IQuotaTimeSeriesStore, IAsyncDisposable, IDisposable
{
    private const string OverallWindowSentinel = "overall";
    private const int HardQueryCeiling = 1_000_000;

    private readonly string _path;
    private readonly SqliteConnection _writeConn;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SqliteCommand _insertSampleCmd;
    private readonly SqliteCommand _insertRawCmd;
    private bool _disposed;

    public string DatabasePath => _path;

    public QuotaTimeSeriesSqliteStore(string path)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _writeConn = new SqliteConnection($"Data Source={_path}");
        _writeConn.Open();

        using (var pragma = _writeConn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=30000;";
            pragma.ExecuteNonQuery();
        }

        using (var create = _writeConn.CreateCommand())
        {
            // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- hardcoded DDL only
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS quota_sample (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    snapshot_id     TEXT NOT NULL,
                    sampled_at      TEXT NOT NULL,
                    agent           TEXT NOT NULL,
                    model_id        TEXT,
                    overall_pct     REAL NOT NULL,
                    would_allow     INTEGER NOT NULL,
                    notes           TEXT,
                    window_name     TEXT,
                    window_pct      REAL,
                    window_reset_at TEXT,
                    is_known        INTEGER NOT NULL,
                    unknown_reason  TEXT
                );

                CREATE INDEX IF NOT EXISTS idx_quota_sample_sampled_at
                    ON quota_sample(sampled_at);

                CREATE INDEX IF NOT EXISTS idx_quota_sample_agent_time
                    ON quota_sample(agent, sampled_at);

                CREATE INDEX IF NOT EXISTS idx_quota_sample_agent_window_time
                    ON quota_sample(agent, window_name, sampled_at);

                CREATE TABLE IF NOT EXISTS quota_raw (
                    id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    snapshot_id TEXT NOT NULL,
                    sampled_at  TEXT NOT NULL,
                    agent       TEXT NOT NULL,
                    model_id    TEXT,
                    raw_json    TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_quota_raw_sampled_at
                    ON quota_raw(sampled_at);

                CREATE INDEX IF NOT EXISTS idx_quota_raw_snapshot_id
                    ON quota_raw(snapshot_id);
                """;
            create.ExecuteNonQuery();
        }

        _insertSampleCmd = _writeConn.CreateCommand();
        _insertSampleCmd.CommandText = """
            INSERT INTO quota_sample (
                snapshot_id, sampled_at, agent, model_id, overall_pct, would_allow,
                notes, window_name, window_pct, window_reset_at, is_known, unknown_reason
            ) VALUES (
                $snapshot, $time, $agent, $model, $overall, $allow,
                $notes, $window, $windowPct, $windowReset, $isKnown, $unknownReason
            );
            """;
        AddSampleParameters(_insertSampleCmd);
        _insertSampleCmd.Prepare();

        _insertRawCmd = _writeConn.CreateCommand();
        _insertRawCmd.CommandText = """
            INSERT INTO quota_raw (snapshot_id, sampled_at, agent, model_id, raw_json)
            VALUES ($snapshot, $time, $agent, $model, $raw);
            """;
        _insertRawCmd.Parameters.Add("$snapshot", SqliteType.Text);
        _insertRawCmd.Parameters.Add("$time", SqliteType.Text);
        _insertRawCmd.Parameters.Add("$agent", SqliteType.Text);
        _insertRawCmd.Parameters.Add("$model", SqliteType.Text);
        _insertRawCmd.Parameters.Add("$raw", SqliteType.Text);
        _insertRawCmd.Prepare();
    }

    private static void AddSampleParameters(SqliteCommand cmd)
    {
        cmd.Parameters.Add("$snapshot", SqliteType.Text);
        cmd.Parameters.Add("$time", SqliteType.Text);
        cmd.Parameters.Add("$agent", SqliteType.Text);
        cmd.Parameters.Add("$model", SqliteType.Text);
        cmd.Parameters.Add("$overall", SqliteType.Real);
        cmd.Parameters.Add("$allow", SqliteType.Integer);
        cmd.Parameters.Add("$notes", SqliteType.Text);
        cmd.Parameters.Add("$window", SqliteType.Text);
        cmd.Parameters.Add("$windowPct", SqliteType.Real);
        cmd.Parameters.Add("$windowReset", SqliteType.Text);
        cmd.Parameters.Add("$isKnown", SqliteType.Integer);
        cmd.Parameters.Add("$unknownReason", SqliteType.Text);
    }

    /// <summary>
    /// Persist one probe call's worth of rows: the snapshot, plus its raw JSON,
    /// in a single transaction so a partial write never produces orphaned rows.
    /// </summary>
    public async Task WriteSnapshotAsync(
        string agent,
        AgentQuotaSnapshot snapshot,
        bool wouldAllow,
        DateTimeOffset sampledAt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(snapshot);
        ThrowIfDisposed();

        var snapshotId = Guid.NewGuid().ToString("n");
        var rawJson = JsonSerializer.Serialize(snapshot);

        await _writeLock.WaitAsync(ct);
        try
        {
            using var tx = _writeConn.BeginTransaction();
            _insertSampleCmd.Transaction = tx;
            _insertRawCmd.Transaction = tx;
            try
            {
                // 1. Overall (aggregated) row — one per probe call.
                WriteRow(
                    snapshotId,
                    sampledAt,
                    agent,
                    modelId: null,
                    overallPct: snapshot.AvailablePct,
                    wouldAllow: wouldAllow,
                    notes: snapshot.Notes,
                    windowName: null,
                    windowPct: null,
                    windowResetAt: null,
                    isKnown: snapshot.IsKnown,
                    unknownReason: snapshot.Unknown?.ToString());

                // 2. Per-window rows for the overall account.
                foreach (var window in snapshot.Windows)
                {
                    WriteRow(
                        snapshotId,
                        sampledAt,
                        agent,
                        modelId: null,
                        overallPct: snapshot.AvailablePct,
                        wouldAllow: wouldAllow,
                        notes: snapshot.Notes,
                        windowName: window.Name,
                        windowPct: window.AvailablePct,
                        windowResetAt: window.ResetAt,
                        isKnown: snapshot.IsKnown,
                        unknownReason: snapshot.Unknown?.ToString());
                }

                // 3. Per-model rows (aggregated + per-window expansion).
                foreach (var (modelId, modelQuota) in snapshot.PerModel)
                {
                    WriteRow(
                        snapshotId,
                        sampledAt,
                        agent,
                        modelId,
                        overallPct: modelQuota.AvailablePct,
                        wouldAllow: wouldAllow,
                        notes: snapshot.Notes,
                        windowName: null,
                        windowPct: null,
                        windowResetAt: modelQuota.ResetAt,
                        isKnown: snapshot.IsKnown,
                        unknownReason: snapshot.Unknown?.ToString());

                    foreach (var window in modelQuota.Windows)
                    {
                        WriteRow(
                            snapshotId,
                            sampledAt,
                            agent,
                            modelId,
                            overallPct: modelQuota.AvailablePct,
                            wouldAllow: wouldAllow,
                            notes: snapshot.Notes,
                            windowName: window.Name,
                            windowPct: window.AvailablePct,
                            windowResetAt: window.ResetAt,
                            isKnown: snapshot.IsKnown,
                            unknownReason: snapshot.Unknown?.ToString());
                    }
                }

                // 4. Raw JSON — one row per probe call (no expansion).
                _insertRawCmd.Parameters["$snapshot"].Value = snapshotId;
                _insertRawCmd.Parameters["$time"].Value = FormatTimestamp(sampledAt);
                _insertRawCmd.Parameters["$agent"].Value = agent;
                _insertRawCmd.Parameters["$model"].Value = DBNull.Value;
                _insertRawCmd.Parameters["$raw"].Value = rawJson;
                await _insertRawCmd.ExecuteNonQueryAsync(ct);

                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
            finally
            {
                _insertSampleCmd.Transaction = null;
                _insertRawCmd.Transaction = null;
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void WriteRow(
        string snapshotId,
        DateTimeOffset sampledAt,
        string agent,
        string? modelId,
        double overallPct,
        bool wouldAllow,
        string? notes,
        string? windowName,
        double? windowPct,
        DateTimeOffset? windowResetAt,
        bool isKnown,
        string? unknownReason)
    {
        var p = _insertSampleCmd.Parameters;
        p["$snapshot"].Value = snapshotId;
        p["$time"].Value = FormatTimestamp(sampledAt);
        p["$agent"].Value = agent;
        p["$model"].Value = modelId is null ? DBNull.Value : modelId;
        p["$overall"].Value = overallPct;
        p["$allow"].Value = wouldAllow ? 1 : 0;
        p["$notes"].Value = notes is null ? DBNull.Value : notes;
        p["$window"].Value = windowName is null ? DBNull.Value : windowName;
        p["$windowPct"].Value = windowPct.HasValue ? windowPct.Value : DBNull.Value;
        p["$windowReset"].Value = windowResetAt.HasValue ? FormatTimestamp(windowResetAt.Value) : DBNull.Value;
        p["$isKnown"].Value = isKnown ? 1 : 0;
        p["$unknownReason"].Value = unknownReason is null ? DBNull.Value : unknownReason;
        _insertSampleCmd.ExecuteNonQuery();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<QuotaSampleRow>> QueryAsync(
        QuotaTimeSeriesFilter filter,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ThrowIfDisposed();

        var (where, parameters) = BuildWhere(filter, includeWindowFilter: true);
        var limit = ClampLimit(filter.Limit);

        using var readConn = new SqliteConnection($"Data Source={_path};Mode=ReadOnly");
        await readConn.OpenAsync(ct);

        using var cmd = readConn.CreateCommand();
        // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- WHERE clause is built from a fixed enumeration of allowed predicates; values are bound parameters
        cmd.CommandText = $"""
            SELECT sampled_at, agent, model_id, overall_pct, would_allow, notes,
                   window_name, window_pct, window_reset_at, is_known, unknown_reason
            FROM quota_sample
            {where}
            ORDER BY sampled_at ASC, id ASC
            LIMIT $limit;
            """;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        cmd.Parameters.AddWithValue("$limit", limit);

        var rows = new List<QuotaSampleRow>(capacity: Math.Min(limit, 256));
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new QuotaSampleRow(
                SampledAt: ParseTimestamp(reader.GetString(0)),
                Agent: reader.GetString(1),
                ModelId: reader.IsDBNull(2) ? null : reader.GetString(2),
                OverallPct: reader.GetDouble(3),
                WouldAllow: reader.GetInt64(4) != 0,
                Notes: reader.IsDBNull(5) ? null : reader.GetString(5),
                WindowName: reader.IsDBNull(6) ? null : reader.GetString(6),
                WindowPct: reader.IsDBNull(7) ? null : reader.GetDouble(7),
                WindowResetAt: reader.IsDBNull(8) ? null : ParseTimestamp(reader.GetString(8)),
                IsKnown: reader.GetInt64(9) != 0,
                UnknownReason: reader.IsDBNull(10) ? null : reader.GetString(10)));
        }

        return rows;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<QuotaRawSnapshotRow>> QueryRawAsync(
        QuotaTimeSeriesFilter filter,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ThrowIfDisposed();

        var (where, parameters) = BuildWhere(filter, includeWindowFilter: false);
        var limit = ClampLimit(filter.Limit);

        using var readConn = new SqliteConnection($"Data Source={_path};Mode=ReadOnly");
        await readConn.OpenAsync(ct);

        using var cmd = readConn.CreateCommand();
        // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- WHERE clause is built from a fixed enumeration of allowed predicates; values are bound parameters
        cmd.CommandText = $"""
            SELECT sampled_at, agent, model_id, raw_json
            FROM quota_raw
            {where}
            ORDER BY sampled_at ASC, id ASC
            LIMIT $limit;
            """;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        cmd.Parameters.AddWithValue("$limit", limit);

        var rows = new List<QuotaRawSnapshotRow>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new QuotaRawSnapshotRow(
                SampledAt: ParseTimestamp(reader.GetString(0)),
                Agent: reader.GetString(1),
                ModelId: reader.IsDBNull(2) ? null : reader.GetString(2),
                RawJson: reader.GetString(3)));
        }

        return rows;
    }

    private (string Where, List<(string Name, object Value)> Parameters) BuildWhere(
        QuotaTimeSeriesFilter filter,
        bool includeWindowFilter)
    {
        var predicates = new List<string>(capacity: 5);
        var parameters = new List<(string Name, object Value)>(capacity: 5);

        if (!string.IsNullOrWhiteSpace(filter.Agent))
        {
            predicates.Add("agent = $agent COLLATE NOCASE");
            parameters.Add(("$agent", filter.Agent.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(filter.ModelId))
        {
            predicates.Add("model_id = $model COLLATE NOCASE");
            parameters.Add(("$model", filter.ModelId.Trim()));
        }

        if (includeWindowFilter && !string.IsNullOrWhiteSpace(filter.WindowName))
        {
            // "overall" is a sentinel for "the aggregated row that has no window",
            // which is stored as NULL window_name. Without the sentinel an operator
            // would have to pick between the aggregated row and a specific window
            // every time — the two cases together being the most common query.
            if (filter.WindowName.Trim().Equals(OverallWindowSentinel, StringComparison.OrdinalIgnoreCase))
            {
                predicates.Add("window_name IS NULL");
            }
            else
            {
                predicates.Add("window_name = $window COLLATE NOCASE");
                parameters.Add(("$window", filter.WindowName.Trim()));
            }
        }

        if (filter.FromUtc.HasValue)
        {
            predicates.Add("sampled_at >= $from");
            parameters.Add(("$from", FormatTimestamp(filter.FromUtc.Value)));
        }

        if (filter.ToUtc.HasValue)
        {
            predicates.Add("sampled_at < $to");
            parameters.Add(("$to", FormatTimestamp(filter.ToUtc.Value)));
        }

        var where = predicates.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", predicates);
        return (where, parameters);
    }

    private int ClampLimit(int requested)
    {
        if (requested <= 0) return 1;
        if (requested > HardQueryCeiling) return HardQueryCeiling;
        return requested;
    }

    /// <summary>
    /// Delete every row older than <paramref name="cutoffUtc"/>. Returns the
    /// total number of (sample + raw) rows removed.
    /// </summary>
    public async Task<int> PruneAsync(DateTimeOffset cutoffUtc, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var cutoff = FormatTimestamp(cutoffUtc);

        await _writeLock.WaitAsync(ct);
        try
        {
            var deleted = 0;
            using var cmd = _writeConn.CreateCommand();
            cmd.CommandText = "DELETE FROM quota_sample WHERE sampled_at < $cutoff;";
            cmd.Parameters.AddWithValue("$cutoff", cutoff);
            deleted += await cmd.ExecuteNonQueryAsync(ct);

            using var rawCmd = _writeConn.CreateCommand();
            rawCmd.CommandText = "DELETE FROM quota_raw WHERE sampled_at < $cutoff;";
            rawCmd.Parameters.AddWithValue("$cutoff", cutoff);
            deleted += await rawCmd.ExecuteNonQueryAsync(ct);

            return deleted;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static string FormatTimestamp(DateTimeOffset value)
        => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture).ToUniversalTime();

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(QuotaTimeSeriesSqliteStore));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _insertSampleCmd.Dispose();
        _insertRawCmd.Dispose();
        _writeConn.Dispose();
        _writeLock.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
