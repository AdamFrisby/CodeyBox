using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <see cref="PhaseCancellation"/> source attribution and the
/// resulting <see cref="PhaseCancellationException"/> wrapping. Covers the
/// catch-routing inputs the pipeline depends on without spinning a full
/// PipelineRunner harness.
/// </summary>
public sealed class PhaseCancellationTests
{
    [Fact]
    public async Task PhaseTimeout_AttributesAsTimeoutPhase()
    {
        using var phase = new PhaseCancellation("work", CancellationToken.None);
        phase.SetPhaseTimeout(TimeSpan.FromMilliseconds(50));

        var ex = await Assert.ThrowsAsync<PhaseCancellationException>(() =>
            phase.RunAsync(ct => Task.Delay(Timeout.InfiniteTimeSpan, ct)));

        Assert.Equal("work", ex.Phase);
        Assert.Equal(CancellationSources.PhaseTimeout("work"), ex.Source);
        Assert.True(CancellationSources.IsPhaseTimeout(ex.Source));
        Assert.False(CancellationSources.IsTransient(ex.Source));
    }

    [Fact]
    public async Task OperatorParent_AttributesAsOperator()
    {
        using var operatorCts = new CancellationTokenSource();
        using var phase = new PhaseCancellation("work", operatorCts.Token);

        var task = phase.RunAsync(ct => Task.Delay(Timeout.InfiniteTimeSpan, ct));
        await operatorCts.CancelAsync();

        var ex = await Assert.ThrowsAsync<PhaseCancellationException>(() => task);
        Assert.Equal(CancellationSources.Operator, ex.Source);
    }

    [Fact]
    public async Task HostShutdown_WithZeroGrace_AttributesExactlyHostShutdown()
    {
        // Grace=0 short-circuits the deadline-upgrade callback (HookHostShutdown
        // skips registering the deadline timer when grace <= 0), so the phase
        // can ONLY observe HostShutdown. Pins HostShutdown as the attributed
        // source — a bug that always emitted HostShutdownDeadline would fail.
        using var hostCts = new CancellationTokenSource();
        using var phase = new PhaseCancellation("rework", CancellationToken.None);
        phase.HookHostShutdown(hostCts.Token, grace: TimeSpan.Zero);

        var task = phase.RunAsync(ct => Task.Delay(Timeout.InfiniteTimeSpan, ct));
        await hostCts.CancelAsync();

        var ex = await Assert.ThrowsAsync<PhaseCancellationException>(() => task);
        Assert.Equal(CancellationSources.HostShutdown, ex.Source);
        Assert.Equal("rework", ex.Phase);
    }

    [Fact]
    public async Task HostShutdown_WhenGraceElapses_UpgradesPhaseSourceToHostShutdownDeadline()
    {
        // Grace>0 registers the deadline-upgrade callback. The phase task ignores
        // its token and waits past the grace, so by the time we read the settled
        // PhaseCancellation.Source field both `_cts.CancelAfter(grace)` and the
        // deadline-upgrade CompareExchange have run. We pin the final settled
        // source rather than the exception's frozen-at-Wrap()-time copy, because
        // the two callbacks fire concurrently at grace expiry and the race is
        // fundamental — but the upgrade itself (HostShutdown -> HostShutdownDeadline)
        // must always eventually take effect.
        using var hostCts = new CancellationTokenSource();
        using var phase = new PhaseCancellation("rework", CancellationToken.None);
        phase.HookHostShutdown(hostCts.Token, grace: TimeSpan.FromMilliseconds(10));

        await hostCts.CancelAsync();
        // Wait well past the grace window so the deadline-upgrade CompareExchange
        // has definitely run. Polls because timer callbacks are not synchronous.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (phase.Source != CancellationSources.HostShutdownDeadline && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.Equal(CancellationSources.HostShutdownDeadline, phase.Source);
        // The deadline source is intentionally NOT transient — it can only fire
        // after host shutdown, and the host-shutdown catch in RunAsync owns the
        // routing (item left mid-flight for the recovery loop on next startup).
        Assert.False(CancellationSources.IsTransient(phase.Source));
    }

    [Fact]
    public void AttemptTimeout_DisposeAndRestartGivesFallbackAgentFreshDeadline()
    {
        var time = new ManualTimeProvider();
        using var phase = new PhaseCancellation("rework", CancellationToken.None, time);
        phase.SetPhaseTimeout(TimeSpan.FromMinutes(720));

        using (phase.BeginAttemptTimeout(TimeSpan.FromMinutes(240)))
        {
            time.Advance(TimeSpan.FromMinutes(200));
            Assert.False(phase.Token.IsCancellationRequested);
        }

        using (phase.BeginAttemptTimeout(TimeSpan.FromMinutes(240)))
        {
            time.Advance(TimeSpan.FromMinutes(239));
            Assert.False(phase.Token.IsCancellationRequested);

            time.Advance(TimeSpan.FromMinutes(1));
            Assert.True(phase.Token.IsCancellationRequested);
        }

        Assert.Equal(CancellationSources.PhaseTimeout("rework"), phase.Source);
    }

