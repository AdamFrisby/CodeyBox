using CodeyBox.Api;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Tests;

/// <summary>
/// The host-pool capacity reporter derives the process-wide ceiling as the
/// sum of host capacities (capacity accounting lives on the members) and
/// reports per-member headroom. There is no independently-imposed global
/// scalar left to clamp against, so no "excess capacity will not be used"
/// warning is ever emitted — not for finite pools above any worker/sandbox
/// count, and not for unbounded pools.
/// </summary>
public sealed class RemoteHostPoolCapacityLoggerTests
{
    [Fact]
    public void Log_ReportsDerivedCeilingAsHostSumWithoutWarning()
    {
        var logger = new CapturingLogger<RemoteHostPoolCapacityLoggerTests>();
        var pool = new StaticHostPool(
        [
            Host("a", 4),
            Host("b", 3),
        ]);

        RemoteHostPoolCapacityLogger.Log(pool, logger);

        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
        var info = Assert.Single(logger.Entries, e => e.Level == LogLevel.Information);
        Assert.Equal("7", info.Properties["Capacity"]);
        Assert.Equal(2, info.Properties["HostCount"]);
        var hosts = Assert.IsType<string>(info.Properties["Hosts"]);
        Assert.Contains("a=4/headroom=4", hosts);
        Assert.Contains("b=3/headroom=3", hosts);
    }

    [Fact]
    public void Log_ReportsPerMemberHeadroomNetOfReservations()
    {
        var logger = new CapturingLogger<RemoteHostPoolCapacityLoggerTests>();
        var pool = new StaticHostPool(
        [
            Host("a", 4, reserved: 3),
            Host("b", 2, reserved: 0, cordoned: true),
            Host("c", 1, reserved: 0, healthy: false),
        ]);

        RemoteHostPoolCapacityLogger.Log(pool, logger);

        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
        var info = Assert.Single(logger.Entries, e => e.Level == LogLevel.Information);
        Assert.Equal("7", info.Properties["Capacity"]);
        var hosts = Assert.IsType<string>(info.Properties["Hosts"]);
        Assert.Contains("a=4/headroom=1", hosts);
        Assert.Contains("b=2/headroom=2:cordoned", hosts);
        Assert.Contains("c=1/headroom=1:unhealthy", hosts);
    }

    [Fact]
    public void Log_UnboundedPool_ReportsUnboundedWithoutWarning()
    {
        var logger = new CapturingLogger<RemoteHostPoolCapacityLoggerTests>();
        var pool = new StaticHostPool([Host("a", int.MaxValue)]);

        RemoteHostPoolCapacityLogger.Log(pool, logger);

        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
        var info = Assert.Single(logger.Entries, e => e.Level == LogLevel.Information);
        Assert.Equal("unbounded", info.Properties["Capacity"]);
        var hosts = Assert.IsType<string>(info.Properties["Hosts"]);
        Assert.Contains("a=unbounded/headroom=unbounded", hosts);
    }

    [Fact]
    public void Log_DoesNotLogWhenHostPoolIsEmpty()
    {
        var logger = new CapturingLogger<RemoteHostPoolCapacityLoggerTests>();
        var pool = new StaticHostPool([]);

        RemoteHostPoolCapacityLogger.Log(pool, logger);

        Assert.Empty(logger.Entries);
    }

    private static SandboxHostPoolEntry Host(
        string id,
        int capacity,
        int reserved = 0,
        bool cordoned = false,
        bool healthy = true) =>
        new(
            HostId: id,
            Capacity: capacity,
            Reserved: reserved,
            Cordoned: cordoned,
            ConfiguredHealthy: healthy,
            RuntimeHealthy: true,
            RuntimeUnhealthyReason: null,
            RuntimeUnhealthyUntil: null,
            AllowedNetworkProfiles: []);

    private sealed class StaticHostPool(IReadOnlyList<SandboxHostPoolEntry> rows) : ISandboxHostPoolSnapshot
    {
        public IReadOnlyList<SandboxHostPoolEntry> SnapshotHostPool() => rows;
    }
}
