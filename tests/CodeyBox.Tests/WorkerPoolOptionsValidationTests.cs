using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Validates that invalid WorkerPoolOptions values are rejected by
/// OrchestratorOptionsFactory.Build — the same code path that Program.cs calls
/// at startup via the DI factory.
/// </summary>
public sealed class WorkerPoolOptionsValidationTests
{
    private static OrchestratorOptions Build(WorkerPoolOptions opts) =>
        OrchestratorOptionsFactory.Build(null, opts, NullLogger.Instance);

    [Fact]
    public void MaxConcurrentWorkers_Zero_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Build(new WorkerPoolOptions { MaxConcurrentWorkers = 0 }));
        Assert.Contains("MaxConcurrentWorkers", ex.Message);
    }

    [Fact]
    public void MaxConcurrentWorkers_Negative_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Build(new WorkerPoolOptions { MaxConcurrentWorkers = -3 }));
        Assert.Contains("MaxConcurrentWorkers", ex.Message);
    }

    [Fact]
    public void DeriveDefaultMaxConcurrentSandboxes_InvalidWorkerCount_Throws()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            OrchestratorOptionsFactory.DeriveDefaultMaxConcurrentSandboxes(0));
        Assert.Equal("maxConcurrentWorkers", ex.ParamName);
    }

    [Fact]
    public void MinSpawnInterval_Negative_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Build(new WorkerPoolOptions
            {
                MaxConcurrentWorkers = 1,
                MinSpawnInterval = TimeSpan.FromSeconds(-1),
            }));
        Assert.Contains("MinSpawnInterval", ex.Message);
    }

    [Fact]
    public void MaxConcurrentSandboxes_Zero_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Build(new WorkerPoolOptions
            {
                MaxConcurrentWorkers = 2,
                MaxConcurrentSandboxes = 0,
            }));
        Assert.Contains("MaxConcurrentSandboxes", ex.Message);
    }

    [Fact]
    public void MaxConcurrentSandboxes_Negative_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Build(new WorkerPoolOptions
            {
                MaxConcurrentWorkers = 2,
                MaxConcurrentSandboxes = -1,
            }));
        Assert.Contains("MaxConcurrentSandboxes", ex.Message);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 6)]
    [InlineData(4, 8)]
    public void MaxConcurrentSandboxes_DefaultsToTwiceWorkerCount(int workers, int expectedSandboxes)
    {
        var opts = Build(new WorkerPoolOptions { MaxConcurrentWorkers = workers });

        Assert.Equal(expectedSandboxes, opts.MaxConcurrentSandboxes);
    }

    [Fact]
    public void MaxConcurrentSandboxes_ExplicitValueWins()
    {
        var opts = Build(new WorkerPoolOptions
        {
            MaxConcurrentWorkers = 4,
            MaxConcurrentSandboxes = 9,
        });

        Assert.Equal(9, opts.MaxConcurrentSandboxes);
    }

    [Fact]
    public void MaxConcurrentSandboxes_AtMinimumTwiceWorkers_IsAccepted()
    {
        var opts = Build(new WorkerPoolOptions
        {
            MaxConcurrentWorkers = 6,
            MaxConcurrentSandboxes = 12,
        });

        Assert.Equal(12, opts.MaxConcurrentSandboxes);
    }

    [Theory]
    // The 2026-09-11 incident ran 6 workers against 6 permits: every worker
    // held one sandbox while waiting for another and dispatch stalled for
    // hours. Any ratio at or below 1:1 — and anything below 2:1 — must fail
    // fast at startup with a message naming both keys and the minimum.
    [InlineData(6, 6)]
    [InlineData(6, 5)]
    [InlineData(6, 11)]
    [InlineData(1, 1)]
    [InlineData(2, 3)]
    public void MaxConcurrentSandboxes_BelowTwiceWorkers_ThrowsNamingBothKeysAndMinimum(
        int workers, int sandboxes)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Build(new WorkerPoolOptions
            {
                MaxConcurrentWorkers = workers,
                MaxConcurrentSandboxes = sandboxes,
            }));

        Assert.Contains("CodeyBox:WorkerPool:MaxConcurrentSandboxes", ex.Message);
        Assert.Contains("CodeyBox:WorkerPool:MaxConcurrentWorkers", ex.Message);
        Assert.Contains((2 * workers).ToString(), ex.Message);
    }

    [Fact]
    public void MinSpawnInterval_OneHour_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Build(new WorkerPoolOptions
            {
                MaxConcurrentWorkers = 1,
                MinSpawnInterval = TimeSpan.FromHours(1),
            }));
        Assert.Contains("MinSpawnInterval", ex.Message);
    }

    [Fact]
    public void MinSpawnInterval_FiveHours_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Build(new WorkerPoolOptions
            {
                MaxConcurrentWorkers = 1,
                MinSpawnInterval = TimeSpan.FromHours(5),
            }));
        Assert.Contains("MinSpawnInterval", ex.Message);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(4, 0)]
    [InlineData(1, 30)]
    [InlineData(4, 3599)]
    public void ValidValues_DoNotThrow(int max, int intervalSeconds)
    {
        Build(new WorkerPoolOptions
        {
            MaxConcurrentWorkers = max,
            MinSpawnInterval = TimeSpan.FromSeconds(intervalSeconds),
        });
    }
}
