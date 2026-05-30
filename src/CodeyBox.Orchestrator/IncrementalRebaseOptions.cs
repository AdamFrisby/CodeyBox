namespace CodeyBox.Orchestrator;

/// <summary>
/// Controls the incremental rebase that <see cref="PipelineRunner"/> may run
/// between audit iterations to keep a long-lived work branch close to
/// <c>baseBranch</c>. Bind under <c>CodeyBox:IncrementalRebase</c>.
///
/// <para>
/// The incremental rebase is best-effort: any failure (clone, fetch,
/// resolver routing, security review, push) is logged at warning and the
/// next rework dispatch proceeds against the un-rebased branch. The
/// merge-time rebase that runs at pickup is unchanged and remains the
/// authoritative pre-merge consolidation.
/// </para>
///
/// <para>
/// Default <see cref="Enabled"/>=<c>false</c>: a long-lived feature that
/// touches the merge pipeline ships dark and is turned on per-operator,
/// matching the existing convention used by <see cref="AutoRetryOnQuotaFailureOptions"/>
/// and other merge-adjacent knobs.
/// </para>
/// </summary>
public sealed class IncrementalRebaseOptions
{
    /// <summary>
    /// Master switch. When <c>false</c>, <c>MaybeIncrementalRebaseAsync</c> is
    /// a no-op and the pipeline behaves exactly as before this option was
    /// introduced. Hot-reloadable via <see cref="IncrementalRebaseSnapshot"/>.
    /// </summary>
    public bool Enabled { get; set; } = false;
}

/// <summary>
/// Shared, swappable holder for the current <see cref="IncrementalRebaseOptions"/>.
/// Registered as a DI singleton so <see cref="PipelineRunner"/> reads
/// through the same reference the hot-reload coordinator writes to.
/// Mirrors the <see cref="AgentConcurrencySnapshot"/> pattern.
/// </summary>
public sealed class IncrementalRebaseSnapshot
{
    private IncrementalRebaseOptions _current;

    public IncrementalRebaseSnapshot(IncrementalRebaseOptions initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _current = initial;
    }

    /// <summary>
    /// Current snapshot. Volatile read so a concurrent <see cref="Replace"/>
    /// cannot tear the reference. Callers should bind once into a local for
    /// any compound read.
    /// </summary>
    public IncrementalRebaseOptions Current => Volatile.Read(ref _current);

    /// <summary>
    /// Atomically publishes <paramref name="next"/> as the new snapshot.
    /// In-flight reads observe either the old or the new reference, never a
    /// partial state.
    /// </summary>
    public void Replace(IncrementalRebaseOptions next)
    {
        ArgumentNullException.ThrowIfNull(next);
        Volatile.Write(ref _current, next);
    }
}
