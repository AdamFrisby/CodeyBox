namespace CodeyBox.Orchestrator;

/// <summary>
/// Tuning knobs for the heartbeat / dead-worker-reaper subsystem.
/// Bind under <c>CodeyBox:DeadWorker</c>.
/// </summary>
public sealed class DeadWorkerOptions
{
    /// <summary>How often each worker slot writes a fresh heartbeat. Default 15 s.</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// A worker whose <c>last_heartbeat_at</c> is older than this threshold is
    /// presumed dead. Must be ≥ 3× <see cref="HeartbeatInterval"/> to avoid
    /// false positives from a worker that is merely slow to heartbeat.
    /// Default 90 s.
    /// </summary>
    public TimeSpan DeadWorkerThreshold { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>How often the periodic reaper sweep runs. Default 60 s.</summary>
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Maximum number of automatic recovery transitions for a single work item
    /// before the reaper gives up and transitions it to
    /// <c>AbandonedAfterRecoveryAttempts</c>. Default 10.
    /// Pairs with <see cref="OrchestratorOptions.MaxRecoveryAttempts"/> (the
    /// startup-replay counterpart) — both default to 10 so a healthy work item
    /// survives routine operator activity (config tweaks, restarts) without
    /// burning its recovery budget. 2 was too tight in practice: a long-running
    /// audit could be interrupted by a single restart, recovered once, then
    /// interrupted again and abandoned even though the work was fine.
    /// </summary>
    public int MaxRecoveryAttempts { get; set; } = 10;

    /// <summary>
    /// Maximum number of CONSECUTIVE infrastructure-caused requeues (worker
    /// death without a preempt checkpoint) for a single work item before the
    /// reaper parks it at <c>NeedsOperatorInput</c> for triage instead of
    /// requeueing. Default 20. Infrastructure requeues never consume
    /// <see cref="MaxRecoveryAttempts"/>, so without this separate bound a
    /// poison input that deterministically kills every worker would retry
    /// forever; the counter resets whenever a phase completes or an operator
    /// retries/resumes the item, so routine restarts never approach the cap.
    /// Pairs with <see cref="OrchestratorOptions.MaxConsecutiveInfrastructureRecoveries"/>
    /// (the startup-replay / shutdown-recovery counterpart).
    /// Set to 0 (or any negative value) to disable the bound and requeue
    /// indefinitely (not recommended in production).
    /// </summary>
    public int MaxConsecutiveInfrastructureRecoveries { get; set; } = 20;

    /// <summary>
    /// Validates that the threshold is large enough to avoid false positives.
    /// Throws <see cref="InvalidOperationException"/> on misconfiguration.
    /// </summary>
    public void Validate()
    {
        if (DeadWorkerThreshold < 3 * HeartbeatInterval)
            throw new InvalidOperationException(
                $"CodeyBox:DeadWorker:DeadWorkerThreshold ({DeadWorkerThreshold.TotalSeconds}s) " +
                $"must be >= 3 × HeartbeatInterval ({HeartbeatInterval.TotalSeconds}s). " +
                $"Increase the threshold or decrease the heartbeat interval to avoid false-positive reaping.");
    }
}
