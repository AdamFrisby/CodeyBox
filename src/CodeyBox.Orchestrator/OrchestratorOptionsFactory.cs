using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Builds and validates <see cref="OrchestratorOptions"/> from raw config values.
/// Extracted here so the DI factory in Program.cs and unit tests both invoke
/// the same code path (no duplication, regressions caught by tests).
/// </summary>
public static class OrchestratorOptionsFactory
{
    /// <summary>
    /// Builds <see cref="OrchestratorOptions"/> from worker-pool config and the
    /// optional legacy <c>CodeyBox:Concurrency</c> value.
    /// </summary>
    /// <param name="legacyConcurrency">
    /// Value of <c>CodeyBox:Concurrency</c> (nullable). When non-null, it is
    /// used as <see cref="WorkerPoolOptions.MaxConcurrentWorkers"/> and a
    /// deprecation warning is emitted via <paramref name="log"/>.
    /// </param>
    /// <param name="workerPool">Parsed <see cref="WorkerPoolOptions"/> (from <c>CodeyBox:WorkerPool</c>).</param>
    /// <param name="log">Logger for the deprecation warning.</param>
    /// <returns>Validated <see cref="OrchestratorOptions"/>.</returns>
    /// <exception cref="InvalidOperationException">Thrown when any option value is out of range.</exception>
    public static OrchestratorOptions Build(int? legacyConcurrency, WorkerPoolOptions workerPool, ILogger log)
    {
        var wp = workerPool;

        if (legacyConcurrency is { } legacyValue)
        {
            log.LogWarning(
                "CodeyBox:Concurrency is deprecated and will be removed in a future version. " +
                "Use CodeyBox:WorkerPool:MaxConcurrentWorkers instead. " +
                "Current value ({LegacyValue}) is being used as MaxConcurrentWorkers.",
                legacyValue);
            wp = new WorkerPoolOptions
            {
                MaxConcurrentWorkers = legacyValue,
                MinSpawnInterval = wp.MinSpawnInterval,
            };
        }

        if (wp.MaxConcurrentWorkers < 1)
            throw new InvalidOperationException(
                "CodeyBox:WorkerPool:MaxConcurrentWorkers must be >= 1");
        if (wp.MinSpawnInterval < TimeSpan.Zero)
            throw new InvalidOperationException(
                "CodeyBox:WorkerPool:MinSpawnInterval must be >= 0");
        if (wp.MinSpawnInterval >= TimeSpan.FromHours(1))
            throw new InvalidOperationException(
                "CodeyBox:WorkerPool:MinSpawnInterval must be < 1 hour (values >= 1h are almost certainly a configuration error)");

        return new OrchestratorOptions
        {
            MaxConcurrentWorkers = wp.MaxConcurrentWorkers,
            MinSpawnInterval = wp.MinSpawnInterval,
        };
    }
}
