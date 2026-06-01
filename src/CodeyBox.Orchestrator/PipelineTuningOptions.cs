namespace CodeyBox.Orchestrator;

/// <summary>
/// Operational tuning knobs consumed by <see cref="PipelineRunner"/>'s
/// quota-fallback and merge-staging retry paths. Bound from
/// <c>CodeyBox:PipelineTuning</c> and hot-reloaded via
/// <see cref="PipelineTuningSnapshot"/>.
/// </summary>
public sealed class PipelineTuningOptions
{
    /// <summary>
    /// Last-resort pause applied when a quota-shaped terminal failure occurs and
    /// neither the agent output nor quota probes expose a reset window.
    /// Default 5 minutes.
    /// </summary>
    public TimeSpan DefaultQuotaFailurePause { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Per-process exhausted-member TTL when the chosen agent hits quota
    /// mid-flight. Subscription windows reset on the order of hours; one hour
    /// is a conservative upper bound that keeps the in-process cache useful
    /// across consecutive pickups without blocking long enough to delay an
    /// actual reset by a meaningful amount. Default 1 hour.
    /// </summary>
    public TimeSpan QuotaExhaustionFallbackTtl { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Upper bound for parsed reset-window hints extracted from an agent's
    /// stdout/stderr. Without a cap, a maliciously-crafted Retry-After header
    /// could park an item arbitrarily far in the future. Default 24 hours.
    /// </summary>
    public TimeSpan MaxParsedQuotaResetWindow { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Maximum attempts to restore a missing staging clone before rethrowing
    /// during merge-sandbox creation. Default 2. One retry is the production
    /// heal contract — if the source disappears AGAIN after restore, the loop
    /// falls through to rethrow rather than spinning indefinitely.
    /// </summary>
    public int MergeSandboxStagingRestoreAttempts { get; set; } = 2;
}

/// <summary>
/// Shared, swappable holder for the current <see cref="PipelineTuningOptions"/>.
/// Registered as a DI singleton so <see cref="PipelineRunner"/> reads through
/// the same reference the hot-reload coordinator writes to.
/// Mirrors the <see cref="AgentConcurrencySnapshot"/> pattern.
/// </summary>
public sealed class PipelineTuningSnapshot
{
    private PipelineTuningOptions _current;

    public PipelineTuningSnapshot(PipelineTuningOptions initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _current = initial;
    }

    /// <summary>
    /// Current snapshot. Volatile read so a concurrent <see cref="Replace"/>
    /// cannot tear the reference. Callers should bind once into a local for
    /// any compound read.
    /// </summary>
    public PipelineTuningOptions Current => Volatile.Read(ref _current);

    /// <summary>
    /// Atomically publishes <paramref name="next"/> as the new snapshot.
    /// In-flight reads observe either the old or the new reference, never a
    /// partial state.
    /// </summary>
    public void Replace(PipelineTuningOptions next)
    {
        ArgumentNullException.ThrowIfNull(next);
        Volatile.Write(ref _current, next);
    }
}
