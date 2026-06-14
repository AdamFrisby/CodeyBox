namespace CodeyBox.Orchestrator;

/// <summary>
/// Pipeline transition-health tuning. Bound from <c>CodeyBox:TransitionHealth</c>
/// in config and applied through <see cref="TransitionHealthOptionsSnapshot"/>
/// so the rolling window can be tuned without a process restart.
///
/// <para>
/// The transition-health score measures INFRASTRUCTURE health independently of
/// work throughput. Done-rate conflates plumbing health with work difficulty,
/// quota state, and concurrency throttling; this metric isolates plumbing
/// health by classifying every stage transition as LEGITIMATE forward progress
/// (or genuine-finding rework, which is the audit loop working as designed) or
/// as an INFRA failure (auditor failing to run, agent transport crash,
/// quota-exhaustion mid-run, terminal infra-failed / MergeConflictResolutionFailed,
/// …).
/// </para>
/// </summary>
public sealed record TransitionHealthOptions
{
    /// <summary>
    /// Enable the <c>/fleet/transition-health</c> endpoint and computation.
    /// Default true. Operators on offline-only deployments can set this to
    /// false to short-circuit data-source reads.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Rolling window over which transitions are aggregated. Default 24 h.
    /// Floor 5 minutes, ceiling 30 days; values outside the range are clamped
    /// at <see cref="TransitionHealthConfigMapper.ToOptions"/> binding time.
    /// </summary>
    public TimeSpan Window { get; init; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Optional cap on the number of transitions considered. When set, the
    /// most recent <c>MaxTransitions</c> stage transitions (ordered by their
    /// completion timestamp) are scored regardless of how long ago they
    /// happened. Setting this surfaces "last N transitions" semantics for
    /// operators who want a fixed-size sample rather than a wall-clock
    /// window. Null = use the window only. Floor 50, ceiling 100_000.
    /// </summary>
    public int? MaxTransitions { get; init; }
}

/// <summary>
/// Shared, swappable holder for the current <see cref="TransitionHealthOptions"/>.
/// Registered as a DI singleton so the read endpoint reads through the same
/// reference the hot-reload coordinator writes to.
/// </summary>
public sealed class TransitionHealthOptionsSnapshot
{
    private TransitionHealthOptions _current;

    public TransitionHealthOptionsSnapshot(TransitionHealthOptions initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _current = initial;
    }

    /// <summary>
    /// Current snapshot. Volatile read so a concurrent <see cref="Replace"/>
    /// cannot tear the reference. Callers should bind once into a local for
    /// any compound read.
    /// </summary>
    public TransitionHealthOptions Current => Volatile.Read(ref _current);

    public bool Enabled => Current.Enabled;

    /// <summary>
    /// Atomically publishes <paramref name="next"/> as the new snapshot.
    /// </summary>
    public void Replace(TransitionHealthOptions next)
    {
        ArgumentNullException.ThrowIfNull(next);
        Volatile.Write(ref _current, next);
    }
}
