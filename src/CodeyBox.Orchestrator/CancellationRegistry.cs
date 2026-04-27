using System.Collections.Concurrent;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Per-work-item cancellation tokens. The pipeline registers a CTS when it
/// starts work; the API DELETE endpoint cancels it. The CTS is linked to
/// the orchestrator's stopping token so process shutdown still cascades.
/// </summary>
public sealed class CancellationRegistry : IDisposable
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _ctsById = new();
    private readonly CancellationToken _root;
    private bool _disposed;

    public CancellationRegistry(CancellationToken root)
    {
        _root = root;
    }

    /// <summary>
    /// Creates and registers a CTS for this work item. Caller must dispose
    /// the returned <see cref="Registration"/> when the work item finishes.
    /// </summary>
    public Registration Register(WorkItemId id)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_root);
        if (!_ctsById.TryAdd(id.Value, cts))
        {
            cts.Dispose();
            throw new InvalidOperationException($"Work item {id} is already registered");
        }
        return new Registration(this, id, cts);
    }

    /// <summary>Returns true if a token was found and cancelled.</summary>
    public bool Cancel(WorkItemId id)
    {
        if (_ctsById.TryGetValue(id.Value, out var cts))
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { /* races with completion */ }
            return true;
        }
        return false;
    }

    public bool IsActive(WorkItemId id) => _ctsById.ContainsKey(id.Value);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var cts in _ctsById.Values)
        {
            try { cts.Dispose(); } catch { /* best-effort */ }
        }
        _ctsById.Clear();
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
            Source.Dispose();
        }
    }
}
