namespace CodeyBox.Tests;

internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _utcNow = DateTimeOffset.UnixEpoch;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
            return _utcNow;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_gate)
            _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan delta)
    {
        if (delta < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(delta));

        List<(TimerCallback Callback, object? State)> callbacks = [];
        lock (_gate)
        {
            _utcNow += delta;
            foreach (var timer in _timers.ToArray())
            {
                if (timer.TryConsumeDue(_utcNow, out var callback))
                    callbacks.Add(callback);
            }

            _timers.RemoveAll(static t => t.IsDisposed);
        }

        foreach (var (callback, state) in callbacks)
            callback(state);
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualTimeProvider _provider;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private DateTimeOffset? _dueAt;
        private TimeSpan _period;
        private bool _disposed;

        public ManualTimer(ManualTimeProvider provider, TimerCallback callback, object? state)
        {
            _provider = provider;
            _callback = callback;
            _state = state;
        }

        public bool IsDisposed => _disposed;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_provider._gate)
            {
                if (_disposed)
                    return false;

                _period = period;
                _dueAt = dueTime == Timeout.InfiniteTimeSpan
                    ? null
                    : _provider._utcNow + dueTime;
                return true;
            }
        }

        public bool TryConsumeDue(DateTimeOffset now, out (TimerCallback Callback, object? State) callback)
        {
            lock (_provider._gate)
            {
                callback = default;
                if (_disposed || _dueAt is not { } dueAt || dueAt > now)
                    return false;

                callback = (_callback, _state);
                if (_period > TimeSpan.Zero && _period != Timeout.InfiniteTimeSpan)
                {
                    do
                    {
                        dueAt += _period;
                    } while (dueAt <= now);
                    _dueAt = dueAt;
                }
                else
                {
                    _dueAt = null;
                }

                return true;
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        public void Dispose()
        {
            lock (_provider._gate)
                _disposed = true;
        }
    }
}
