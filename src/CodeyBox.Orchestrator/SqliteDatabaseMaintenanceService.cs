using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Periodic freelist bound for the SQLite state database. Each iteration
/// inspects <c>PRAGMA freelist_count</c> and runs a VACUUM only when the
/// reclaimable page count reaches
/// <see cref="SqliteMaintenanceOptions.FreelistPageThreshold"/>.
/// </summary>
/// <remarks>
/// VACUUM rewrites the whole file and cannot run inside a transaction, so it
/// executes on a dedicated maintenance connection while holding the
/// per-database write gate (which excludes every in-process writer on the
/// same file). Reader connections are short-lived; a read racing the VACUUM
/// fails with SQLITE_LOCKED and is deferred to the next interval rather than
/// blocking maintenance. Lock contention surfaces as a Warning, never as a
/// host fault.
/// </remarks>
public sealed class SqliteDatabaseMaintenanceService : BackgroundService
{
    private readonly string _dbPath;
    private readonly Func<SqliteMaintenanceOptions> _optionsAccessor;
    private readonly SqliteDatabaseWriteGateFactory _writeGateFactory;
    private readonly TimeProvider _time;
    private readonly ILogger<SqliteDatabaseMaintenanceService> _log;

    /// <param name="dbPath">State database file to maintain.</param>
    /// <param name="optionsAccessor">
    /// Thread-safe accessor invoked every iteration; must be non-blocking and
    /// return a valid snapshot. Enables hot-reload of the interval/threshold.
    /// </param>
    public SqliteDatabaseMaintenanceService(
        string dbPath,
        Func<SqliteMaintenanceOptions> optionsAccessor,
        SqliteDatabaseWriteGateFactory writeGateFactory,
        ILogger<SqliteDatabaseMaintenanceService> log,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        ArgumentNullException.ThrowIfNull(optionsAccessor);
        ArgumentNullException.ThrowIfNull(writeGateFactory);
        ArgumentNullException.ThrowIfNull(log);

        _dbPath = dbPath;
        _optionsAccessor = optionsAccessor;
        _writeGateFactory = writeGateFactory;
        _log = log;
        _time = timeProvider ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            SqliteMaintenanceOptions options;
            try
            {
                options = _optionsAccessor()
                    ?? throw new InvalidOperationException("The SQLite maintenance options accessor returned null.");
                options.Validate();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "SQLite maintenance options are invalid; retrying at the next check");
                options = new SqliteMaintenanceOptions();
            }

            try
            {
                await RunMaintenanceOnceAsync(options, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "SQLite database maintenance failed; retrying at the next check");
            }

            try
            {
                await Task.Delay(options.CheckInterval, _time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    internal async Task<MaintenanceOutcome> RunMaintenanceOnceAsync(
        SqliteMaintenanceOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        if (!options.Enabled)
            return MaintenanceOutcome.Disabled;

        using var gate = _writeGateFactory.ForPath(_dbPath);
        try
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (SqliteWriteGateAcquisitionTimeoutException ex)
        {
            _log.LogWarning(
                ex,
                "SQLite maintenance skipped: could not acquire the write gate (holder: {CurrentHolder}); retrying at the next check",
                ex.CurrentHolder ?? "unknown");
            return MaintenanceOutcome.DeferredContention;
        }

        try
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA busy_timeout=30000;";
                await pragma.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            var (pageSize, pageCount, freelistCount, autoVacuum) = await InspectAsync(conn, ct).ConfigureAwait(false);
            if (freelistCount < options.FreelistPageThreshold)
            {
                _log.LogDebug(
                    "SQLite maintenance: freelist {FreelistPages} pages ({FreelistBytes} bytes) below threshold {Threshold}; no action",
                    freelistCount,
                    freelistCount * pageSize,
                    options.FreelistPageThreshold);
                return MaintenanceOutcome.NoAction;
            }

            if (autoVacuum == 2)
            {
                using var vacuum = conn.CreateCommand();
                vacuum.CommandTimeout = TimeoutSeconds(options.VacuumTimeout);
                // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- PRAGMA takes no parameters; the interpolated value is a validated row count read back from the database itself
                vacuum.CommandText = $"PRAGMA incremental_vacuum({freelistCount});";
                await vacuum.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            else
            {
                using var vacuum = conn.CreateCommand();
                vacuum.CommandTimeout = TimeoutSeconds(options.VacuumTimeout);
                vacuum.CommandText = "VACUUM;";
                await vacuum.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            var (_, pageCountAfter, freelistAfter, _) = await InspectAsync(conn, ct).ConfigureAwait(false);
            _log.LogWarning(
                "SQLite maintenance: reclaimed freelist from {FreelistBefore} to {FreelistAfter} pages ({BytesBefore} to {BytesAfter} bytes); file pages {PagesBefore} -> {PagesAfter}",
                freelistCount,
                freelistAfter,
                freelistCount * pageSize,
                freelistAfter * pageSize,
                pageCount,
                pageCountAfter);
            return MaintenanceOutcome.Vacuumed;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
        {
            _log.LogWarning(ex, "SQLite maintenance deferred: database is locked; retrying at the next check");
            return MaintenanceOutcome.DeferredContention;
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<(long PageSize, long PageCount, long FreelistCount, long AutoVacuum)> InspectAsync(
        SqliteConnection conn,
        CancellationToken ct)
    {
        static async Task<long> PragmaAsync(SqliteConnection conn, string name, CancellationToken ct)
        {
            using var cmd = conn.CreateCommand();
            // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- 'name' is always one of four compile-time literals from the call sites below, never caller input
            cmd.CommandText = $"PRAGMA {name};";
            return (long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
        }

        return (
            await PragmaAsync(conn, "page_size", ct).ConfigureAwait(false),
            await PragmaAsync(conn, "page_count", ct).ConfigureAwait(false),
            await PragmaAsync(conn, "freelist_count", ct).ConfigureAwait(false),
            await PragmaAsync(conn, "auto_vacuum", ct).ConfigureAwait(false));
    }

    private static int TimeoutSeconds(TimeSpan timeout) =>
        timeout.TotalSeconds >= int.MaxValue ? int.MaxValue : Math.Max(1, (int)timeout.TotalSeconds);

    internal enum MaintenanceOutcome
    {
        Disabled,
        NoAction,
        Vacuumed,
        DeferredContention,
    }
}
