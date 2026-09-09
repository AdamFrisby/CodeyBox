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
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _ctsById = new();
    private readonly ConcurrentDictionary<Guid, CancellationRequestKind> _requestKindById = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _completionById = new();
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
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_ctsById.ContainsKey(id.Value) || _completionById.ContainsKey(id.Value))
                throw new InvalidOperationException($"Work item {id} is already registered");

            var cts = new CancellationTokenSource();
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_completionById.TryAdd(id.Value, completion)
                || !_ctsById.TryAdd(id.Value, cts))
            {
                _completionById.TryRemove(
                    new KeyValuePair<Guid, TaskCompletionSource>(id.Value, completion));
                _ctsById.TryRemove(
                    new KeyValuePair<Guid, CancellationTokenSource>(id.Value, cts));
                completion.TrySetResult();
                cts.Dispose();
                throw new InvalidOperationException($"Work item {id} is already registered");
            }
            return new Registration(this, id, cts, completion);
        }
    }

    /// <summary>Returns true if a token was found and cancelled for operator-requested cancellation.</summary>
    public bool Cancel(WorkItemId id) => Cancel(id, CancellationRequestKind.Operator);

    /// <summary>Returns true if a token was found and cancelled for recovery-owned worker abort.</summary>
    public bool CancelForRecovery(WorkItemId id) => Cancel(id, CancellationRequestKind.Recovery);

    private bool Cancel(WorkItemId id, CancellationRequestKind kind)
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (!_ctsById.TryGetValue(id.Value, out var registered) || registered is null)
                return false;
            cts = registered;
            _requestKindById[id.Value] = kind;
        }
        try { cts.Cancel(); } catch (ObjectDisposedException) { /* races with completion */ }
        return true;
    }

    public bool IsActive(WorkItemId id)
    {
        lock (_gate)
            return _ctsById.ContainsKey(id.Value);
    }

    public CancellationRequestKind? GetRequestKind(WorkItemId id)
    {
        lock (_gate)
            return _requestKindById.TryGetValue(id.Value, out var kind) ? kind : null;
    }

    /// <summary>
    /// Completes once the active pipeline registration has been disposed. A
    /// missing registration is already inactive.
    /// </summary>
    public Task WaitForInactiveAsync(WorkItemId id, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return _completionById.TryGetValue(id.Value, out var completion)
                ? completion.Task.WaitAsync(ct)
                : Task.CompletedTask;
        }
    }

    public void Dispose()
    {
        CancellationTokenSource[] sources;
        TaskCompletionSource[] completions;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            sources = [.. _ctsById.Values];
            completions = [.. _completionById.Values];
            _ctsById.Clear();
            _requestKindById.Clear();
            _completionById.Clear();
        }
        foreach (var completion in completions)
            completion.TrySetResult();
        foreach (var cts in sources)
        {
            try { cts.Dispose(); } catch { /* best-effort */ }
        }
    }

    public sealed class Registration : IDisposable
    {
        private readonly CancellationRegistry _registry;
        private readonly WorkItemId _id;
        private readonly TaskCompletionSource _completion;
        private int _disposed;
        public CancellationTokenSource Source { get; }
        public CancellationToken Token => Source.Token;

        internal Registration(
            CancellationRegistry registry,
            WorkItemId id,
            CancellationTokenSource cts,
            TaskCompletionSource completion)
        {
            _registry = registry;
            _id = id;
            Source = cts;
            _completion = completion;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            lock (_registry._gate)
            {
                var removed = _registry._ctsById.TryRemove(
                    new KeyValuePair<Guid, CancellationTokenSource>(_id.Value, Source));
                if (removed)
                    _registry._requestKindById.TryRemove(_id.Value, out _);

                _registry._completionById.TryRemove(
                    new KeyValuePair<Guid, TaskCompletionSource>(_id.Value, _completion));
            }
            _completion.TrySetResult();
            Source.Dispose();
        }
    }
}
