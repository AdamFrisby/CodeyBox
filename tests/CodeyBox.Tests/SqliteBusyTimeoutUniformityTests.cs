using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Every connection opened against the state database — the work-item store
/// writer and readers, the worker registry writer and readers, and the
/// maintenance connection — must observe the single shared busy-timeout from
/// <see cref="SqliteDefaults"/>. A component that hardcoded its own value or
/// forgot the PRAGMA fails this test with a mismatched read-back.
/// </summary>
public sealed class SqliteBusyTimeoutUniformityTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"codeybox-busytimeout-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public async Task EveryConnection_ObservesSharedBusyTimeout()
    {
        using var store = new SqliteWorkItemStore(_dbPath);
        using var registry = new SqliteWorkerRegistry(_dbPath);
        var maintenance = BuildService(_dbPath);
        var expected = SqliteDefaults.BusyTimeoutMilliseconds;

        Assert.Equal(expected, await ReadBusyTimeoutAsync(StoreWriter(store)));

        using (var storeReader = await OpenConnectionAsync(store, "OpenReadConnectionAsync"))
            Assert.Equal(expected, await ReadBusyTimeoutAsync(storeReader));

        Assert.Equal(expected, await ReadBusyTimeoutAsync(RegistryWriter(registry)));

        using (var registryReader = await OpenConnectionAsync(registry, "OpenReadConnectionAsync"))
            Assert.Equal(expected, await ReadBusyTimeoutAsync(registryReader));

        using (var maintenanceConn = await OpenConnectionAsync(maintenance, "OpenMaintenanceConnectionAsync"))
            Assert.Equal(expected, await ReadBusyTimeoutAsync(maintenanceConn));
    }

    [Fact]
    public async Task WorkerRegistry_DefaultMatchesSharedBusyTimeout()
    {
        using var registry = new SqliteWorkerRegistry(_dbPath);

        Assert.Equal(
            SqliteDefaults.BusyTimeoutMilliseconds,
            await ReadBusyTimeoutAsync(RegistryWriter(registry)));
    }

    private static SqliteDatabaseMaintenanceService BuildService(string dbPath) =>
        new(
            dbPath,
            static () => new SqliteMaintenanceOptions(),
            new SqliteDatabaseWriteGateFactory(
                static () => new SqliteWriteGateOptions
                {
                    AcquisitionTimeout = TimeSpan.FromSeconds(5),
                    MaxHoldDuration = TimeSpan.FromSeconds(30),
                },
                NullLoggerFactory.Instance),
            NullLogger<SqliteDatabaseMaintenanceService>.Instance,
            TimeProvider.System);

    private static SqliteConnection StoreWriter(SqliteWorkItemStore store) =>
        (SqliteConnection)typeof(SqliteWorkItemStore)
            .GetField("_conn", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(store)!;

    private static SqliteConnection RegistryWriter(SqliteWorkerRegistry registry) =>
        (SqliteConnection)typeof(SqliteWorkerRegistry)
            .GetField("_conn", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(registry)!;

    private static async Task<SqliteConnection> OpenConnectionAsync(object target, string methodName)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance)!;
        var task = (Task<SqliteConnection>)method.Invoke(target, [CancellationToken.None])!;
        return await task.ConfigureAwait(false);
    }

    private static async Task<long> ReadBusyTimeoutAsync(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout;";
        return (long)(await cmd.ExecuteScalarAsync().ConfigureAwait(false) ?? 0L);
    }
}
