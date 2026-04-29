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
