using System.Diagnostics;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using IdleClock = Microsoft.Extensions.Time.Testing.FakeTimeProvider;

namespace CodeyBox.Tests;

/// <summary>
/// Exercises the production <see cref="AuditorIdleGuard.WaitAsync{T}"/> wait
/// loop with real tasks: a quiet-but-alive auditor run completes instead of
/// being recorded <c>incomplete</c>, while a dead run is still terminated and
/// a never-finishing run hits the absolute bound.
/// </summary>
public sealed class AuditorIdleGuardTests
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(10);

    [Fact]
    public async Task QuietButAliveRun_CompletesInsteadOfTimingOut()
    {
        // No output for the whole run (last-activity never touched) and a
        // runtime past the idle window — the legacy wall-time guard would
        // have killed this at 100 ms. Live sandbox work keeps the slot.
        var idle = TimeSpan.FromMilliseconds(100);
        var absolute = TimeSpan.FromSeconds(30);
        var start = Stopwatch.GetTimestamp();
        var run = Task.Delay(TimeSpan.FromMilliseconds(350)).ContinueWith(_ => "verdict");

        var result = await AuditorIdleGuard.WaitAsync(
            run,
            "csharp:test-pass",
            AgentKind.Claude,
            hasActiveExecs: () => !run.IsCompleted,
            getBudgets: () => (idle, absolute),
            getLastActivityTicks: () => start,
            startTicks: start,
            touch: () => { },
            pollInterval: Poll,
            CancellationToken.None);

        Assert.Equal("verdict", result);
    }

    [Fact]
    public async Task DeadRun_WithNoLiveWork_IsTerminated()
    {
        var idle = TimeSpan.FromMilliseconds(100);
        var start = Stopwatch.GetTimestamp();
        var never = new TaskCompletionSource<string>().Task;

        var ex = await Assert.ThrowsAsync<AuditorIdleTimeoutException>(() =>
            AuditorIdleGuard.WaitAsync(
                never,
                "csharp:test-pass",
                AgentKind.Claude,
                hasActiveExecs: () => false,
                getBudgets: () => (idle, TimeSpan.Zero),
                getLastActivityTicks: () => start,
                startTicks: start,
                touch: () => { },
                pollInterval: Poll,
                CancellationToken.None));

        Assert.Equal(idle, ex.Timeout);
        Assert.Equal("csharp:test-pass", ex.AuditorName);
        Assert.Equal(AuditBudgetOrdering.AuditorIdleTimeoutPath, ex.BudgetPath);
        Assert.Contains(AuditBudgetOrdering.AuditorIdleTimeoutPath, ex.Message);
        Assert.Contains(idle.ToString(), ex.Message);
    }

    [Fact]
    public async Task LiveForeverRun_HitsAbsoluteBound()
    {
        var start = Stopwatch.GetTimestamp();
        var never = new TaskCompletionSource<string>().Task;
        var absolute = TimeSpan.FromMilliseconds(250);

        var ex = await Assert.ThrowsAsync<AuditorAbsoluteTimeoutException>(() =>
            AuditorIdleGuard.WaitAsync(
                never,
                "csharp:test-pass",
                AgentKind.Claude,
                hasActiveExecs: () => true,
                getBudgets: () => (TimeSpan.FromMilliseconds(100), absolute),
                getLastActivityTicks: () => start,
                startTicks: start,
                touch: () => { },
                pollInterval: Poll,
                CancellationToken.None));

        Assert.Equal(absolute, ex.Timeout);
        Assert.Equal("CodeyBox:PipelineTuning:AuditorAbsoluteTimeout", ex.BudgetPath);
        Assert.Contains("CodeyBox:PipelineTuning:AuditorAbsoluteTimeout", ex.Message);
        Assert.Contains(absolute.ToString(), ex.Message);
    }

    [Fact]
    public async Task AlreadyCompletedRun_ReturnsResult()
    {
        var start = Stopwatch.GetTimestamp();

        var result = await AuditorIdleGuard.WaitAsync(
            Task.FromResult("fast"),
            "quality:fast",
            AgentKind.Claude,
            hasActiveExecs: () => false,
            getBudgets: () => (TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50)),
            getLastActivityTicks: () => start,
            startTicks: start,
            touch: () => { },
            pollInterval: Poll,
            CancellationToken.None);

        Assert.Equal("fast", result);
    }

    [Fact]
    public async Task CancelledWait_PropagatesCancellation()
    {
        var start = Stopwatch.GetTimestamp();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AuditorIdleGuard.WaitAsync(
                new TaskCompletionSource<string>().Task,
                "csharp:test-pass",
                AgentKind.Claude,
                hasActiveExecs: () => true,
                getBudgets: () => (TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30)),
                getLastActivityTicks: () => start,
                startTicks: start,
                touch: () => { },
                pollInterval: Poll,
                cts.Token));
    }

    [Fact]
    public async Task FrozenClock_IdleBudgetNeverFiresDespiteRealTimePassing()
    {
        // The idle budget runs on the injected clock: real wall time advances
        // past the 100 ms budget while the frozen fake clock reports zero
        // elapsed, so a quiet run with no live work still completes instead
        // of being declared idle. Proves the guard does not race the wall
        // clock when tests freeze it.
        var clock = new IdleClock();
        var idle = TimeSpan.FromMilliseconds(100);
        var start = clock.GetTimestamp();
        var run = Task.Delay(TimeSpan.FromMilliseconds(250)).ContinueWith(_ => "verdict");

        var result = await AuditorIdleGuard.WaitAsync(
            run,
            "csharp:test-pass",
            AgentKind.Claude,
            hasActiveExecs: () => false,
            getBudgets: () => (idle, TimeSpan.Zero),
            getLastActivityTicks: () => start,
            startTicks: start,
            touch: () => { },
            pollInterval: Poll,
            CancellationToken.None,
            timeProvider: clock);

        Assert.Equal("verdict", result);
    }

    [Fact]
    public async Task AdvancedClock_DeadRunDeclaredIdleDeterministically()
    {
        // Advancing the injected clock past the idle budget terminates a
        // quiet run with no live work without waiting out any real time.
        var clock = new IdleClock();
        var idle = TimeSpan.FromMilliseconds(100);
        var start = clock.GetTimestamp();
        var never = new TaskCompletionSource<string>().Task;

        var wait = AuditorIdleGuard.WaitAsync(
            never,
            "csharp:test-pass",
            AgentKind.Claude,
            hasActiveExecs: () => false,
            getBudgets: () => (idle, TimeSpan.Zero),
            getLastActivityTicks: () => start,
            startTicks: start,
            touch: () => { },
            pollInterval: Poll,
            CancellationToken.None,
            timeProvider: clock);
        Assert.False(wait.IsCompleted);

        clock.Advance(TimeSpan.FromMilliseconds(150));
        var ex = await Assert.ThrowsAsync<AuditorIdleTimeoutException>(() => wait);
        Assert.Equal(idle, ex.Timeout);
        Assert.Equal(AuditBudgetOrdering.AuditorIdleTimeoutPath, ex.BudgetPath);
    }
}
