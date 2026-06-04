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
    /// Value of <c>CodeyBox:Concurrency</c> (nullable). Used as
    /// <see cref="WorkerPoolOptions.MaxConcurrentWorkers"/> only when that key is
    /// not explicitly set; a deprecation warning is emitted via <paramref name="log"/>.
    /// </param>
    /// <param name="workerPool">Parsed <see cref="WorkerPoolOptions"/> (from <c>CodeyBox:WorkerPool</c>).</param>
    /// <param name="log">Logger for deprecation warnings.</param>
    /// <returns>Validated <see cref="OrchestratorOptions"/>.</returns>
    /// <exception cref="InvalidOperationException">Thrown when any option value is out of range.</exception>
    public static OrchestratorOptions Build(int? legacyConcurrency, WorkerPoolOptions workerPool, ILogger log)
    {
        var wp = workerPool;
        int maxConcurrent;

        if (wp.MaxConcurrentWorkers is { } workerPoolMax)
        {
            maxConcurrent = workerPoolMax;
            if (legacyConcurrency is { } legacyValue)
            {
                log.LogWarning(
                    "CodeyBox:Concurrency is deprecated and will be removed in a future version. " +
                    "Deprecated value ({LegacyValue}) is set but overridden by " +
                    "CodeyBox:WorkerPool:MaxConcurrentWorkers={WorkerPoolMax}; remove the deprecated key.",
                    legacyValue, workerPoolMax);
            }
        }
        else if (legacyConcurrency is { } legacyValue)
        {
            log.LogWarning(
                "CodeyBox:Concurrency is deprecated and will be removed in a future version. " +
                "Use CodeyBox:WorkerPool:MaxConcurrentWorkers instead. " +
                "Current value ({LegacyValue}) is being used as MaxConcurrentWorkers.",
                legacyValue);
            maxConcurrent = legacyValue;
        }
        else
        {
            maxConcurrent = 1;
        }

        if (maxConcurrent < 1)
            throw new InvalidOperationException(
                "CodeyBox:WorkerPool:MaxConcurrentWorkers must be >= 1");
        var maxConcurrentSandboxes = wp.MaxConcurrentSandboxes
            ?? DeriveDefaultMaxConcurrentSandboxes(maxConcurrent);
        if (maxConcurrentSandboxes < 1)
            throw new InvalidOperationException(
                "CodeyBox:WorkerPool:MaxConcurrentSandboxes must be >= 1");
        if (wp.MinSpawnInterval < TimeSpan.Zero)
            throw new InvalidOperationException(
                "CodeyBox:WorkerPool:MinSpawnInterval must be >= 0");
        if (wp.MinSpawnInterval >= TimeSpan.FromHours(1))
            throw new InvalidOperationException(
                "CodeyBox:WorkerPool:MinSpawnInterval must be < 1 hour (values >= 1h are almost certainly a configuration error)");

        return new OrchestratorOptions
        {
            MaxConcurrentWorkers = maxConcurrent,
            MaxConcurrentSandboxes = maxConcurrentSandboxes,
            MinSpawnInterval = wp.MinSpawnInterval,
        };
    }

    public static int DeriveDefaultMaxConcurrentSandboxes(int maxConcurrentWorkers)
    {
        if (maxConcurrentWorkers < 1)
            throw new ArgumentOutOfRangeException(
                nameof(maxConcurrentWorkers),
                "MaxConcurrentWorkers must be >= 1");
        return Math.Max(1, (maxConcurrentWorkers * 3 + 1) / 2);
    }

    public static OrchestratorOptions Build(
        int? legacyConcurrency,
        WorkerPoolOptions workerPool,
        bool autoRetryEnabled,
        string autoRetryPeriodicInterval,
        string autoRetryDriftMargin,
        int autoRetryMaxRetries,
        ILogger log)
    {
        var options = Build(legacyConcurrency, workerPool, log);

        options = options with
        {
            AutoRetryOnQuotaFailure = BuildAutoRetryOptions(
                autoRetryEnabled,
                autoRetryPeriodicInterval,
                autoRetryDriftMargin,
                autoRetryMaxRetries)
        };

        return options;
    }

    public static AutoRetryOnQuotaFailureOptions BuildAutoRetryOptions(
        bool enabled,
        string periodicCheckInterval,
        string clockDriftMargin,
        int maxRetriesPerWorkItem)
    {
        if (!enabled)
            return new AutoRetryOnQuotaFailureOptions { Enabled = false };

        if (!TimeSpan.TryParse(periodicCheckInterval, out TimeSpan periodic))
            throw new InvalidOperationException("CodeyBox:AutoRetryOnQuotaFailure:PeriodicCheckInterval must be a valid TimeSpan (e.g. '01:00:00')");
        if (periodic <= TimeSpan.Zero)
            throw new InvalidOperationException("CodeyBox:AutoRetryOnQuotaFailure:PeriodicCheckInterval must be positive");

        if (!TimeSpan.TryParse(clockDriftMargin, out TimeSpan drift))
            throw new InvalidOperationException("CodeyBox:AutoRetryOnQuotaFailure:ClockDriftSafetyMargin must be a valid TimeSpan (e.g. '00:02:00')");
        if (drift < TimeSpan.Zero)
            throw new InvalidOperationException("CodeyBox:AutoRetryOnQuotaFailure:ClockDriftSafetyMargin must be non-negative");

        if (maxRetriesPerWorkItem < 0)
            throw new InvalidOperationException("CodeyBox:AutoRetryOnQuotaFailure:MaxAutoRetriesPerWorkItem must be non-negative");

        return new AutoRetryOnQuotaFailureOptions
        {
            Enabled = true,
            PeriodicCheckInterval = periodic,
            ClockDriftSafetyMargin = drift,
            MaxAutoRetriesPerWorkItem = maxRetriesPerWorkItem,
        };
    }
}
