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
        ILogger log,
        int autoRetryMaxWaitingForQuotaResetSweepBatchSize =
            AutoRetryOnQuotaFailureOptions.DefaultWaitingForQuotaResetSweepBatchSize)
    {
        var options = Build(legacyConcurrency, workerPool, log);

        options = options with
        {
            AutoRetryOnQuotaFailure = BuildAutoRetryOptions(
                autoRetryEnabled,
                autoRetryPeriodicInterval,
                autoRetryDriftMargin,
                autoRetryMaxRetries,
                autoRetryMaxWaitingForQuotaResetSweepBatchSize)
        };

        return options;
    }

    public static TerminalFailureRecoveryOptions BuildTerminalFailureRecoveryOptions(
        bool enabled,
        string periodicCheckInterval,
        string baseBackoff,
        string maxBackoff,
        double jitterFraction,
        int maxAutoRetriesPerWorkItem)
    {
        if (!enabled)
            return new TerminalFailureRecoveryOptions { Enabled = false };

        if (!TimeSpan.TryParse(periodicCheckInterval, out TimeSpan periodic))
            throw new InvalidOperationException(
                "CodeyBox:TerminalFailureRecovery:PeriodicCheckInterval must be a valid TimeSpan (e.g. '00:05:00')");
        if (periodic <= TimeSpan.Zero)
            throw new InvalidOperationException(
                "CodeyBox:TerminalFailureRecovery:PeriodicCheckInterval must be positive");

        if (!TimeSpan.TryParse(baseBackoff, out TimeSpan baseBack))
            throw new InvalidOperationException(
                "CodeyBox:TerminalFailureRecovery:BaseBackoff must be a valid TimeSpan (e.g. '00:01:00')");
        if (baseBack <= TimeSpan.Zero)
            throw new InvalidOperationException(
                "CodeyBox:TerminalFailureRecovery:BaseBackoff must be positive");

        if (!TimeSpan.TryParse(maxBackoff, out TimeSpan maxBack))
            throw new InvalidOperationException(
                "CodeyBox:TerminalFailureRecovery:MaxBackoff must be a valid TimeSpan (e.g. '00:30:00')");
        if (maxBack < baseBack)
            throw new InvalidOperationException(
                "CodeyBox:TerminalFailureRecovery:MaxBackoff must be >= BaseBackoff");

        if (jitterFraction < 0 || jitterFraction > 1.0)
            throw new InvalidOperationException(
                "CodeyBox:TerminalFailureRecovery:JitterFraction must be between 0 and 1");

        if (maxAutoRetriesPerWorkItem < 0)
            throw new InvalidOperationException(
                "CodeyBox:TerminalFailureRecovery:MaxAutoRetriesPerWorkItem must be non-negative");

        return new TerminalFailureRecoveryOptions
        {
            Enabled = true,
            PeriodicCheckInterval = periodic,
            BaseBackoff = baseBack,
            MaxBackoff = maxBack,
            JitterFraction = jitterFraction,
            MaxAutoRetriesPerWorkItem = maxAutoRetriesPerWorkItem,
        };
    }

    public static AgentRestoreRetryOptions BuildAgentRestoreRetryOptions(
        bool enabled,
        string lookbackGrace,
        string postRestoreMargin,
        string? involvementTerminalLookback = null,
        string? involvementTerminalClockSkew = null,
        int maxCandidatesPerSweep = AgentRestoreRetryOptions.DefaultMaxCandidatesPerSweep,
        int eventQueueCapacity = AgentRestoreRetryOptions.DefaultEventQueueCapacity)
    {
        if (!enabled)
            return new AgentRestoreRetryOptions { Enabled = false };

        var lookback = ParseNonNegativeTimeSpan(
            "CodeyBox:AutoRequeueOnAgentRestore:LookbackGrace",
            lookbackGrace,
            AgentRestoreRetryOptions.DefaultLookbackGraceConfigValue);
        var margin = ParseNonNegativeTimeSpan(
            "CodeyBox:AutoRequeueOnAgentRestore:PostRestoreMargin",
            postRestoreMargin,
            AgentRestoreRetryOptions.DefaultPostRestoreMarginConfigValue);
        var terminalLookback = ParseNonNegativeTimeSpan(
            "CodeyBox:AutoRequeueOnAgentRestore:InvolvementTerminalLookback",
            involvementTerminalLookback ?? AgentRestoreRetryOptions.DefaultInvolvementTerminalLookbackConfigValue,
            AgentRestoreRetryOptions.DefaultInvolvementTerminalLookbackConfigValue);
        var terminalClockSkew = ParseNonNegativeTimeSpan(
            "CodeyBox:AutoRequeueOnAgentRestore:InvolvementTerminalClockSkew",
            involvementTerminalClockSkew ?? AgentRestoreRetryOptions.DefaultInvolvementTerminalClockSkewConfigValue,
            AgentRestoreRetryOptions.DefaultInvolvementTerminalClockSkewConfigValue);

        if (maxCandidatesPerSweep <= 0)
            throw new InvalidOperationException(
                "CodeyBox:AutoRequeueOnAgentRestore:MaxCandidatesPerSweep must be positive");

        if (eventQueueCapacity <= 0)
            throw new InvalidOperationException(
                "CodeyBox:AutoRequeueOnAgentRestore:EventQueueCapacity must be positive");

        return new AgentRestoreRetryOptions
        {
            Enabled = true,
            LookbackGrace = lookback,
            PostRestoreMargin = margin,
            InvolvementTerminalLookback = terminalLookback,
            InvolvementTerminalClockSkew = terminalClockSkew,
            MaxCandidatesPerSweep = maxCandidatesPerSweep,
            EventQueueCapacity = eventQueueCapacity,
        };
    }

    private static TimeSpan ParseNonNegativeTimeSpan(
        string configPath,
        string? rawValue,
        string exampleValue)
    {
        if (!TimeSpan.TryParse(rawValue, System.Globalization.CultureInfo.InvariantCulture, out TimeSpan value))
            throw new InvalidOperationException(
                $"{configPath} must be a valid TimeSpan (e.g. '{exampleValue}')");
        if (value < TimeSpan.Zero)
            throw new InvalidOperationException(
                $"{configPath} must be non-negative");
        return value;
    }

    public static AutoRetryOnQuotaFailureOptions BuildAutoRetryOptions(
        bool enabled,
        string periodicCheckInterval,
        string clockDriftMargin,
        int maxRetriesPerWorkItem,
        int maxWaitingForQuotaResetSweepBatchSize =
            AutoRetryOnQuotaFailureOptions.DefaultWaitingForQuotaResetSweepBatchSize)
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
        if (maxWaitingForQuotaResetSweepBatchSize <= 0)
            throw new InvalidOperationException("CodeyBox:AutoRetryOnQuotaFailure:MaxWaitingForQuotaResetSweepBatchSize must be positive");

        return new AutoRetryOnQuotaFailureOptions
        {
            Enabled = true,
            PeriodicCheckInterval = periodic,
            ClockDriftSafetyMargin = drift,
            MaxAutoRetriesPerWorkItem = maxRetriesPerWorkItem,
            MaxWaitingForQuotaResetSweepBatchSize = maxWaitingForQuotaResetSweepBatchSize,
        };
    }

    public static AutoRetryOnTransientFailureOptions BuildTransientRetryOptions(
        bool enabled,
        string periodicCheckInterval,
        string baseDelay,
        double multiplier,
        string maxDelay,
        int maxRetriesPerWorkItem,
        string maxElapsedTime,
        string jitterMode)
    {
        if (!enabled)
            return new AutoRetryOnTransientFailureOptions { Enabled = false };

        var periodic = ParseTimeSpan(
            periodicCheckInterval,
            "CodeyBox:AutoRetryOnTransientFailure:PeriodicCheckInterval",
            requirePositive: true);
        var parsedBaseDelay = ParseTimeSpan(
            baseDelay,
            "CodeyBox:AutoRetryOnTransientFailure:BaseDelay",
            requirePositive: true);
        var parsedMaxDelay = ParseTimeSpan(
            maxDelay,
            "CodeyBox:AutoRetryOnTransientFailure:MaxDelay",
            requirePositive: true);
        var parsedMaxElapsed = ParseTimeSpan(
            maxElapsedTime,
            "CodeyBox:AutoRetryOnTransientFailure:MaxElapsedTime",
            requirePositive: true);

        if (multiplier < 1.0 || double.IsNaN(multiplier) || double.IsInfinity(multiplier))
            throw new InvalidOperationException("CodeyBox:AutoRetryOnTransientFailure:Multiplier must be >= 1");
        if (parsedMaxDelay < parsedBaseDelay)
            throw new InvalidOperationException("CodeyBox:AutoRetryOnTransientFailure:MaxDelay must be >= BaseDelay");
        if (maxRetriesPerWorkItem < 0)
            throw new InvalidOperationException("CodeyBox:AutoRetryOnTransientFailure:MaxAutoRetriesPerWorkItem must be non-negative");
        if (!Enum.TryParse<TransientRetryJitterMode>(jitterMode, ignoreCase: true, out var parsedJitter))
            throw new InvalidOperationException("CodeyBox:AutoRetryOnTransientFailure:JitterMode must be one of: None, Full, Decorrelated");

        return new AutoRetryOnTransientFailureOptions
        {
            Enabled = true,
            PeriodicCheckInterval = periodic,
            BaseDelay = parsedBaseDelay,
            Multiplier = multiplier,
            MaxDelay = parsedMaxDelay,
            MaxAutoRetriesPerWorkItem = maxRetriesPerWorkItem,
            MaxElapsedTime = parsedMaxElapsed,
            JitterMode = parsedJitter,
        };
    }

    private static TimeSpan ParseTimeSpan(string value, string key, bool requirePositive)
    {
        if (!TimeSpan.TryParse(value, out var parsed))
            throw new InvalidOperationException($"{key} must be a valid TimeSpan (e.g. '00:00:30')");
        if (requirePositive && parsed <= TimeSpan.Zero)
            throw new InvalidOperationException($"{key} must be positive");
        if (!requirePositive && parsed < TimeSpan.Zero)
            throw new InvalidOperationException($"{key} must be non-negative");
        return parsed;
    }
}
