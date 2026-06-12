using System.Collections.Concurrent;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

public enum CancellationRequestKind
{
    Operator,
    Recovery,
}

/// <summary>
/// Per-work-item cancellation tokens. The pipeline registers a CTS when it
/// starts work; the API DELETE endpoint cancels it.
///
/// Host shutdown is intentionally not linked here. The pipeline receives the
/// host stopping token separately so it can preempt or drain the active phase
/// instead of treating SIGTERM as an operator-requested item cancellation.
/// </summary>
public sealed class CancellationRegistry : IDisposable
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _ctsById = new();
    private readonly ConcurrentDictionary<Guid, CancellationRequestKind> _requestKindById = new();
    private bool _disposed;

    public CancellationRegistry(CancellationToken root = default)
    {
        _ = root;
    }

    /// <summary>
    /// Creates and registers a CTS for this work item. Caller must dispose
    /// the returned <see cref="Registration"/> when the work item finishes.
    /// </summary>
    public Registration Register(WorkItemId id)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var cts = new CancellationTokenSource();
        if (!_ctsById.TryAdd(id.Value, cts))
        {
            cts.Dispose();
            throw new InvalidOperationException($"Work item {id} is already registered");
        }
        return new Registration(this, id, cts);
    }

    /// <summary>Returns true if a token was found and cancelled for operator-requested cancellation.</summary>
    public bool Cancel(WorkItemId id) => Cancel(id, CancellationRequestKind.Operator);

    /// <summary>Returns true if a token was found and cancelled for recovery-owned worker abort.</summary>
    public bool CancelForRecovery(WorkItemId id) => Cancel(id, CancellationRequestKind.Recovery);

    private bool Cancel(WorkItemId id, CancellationRequestKind kind)
    {
        if (_ctsById.TryGetValue(id.Value, out var cts))
        {
            _requestKindById[id.Value] = kind;
            try { cts.Cancel(); } catch (ObjectDisposedException) { /* races with completion */ }
            return true;
        }
        return false;
    }

    public bool IsActive(WorkItemId id) => _ctsById.ContainsKey(id.Value);

    public CancellationRequestKind? GetRequestKind(WorkItemId id) =>
        _requestKindById.TryGetValue(id.Value, out var kind) ? kind : null;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var cts in _ctsById.Values)
        {
            try { cts.Dispose(); } catch { /* best-effort */ }
        }
        _ctsById.Clear();
        _requestKindById.Clear();
    }

    public sealed class Registration : IDisposable
    {
        private readonly CancellationRegistry _registry;
        private readonly WorkItemId _id;
        public CancellationTokenSource Source { get; }
        public CancellationToken Token => Source.Token;

        internal Registration(CancellationRegistry registry, WorkItemId id, CancellationTokenSource cts)
        {
            _registry = registry;
            _id = id;
            Source = cts;
        }

        public void Dispose()
        {
            _registry._ctsById.TryRemove(_id.Value, out _);
            _registry._requestKindById.TryRemove(_id.Value, out _);
            Source.Dispose();
        }
    }
}
