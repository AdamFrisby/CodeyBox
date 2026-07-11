using Microsoft.Data.Sqlite;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// SQLite-backed store for per-VM teardown resource usage records.
/// </summary>
public sealed class SqliteSandboxResourceUsageStore : ISandboxResourceUsageStore, IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly SqliteDatabaseWriteGate _writeLock;
    private readonly SqliteCommand _insertCmd;

    public SqliteSandboxResourceUsageStore(
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

            using (var walCmd = _conn.CreateCommand())
            {
                walCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=30000;";
                walCmd.ExecuteNonQuery();
            }

            using var createCmd = _conn.CreateCommand();
            // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- hardcoded DDL only
            createCmd.CommandText = """
                CREATE TABLE IF NOT EXISTS sandbox_resource_usage (
                    id               TEXT PRIMARY KEY,
                    work_item_id     TEXT NOT NULL,
                    phase            TEXT NOT NULL,
                    vm_name          TEXT NOT NULL,
                    duration_sec     REAL,
                    avg_cpu_pct      REAL,
                    peak_ram_mb      REAL,
                    net_rx_mb        REAL,
                    net_tx_mb        REAL,
                    baseline_ref     TEXT,
                    network_profile  TEXT,
                    loadavg_1        REAL,
                    loadavg_5        REAL,
                    loadavg_15       REAL,
                    captured_at      TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_sandbox_resource_usage_recent
                    ON sandbox_resource_usage(captured_at DESC);
                CREATE INDEX IF NOT EXISTS idx_sandbox_resource_usage_work_item
                    ON sandbox_resource_usage(work_item_id, phase, captured_at);
                """;
            createCmd.ExecuteNonQuery();

            _insertCmd = _conn.CreateCommand();
            _insertCmd.CommandText = """
                INSERT INTO sandbox_resource_usage
                    (id, work_item_id, phase, vm_name, duration_sec, avg_cpu_pct, peak_ram_mb,
                     net_rx_mb, net_tx_mb, baseline_ref, network_profile, loadavg_1, loadavg_5,
                     loadavg_15, captured_at)
                VALUES
                    ($id, $wid, $phase, $vm, $duration, $cpu, $peak, $rx, $tx, $baseline,
                     $network, $load1, $load5, $load15, $captured)
                """;
            _insertCmd.Parameters.Add("$id", SqliteType.Text);
            _insertCmd.Parameters.Add("$wid", SqliteType.Text);
            _insertCmd.Parameters.Add("$phase", SqliteType.Text);
            _insertCmd.Parameters.Add("$vm", SqliteType.Text);
            _insertCmd.Parameters.Add("$duration", SqliteType.Real);
            _insertCmd.Parameters.Add("$cpu", SqliteType.Real);
            _insertCmd.Parameters.Add("$peak", SqliteType.Real);
            _insertCmd.Parameters.Add("$rx", SqliteType.Real);
            _insertCmd.Parameters.Add("$tx", SqliteType.Real);
            _insertCmd.Parameters.Add("$baseline", SqliteType.Text);
            _insertCmd.Parameters.Add("$network", SqliteType.Text);
            _insertCmd.Parameters.Add("$load1", SqliteType.Real);
            _insertCmd.Parameters.Add("$load5", SqliteType.Real);
            _insertCmd.Parameters.Add("$load15", SqliteType.Real);
            _insertCmd.Parameters.Add("$captured", SqliteType.Text);
            _insertCmd.Prepare();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task RecordAsync(SandboxResourceUsageRecord record, CancellationToken ct = default)
    {
        await _connectionLock.WaitAsync(ct);
        try
        {
            await _writeLock.WaitAsync(ct);
            try
            {
                _insertCmd.Parameters["$id"].Value = Guid.NewGuid().ToString("N");
                _insertCmd.Parameters["$wid"].Value = record.WorkItemId.ToString();
                _insertCmd.Parameters["$phase"].Value = record.Phase;
                _insertCmd.Parameters["$vm"].Value = record.VmName;
                SetNullable(_insertCmd, "$duration", record.DurationSeconds);
                SetNullable(_insertCmd, "$cpu", record.AvgCpuPercent);
                SetNullable(_insertCmd, "$peak", record.PeakRamMb);
                SetNullable(_insertCmd, "$rx", record.NetRxMb);
                SetNullable(_insertCmd, "$tx", record.NetTxMb);
                _insertCmd.Parameters["$baseline"].Value = string.IsNullOrWhiteSpace(record.BaselineRef)
                    ? DBNull.Value
                    : record.BaselineRef;
                _insertCmd.Parameters["$network"].Value = string.IsNullOrWhiteSpace(record.NetworkProfile)
                    ? DBNull.Value
                    : record.NetworkProfile;
                SetNullable(_insertCmd, "$load1", record.LoadAvg1);
                SetNullable(_insertCmd, "$load5", record.LoadAvg5);
                SetNullable(_insertCmd, "$load15", record.LoadAvg15);
                _insertCmd.Parameters["$captured"].Value = record.CapturedAt.ToUniversalTime().ToString("O");
                await _insertCmd.ExecuteNonQueryAsync(ct);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task<IReadOnlyList<SandboxResourceUsageRecord>> ListRecentAsync(
        int limit,
        DateTimeOffset? sinceUtc = null,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 1000);
        await _connectionLock.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sinceUtc.HasValue
                ? """
                  SELECT work_item_id, phase, vm_name, duration_sec, avg_cpu_pct, peak_ram_mb,
                         net_rx_mb, net_tx_mb, baseline_ref, network_profile, loadavg_1,
                         loadavg_5, loadavg_15, captured_at
                  FROM sandbox_resource_usage
                  WHERE captured_at >= $since
                  ORDER BY captured_at DESC
                  LIMIT $lim
                  """
                : """
                  SELECT work_item_id, phase, vm_name, duration_sec, avg_cpu_pct, peak_ram_mb,
                         net_rx_mb, net_tx_mb, baseline_ref, network_profile, loadavg_1,
                         loadavg_5, loadavg_15, captured_at
                  FROM sandbox_resource_usage
                  ORDER BY captured_at DESC
                  LIMIT $lim
                  """;
            cmd.Parameters.AddWithValue("$lim", limit);
            if (sinceUtc.HasValue)
                cmd.Parameters.AddWithValue("$since", sinceUtc.Value.ToUniversalTime().ToString("O"));

            var results = new List<SandboxResourceUsageRecord>();
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                results.Add(ReadRecord(reader));
            return results;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private static SandboxResourceUsageRecord ReadRecord(SqliteDataReader r) => new()
    {
        WorkItemId = new WorkItemId(Guid.Parse(r.GetString(0))),
        Phase = r.GetString(1),
        VmName = r.GetString(2),
        DurationSeconds = ReadNullableDouble(r, 3),
        AvgCpuPercent = ReadNullableDouble(r, 4),
        PeakRamMb = ReadNullableDouble(r, 5),
        NetRxMb = ReadNullableDouble(r, 6),
        NetTxMb = ReadNullableDouble(r, 7),
        BaselineRef = r.IsDBNull(8) ? null : r.GetString(8),
        NetworkProfile = r.IsDBNull(9) ? null : r.GetString(9),
        LoadAvg1 = ReadNullableDouble(r, 10),
        LoadAvg5 = ReadNullableDouble(r, 11),
        LoadAvg15 = ReadNullableDouble(r, 12),
        CapturedAt = DateTimeOffset.Parse(r.GetString(13)),
    };

    private static double? ReadNullableDouble(SqliteDataReader r, int ordinal) =>
        r.IsDBNull(ordinal) ? null : r.GetDouble(ordinal);

    private static void SetNullable(SqliteCommand cmd, string parameterName, double? value) =>
        cmd.Parameters[parameterName].Value = value.HasValue ? value.Value : DBNull.Value;

    public void Dispose()
    {
        _insertCmd.Dispose();
        _conn.Dispose();
        _connectionLock.Dispose();
        _writeLock.Dispose();
    }
}
