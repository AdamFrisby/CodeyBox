namespace CodeyBox.Orchestrator;

/// <summary>
/// Tuning knobs for the dispatcher-level worker-pool watchdog.
/// Bind under <c>CodeyBox:WorkerPoolHealthWatchdog</c>. The hosted service
/// reads the current value on every sweep, so timeout and recovery settings are
/// hot-reloadable without restarting the orchestrator.
/// </summary>
public sealed class WorkerPoolHealthWatchdogOptions
{
    /// <summary>
    /// Wall-clock window for which the pool may remain under-filled while
    /// runnable work and an available agent exist before the watchdog fires.
    /// Default 10 min. Set to <see cref="TimeSpan.Zero"/> to disable the
    /// watchdog.
    /// </summary>
    public TimeSpan StallTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>How often the watchdog evaluates pool health. Default 60 s.</summary>
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Maximum number of self-recovery attempts while the same stuck condition
    /// remains active before emitting an operator-restart-required escalation.
    /// Default 2.
    /// </summary>
    public int MaxRecoveryAttempts { get; set; } = 2;

    /// <summary>
    /// Maximum number of runnable candidate IDs to enqueue on each recovery
    /// attempt. Default 32 to keep the recovery bounded while still filling
    /// typical pools in one pass.
    /// </summary>
    public int MaxRecoveryEnqueueBatchSize { get; set; } = 32;

    /// <summary>
    /// Maximum number of persisted candidates to inspect while deciding whether
    /// runnable work exists. Kept separate from the recovery enqueue batch so
    /// a small recovery kick cannot hide lower-priority routable work behind
    /// unroutable top-priority items.
    /// </summary>
    public int MaxHealthCheckCandidateScan { get; set; } = 256;

    /// <summary>
    /// Delay after a recovery attempt before the watchdog re-evaluates the pool
    /// to decide whether escalation is needed. Default 5 s.
    /// </summary>
    public TimeSpan RecoveryVerificationDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Validates configured values. Throws <see cref="InvalidOperationException"/>
    /// on misconfiguration.
    /// </summary>
    public void Validate()
    {
        if (StallTimeout < TimeSpan.Zero)
            throw new InvalidOperationException(
                $"CodeyBox:WorkerPoolHealthWatchdog:StallTimeout ({StallTimeout}) must be >= 0.");

        if (CheckInterval <= TimeSpan.Zero)
            throw new InvalidOperationException(
                $"CodeyBox:WorkerPoolHealthWatchdog:CheckInterval ({CheckInterval}) must be > 0.");

        if (StallTimeout > TimeSpan.Zero && StallTimeout < CheckInterval)
            throw new InvalidOperationException(
                $"CodeyBox:WorkerPoolHealthWatchdog:StallTimeout ({StallTimeout.TotalSeconds}s) must be >= CheckInterval ({CheckInterval.TotalSeconds}s) " +
                "so a tick can observe at least one full stuck window before tripping.");

        if (MaxRecoveryAttempts < 0)
            throw new InvalidOperationException(
                $"CodeyBox:WorkerPoolHealthWatchdog:MaxRecoveryAttempts ({MaxRecoveryAttempts}) must be >= 0.");

        if (MaxRecoveryEnqueueBatchSize <= 0)
            throw new InvalidOperationException(
                $"CodeyBox:WorkerPoolHealthWatchdog:MaxRecoveryEnqueueBatchSize ({MaxRecoveryEnqueueBatchSize}) must be > 0.");

        if (MaxHealthCheckCandidateScan <= 0)
            throw new InvalidOperationException(
                $"CodeyBox:WorkerPoolHealthWatchdog:MaxHealthCheckCandidateScan ({MaxHealthCheckCandidateScan}) must be > 0.");

        if (RecoveryVerificationDelay < TimeSpan.Zero)
            throw new InvalidOperationException(
                $"CodeyBox:WorkerPoolHealthWatchdog:RecoveryVerificationDelay ({RecoveryVerificationDelay}) must be >= 0.");
    }
}