    [Fact]
    public void PhaseAbsoluteTimeout_BoundsCumulativeFallbackAttempts()
    {
        var time = new ManualTimeProvider();
        using var phase = new PhaseCancellation("rework", CancellationToken.None, time);
        phase.SetPhaseTimeout(TimeSpan.FromMinutes(720));

        for (var i = 0; i < 3; i++)
        {
            using var attempt = phase.BeginAttemptTimeout(TimeSpan.FromMinutes(240));
            time.Advance(TimeSpan.FromMinutes(239));
            Assert.False(phase.Token.IsCancellationRequested);
        }

        time.Advance(TimeSpan.FromMinutes(2));
        Assert.False(phase.Token.IsCancellationRequested);

        time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(phase.Token.IsCancellationRequested);
        Assert.Equal(CancellationSources.PhaseTimeout("rework"), phase.Source);
    }

    [Fact]
    public async Task StuckProbe_AttributesAsStuckProbe()
    {
        using var phase = new PhaseCancellation("merge", CancellationToken.None);
        phase.RecordStuckProbe();
        await phase.Cts.CancelAsync();

        var ex = await Assert.ThrowsAsync<PhaseCancellationException>(() =>
            phase.RunAsync(ct => Task.Delay(Timeout.InfiniteTimeSpan, ct)));

        Assert.Equal(CancellationSources.StuckProbe, ex.Source);
    }

    [Fact]
    public async Task UnattributedCancel_ResolvesToUnknown()
    {
        using var phase = new PhaseCancellation("audit", CancellationToken.None);
        // Cancel the inner CTS directly without going through any of our
        // registered hooks. Mirrors the smoking-gun pattern from prod where
        // an external/leaked supervisor token cancels the linked CTS.
        await phase.Cts.CancelAsync();

        var ex = await Assert.ThrowsAsync<PhaseCancellationException>(() =>
            phase.RunAsync(ct => Task.Delay(Timeout.InfiniteTimeSpan, ct)));

        Assert.Equal(CancellationSources.Unknown, ex.Source);
        Assert.True(CancellationSources.IsTransient(ex.Source));
    }

    [Fact]
    public async Task FirstSourceWins_OnRace()
    {
        // Two contributors arming the same phase: the first to fire records
        // attribution; later ones are no-ops. The CompareExchange in
        // TryRecordSource provides the linearisation point.
        using var operatorCts = new CancellationTokenSource();
        using var hostCts = new CancellationTokenSource();
        using var phase = new PhaseCancellation("work", operatorCts.Token);
        phase.HookHostShutdown(hostCts.Token, grace: TimeSpan.FromMilliseconds(50));

        await operatorCts.CancelAsync();
        // Race the host shutdown after the operator cancel was already recorded.
        await hostCts.CancelAsync();

        var ex = await Assert.ThrowsAsync<PhaseCancellationException>(() =>
            phase.RunAsync(ct => Task.Delay(Timeout.InfiniteTimeSpan, ct)));

        // Operator fired first → host shutdown should not steal the slot.
        Assert.Equal(CancellationSources.Operator, ex.Source);
    }

    [Fact]
    public async Task Wrap_PreservesInnerMessageForLogAggregation()
    {
        using var phase = new PhaseCancellation("work", CancellationToken.None);
        var inner = new TaskCanceledException(); // default message: "A task was canceled."

        var ex = phase.Wrap(inner);

        Assert.Contains("A task was canceled.", ex.Message);
        Assert.Contains("work", ex.Message);
        Assert.Contains("source=", ex.Message);
        Assert.Same(inner, ex.InnerException);
        await Task.CompletedTask;
    }

    [Fact]
    public void Wrap_DoubleWrapIsIdempotent()
    {
        using var phase = new PhaseCancellation("rework", CancellationToken.None);
        var first = phase.Wrap(new TaskCanceledException());
        var second = phase.Wrap(first);

        Assert.Same(first, second);
    }

    [Fact]
    public void IsTransient_ClassifiesSourcesCorrectly()
    {
        Assert.True(CancellationSources.IsTransient(CancellationSources.Unknown));
        // HostShutdownDeadline is intentionally NOT transient — by construction
        // the host-shutdown token is already cancelled when the deadline fires,
        // so the host-shutdown catch in RunAsync wins and the item is left
        // mid-flight for the recovery loop. Auto-retrying would race the host
        // going away. See CancellationSources.IsTransient docstring.
        Assert.False(CancellationSources.IsTransient(CancellationSources.HostShutdownDeadline));
        Assert.False(CancellationSources.IsTransient(CancellationSources.Operator));
        Assert.False(CancellationSources.IsTransient(CancellationSources.HostShutdown));
        Assert.False(CancellationSources.IsTransient(CancellationSources.StuckProbe));
        Assert.False(CancellationSources.IsTransient(CancellationSources.PhaseTimeout("work")));
        Assert.False(CancellationSources.IsTransient(null));
    }

    private sealed class ManualTimeProvider : TimeProvider
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
                    if (!timer.TryConsumeDue(_utcNow, out var callback))
                        continue;
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
                        _disposed = true;
                        _dueAt = null;
                    }

                    return true;
                }
            }

            public void Dispose()
            {
                lock (_provider._gate)
                    _disposed = true;
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
