namespace CodeyBox.Orchestrator;

/// <summary>
/// Process-wide registry of per-base-branch merge-landing gates.
///
/// Upstream merges into the same base branch must land one at a time: two
/// items completing into the same forge base concurrently would otherwise
/// race, and the loser observes the base move mid-merge and burns its
/// retry budget in auto-merge race recovery instead of landing cleanly.
/// The gate is keyed by the <em>shared</em> upstream identity
/// (forge kind + repository URL + base branch) — never by the per-item
/// bare-repo id — so every item landing into the same upstream base
/// contends on the same slot.
///
/// This registry is shared between the upstream landing path
/// (<c>PipelineRunner.UpstreamPush</c>) and merge-result verification, so
/// both observe the same mutual exclusion for a given base branch. The
/// lock table itself carries no work-item state — it is safe to share
/// across items — so it lives in an injectable singleton instead of
/// process-wide statics on the runner. Production DI shares one instance
/// process-wide (the <see cref="Shared"/> fallback preserves that when no
/// registry is injected); tests inject a fresh instance per fixture for
/// isolation. Mirrors <see cref="PickupRebaseLockRegistry"/>.
/// </summary>
public sealed class MergeLandingGate
{
    private readonly object _gate = new();
    private readonly Dictionary<string, MergeLandingSlot> _locks = new(StringComparer.Ordinal);

    /// <summary>
    /// Process-wide fallback used when no registry is injected. Preserves
    /// process-wide queue semantics for legacy construction paths.
    /// </summary>
    public static MergeLandingGate Shared { get; } = new();

    /// <summary>
    /// Canonical lock key for landing into a shared upstream base branch.
    /// All three parts identify the contention domain: the forge kind and
    /// repository URL pin the shared remote, the base branch pins the
    /// ref within it. Two items landing into the same upstream base —
    /// regardless of work item, work branch, or local bare repo — receive
    /// the same key and land in order.
    /// </summary>
    public static string KeyFor(string upstreamKind, string repositoryUrl, string baseBranch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(upstreamKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseBranch);
        return $"{upstreamKind}::{repositoryUrl}::{baseBranch}";
    }

    /// <summary>
    /// Retains (or creates) the slot for <paramref name="key"/> and bumps
    /// its reference count. The caller must pair every retain with
    /// <see cref="Release"/> and must hold
    /// <see cref="MergeLandingSlot.Semaphore"/> only between retain and
    /// release.
    /// </summary>
    public MergeLandingSlot Retain(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_gate)
        {
            if (!_locks.TryGetValue(key, out var slot))
            {
                slot = new MergeLandingSlot();
                _locks.Add(key, slot);
            }

            slot.ReferenceCount++;
            return slot;
        }
    }

    /// <summary>
    /// Releases a previously retained slot. When
    /// <paramref name="releaseSemaphore"/> is true the semaphore is
    /// released first; the entry is removed once its reference count
    /// reaches zero.
    /// </summary>
    public void Release(string key, MergeLandingSlot slot, bool releaseSemaphore)
    {
        ArgumentNullException.ThrowIfNull(slot);
        if (releaseSemaphore)
            slot.Semaphore.Release();

        lock (_gate)
        {
            slot.ReferenceCount--;
            if (slot.ReferenceCount == 0
                && _locks.TryGetValue(key, out var current)
                && ReferenceEquals(current, slot))
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

    /// <summary>Per-base-branch landing slot: mutual exclusion plus ref-counting.</summary>
    public sealed class MergeLandingSlot
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int ReferenceCount { get; internal set; }
    }
}
