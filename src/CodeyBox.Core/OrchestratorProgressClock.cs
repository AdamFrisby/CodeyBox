namespace CodeyBox.Core;

/// <summary>
/// Shared singleton that tracks the time of the most recent orchestrator
/// state transition. Stamped by <c>OrchestratorService</c> after the
/// dead-worker reaper init sweep and when a work item completes;
/// read by stall-detection consumers. Thread-safe via Interlocked.
/// </summary>
public sealed class OrchestratorProgressClock
{
    private long _lastTransitionTicks;

    public DateTimeOffset LastTransition => _lastTransitionTicks == 0
        ? DateTimeOffset.MinValue
        : new DateTimeOffset(_lastTransitionTicks, TimeSpan.Zero);

    public void Stamp(DateTimeOffset timestamp)
    {
        var ticks = timestamp.UtcTicks;
        while (true)
        {
            var current = Interlocked.Read(ref _lastTransitionTicks);
            if (ticks <= current) return;
            if (Interlocked.CompareExchange(ref _lastTransitionTicks, ticks, current) == current)
                return;
        }
    }
}
