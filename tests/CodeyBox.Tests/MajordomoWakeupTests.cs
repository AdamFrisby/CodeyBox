using CodeyBox.Core;
using CodeyBox.Majordomo;

namespace CodeyBox.Tests;

/// <summary>
/// Acceptance coverage for majordomo self-wakeups: scheduled cadence ticks
/// with a purpose prompt, debounced event triggers, mode-parity for wakeup
/// mutations, in-flight skipping without backlog, and silent no-findings
/// passes. All assertions run through the real coordinator and the real
/// in-memory conversation store with an injected clock — never mocks.
/// </summary>
public sealed class MajordomoWakeupTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private sealed class ControllableClock : TimeProvider
    {
        private DateTimeOffset _now;
        public ControllableClock(DateTimeOffset start) => _now = start;
        public void Advance(TimeSpan delta) => _now += delta;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private static MajordomoWakeupCoordinator Coordinator(
        ControllableClock clock,
        IMajordomoConversationStore store,
        MajordomoAutonomyMode mode = MajordomoAutonomyMode.Proposed,
        TimeSpan? interval = null,
        TimeSpan? triggerFloor = null,
        bool enabled = true)
    {
        var wakeup = new MajordomoWakeupOptions
        {
            Enabled = enabled,
            WakeupInterval = interval ?? TimeSpan.FromMinutes(15),
            MinTriggerInterval = triggerFloor ?? TimeSpan.FromMinutes(5),
        };
        var policy = new MajordomoOptions { Mode = mode };
        return new MajordomoWakeupCoordinator(store, () => wakeup, () => policy, clock: clock);
    }

    private static Func<CancellationToken, Task<MajordomoWakeupAssessment>> Findings(string report)
        => _ => Task.FromResult(new MajordomoWakeupAssessment(true, report));

    private static MajordomoWakeupMutation RetryMutation()
        => new("retry_work_item", new RetryWorkItemArgs(WorkItemId.New()));

    [Fact]
    public async Task ScheduledCadence_FiresWhenDue_AndBurstCollapsesToOneWakeup()
    {
        var clock = new ControllableClock(T0);
        var store = new InMemoryMajordomoConversationStore();
        var coordinator = Coordinator(clock, store);

        Assert.True(coordinator.IsScheduledDue());

        var first = await coordinator.RunScheduledAsync(Findings("queue-health: 2 failed items need attention."));
        Assert.Equal(MajordomoWakeupOutcome.Reported, first.Outcome);
        Assert.Equal(1, await store.CountAsync());

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False(coordinator.IsScheduledDue());

        var notDue = await coordinator.RunScheduledAsync(Findings("should not run"));
        Assert.Equal(MajordomoWakeupOutcome.NotDue, notDue.Outcome);
        Assert.Equal(1, await store.CountAsync());

        var burst = new List<MajordomoWakeupResult>();
        for (var i = 0; i < 5; i++)
            burst.Add(await coordinator.NotifyEventAsync(
                MajordomoWakeupKind.TerminalFailure, $"failure {i}", Findings("failure detail")));
        Assert.All(burst, r => Assert.Equal(MajordomoWakeupOutcome.CollapsedBurst, r.Outcome));
        Assert.Equal(1, await store.CountAsync());

        clock.Advance(TimeSpan.FromMinutes(15));
        Assert.True(coordinator.IsScheduledDue());
        var second = await coordinator.RunScheduledAsync(Findings("queue-health: all clear except one stuck item."));
        Assert.Equal(MajordomoWakeupOutcome.Reported, second.Outcome);
        Assert.Equal(2, await store.CountAsync());
    }

    [Fact]
    public async Task ProposedMode_WakeupProducesProposalsAndMutatesNothing()
    {
        var clock = new ControllableClock(T0);
        var store = new InMemoryMajordomoConversationStore();
        var coordinator = Coordinator(clock, store, MajordomoAutonomyMode.Proposed);

        var executed = 0;
        var proposed = 0;
        var assessment = new MajordomoWakeupAssessment(true, "one item needs a retry.", [RetryMutation()]);

        var result = await coordinator.RunScheduledAsync(
            _ => Task.FromResult(assessment),
            execute: (_, _) => { executed++; return Task.CompletedTask; },
            propose: (_, _) => { proposed++; return Task.CompletedTask; });

        Assert.Equal(MajordomoWakeupOutcome.Reported, result.Outcome);
        Assert.Equal(0, result.Executed);
        Assert.Equal(1, result.Proposed);
        Assert.Equal(0, executed);
        Assert.Equal(1, proposed);
        Assert.Equal(1, await store.CountAsync());

        var autonomous = Coordinator(new ControllableClock(T0), new InMemoryMajordomoConversationStore(), MajordomoAutonomyMode.Autonomous);
        var autoExecuted = 0;
        var autoProposed = 0;
        var autoResult = await autonomous.RunScheduledAsync(
            _ => Task.FromResult(assessment),
            execute: (_, _) => { autoExecuted++; return Task.CompletedTask; },
            propose: (_, _) => { autoProposed++; return Task.CompletedTask; });
        Assert.Equal(1, autoResult.Executed);
        Assert.Equal(0, autoResult.Proposed);
        Assert.Equal(1, autoExecuted);
        Assert.Equal(0, autoProposed);
    }

    [Fact]
    public async Task Wakeup_SkippedWhileTurnInFlight_AndDoesNotAccumulateBacklog()
    {
        var clock = new ControllableClock(T0);
        var store = new InMemoryMajordomoConversationStore();
        var coordinator = Coordinator(clock, store);

        using (coordinator.AcquireTurn())
        {
            var scheduled = await coordinator.RunScheduledAsync(Findings("report"));
            Assert.Equal(MajordomoWakeupOutcome.SkippedTurnInFlight, scheduled.Outcome);

            var triggered = await coordinator.NotifyEventAsync(
                MajordomoWakeupKind.QueueStalled, "stalled", Findings("report"));
            Assert.Equal(MajordomoWakeupOutcome.SkippedTurnInFlight, triggered.Outcome);

            Assert.Equal(0, await store.CountAsync());
        }

        var after = await coordinator.RunScheduledAsync(Findings("report after turn drained."));
        Assert.Equal(MajordomoWakeupOutcome.Reported, after.Outcome);
        Assert.Equal(1, await store.CountAsync());

        var skips = coordinator.History.Count(r => r.Outcome == MajordomoWakeupOutcome.SkippedTurnInFlight);
        var reports = coordinator.History.Count(r => r.Outcome == MajordomoWakeupOutcome.Reported);
        Assert.Equal(2, skips);
        Assert.Equal(1, reports);
    }

    [Fact]
    public async Task NoFindings_WakeupProducesNoConversationNoise_ButStaysVisible()
    {
        var clock = new ControllableClock(T0);
        var store = new InMemoryMajordomoConversationStore();
        var coordinator = Coordinator(clock, store);

        var quiet = await coordinator.RunScheduledAsync(
            _ => Task.FromResult(new MajordomoWakeupAssessment(false)));
        Assert.Equal(MajordomoWakeupOutcome.QuietNoFindings, quiet.Outcome);
        Assert.Equal(0, await store.CountAsync());

        clock.Advance(TimeSpan.FromMinutes(16));
        var blank = await coordinator.RunScheduledAsync(
            _ => Task.FromResult(new MajordomoWakeupAssessment(true, "   ")));
        Assert.Equal(MajordomoWakeupOutcome.QuietNoFindings, blank.Outcome);
        Assert.Equal(0, await store.CountAsync());

        Assert.Contains(
            coordinator.History,
            r => r.Outcome == MajordomoWakeupOutcome.QuietNoFindings);

        clock.Advance(TimeSpan.FromMinutes(16));
        var reported = await coordinator.RunScheduledAsync(Findings("one failure needs the operator."));
        Assert.Equal(MajordomoWakeupOutcome.Reported, reported.Outcome);
        Assert.Equal(1, await store.CountAsync());
    }
}
