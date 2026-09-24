using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

// Framework FakeTimeProvider (CreateTimer fires on Advance); aliased to avoid
// the namespace-local FakeTimeProvider in AgentClassRouterScoreTests.cs whose
// CreateTimer would stay on the system clock. See WorkerPoolSlotReleaseWakeTests.
using ControllableTimeProvider = Microsoft.Extensions.Time.Testing.FakeTimeProvider;

namespace CodeyBox.Tests;

/// <summary>
/// Regression tests for the per-agent-cap deferral starvation bug: a work item
/// deferred because its routed agent was at cap used to leave the ranked
/// candidate set until its blind recheck timer fired, so a slot freed in the
/// meantime went to any fresh Queued item evaluated first — regardless of
/// priority or queue position. With the in-flight-before-fresh preference
/// (WorkerPool:PreferInFlightOverFresh, default on) a cap deferral records the
/// routes it waits on, a release on one of those routes wakes the item back
/// into dispatch ordering immediately, and the pickup ranking keeps its place
/// against fresh work.
/// </summary>
[Collection("Background service timing")]
public sealed class DeferredCapWaiterDispatchTests : IDisposable
{
    private static readonly AgentKind Codex = AgentKind.Codex;
    private static readonly AgentKind Claude = AgentKind.Claude;

    // Positive waits land in ~300ms locally; the 120s ceiling is CI-contention
    // headroom only (matches WorkerPoolFinishingPrecedenceTests).
    private static readonly TimeSpan DispatchWaitTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan DeferralWaitTimeout = TimeSpan.FromSeconds(30);

    // Deliberately far longer than any test run: if a deferred item dispatches
    // without advancing the fake clock, it could only have come from the
    // slot-release wake, never from the recheck timer.
    private static readonly TimeSpan CapRetryRecheckInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan QuotaRecheckInterval = TimeSpan.FromMinutes(30);

    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"codeybox-cap-waiter-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;

