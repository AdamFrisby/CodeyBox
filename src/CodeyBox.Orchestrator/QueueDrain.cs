namespace CodeyBox.Orchestrator;

/// <summary>
/// Pause-and-wait drain for graceful restarts. A plain queue pause only
/// blocks NEW pickup — in-flight workers keep running, so an operator who
/// restarts immediately after pausing still interrupts running work (which
/// then follows the infrastructure-recovery requeue path). Drain closes that
/// gap: after pausing, it blocks until the worker pool reports no running
/// workers or the deadline elapses, so a restart can proceed without
/// disturbing running work.
/// </summary>
public static class QueueDrain
{
    /// <summary>
    /// Cadence for re-reading the running-worker count while draining. An
    /// implementation detail of the wait loop, not an operator-tunable
    /// threshold; callers that need a different cadence (tests) pass
    /// <paramref name="pollInterval"/> explicitly.
    /// </summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Blocks until <paramref name="getRunningCountAsync"/> reports zero
    /// running workers. Returns true when the pool reached quiescence before
    /// <paramref name="timeout"/> elapsed, false on timeout. A non-positive
    /// timeout performs a single check without waiting. Caller cancellation
    /// propagates as <see cref="OperationCanceledException"/>.
    /// </summary>
    public static async Task<bool> WaitForQuiescenceAsync(
        Func<CancellationToken, Task<int>> getRunningCountAsync,
        TimeSpan timeout,
        CancellationToken ct,
        TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(getRunningCountAsync);
        var poll = pollInterval is { } p && p > TimeSpan.Zero ? p : DefaultPollInterval;

        if (timeout <= TimeSpan.Zero)
            return await getRunningCountAsync(ct).ConfigureAwait(false) <= 0;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        while (true)
        {
            int running;
            try
            {
                running = await getRunningCountAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return false;
            }

            if (running <= 0)
                return true;

            try
            {
                await Task.Delay(poll, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return false;
            }
        }
    }
}
