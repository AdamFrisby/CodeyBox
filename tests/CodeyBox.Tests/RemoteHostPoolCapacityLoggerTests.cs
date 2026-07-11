using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Tests;

public sealed class RemoteHostPoolCapacityLoggerTests
{
    [Fact]
    public void Log_WarnsWhenFiniteHostCapacityExceedsGlobalFanoutCap()
    {
        var logger = new CapturingLogger<RemoteHostPoolCapacityLoggerTests>();
        var pool = new StaticHostPool(
        [
            Host("a", 4),
            Host("b", 3),
        ]);

        RemoteHostPoolCapacityLogger.Log(
            pool,
            new OrchestratorOptions { MaxConcurrentWorkers = 5, MaxConcurrentSandboxes = 10 },
            logger);

        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Equal("7", warning.Properties["HostCapacity"]);
        Assert.Equal(5, warning.Properties["GlobalCap"]);
    }

    [Fact]
    public void Log_WarnsWhenHostCapacityIsUnbounded()
    {
        var logger = new CapturingLogger<RemoteHostPoolCapacityLoggerTests>();
        var pool = new StaticHostPool([Host("a", int.MaxValue)]);

        RemoteHostPoolCapacityLogger.Log(
            pool,
            new OrchestratorOptions { MaxConcurrentWorkers = 100, MaxConcurrentSandboxes = 100 },
            logger);

        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Equal("unbounded", warning.Properties["HostCapacity"]);
        Assert.Equal(100, warning.Properties["GlobalCap"]);
    }

    [Fact]
    public void Log_DoesNotWarnWhenFiniteHostCapacityIsWithinGlobalFanoutCap()
    {
        var logger = new CapturingLogger<RemoteHostPoolCapacityLoggerTests>();
        var pool = new StaticHostPool([Host("a", 2), Host("b", 3)]);

        RemoteHostPoolCapacityLogger.Log(
            pool,
            new OrchestratorOptions { MaxConcurrentWorkers = 10, MaxConcurrentSandboxes = 5 },
            logger);

        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Single(logger.Entries, e => e.Level == LogLevel.Information);
    }

    [Fact]
    public void Log_DoesNotLogWhenHostPoolIsEmpty()
    {
        var logger = new CapturingLogger<RemoteHostPoolCapacityLoggerTests>();
        var pool = new StaticHostPool([]);

        RemoteHostPoolCapacityLogger.Log(
            pool,
            new OrchestratorOptions { MaxConcurrentWorkers = 10, MaxConcurrentSandboxes = 10 },
            logger);

        Assert.Empty(logger.Entries);
    }

    private static SandboxHostPoolEntry Host(string id, int capacity) =>
        new(
            HostId: id,
            Capacity: capacity,
            Reserved: 0,
            Cordoned: false,
            ConfiguredHealthy: true,
            RuntimeHealthy: true,
            RuntimeUnhealthyReason: null,
            RuntimeUnhealthyUntil: null,
            AllowedNetworkProfiles: []);

    private sealed class StaticHostPool(IReadOnlyList<SandboxHostPoolEntry> rows) : ISandboxHostPoolSnapshot
    {
        public IReadOnlyList<SandboxHostPoolEntry> SnapshotHostPool() => rows;
    }
}
