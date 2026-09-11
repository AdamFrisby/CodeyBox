using System.Diagnostics;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Production wait loop behind <c>PipelineRunner.RunAuditorWithIdleTimeoutAsync</c>,
/// extracted so the liveness-aware idle semantics are testable with real tasks.
///
/// <para>
/// At every poll tick the loop consults <see cref="AuditorIdlePolicy"/>: a run
/// that already finished is collected; a quiet run that still holds live
/// sandbox work has its quiet window extended instead of being killed for
/// being quiet; a quiet run with no live work is declared idle; and a run
/// that outlives the absolute bound is terminated no matter how chatty or
/// busy it looks. Both timeout budgets are re-read through
/// <paramref name="getBudgets"/> every tick so hot-reload edits take effect
/// on the next tick without restarting the run.
/// </para>
/// </summary>
internal static class AuditorIdleGuard
{
    public static async Task<T> WaitAsync<T>(
        Task<T> task,
        string auditorName,
        AgentKind agentKind,
        Func<bool> hasActiveExecs,
        Func<(TimeSpan IdleTimeout, TimeSpan AbsoluteTimeout)> getBudgets,
        Func<long> getLastActivityTicks,
        long startTicks,
        Action touch,
        TimeSpan pollInterval,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(hasActiveExecs);
        ArgumentNullException.ThrowIfNull(getBudgets);
        ArgumentNullException.ThrowIfNull(getLastActivityTicks);
        ArgumentNullException.ThrowIfNull(touch);
        if (string.IsNullOrWhiteSpace(auditorName))
            throw new ArgumentException("Auditor name must not be empty.", nameof(auditorName));
        if (pollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(pollInterval), "Poll interval must be positive.");

        while (true)
        {
            var completed = await Task.WhenAny(
                task,
                Task.Delay(pollInterval, ct)).ConfigureAwait(false);
            if (completed == task || task.IsCompleted)
                return await task.ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();

            var (idleTimeout, absoluteTimeout) = getBudgets();
            var decision = AuditorIdlePolicy.Decide(
                Stopwatch.GetElapsedTime(getLastActivityTicks()),
                idleTimeout,
                Stopwatch.GetElapsedTime(startTicks),
                absoluteTimeout,
                task.IsCompleted,
                hasActiveExecs());
            switch (decision)
            {
                case AuditorIdleDecision.KeepWaiting:
                    if (idleTimeout > TimeSpan.Zero
                        && Stopwatch.GetElapsedTime(getLastActivityTicks()) >= idleTimeout
                        && hasActiveExecs())
                    {
                        touch();
                    }

                    continue;
                case AuditorIdleDecision.AbsoluteExceeded:
                    throw new AuditorAbsoluteTimeoutException(auditorName, agentKind, absoluteTimeout);
                default:
                    throw new AuditorIdleTimeoutException(auditorName, agentKind, idleTimeout);
            }
        }
    }
}
