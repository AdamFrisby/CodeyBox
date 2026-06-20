namespace CodeyBox.Orchestrator;

/// <summary>
/// Concurrency gate whose admission ceiling can be resized at runtime without
/// aborting in-flight permits.
///
/// <para>
/// <b>Why not <see cref="SemaphoreSlim"/>?</b> A semaphore's capacity is fixed
/// at construction; raising it requires <see cref="SemaphoreSlim.Release(int)"/>
/// (bounded by <c>maxCount</c>) and lowering it has no in-place primitive at
/// all (you would have to acquire-and-hold delta permits via background tasks,
/// which races on a subsequent grow because pending hold-acquires may complete
/// after the counter has already been adjusted).
/// </para>
///
/// <para>
/// This gate tracks the desired target size and the in-flight count
/// independently. <see cref="Resize"/> changes the target; <see cref="Release"/>
/// drains it. If <see cref="Resize"/> lowers the target below the current
/// in-flight count, no permits are interrupted — the gate simply refuses to
/// admit new entries above the new target until in-flight drops, satisfying
/// the contract that "shrinking never aborts in-flight items".
/// </para>
/// </summary>
public sealed class ResizableConcurrencyGate : IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<Waiter> _waiters = new();
    private int _target;
    private int _inFlight;
    private bool _disposed;

    public ResizableConcurrencyGate(int initialTarget)
    {
        if (initialTarget < 1)
            throw new ArgumentOutOfRangeException(
                nameof(initialTarget),
                initialTarget,
                "Concurrency gate target must be >= 1.");
        _target = initialTarget;
    }

    /// <summary>The currently configured admission target.</summary>
    public int CurrentTarget
    {
        get { lock (_gate) return _target; }
    }

    /// <summary>The number of permits currently held by callers that have not yet released.</summary>
    public int CurrentInFlight
    {
        get { lock (_gate) return _inFlight; }
    }

    /// <summary>
    /// Asynchronously waits for an admission slot. Throws
    /// <see cref="OperationCanceledException"/> if <paramref name="ct"/> fires
    /// before a slot becomes available; throws
    /// <see cref="ObjectDisposedException"/> if the gate is disposed.
    /// </summary>
    public Task WaitAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Waiter waiter;
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            if (_inFlight < _target)
            {
                _inFlight++;
                return Task.CompletedTask;
            }
            waiter = new Waiter();
            _waiters.Enqueue(waiter);
        }
        if (ct.CanBeCanceled)
        {
            waiter.Registration = ct.Register(static state =>
            {
                var w = (Waiter)state!;
                w.TrySetCanceled();
            }, waiter);
        }
        return waiter.Task;
    }

    /// <summary>
    /// Non-blocking attempt to take a slot. Returns false instead of waiting if
    /// the gate is at capacity.
    /// </summary>
    public bool TryEnter()
    {
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            if (_inFlight < _target)
            {
                _inFlight++;
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Releases one permit. Safe to call after <see cref="Dispose"/> — it
    /// silently no-ops, mirroring the existing release path that swallowed
    /// <see cref="ObjectDisposedException"/> from the disposed semaphore at
    /// shutdown.
    /// </summary>
    public void Release()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_inFlight <= 0)
                throw new InvalidOperationException(
                    "ResizableConcurrencyGate.Release called with no in-flight permit.");
            _inFlight--;
        }
        DrainWaiters();
    }

    /// <summary>
    /// Changes the admission target. Growing immediately wakes up to
    /// (newTarget - inFlight) queued waiters; shrinking does NOT interrupt
    /// any in-flight permit — the pool naturally converges down as
    /// in-flight callers <see cref="Release"/>.
    /// </summary>
    /// <returns>The old target, new target, and current in-flight count
    /// observed inside the resize lock.</returns>
    public ResizeResult Resize(int newTarget)
    {
        if (newTarget < 1)
            throw new ArgumentOutOfRangeException(
                nameof(newTarget),
                newTarget,
                "Concurrency gate target must be >= 1.");
        int oldTarget;
        int inFlightSnapshot;
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            oldTarget = _target;
            if (oldTarget == newTarget)
                return new ResizeResult(oldTarget, newTarget, _inFlight);
            _target = newTarget;
            inFlightSnapshot = _inFlight;
        }
        // Grow path wakes queued waiters; shrink path is a no-op for in-flight
        // (they keep their permits, future Release calls converge the count
        // down towards the new target). Drain is safe on both paths because
        // it only wakes waiters when _inFlight < _target.
        DrainWaiters();
        return new ResizeResult(oldTarget, newTarget, inFlightSnapshot);
    }

    public void Dispose()
    {
        List<Waiter>? toCancel = null;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            while (_waiters.Count > 0)
                (toCancel ??= new()).Add(_waiters.Dequeue());
        }
        if (toCancel is null) return;
        foreach (var w in toCancel)
            w.TrySetCanceled();
    }

    private void DrainWaiters()
    {
        while (true)
        {
            Waiter? toWake = null;
            lock (_gate)
            {
                if (_disposed) return;
                while (_inFlight < _target && _waiters.Count > 0)
                {
                    var candidate = _waiters.Dequeue();
                    if (candidate.IsCompleted) continue;
                    _inFlight++;
                    toWake = candidate;
                    break;
                }
            }
            if (toWake is null) return;
            if (toWake.TrySetResult()) continue;
            // Waiter was cancelled between dequeue and wake; reverse the
            // increment and look for another waiter.
            lock (_gate) { _inFlight--; }
        }
    }

    private void ThrowIfDisposedLocked()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ResizableConcurrencyGate));
    }

    private sealed class Waiter
    {
        private readonly TaskCompletionSource _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationTokenRegistration Registration { get; set; }
        public Task Task => _tcs.Task;
        public bool IsCompleted => _tcs.Task.IsCompleted;

        public bool TrySetResult()
        {
            var ok = _tcs.TrySetResult();
            Registration.Dispose();
            return ok;
        }

        public bool TrySetCanceled()
        {
            var ok = _tcs.TrySetCanceled();
            Registration.Dispose();
            return ok;
        }
    }
}

/// <summary>
/// Result of a <see cref="ResizableConcurrencyGate.Resize"/> call. Surfaced so
/// callers can log the before/after transition without re-reading state under
/// a second lock.
/// </summary>
public readonly record struct ResizeResult(int OldTarget, int NewTarget, int InFlight);
