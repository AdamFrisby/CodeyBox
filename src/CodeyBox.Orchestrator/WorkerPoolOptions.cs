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
    /// Minimum wall-clock interval between two consecutive worker spawns.
    /// Spreads out quota usage and sandbox-launch load. Default 0 (no pacing).
    /// </summary>
    public TimeSpan MinSpawnInterval { get; set; } = TimeSpan.Zero;
}