    public DeferredCapWaiterDispatchTests() => _store = new SqliteWorkItemStore(_dbPath);

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
        TestScratchDirectory.DeleteSqliteCompanions(_dbPath);
    }

    private static WorkItem Item(
        WorkItemState state = WorkItemState.Queued,
        int priority = 0,
        long queuePosition = 0,
        string agentClassId = "codex-cls",
        DateTimeOffset? createdAt = null) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("p"),
        Title = "t",
        Prompt = "p",
        State = state,
        Priority = priority,
        QueuePosition = queuePosition,
        AgentClassId = agentClassId,
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
        StartedAt = state == WorkItemState.Queued ? null : DateTimeOffset.UtcNow,
        PushUpstream = false,
    };

    private static AgentClass SingleAgentClass(string classId, AgentKind agent) => new()
    {
        Id = classId,
        DisplayName = classId,
        Members =
        [
            new AgentMembership { Agent = agent, Billing = AgentBilling.Subscription, QualityScore = 100 },
        ],
    };

    private static AgentClassRouter BuildRouter(
        double codexAvailablePct,
        bool includeClaude = false)
    {
        var classes = includeClaude
            ? new[] { SingleAgentClass("codex-cls", Codex), SingleAgentClass("claude-cls", Claude) }
            : new[] { SingleAgentClass("codex-cls", Codex) };
        var probes = includeClaude
            ? new IAgentQuotaProbe[] { new FakeProbe(Codex, codexAvailablePct), new FakeProbe(Claude, 100.0) }
            : new IAgentQuotaProbe[] { new FakeProbe(Codex, codexAvailablePct) };
        return new AgentClassRouter(
            classes,
            probes,
            new QuotaRouterOptions
            {
                MinQuotaPct = 5.0,
                QuotaRecheckInterval = QuotaRecheckInterval,
                CapRetryRecheckInterval = CapRetryRecheckInterval,
            },
            NullLogger<AgentClassRouter>.Instance);
    }

    private static AgentConcurrencyOptions Caps(int codexMax) => new()
    {
        Members =
        {
            ["codex"] = new AgentConcurrencyEntry { MaxConcurrent = codexMax },
        },
    };

    /// <summary>
    /// Builds the deterministic competition shape used by the ranked-slot
    /// tests: a 3-slot pool where the codex occupant and one claude filler are
    /// pinned, so exactly one global slot stays free — enough for the
    /// deferred item's own pickup to run, but never enough for a second
    /// concurrently-spawned worker to race the freed route reservation once
    /// the occupant exits.
    /// </summary>
    private async Task<(OrchestratorService Svc, InMemoryTaskQueue Queue, ItemGatedPipeline Pipeline, WorkItem Occupant, WorkItem Filler)> StartContendedPoolAsync()
    {
        var queue = new InMemoryTaskQueue();
        var pipeline = new ItemGatedPipeline(_store);
        var svc = new OrchestratorService(
            queue, _store, pipeline, new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions { MaxConcurrentWorkers = 3 },
            NullLogger<OrchestratorService>.Instance,
            router: BuildRouter(codexAvailablePct: 100.0, includeClaude: true),
            agentConcurrency: Caps(codexMax: 1),
            timeProvider: new ControllableTimeProvider());

        var occupant = Item();
        var filler1 = Item(agentClassId: "claude-cls");
        foreach (var item in new[] { occupant, filler1 })
        {
            await _store.CreateAsync(item);
            await queue.EnqueueAsync(item.Id);
        }

        await svc.StartAsync(CancellationToken.None);
        Assert.True(await pipeline.WaitForEnteredAsync(occupant.Id, DispatchWaitTimeout));
        Assert.True(await pipeline.WaitForEnteredAsync(filler1.Id, DispatchWaitTimeout));
        return (svc, queue, pipeline, occupant, filler1);
    }

    [Fact]
    public async Task SlotRelease_DeferredInFlightItem_BeatsEqualPriorityFreshItem()
    {
        // codex cap 1: the occupant pins the only codex slot. A (WorkComplete,
        // already past the work phase) defers at the cap; B is a fresh Queued
        // item at the same priority with a LATER queue position. Releasing the
        // slot must dispatch A — the ranked order keeps deferred in-flight
        // work ahead of fresh starts — not B. The second claude filler pins
        // the remaining global slot so B cannot spawn a racing worker while
        // the freed slot is decided.
        var (svc, queue, pipeline, occupant, filler1) = await StartContendedPoolAsync();
        try
        {
            var a = Item(WorkItemState.WorkComplete, priority: 20, queuePosition: 10);
            await _store.CreateAsync(a);
            await queue.EnqueueAsync(a.Id);
            Assert.True(await WaitUntilAsync(() => svc.IsDeferredForTest(a.Id), DeferralWaitTimeout));

            // Fill the last global slot so B stays a queued candidate, not a
            // concurrently-spawned racer for the freed route reservation.
            var filler2 = Item(agentClassId: "claude-cls");
            await _store.CreateAsync(filler2);
            await queue.EnqueueAsync(filler2.Id);
            Assert.True(await pipeline.WaitForEnteredAsync(filler2.Id, DispatchWaitTimeout));

            var b = Item(WorkItemState.Queued, priority: 20, queuePosition: 20);
            await _store.CreateAsync(b);
            await queue.EnqueueAsync(b.Id);

            // Free the codex slot. The fake clock is never advanced: the 1h
            // recheck timer cannot have fired, so any dispatch from here can
            // only come through the slot-release wake.
            pipeline.Release(occupant.Id);

            Assert.True(await pipeline.WaitForEnteredAsync(a.Id, DispatchWaitTimeout));
            Assert.False(pipeline.HasEntered(b.Id));

            pipeline.Release(a.Id);
        }
        finally
        {
            pipeline.Release(occupant.Id);
            pipeline.Release(filler1.Id);
            await svc.StopAsync(CancellationToken.None);
            svc.Dispose();
        }
    }

    [Fact]
    public async Task SlotRelease_HigherPriorityFreshItem_BeatsLowerPriorityInFlightItem()
    {
        // The preference is a tiebreak below priority, not above it: a fresh
        // Queued item at priority 50 still wins the freed slot over a
        // WorkComplete item deferred at priority 10.
        var (svc, queue, pipeline, occupant, filler1) = await StartContendedPoolAsync();
        try
        {
            var a = Item(WorkItemState.WorkComplete, priority: 10, queuePosition: 10);
            await _store.CreateAsync(a);
            await queue.EnqueueAsync(a.Id);
            Assert.True(await WaitUntilAsync(() => svc.IsDeferredForTest(a.Id), DeferralWaitTimeout));

            var filler2 = Item(agentClassId: "claude-cls");
            await _store.CreateAsync(filler2);
            await queue.EnqueueAsync(filler2.Id);
            Assert.True(await pipeline.WaitForEnteredAsync(filler2.Id, DispatchWaitTimeout));

            var b = Item(WorkItemState.Queued, priority: 50, queuePosition: 20);
            await _store.CreateAsync(b);
            await queue.EnqueueAsync(b.Id);

            pipeline.Release(occupant.Id);

            Assert.True(await pipeline.WaitForEnteredAsync(b.Id, DispatchWaitTimeout));
            Assert.False(pipeline.HasEntered(a.Id));

            pipeline.Release(b.Id);
        }
        finally
        {
            pipeline.Release(occupant.Id);
            pipeline.Release(filler1.Id);
            await svc.StopAsync(CancellationToken.None);
            svc.Dispose();
        }
    }

    [Fact]
    public async Task SlotRelease_WakesDeferredItem_PromptlyWithoutRecheckWait()
    {
        // Direct check of the wake path with a synthetic route reservation:
        // the deferred item must dispatch on Release, without the fake clock
        // ever reaching the 1h cap-retry interval.
        var time = new ControllableTimeProvider();
        var queue = new InMemoryTaskQueue();
        var pipeline = new ItemGatedPipeline(_store);
        using var registry = new CancellationRegistry(CancellationToken.None);
        using var svc = new OrchestratorService(
            queue, _store, pipeline, registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 2 },
            NullLogger<OrchestratorService>.Instance,
            router: BuildRouter(codexAvailablePct: 100.0),
            agentConcurrency: Caps(codexMax: 1),
            timeProvider: time);

        Assert.True(svc.TryReserveAgentSlotForTest(Codex));

        var a = Item(WorkItemState.WorkComplete, priority: 0, queuePosition: 1);
        await _store.CreateAsync(a);
        await queue.EnqueueAsync(a.Id);
        await svc.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await WaitUntilAsync(() => svc.IsDeferredForTest(a.Id), DeferralWaitTimeout));

            svc.ReleaseAgentSlotForTest(Codex);

            Assert.True(await pipeline.WaitForEnteredAsync(a.Id, DispatchWaitTimeout));
            pipeline.Release(a.Id);
        }
        finally
        {
            await svc.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task QuotaOnlyDeferral_StillRechecksOnInterval_NotOnSlotRelease()
    {
        // Regression guard: only cap deferrals ride the release signal. A
        // quota-only wait (no member was at cap) must keep sleeping until its
        // recheck interval — the wake mechanism must not turn quota stalls
        // into a spin loop on unrelated releases.
        var time = new ControllableTimeProvider();
        var queue = new InMemoryTaskQueue();
        var pipeline = new ItemGatedPipeline(_store);
        var deferrals = 0;
        using var registry = new CancellationRegistry(CancellationToken.None);
        using var svc = new OrchestratorService(
            queue, _store, pipeline, registry,
            new OrchestratorOptions
            {
                MaxConcurrentWorkers = 2,
                OnDeferredForTest = (_, _) => Interlocked.Increment(ref deferrals),
            },
            NullLogger<OrchestratorService>.Instance,
            // 0% quota: every member denied by the floor → pure quota stall.
            router: BuildRouter(codexAvailablePct: 0.0),
            timeProvider: time);

        var a = Item();
        await _store.CreateAsync(a);
        await queue.EnqueueAsync(a.Id);
        await svc.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await WaitUntilAsync(() => svc.IsDeferredForTest(a.Id), DeferralWaitTimeout));
            Assert.Equal(1, Volatile.Read(ref deferrals));

            // A real release on the item's route must not wake a quota-only
            // deferral early.
            Assert.True(svc.TryReserveAgentSlotForTest(Codex));
            svc.ReleaseAgentSlotForTest(Codex);
            await Task.Delay(500);

            Assert.True(svc.IsDeferredForTest(a.Id));
            Assert.Equal(1, Volatile.Read(ref deferrals));
            Assert.False(pipeline.HasEntered(a.Id));

            // Advancing to the quota recheck interval re-enqueues the item;
            // the re-pickup hits the same quota floor and re-defers.
            time.Advance(QuotaRecheckInterval + TimeSpan.FromSeconds(1));

            Assert.True(await WaitUntilAsync(() => Volatile.Read(ref deferrals) >= 2, DeferralWaitTimeout));
            Assert.False(pipeline.HasEntered(a.Id));
        }
        finally
        {
            await svc.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task PreferenceOff_LegacyOrdering_StillWakesCapWaiterOnRelease()
    {
        // The knob governs the ordering preference only — the cap-deferral
        // release wake is unconditional. Here B is a fresh Queued item at the
        // same priority created EARLIER than A: under the legacy ordering
        // (created_at tiebreak) B wins the freed slot even though A was woken
        // back into the candidate set. Under InFlightBeforeFresh A would have
        // won — that contrast is exactly what the knob toggles.
        var time = new ControllableTimeProvider();
        var queue = new InMemoryTaskQueue();
        var pipeline = new ItemGatedPipeline(_store);
        using var registry = new CancellationRegistry(CancellationToken.None);
        using var svc = new OrchestratorService(
            queue, _store, pipeline, registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 3, PreferInFlightOverFresh = false },
            NullLogger<OrchestratorService>.Instance,
            router: BuildRouter(codexAvailablePct: 100.0, includeClaude: true),
            agentConcurrency: Caps(codexMax: 1),
            timeProvider: time);
        Assert.Equal(DispatchCandidateOrdering.FinishingThenPriority, svc.CurrentDispatchOrdering);

        var occupant = Item();
        var filler = Item(agentClassId: "claude-cls");
        foreach (var item in new[] { occupant, filler })
        {
            await _store.CreateAsync(item);
            await queue.EnqueueAsync(item.Id);
        }
        await svc.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await pipeline.WaitForEnteredAsync(occupant.Id, DispatchWaitTimeout));
            Assert.True(await pipeline.WaitForEnteredAsync(filler.Id, DispatchWaitTimeout));

            var a = Item(WorkItemState.WorkComplete, priority: 20, queuePosition: 10,
                createdAt: DateTimeOffset.UtcNow.AddSeconds(10));
            await _store.CreateAsync(a);
            await queue.EnqueueAsync(a.Id);
            Assert.True(await WaitUntilAsync(() => svc.IsDeferredForTest(a.Id), DeferralWaitTimeout));

            var filler2 = Item(agentClassId: "claude-cls");
            await _store.CreateAsync(filler2);
            await queue.EnqueueAsync(filler2.Id);
            Assert.True(await pipeline.WaitForEnteredAsync(filler2.Id, DispatchWaitTimeout));

            var b = Item(WorkItemState.Queued, priority: 20, queuePosition: 20);
            await _store.CreateAsync(b);
            await queue.EnqueueAsync(b.Id);

            pipeline.Release(occupant.Id);

            // A was woken (no longer deferred) but loses the freed slot to B
            // under the legacy created_at ordering.
            Assert.True(await pipeline.WaitForEnteredAsync(b.Id, DispatchWaitTimeout));
            Assert.False(svc.IsDeferredForTest(a.Id));
            Assert.False(pipeline.HasEntered(a.Id));

            pipeline.Release(b.Id);
        }
        finally
        {
            pipeline.Release(occupant.Id);
            pipeline.Release(filler.Id);
            await svc.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task DispatchOrdering_InFlightBeforeFresh_PriorityThenProgressThenQueuePosition()
    {
        // Store-level pin of the ranked candidate order: finishing bucket
        // first (unchanged), then priority, then past-work-phase items before
        // fresh starts, then queue_position — with created_at deliberately
        // scrambled against queue_position to prove which key decides.
        var now = DateTimeOffset.UtcNow;
        var highPriorityFresh = Item(WorkItemState.Queued, priority: 60, queuePosition: 99,
            createdAt: now.AddSeconds(-10));
        var inFlightEarlierPos = Item(WorkItemState.WorkComplete, priority: 20, queuePosition: 10,
            createdAt: now);
        var inFlightLaterPos = Item(WorkItemState.WorkComplete, priority: 20, queuePosition: 90,
            createdAt: now.AddSeconds(-3));
        var freshEarlierPos = Item(WorkItemState.Queued, priority: 20, queuePosition: 5,
            createdAt: now.AddSeconds(-1));
        var freshLaterPos = Item(WorkItemState.Queued, priority: 20, queuePosition: 50,
            createdAt: now.AddSeconds(-2));

        foreach (var item in new[] { highPriorityFresh, inFlightEarlierPos, inFlightLaterPos, freshEarlierPos, freshLaterPos })
            await _store.CreateAsync(item);

        var ordered = new List<WorkItemId>();
        await foreach (var item in _store.ListDispatchEligibleByPriorityAsync(
            new HashSet<WorkItemId>(), DispatchCandidateOrdering.InFlightBeforeFresh))
        {
            ordered.Add(item.Id);
        }

        Assert.Equal(
            [highPriorityFresh.Id, inFlightEarlierPos.Id, inFlightLaterPos.Id, freshEarlierPos.Id, freshLaterPos.Id],
            ordered);
    }

    [Fact]
    public async Task DispatchOrdering_Legacy_IgnoresProgressAndQueuePosition()
    {
        // Same fixture shape as the InFlightBeforeFresh pin: under the legacy
        // ordering, equal-priority candidates sort by created_at only.
        var now = DateTimeOffset.UtcNow;
        var highPriorityFresh = Item(WorkItemState.Queued, priority: 60, queuePosition: 99,
            createdAt: now.AddSeconds(-10));
        var inFlightEarlierPos = Item(WorkItemState.WorkComplete, priority: 20, queuePosition: 10,
            createdAt: now);
        var inFlightLaterPos = Item(WorkItemState.WorkComplete, priority: 20, queuePosition: 90,
            createdAt: now.AddSeconds(-3));
        var freshEarlierPos = Item(WorkItemState.Queued, priority: 20, queuePosition: 5,
            createdAt: now.AddSeconds(-1));
        var freshLaterPos = Item(WorkItemState.Queued, priority: 20, queuePosition: 50,
            createdAt: now.AddSeconds(-2));

        foreach (var item in new[] { highPriorityFresh, inFlightEarlierPos, inFlightLaterPos, freshEarlierPos, freshLaterPos })
            await _store.CreateAsync(item);

        var ordered = new List<WorkItemId>();
        await foreach (var item in _store.ListDispatchEligibleByPriorityAsync(
            new HashSet<WorkItemId>(), DispatchCandidateOrdering.FinishingThenPriority))
        {
            ordered.Add(item.Id);
        }

        Assert.Equal(
            [highPriorityFresh.Id, inFlightLaterPos.Id, freshLaterPos.Id, freshEarlierPos.Id, inFlightEarlierPos.Id],
            ordered);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (predicate())
                return true;
            await Task.Delay(25);
        }
        return predicate();
    }

    /// <summary>
    /// Pipeline that records entry order and blocks each item until released,
    /// then marks it Done — lets the test pin items into "running" state and
    /// observe which candidate the dispatcher chose.
    /// </summary>
    private sealed class ItemGatedPipeline(IWorkItemStore store) : IPipelineRunner
    {
        private readonly ConcurrentDictionary<WorkItemId, TaskCompletionSource> _entered = new();
        private readonly ConcurrentDictionary<WorkItemId, TaskCompletionSource> _released = new();

        public bool HasEntered(WorkItemId id) => _entered.ContainsKey(id);

        public void Release(WorkItemId id) =>
            _released.GetOrAdd(id, static _ => NewSignal()).TrySetResult();

        public Task<bool> WaitForEnteredAsync(WorkItemId id, TimeSpan timeout) =>
            WaitForSignalAsync(_entered.GetOrAdd(id, static _ => NewSignal()), timeout);

        public async Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default)
        {
            _entered.GetOrAdd(item.Id, static _ => NewSignal()).TrySetResult();
            await _released.GetOrAdd(item.Id, static _ => NewSignal()).Task.WaitAsync(ct);
            await store.UpdateAsync(item.With(WorkItemState.Done), ct);
        }

        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private static async Task<bool> WaitForSignalAsync(TaskCompletionSource signal, TimeSpan timeout)
        {
            var completed = await Task.WhenAny(signal.Task, Task.Delay(timeout));
            return completed == signal.Task;
        }
    }
}
