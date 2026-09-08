namespace CodeyBox.Orchestrator;

/// <summary>
/// Controls the size and spawn pacing of the orchestrator worker pool.
/// Bind under <c>CodeyBox:WorkerPool</c>.
/// </summary>
public sealed class WorkerPoolOptions
{
    /// <summary>
    /// Maximum number of work items that can run concurrently.
    /// When unset (<see langword="null"/>), falls back to deprecated
    /// <c>CodeyBox:Concurrency</c> if present, otherwise <c>1</c>.
    /// When set, takes precedence over <c>CodeyBox:Concurrency</c>.
    /// </summary>
    public int? MaxConcurrentWorkers { get; set; }

    /// <summary>
    /// Maximum number of live sandbox instances this process may hold at once.
    /// When unset, defaults to <c>ceil(MaxConcurrentWorkers * 1.5)</c> so the
    /// worker pool has a small audit/merge headroom without letting per-item
    /// fan-out multiply into an unbounded VM count. Captured at startup; restart
    /// CodeyBox to resize the live admission gate.
    /// </summary>
    public int? MaxConcurrentSandboxes { get; set; }

    /// <summary>
    /// Minimum wall-clock interval between two consecutive worker spawns.
    /// Spreads out quota usage and sandbox-launch load. Default 0 (no pacing).
    /// </summary>
    public TimeSpan MinSpawnInterval { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Backoff between dispatch-pickup attempts after a SQLite write-gate
    /// acquisition timeout. A single timeout is transient (holder momentarily
    /// slow), so the loop logs at Warning and retries after this delay
    /// instead of faulting the host. Must be non-negative and under 1 hour.
    /// Default 1 second.
    /// </summary>
    public TimeSpan DispatchGateAcquisitionBackoff { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Consecutive pickup gate-acquisition timeouts before the dispatch loop
    /// escalates with <c>SqliteWriteGatePersistentlyUnavailableException</c>
    /// (fatal: stops the host with a non-zero exit so a supervisor restarts).
    /// Must be at least 1. Default 10.
    /// </summary>
    public int MaxConsecutiveDispatchGateTimeoutsBeforeEscalation { get; set; } = 10;
}
