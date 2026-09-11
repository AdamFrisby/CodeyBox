using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Outcome of an executor reconnect reconciliation: how many tracked sandboxes
/// were retained for resumption and how many untracked ones were torn down.
/// </summary>
public sealed record ExecutorReconnectOutcome(
    int RetainedCount,
    int ReclaimedCount,
    bool InventoryComplete);

/// <summary>
/// Tracks the sandboxes an executor host provisioned locally and implements
/// the <see cref="ExecutorDisconnectPolicy.RetainSandboxForResume"/> outcome:
/// losing the orchestrator connection never disposes a running sandbox, and
/// re-establishing it reconciles provider inventory against the tracked set
/// so no sandbox keeps running that nothing tracks.
///
/// <para>Thread-safe. All mutations hold a single lock, so concurrent phase
/// completions cannot corrupt, lose, or cross-contaminate tracked entries.
/// Disposal of reclaimed orphans goes through the provider lifecycle
/// (<see cref="IManagedSandboxLifecycle"/>) by snapshot, never through a
/// handle the tracker does not own.</para>
/// </summary>
public sealed class ExecutorSandboxTracker : IAsyncDisposable
{
    private readonly object _mutex = new();
    private readonly Dictionary<string, TrackedSandbox> _tracked = new(StringComparer.Ordinal);
    private bool _connectionLost;
    private bool _disposed;

    private sealed record TrackedSandbox(string SandboxName, ISandbox Handle);

    /// <summary>Number of sandboxes currently tracked.</summary>
    public int TrackedCount
    {
        get { lock (_mutex) return _tracked.Count; }
    }

    /// <summary>True after <see cref="MarkConnectionLost"/> until the next successful reconcile.</summary>
    public bool IsConnectionLost
    {
        get { lock (_mutex) return _connectionLost; }
    }

    /// <summary>
    /// Binds a locally provisioned sandbox to its phase. The sandbox name is
    /// bound explicitly (rather than derived from the handle) because a
    /// provider's live-handle id and its inventory snapshot name live in
    /// different namespaces on some backends. Throws
    /// <see cref="InvalidOperationException"/> on a duplicate phase id so a
    /// double-track can never silently replace (and leak) a live handle.
    /// </summary>
    public void Track(string phaseId, string sandboxName, ISandbox sandbox)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phaseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxName);
        ArgumentNullException.ThrowIfNull(sandbox);
        lock (_mutex)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_tracked.TryAdd(phaseId, new TrackedSandbox(sandboxName.Trim(), sandbox)))
                throw new InvalidOperationException($"Phase '{phaseId}' is already tracked; refusing to replace a live sandbox handle.");
        }
    }

    /// <summary>
    /// Releases a phase binding after its sandbox was torn down normally.
    /// Returns false when the phase was not tracked. Never disposes: the
    /// caller owns normal-path teardown.
    /// </summary>
    public bool TryUntrack(string phaseId, out ISandbox? sandbox)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phaseId);
        lock (_mutex)
        {
            if (_tracked.Remove(phaseId, out var tracked))
            {
                sandbox = tracked.Handle;
                return true;
            }
            sandbox = null;
            return false;
        }
    }

    /// <summary>
    /// Records connection loss and returns the retained phase ids. Disposes
    /// nothing: retained sandboxes stay alive for resumption. Idempotent.
    /// </summary>
    public IReadOnlyList<string> MarkConnectionLost()
    {
        lock (_mutex)
        {
            _connectionLost = true;
            return [.. _tracked.Keys];
        }
    }

    /// <summary>
    /// Reconciles provider inventory against the tracked set after the
    /// connection is re-established. Every inventoried sandbox whose name
    /// matches no tracked phase is torn down through the lifecycle so nothing
    /// runs untracked; tracked sandboxes are retained for resumption. When the
    /// inventory reports itself incomplete, nothing is reclaimed — disposing
    /// by name against a partial view could kill sandboxes that are tracked
    /// on an uninventoried host.
    /// </summary>
    public async Task<ExecutorReconnectOutcome> ReconcileOnReconnectAsync(
        IManagedSandboxLifecycle lifecycle,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        var inventory = await lifecycle.ListManagedInventoryAsync(ct).ConfigureAwait(false);
        HashSet<string> trackedNames;
        lock (_mutex)
        {
            trackedNames = new HashSet<string>(_tracked.Values.Select(t => t.SandboxName), StringComparer.Ordinal);
        }

        if (!inventory.IsComplete)
        {
            lock (_mutex) _connectionLost = false;
            return new ExecutorReconnectOutcome(trackedNames.Count, 0, InventoryComplete: false);
        }

        var reclaimed = 0;
        foreach (var snapshot in inventory)
        {
            ct.ThrowIfCancellationRequested();
            if (trackedNames.Contains(snapshot.Name))
                continue;
            await lifecycle.DisposeLeakedAsync(snapshot, ct).ConfigureAwait(false);
            reclaimed++;
        }

        int retained;
        lock (_mutex)
        {
            _connectionLost = false;
            retained = _tracked.Count;
        }
        return new ExecutorReconnectOutcome(retained, reclaimed, InventoryComplete: true);
    }

    /// <summary>
    /// Graceful shutdown: tears down every tracked sandbox. Connection-loss
    /// retention deliberately does not apply here — the process is exiting, so
    /// there is nothing left that could resume them.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        List<ISandbox> owned;
        lock (_mutex)
        {
            if (_disposed)
                return;
            _disposed = true;
            owned = _tracked.Values.Select(t => t.Handle).ToList();
            _tracked.Clear();
            _connectionLost = false;
        }
        foreach (var sandbox in owned)
            await sandbox.DisposeAsync().ConfigureAwait(false);
    }
}
