namespace CodeyBox.Orchestrator;

/// <summary>
/// Process-wide registry of per-branch pickup-rebase locks.
///
/// Extracted from <see cref="PipelineRunner"/> (Phase 0 prep for the pipeline
/// split): the rebase core serializes sandbox force-pushes per
/// <c>repoId:workBranch</c> key. The lock table itself carries no work-item
/// state — it is safe to share across items — so it lives in an injectable
/// singleton instead of process-wide statics on the runner. Production DI
/// registers a single shared instance; tests inject a fresh instance per
/// fixture for isolation. When <see cref="PipelineRunner"/> is constructed
/// without an explicit registry it falls back to <see cref="Shared"/>, which
/// preserves the pre-extraction process-wide semantics exactly.
/// </summary>
public sealed class PickupRebaseLockRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, PickupRebaseGate> _locks = new(StringComparer.Ordinal);

    /// <summary>
    /// Process-wide fallback used when no registry is injected. Preserves the
    /// original static-dictionary behavior for legacy construction paths.
    /// </summary>
    public static PickupRebaseLockRegistry Shared { get; } = new();

    /// <summary>
    /// Retains (or creates) the gate for <paramref name="key"/> and bumps its
    /// reference count. The caller must pair every retain with
    /// <see cref="Release"/> and must hold <see cref="PickupRebaseGate.Semaphore"/>
    /// only between retain and release.
    /// </summary>
    public PickupRebaseGate Retain(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_gate)
        {
            if (!_locks.TryGetValue(key, out var gate))
            {
                gate = new PickupRebaseGate();
                _locks.Add(key, gate);
            }

            gate.ReferenceCount++;
            return gate;
        }
    }

    /// <summary>
    /// Releases a previously retained gate. When <paramref name="releaseSemaphore"/>
    /// is true the semaphore is released first; the entry is removed once its
    /// reference count reaches zero.
    /// </summary>
    public void Release(string key, PickupRebaseGate gate, bool releaseSemaphore)
    {
        ArgumentNullException.ThrowIfNull(gate);
        if (releaseSemaphore)
            gate.Semaphore.Release();

        lock (_gate)
        {
            gate.ReferenceCount--;
            if (gate.ReferenceCount == 0
                && _locks.TryGetValue(key, out var current)
                && ReferenceEquals(current, gate))
            {
                _locks.Remove(key);
            }
        }
    }

    /// <summary>Number of live lock entries. Test seam only.</summary>
    internal int Count
    {
        get
        {
            lock (_gate)
                return _locks.Count;
        }
    }

    /// <summary>Per-branch rebase gate: mutual exclusion plus ref-counting.</summary>
    public sealed class PickupRebaseGate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int ReferenceCount { get; set; }
    }
}
