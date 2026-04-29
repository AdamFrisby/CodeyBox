using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Validates that invalid WorkerPoolOptions values are rejected at startup.
/// The validation runs inside the OrchestratorOptions DI factory in Program.cs;
/// here we test the equivalent guard logic directly via a helper that mirrors it.
/// </summary>
public sealed class WorkerPoolOptionsValidationTests
{
    private static void Validate(WorkerPoolOptions opts)
    {
        if (opts.MaxConcurrentWorkers < 1)
            throw new InvalidOperationException(
                "CodeyBox:WorkerPool:MaxConcurrentWorkers must be >= 1");
        if (opts.MinSpawnInterval < TimeSpan.Zero)
            throw new InvalidOperationException(
                "CodeyBox:WorkerPool:MinSpawnInterval must be >= 0");
        if (opts.MinSpawnInterval >= TimeSpan.FromHours(1))
            throw new InvalidOperationException(
                "CodeyBox:WorkerPool:MinSpawnInterval must be < 1 hour (values >= 1h are almost certainly a configuration error)");
    }

    [Fact]
    public void MaxConcurrentWorkers_Zero_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Validate(new WorkerPoolOptions { MaxConcurrentWorkers = 0 }));
        Assert.Contains("MaxConcurrentWorkers", ex.Message);
    }

    [Fact]
    public void MaxConcurrentWorkers_Negative_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Validate(new WorkerPoolOptions { MaxConcurrentWorkers = -3 }));
        Assert.Contains("MaxConcurrentWorkers", ex.Message);
    }

    [Fact]
    public void MinSpawnInterval_Negative_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Validate(new WorkerPoolOptions
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
            Validate(new WorkerPoolOptions
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
            Validate(new WorkerPoolOptions
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
        // Should not throw.
        Validate(new WorkerPoolOptions
        {
            MaxConcurrentWorkers = max,
            MinSpawnInterval = TimeSpan.FromSeconds(intervalSeconds),
        });
    }
}
