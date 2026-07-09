using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Serilog;
using Serilog.Events;

namespace CodeyBox.Tests;

/// <summary>
/// Behavioural tests for <see cref="AgentRestoreRetryScheduler"/>. Covers the
/// four acceptance points called out in the brief: restore trigger,
/// infra-vs-real filter, idempotency, and window bounding. Each test isolates
/// a single bound so a regression in any one is unambiguous.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class AgentRestoreRetrySchedulerTests : IDisposable
{
    private readonly TestSink _sink = new();
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(),
        $"codeybox-agent-restore-retry-{Guid.NewGuid():N}.db");

    public AgentRestoreRetrySchedulerTests()
    {
        Log.Logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Sink(_sink)
            .CreateLogger();
    }

    public void Dispose()
    {
        Log.CloseAndFlush();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task Sweep_RequeuesInfraFailedItemPinnedToRestoredAgent_InWindow()
    {
        using var store = new SqliteWorkItemStore(_dbPath);
        var queue = new InMemoryTaskQueue();
        using var involvement = new SqliteAgentInvolvementStore(_dbPath);
        var scheduler = NewScheduler(store, queue, enabled: true, involvement: involvement);

        var outageStart = DateTimeOffset.UtcNow.AddMinutes(-15);
        var item = NewItem(
            agent: AgentKind.Claude,
            state: WorkItemState.Failed,
            failureKind: WorkItemFailureKinds.Infrastructure,
            updatedAt: outageStart.AddMinutes(5));
        await store.CreateAsync(item);
        await RecordFailedInvolvementAsync(involvement, item.Id, AgentKind.Claude, outageStart.AddMinutes(5));

        var restoredAt = DateTimeOffset.UtcNow;
        var summary = await scheduler.SweepForTestAsync(
            new AgentRestoredEvent(AgentKind.Claude, outageStart, restoredAt));

        Assert.Equal(1, summary.Requeued);
        Assert.Equal(0, summary.Skipped);

        var afterSweep = await store.GetAsync(item.Id);
        Assert.NotNull(afterSweep);
        Assert.Equal(WorkItemState.Queued, afterSweep!.State);
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public async Task Sweep_SkipsGenuineWorkFailures_OnlyInfraShapedAreRequeued()
    {
        using var store = new SqliteWorkItemStore(_dbPath);
        var queue = new InMemoryTaskQueue();
        using var involvement = new SqliteAgentInvolvementStore(_dbPath);
        var scheduler = NewScheduler(store, queue, enabled: true, involvement: involvement);

        var outageStart = DateTimeOffset.UtcNow.AddMinutes(-30);

        var infraItem = NewItem(
            agent: AgentKind.Claude,
            state: WorkItemState.Failed,
            failureKind: WorkItemFailureKinds.Infrastructure,
            updatedAt: outageStart.AddMinutes(5));
        var buildItem = NewItem(
            agent: AgentKind.Claude,
            state: WorkItemState.Failed,
            failureKind: "build",
            updatedAt: outageStart.AddMinutes(5));
        var agentInternalItem = NewItem(
            agent: AgentKind.Claude,
            state: WorkItemState.Failed,
            failureKind: "agent",
            updatedAt: outageStart.AddMinutes(5));
        var configItem = NewItem(
            agent: AgentKind.Claude,
            state: WorkItemState.Failed,
            failureKind: "configuration",
            updatedAt: outageStart.AddMinutes(5));
        var authRequiredItem = NewItem(
            agent: AgentKind.Claude,
            state: WorkItemState.Failed,
            failureKind: WorkItemFailureKinds.AuthRequired,
            updatedAt: outageStart.AddMinutes(5),
            authFailureScope: WorkItemAuthFailureScope.Fleet);
        var agentUnavailableItem = NewItem(
            agent: AgentKind.Claude,
            state: WorkItemState.Failed,
            failureKind: WorkItemFailureKinds.AgentUnavailable,
            updatedAt: outageStart.AddMinutes(5));
        var aggregateRoutingItem = NewItem(
            agent: AgentKind.Claude,
            state: WorkItemState.Failed,
            failureKind: WorkItemFailureKinds.AgentRoutingUnavailable,
            updatedAt: outageStart.AddMinutes(5));

        foreach (var item in new[] { infraItem, buildItem, agentInternalItem, configItem, authRequiredItem, agentUnavailableItem, aggregateRoutingItem })
        {
            await store.CreateAsync(item);
        }
        await RecordFailedInvolvementAsync(involvement, infraItem.Id, AgentKind.Claude, outageStart.AddMinutes(5));

        var summary = await scheduler.SweepForTestAsync(
            new AgentRestoredEvent(AgentKind.Claude, outageStart, DateTimeOffset.UtcNow));

        // infrastructure + auth_required + agent_unavailable = 3 candidates.
        // build / agent / configuration are deterministic and must NOT requeue.
        Assert.Equal(3, summary.Requeued);

        Assert.Equal(WorkItemState.Queued, (await store.GetAsync(infraItem.Id))!.State);
        Assert.Equal(WorkItemState.Queued, (await store.GetAsync(authRequiredItem.Id))!.State);
        Assert.Equal(WorkItemState.Queued, (await store.GetAsync(agentUnavailableItem.Id))!.State);
        Assert.Equal(WorkItemState.Failed, (await store.GetAsync(aggregateRoutingItem.Id))!.State);
        Assert.Equal(WorkItemState.Failed, (await store.GetAsync(buildItem.Id))!.State);
        Assert.Equal(WorkItemState.Failed, (await store.GetAsync(agentInternalItem.Id))!.State);
        Assert.Equal(WorkItemState.Failed, (await store.GetAsync(configItem.Id))!.State);
    }

    [Fact]
    public async Task Sweep_IsIdempotent_RepeatedRestoreEventDoesNotDoubleRequeue()
    {
        using var store = new SqliteWorkItemStore(_dbPath);
        var queue = new InMemoryTaskQueue();
        var scheduler = NewScheduler(store, queue, enabled: true);

        var outageStart = DateTimeOffset.UtcNow.AddMinutes(-10);
        var item = NewItem(
            agent: AgentKind.Claude,
            state: WorkItemState.Failed,
            failureKind: WorkItemFailureKinds.AgentUnavailable,
            updatedAt: outageStart.AddMinutes(2));
        await store.CreateAsync(item);

        var evt = new AgentRestoredEvent(AgentKind.Claude, outageStart, DateTimeOffset.UtcNow);

        var firstSweep = await scheduler.SweepForTestAsync(evt);
        Assert.Equal(1, firstSweep.Requeued);
        Assert.Equal(WorkItemState.Queued, (await store.GetAsync(item.Id))!.State);
        Assert.Equal(1, queue.Count);

        // Firing the same event again must be a no-op — the item is now Queued,
        // not Failed/MergeConflictResolutionFailed, so it's no longer a candidate
        // for the sweep. This protects against duplicated restore signals (operator
        // reset + smoke probe pass race) producing duplicate enqueues.
        var secondSweep = await scheduler.SweepForTestAsync(evt);
        Assert.Equal(0, secondSweep.Requeued);
        Assert.Equal(1, queue.Count);

        var afterFirstRetry = await store.GetAsync(item.Id);
        Assert.NotNull(afterFirstRetry);
        await store.UpdateAsync(afterFirstRetry! with
        {
            State = WorkItemState.Failed,
            FailureKind = WorkItemFailureKinds.AgentUnavailable,
            LastError = "agent still broken after restore retry",
            UpdatedAt = evt.RestoredAt.AddMinutes(1),
        });

        var duplicateAfterRefailure = await scheduler.SweepForTestAsync(
            evt with { RestoredAt = evt.RestoredAt.AddSeconds(30) });
        Assert.Equal(0, duplicateAfterRefailure.Requeued);
        Assert.Equal(1, queue.Count);
        Assert.Equal(WorkItemState.Failed, (await store.GetAsync(item.Id))!.State);
    }

    [Fact]
    public async Task Sweep_ConcurrentDuplicateRestoreEvents_OnlyOneRetryWritesAndEnqueues()
    {
        using var store = new SqliteWorkItemStore(_dbPath);
        var queue = new InMemoryTaskQueue();
        var scheduler = NewScheduler(store, queue, enabled: true);

        var outageStart = DateTimeOffset.UtcNow.AddMinutes(-10);
        var item = NewItem(
            agent: AgentKind.Claude,
            state: WorkItemState.Failed,
            failureKind: WorkItemFailureKinds.AgentUnavailable,
            updatedAt: outageStart.AddMinutes(2));
        await store.CreateAsync(item);

        var evt = new AgentRestoredEvent(AgentKind.Claude, outageStart, DateTimeOffset.UtcNow);
        var sweeps = await Task.WhenAll(
            scheduler.SweepForTestAsync(evt),
            scheduler.SweepForTestAsync(evt));

        Assert.Equal(1, sweeps.Sum(static sweep => sweep.Requeued));
        Assert.Equal(1, queue.Count);
        Assert.Equal(WorkItemState.Queued, (await store.GetAsync(item.Id))!.State);
    }

    [Fact]
    public async Task Sweep_BoundedByWindow_DoesNotRequeueItemFailedBeforeOutage()
    {
        using var store = new SqliteWorkItemStore(_dbPath);
        var queue = new InMemoryTaskQueue();
        var scheduler = NewScheduler(store, queue, enabled: true);

        var outageStart = DateTimeOffset.UtcNow.AddMinutes(-30);
        // LookbackGrace defaults to 30min — failure 2 hours before outage is OUT.
        var ancientFailure = NewItem(
            agent: AgentKind.Claude,
            state: WorkItemState.Failed,
            failureKind: WorkItemFailureKinds.AgentUnavailable,
            updatedAt: outageStart.AddHours(-2));
        await store.CreateAsync(ancientFailure);

        // Failure 10 min before outage start IS in window (within the 30-min grace).
        var withinGrace = NewItem(
            agent: AgentKind.Claude,
            state: WorkItemState.Failed,
            failureKind: WorkItemFailureKinds.AgentUnavailable,
            updatedAt: outageStart.AddMinutes(-10));
        await store.CreateAsync(withinGrace);

        var summary = await scheduler.SweepForTestAsync(
            new AgentRestoredEvent(AgentKind.Claude, outageStart, DateTimeOffset.UtcNow));

        Assert.Equal(1, summary.Requeued);
        Assert.Equal(WorkItemState.Failed, (await store.GetAsync(ancientFailure.Id))!.State);
        Assert.Equal(WorkItemState.Queued, (await store.GetAsync(withinGrace.Id))!.State);
    }

    [Fact]
    public async Task Sweep_IgnoresItemsPinnedToOtherAgent()
    {
        using var store = new SqliteWorkItemStore(_dbPath);
        var queue = new InMemoryTaskQueue();
        var scheduler = NewScheduler(store, queue, enabled: true);

        var outageStart = DateTimeOffset.UtcNow.AddMinutes(-20);
        var claudeItem = NewItem(
            agent: AgentKind.Claude,
            state: WorkItemState.Failed,
            failureKind: WorkItemFailureKinds.AgentUnavailable,
            updatedAt: outageStart.AddMinutes(5));
        var codexItem = NewItem(
            agent: AgentKind.Codex,
            state: WorkItemState.Failed,
            failureKind: WorkItemFailureKinds.AgentUnavailable,
            updatedAt: outageStart.AddMinutes(5));
        await store.CreateAsync(claudeItem);
        await store.CreateAsync(codexItem);

        var summary = await scheduler.SweepForTestAsync(
            new AgentRestoredEvent(AgentKind.Claude, outageStart, DateTimeOffset.UtcNow));

        Assert.Equal(1, summary.Requeued);
        Assert.Equal(WorkItemState.Queued, (await store.GetAsync(claudeItem.Id))!.State);
        Assert.Equal(WorkItemState.Failed, (await store.GetAsync(codexItem.Id))!.State);
    }

    [Fact]
    public async Task Sweep_CandidateCapAppliesAfterRestoredAgentPrefilter()
    {
        using var store = new SqliteWorkItemStore(_dbPath);
        var queue = new InMemoryTaskQueue();
        var scheduler = NewScheduler(store, queue, enabled: true, maxCandidatesPerSweep: 1);

        var outageStart = DateTimeOffset.UtcNow.AddMinutes(-20);
        var unrelatedEarlier = NewItem(
            agent: AgentKind.Codex,
            state: WorkItemState.Failed,
            failureKind: WorkItemFailureKinds.AgentUnavailable,
            updatedAt: outageStart.AddMinutes(1));
        var restoredAgentItem = NewItem(
            agent: AgentKind.Claude,
            state: WorkItemState.Failed,
            failureKind: WorkItemFailureKinds.AgentUnavailable,
            updatedAt: outageStart.AddMinutes(2));
        await store.CreateAsync(unrelatedEarlier);
        await store.CreateAsync(restoredAgentItem);

        var summary = await scheduler.SweepForTestAsync(
            new AgentRestoredEvent(AgentKind.Claude, outageStart, DateTimeOffset.UtcNow));

        Assert.Equal(1, summary.Requeued);
        Assert.Equal(WorkItemState.Failed, (await store.GetAsync(unrelatedEarlier.Id))!.State);
        Assert.Equal(WorkItemState.Queued, (await store.GetAsync(restoredAgentItem.Id))!.State);
    }

    [Fact]
    public async Task Sweep_CandidateCapAppliesAfterLatestInvolvementAttribution()
    {
        using var store = new SqliteWorkItemStore(_dbPath);
        using var involvement = new SqliteAgentInvolvementStore(_dbPath);
        var queue = new InMemoryTaskQueue();
        var scheduler = NewScheduler(
            store,
            queue,
            enabled: true,
            involvement: involvement,
            maxCandidatesPerSweep: 1);

        var outageStart = DateTimeOffset.UtcNow.AddMinutes(-20);
        var attributedToOtherAgent = NewItem(
            agent: AgentKind.Claude,
            state: WorkItemState.Failed,
            failureKind: WorkItemFailureKinds.AgentUnavailable,
            updatedAt: outageStart.AddMinutes(1));
        var restoredAgentItem = NewItem(
            agent: AgentKind.Claude,
            state: WorkItemState.Failed,
            failureKind: WorkItemFailureKinds.AgentUnavailable,
            updatedAt: outageStart.AddMinutes(2));
        await store.CreateAsync(attributedToOtherAgent);
        await store.CreateAsync(restoredAgentItem);
        await RecordFailedInvolvementAsync(
            involvement,
            attributedToOtherAgent.Id,
            AgentKind.Codex,
            outageStart.AddMinutes(1),
            AgentInvolvementOutcomes.FailureAuth);

        var summary = await scheduler.SweepForTestAsync(
            new AgentRestoredEvent(AgentKind.Claude, outageStart, DateTimeOffset.UtcNow));

        Assert.Equal(1, summary.Requeued);
        Assert.Equal(WorkItemState.Failed, (await store.GetAsync(attributedToOtherAgent.Id))!.State);
        Assert.Equal(WorkItemState.Queued, (await store.GetAsync(restoredAgentItem.Id))!.State);
    }

    [Fact]
    public async Task Sweep_TreatsInfraFailureKindCaseInsensitivelyInSqliteStore()
    {
        using var store = new SqliteWorkItemStore(_dbPath);
        var queue = new InMemoryTaskQueue();
        var scheduler = NewScheduler(store, queue, enabled: true);

        var outageStart = DateTimeOffset.UtcNow.AddMinutes(-10);
        var item = NewItem(
            agent: AgentKind.Claude,
            state: WorkItemState.Failed,
            failureKind: WorkItemFailureKinds.AgentUnavailable.ToUpperInvariant(),
            updatedAt: outageStart.AddMinutes(2));
        await store.CreateAsync(item);

        var summary = await scheduler.SweepForTestAsync(
            new AgentRestoredEvent(AgentKind.Claude, outageStart, DateTimeOffset.UtcNow));

        Assert.Equal(1, summary.Requeued);
        Assert.Equal(WorkItemState.Queued, (await store.GetAsync(item.Id))!.State);
    }

    [Fact]
    public async Task Sweep_SkipsWhenOutageStartedAtIsNull()
    {
        using var store = new SqliteWorkItemStore(_dbPath);
        var queue = new InMemoryTaskQueue();
        var scheduler = NewScheduler(store, queue, enabled: true);

        var item = NewItem(
            agent: AgentKind.Claude,
            state: WorkItemState.Failed,
            failureKind: WorkItemFailureKinds.AgentUnavailable,
            updatedAt: DateTimeOffset.UtcNow.AddMinutes(-10));
        await store.CreateAsync(item);

        // OutageStartedAt=null happens when an operator resets an agent that was
        // never marked failed (or on a startup pass). Without a window the sweep
        // would have to either retry EVERY infra-failed item (overreach) or
        // retry NONE (the safe default). The brief requires window bounding —
        // null window = no-op.
        var summary = await scheduler.SweepForTestAsync(
            new AgentRestoredEvent(AgentKind.Claude, null, DateTimeOffset.UtcNow));

        Assert.Equal(0, summary.Requeued);
        Assert.Equal(WorkItemState.Failed, (await store.GetAsync(item.Id))!.State);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task Sweep_AlsoCoversMergeConflictResolutionFailed()
    {
        using var store = new SqliteWorkItemStore(_dbPath);
        var queue = new InMemoryTaskQueue();
        using var involvement = new SqliteAgentInvolvementStore(_dbPath);
        var scheduler = NewScheduler(store, queue, enabled: true, involvement: involvement);

        var outageStart = DateTimeOffset.UtcNow.AddMinutes(-15);
        var item = NewItem(
            agent: AgentKind.Claude,
            state: WorkItemState.MergeConflictResolutionFailed,
            failureKind: WorkItemFailureKinds.Infrastructure,
            updatedAt: outageStart.AddMinutes(5));
        await store.CreateAsync(item);
        await RecordFailedInvolvementAsync(involvement, item.Id, AgentKind.Claude, outageStart.AddMinutes(5));

        var summary = await scheduler.SweepForTestAsync(
            new AgentRestoredEvent(AgentKind.Claude, outageStart, DateTimeOffset.UtcNow));

        Assert.Equal(1, summary.Requeued);
        Assert.Equal(WorkItemState.Queued, (await store.GetAsync(item.Id))!.State);
    }

    [Fact]
    public async Task Sweep_StopsAtConfiguredCandidateCap()
    {
        using var store = new SqliteWorkItemStore(_dbPath);
        var queue = new InMemoryTaskQueue();
        var scheduler = NewScheduler(store, queue, enabled: true, maxCandidatesPerSweep: 3);

        var outageStart = DateTimeOffset.UtcNow.AddMinutes(-10);
        var items = new List<WorkItem>();
        for (var i = 0; i < 5; i++)
        {
            var item = NewItem(
                agent: AgentKind.Claude,
                state: WorkItemState.Failed,
                failureKind: WorkItemFailureKinds.AgentUnavailable,
                updatedAt: outageStart.AddMinutes(2 + i));
            await store.CreateAsync(item);
            items.Add(item);
        }

        var summary = await scheduler.SweepForTestAsync(
            new AgentRestoredEvent(AgentKind.Claude, outageStart, DateTimeOffset.UtcNow));

        Assert.Equal(3, summary.Requeued);
        Assert.Equal(0, summary.Skipped);
        var queuedCount = 0;
        foreach (var item in items)
        {
            var state = (await store.GetAsync(item.Id))!.State;
            if (state == WorkItemState.Queued) queuedCount++;
        }
        Assert.Equal(3, queuedCount);
    }

    [Fact]
    public async Task RestoreSignal_FiresOnSmokeFailToPassTransition()
    {
        var registry = NewRegistry();
        var captured = new List<AgentRestoredEvent>();
        ((IAgentRestoreSignal)registry).AgentRestored += captured.Add;

        registry.MarkSmokeResult(
            AgentKind.Claude,
            new AgentSmokeResult(false, "auth", TimeSpan.Zero, SmokeFailureCategory.Persistent));
        Assert.Empty(captured);

        registry.MarkSmokeResult(
            AgentKind.Claude,
            new AgentSmokeResult(true, null, TimeSpan.FromMilliseconds(50), SmokeFailureCategory.None));

        Assert.Single(captured);
        Assert.Equal(AgentKind.Claude.Value, captured[0].Agent.Value);
        Assert.NotNull(captured[0].OutageStartedAt);

        await Task.CompletedTask;
    }

    [Fact]
    public void RestoreSignal_OutageStartedAt_ReflectsFirstFailure_NotLast()
    {
        // Pin the invariant that OutageStartedAt is the FIRST failure of the
        // outage streak, not the most recent. A long multi-hour outage with
        // 5-minute periodic smoke probes used to overwrite the timestamp
        // every probe, leaving the sweep with a ~5-minute window that
        // silently excluded items failed earlier during the outage.
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var registry = new AgentAvailabilityRegistry(
            new AvailabilityOptions(), fakeTime, NullLogger<AgentAvailabilityRegistry>.Instance);
        var captured = new List<AgentRestoredEvent>();
        ((IAgentRestoreSignal)registry).AgentRestored += captured.Add;

        var firstFailureAt = fakeTime.GetUtcNow();
        registry.MarkSmokeResult(
            AgentKind.Claude,
            new AgentSmokeResult(false, "auth", TimeSpan.Zero, SmokeFailureCategory.Persistent));

        // Several follow-up smoke failures move LastSmokeFailedAt forward.
        for (var i = 0; i < 5; i++)
        {
            fakeTime.Advance(TimeSpan.FromMinutes(30));
            registry.MarkSmokeResult(
                AgentKind.Claude,
                new AgentSmokeResult(false, "auth", TimeSpan.Zero, SmokeFailureCategory.Persistent));
        }

        fakeTime.Advance(TimeSpan.FromMinutes(10));
        registry.MarkSmokeResult(
            AgentKind.Claude,
            new AgentSmokeResult(true, null, TimeSpan.FromMilliseconds(50), SmokeFailureCategory.None));

        Assert.Single(captured);
        Assert.Equal(firstFailureAt, captured[0].OutageStartedAt);
    }

    [Fact]
    public void RestoreSignal_AuthRequiredThenReset_HasOutageStartedAt()
    {
        // Brief's headline scenario: antigravity auth fix on 2026-06-10 left
        // 27 items hand-retried. The flow is MarkAuthRequired → operator
        // POST /admin/agent/{name}/reset. The prior implementation used
        // LastSmokeFailedAt as the outage anchor, which MarkAuthRequired never
        // touched — so Reset emitted OutageStartedAt=null and the sweep was a
        // silent no-op. Pin that the auth-only path produces a non-null
        // OutageStartedAt anchored at the moment MarkAuthRequired ran.
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var registry = new AgentAvailabilityRegistry(
            new AvailabilityOptions(), fakeTime, NullLogger<AgentAvailabilityRegistry>.Instance);
        var captured = new List<AgentRestoredEvent>();
        ((IAgentRestoreSignal)registry).AgentRestored += captured.Add;

        var authFailureAt = fakeTime.GetUtcNow();
        registry.MarkAuthRequired(AgentKind.Antigravity, "OAuth refresh token rejected");

        fakeTime.Advance(TimeSpan.FromHours(2));
        NewReset(registry).Reset(AgentKind.Antigravity);

        Assert.Single(captured);
        Assert.Equal(authFailureAt, captured[0].OutageStartedAt);
    }

    [Fact]
    public async Task Sweep_RequeuesInfraFailures_AfterMarkAuthRequiredThenReset()
    {
        // End-to-end pin for the brief's primary motivating scenario.
        // Bench an agent via MarkAuthRequired (the path PipelineRunner takes
        // when it detects a login prompt in agent output — exactly the
        // antigravity 2026-06-10 flow). After the operator fixes auth and
        // calls Reset, the auth-failed work items should be auto-requeued.
        // Pre-fix this test would fail: MarkAuthRequired did not touch
        // LastSmokeFailedAt so OutageStartedAt was null and the sweep was a
        // no-op even though the brief's acceptance criterion required it.
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var registry = new AgentAvailabilityRegistry(
            new AvailabilityOptions(), fakeTime, NullLogger<AgentAvailabilityRegistry>.Instance);

        using var store = new SqliteWorkItemStore(_dbPath);
        var queue = new InMemoryTaskQueue();
        var capturedRestores = new List<AgentRestoredEvent>();
        ((IAgentRestoreSignal)registry).AgentRestored += capturedRestores.Add;
        var scheduler = NewScheduler(store, queue, enabled: true);

        registry.MarkAuthRequired(AgentKind.Antigravity, "OAuth refresh token rejected");

        fakeTime.Advance(TimeSpan.FromMinutes(10));
        var item = NewItem(
            agent: AgentKind.Antigravity,
            state: WorkItemState.Failed,
            failureKind: WorkItemFailureKinds.AuthRequired,
            updatedAt: fakeTime.GetUtcNow(),
            authFailureScope: WorkItemAuthFailureScope.Fleet);
        await store.CreateAsync(item);

        fakeTime.Advance(TimeSpan.FromMinutes(20));
        NewReset(registry).Reset(AgentKind.Antigravity);
        Assert.Single(capturedRestores);
        Assert.NotNull(capturedRestores[0].OutageStartedAt);

        var summary = await scheduler.SweepForTestAsync(capturedRestores[0]);

        Assert.Equal(1, summary.Requeued);
        Assert.Equal(WorkItemState.Queued, (await store.GetAsync(item.Id))!.State);
    }

    [Fact]
    public async Task Sweep_BoundedByWindow_DoesNotRequeueItemFailedAfterPostRestoreMargin()
    {
        using var store = new SqliteWorkItemStore(_dbPath);
        var queue = new InMemoryTaskQueue();
        var scheduler = NewScheduler(store, queue, enabled: true);

        var outageStart = DateTimeOffset.UtcNow.AddMinutes(-10);
        var restoredAt = outageStart.AddMinutes(10);
        var insideMargin = NewItem(
            agent: AgentKind.Claude,
            state: WorkItemState.Failed,
            failureKind: WorkItemFailureKinds.AgentUnavailable,
            updatedAt: restoredAt.AddMinutes(4));
        var afterMargin = NewItem(
            agent: AgentKind.Claude,
            state: WorkItemState.Failed,
            failureKind: WorkItemFailureKinds.AgentUnavailable,
            updatedAt: restoredAt.AddMinutes(6));
        await store.CreateAsync(insideMargin);
        await store.CreateAsync(afterMargin);

        var summary = await scheduler.SweepForTestAsync(
            new AgentRestoredEvent(AgentKind.Claude, outageStart, restoredAt));

        Assert.Equal(1, summary.Requeued);
        Assert.Equal(WorkItemState.Queued, (await store.GetAsync(insideMargin.Id))!.State);
        Assert.Equal(WorkItemState.Failed, (await store.GetAsync(afterMargin.Id))!.State);
    }

    [Fact]
    public async Task Sweep_NullOutageStartedAt_StillReturnsSummaryForObservability()
    {
        // Null window still emits sweep-level telemetry, but no item can be
        // retried because there is no bounded outage interval to select from.
        using var store = new SqliteWorkItemStore(_dbPath);
        var queue = new InMemoryTaskQueue();
        var scheduler = NewScheduler(store, queue, enabled: true);

        var summary = await scheduler.SweepForTestAsync(
            new AgentRestoredEvent(AgentKind.Claude, null, DateTimeOffset.UtcNow));

        Assert.Equal(0, summary.Requeued);
        Assert.Equal(0, summary.Skipped);
    }

    [Fact]
    public async Task RestoreSignal_BackgroundService_RequeuesFromRegistryEvent()
    {
        using var store = new SqliteWorkItemStore(_dbPath);
        var queue = new InMemoryTaskQueue();
        var registry = NewRegistry();
        var scheduler = NewScheduler(store, queue, enabled: true, signal: registry);

        await scheduler.StartAsync(CancellationToken.None);
        try
        {
            registry.MarkSmokeResult(
                AgentKind.Claude,
                new AgentSmokeResult(false, "missing binary", TimeSpan.Zero, SmokeFailureCategory.Persistent));

            var item = NewItem(
                agent: AgentKind.Claude,
                state: WorkItemState.Failed,
                failureKind: WorkItemFailureKinds.AgentUnavailable,
                updatedAt: DateTimeOffset.UtcNow);
            await store.CreateAsync(item);

            registry.MarkSmokeResult(
                AgentKind.Claude,
                new AgentSmokeResult(true, null, TimeSpan.FromMilliseconds(10), SmokeFailureCategory.None));

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Assert.Equal(item.Id, await queue.DequeueAsync(timeout.Token));
            Assert.Equal(WorkItemState.Queued, (await store.GetAsync(item.Id))!.State);
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task RestoreSignal_BackgroundService_Disabled_DropsEventWithoutRequeueOrSweepAlert()
    {
        using var store = new SqliteWorkItemStore(_dbPath);
        var queue = new InMemoryTaskQueue();
        var registry = NewRegistry();
        var webhooks = new CapturingWebhookDispatcher();
        var logger = new CapturingLogger<AgentRestoreRetryScheduler>();
        var scheduler = NewScheduler(
            store,
            queue,
            enabled: false,
            schedulerLogger: logger,
            signal: registry,
            webhooks: webhooks);

        await scheduler.StartAsync(CancellationToken.None);
        try
        {
            registry.MarkSmokeResult(
                AgentKind.Claude,
                new AgentSmokeResult(false, "missing binary", TimeSpan.Zero, SmokeFailureCategory.Persistent));

            var item = NewItem(
                agent: AgentKind.Claude,
                state: WorkItemState.Failed,
                failureKind: WorkItemFailureKinds.AgentUnavailable,
                updatedAt: DateTimeOffset.UtcNow);
            await store.CreateAsync(item);

            registry.MarkSmokeResult(
                AgentKind.Claude,
                new AgentSmokeResult(true, null, TimeSpan.FromMilliseconds(10), SmokeFailureCategory.None));

            await logger.WaitForEntryAsync(
                e => e.Level == LogLevel.Debug && e.Message.Contains("feature disabled", StringComparison.Ordinal),
                TimeSpan.FromSeconds(5));

            Assert.Equal(WorkItemState.Failed, (await store.GetAsync(item.Id))!.State);
            Assert.Equal(0, queue.Count);
            Assert.DoesNotContain(webhooks.Events, e => e.Event == "agent.restore_requeue_swept");
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Sweep_DoesNotInferDefaultAgentWhenFailureLacksAgentAttribution()
    {
        using var store = new SqliteWorkItemStore(_dbPath);
        var queue = new InMemoryTaskQueue();
        var projects = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId("proj"),
            DisplayName = "Test",
            RepositoryUrl = "https://example.invalid/repo.git",
            DefaultAgent = AgentKind.Claude,
        });
        var scheduler = NewScheduler(store, queue, enabled: true, projects: projects);

        var outageStart = DateTimeOffset.UtcNow.AddMinutes(-10);
        var item = NewItem(
            agent: null,
            state: WorkItemState.Failed,
            failureKind: WorkItemFailureKinds.Infrastructure,
            updatedAt: outageStart.AddMinutes(1));
        await store.CreateAsync(item);

        var summary = await scheduler.SweepForTestAsync(
            new AgentRestoredEvent(AgentKind.Claude, outageStart, DateTimeOffset.UtcNow));

        Assert.Equal(0, summary.Requeued);
        Assert.Equal(WorkItemState.Failed, (await store.GetAsync(item.Id))!.State);
    }

    [Fact]
    public async Task Sweep_DoesNotFallbackToWorkItemAgentForGenericInfrastructureWithoutInvolvement()
    {
        using var store = new SqliteWorkItemStore(_dbPath);
        var queue = new InMemoryTaskQueue();
        var scheduler = NewScheduler(store, queue, enabled: true);

        var outageStart = DateTimeOffset.UtcNow.AddMinutes(-10);
        var item = NewItem(
            agent: AgentKind.Claude,
            state: WorkItemState.Failed,
            failureKind: WorkItemFailureKinds.Infrastructure,
            updatedAt: outageStart.AddMinutes(2));
        await store.CreateAsync(item);

        var summary = await scheduler.SweepForTestAsync(
            new AgentRestoredEvent(AgentKind.Claude, outageStart, DateTimeOffset.UtcNow));

        Assert.Equal(0, summary.Requeued);
        Assert.Equal(WorkItemState.Failed, (await store.GetAsync(item.Id))!.State);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task Sweep_UsesLatestFailedInvolvementWhenWorkItemAgentIsStale()
    {
        using var store = new SqliteWorkItemStore(_dbPath);
        var queue = new InMemoryTaskQueue();
        using var involvement = new SqliteAgentInvolvementStore(_dbPath);
        var scheduler = NewScheduler(store, queue, enabled: true, involvement: involvement);

        var outageStart = DateTimeOffset.UtcNow.AddMinutes(-10);
        var item = NewItem(
            agent: AgentKind.Claude,
            state: WorkItemState.Failed,
            failureKind: WorkItemFailureKinds.AuthRequired,
            updatedAt: outageStart.AddMinutes(2),
            lastError: "auth required from agent output during audit: login prompt matched",
            authFailureScope: WorkItemAuthFailureScope.Fleet);
        await store.CreateAsync(item);

        var involvementId = Guid.NewGuid();
        await involvement.RecordStartAsync(new AgentInvolvement(
            Id: involvementId,
            WorkItemId: item.Id,
            AgentKind: AgentKind.Codex,
            ModelId: null,
            Phase: "audit:llm-review",
            StartedAt: outageStart.AddMinutes(1),
            EndedAt: null,
            Iteration: 1,
            Outcome: null));
        await involvement.FinalizeAsync(involvementId, outageStart.AddMinutes(2), AgentInvolvementOutcomes.FailureAuth);

        var summary = await scheduler.SweepForTestAsync(
            new AgentRestoredEvent(AgentKind.Codex, outageStart, DateTimeOffset.UtcNow));

        Assert.Equal(1, summary.Requeued);
        Assert.Equal(WorkItemState.Queued, (await store.GetAsync(item.Id))!.State);
    }

    [Fact]
    public async Task Sweep_UsesSqliteInvolvementForGenericInfrastructureWhenWorkItemAgentIsStale()
    {
        using var store = new SqliteWorkItemStore(_dbPath);
        using var involvement = new SqliteAgentInvolvementStore(_dbPath);
        var queue = new InMemoryTaskQueue();
        var scheduler = NewScheduler(store, queue, enabled: true, involvement: involvement);

        var outageStart = DateTimeOffset.UtcNow.AddMinutes(-10);
        var item = NewItem(
            agent: AgentKind.Claude,
            state: WorkItemState.Failed,
            failureKind: WorkItemFailureKinds.Infrastructure,
            updatedAt: outageStart.AddMinutes(2),
            lastError: "audit agent execution failed: codex binary missing");
        await store.CreateAsync(item);
        await RecordFailedInvolvementAsync(
            involvement,
            item.Id,
            AgentKind.Codex,
            outageStart.AddMinutes(2),
            AgentInvolvementOutcomes.FailureInfrastructure);

        var summary = await scheduler.SweepForTestAsync(
            new AgentRestoredEvent(AgentKind.Codex, outageStart, DateTimeOffset.UtcNow));

        Assert.Equal(1, summary.Requeued);
        Assert.Equal(WorkItemState.Queued, (await store.GetAsync(item.Id))!.State);
    }

    [Fact]
    public async Task Sweep_FleetAuthPrefilterRunsBeforeLimit()
    {
        using var store = new SqliteWorkItemStore(_dbPath);
        using var involvement = new SqliteAgentInvolvementStore(_dbPath);
        var queue = new InMemoryTaskQueue();
        var scheduler = NewScheduler(
            store,
            queue,
            enabled: true,
            involvement: involvement,
            maxCandidatesPerSweep: 1);

        var outageStart = DateTimeOffset.UtcNow.AddMinutes(-10);
        var unrelated = NewItem(
            agent: AgentKind.Claude,
            state: WorkItemState.Failed,
            failureKind: WorkItemFailureKinds.AuthRequired,
            updatedAt: outageStart.AddMinutes(1),
            lastError: "claude auth required",
            authFailureScope: WorkItemAuthFailureScope.Fleet);
        var restoredAgentItem = NewItem(
            agent: AgentKind.Codex,
            state: WorkItemState.Failed,
            failureKind: WorkItemFailureKinds.AuthRequired,
            updatedAt: outageStart.AddMinutes(2),
            lastError: "codex auth required",
            authFailureScope: WorkItemAuthFailureScope.Fleet);
        await store.CreateAsync(unrelated);
        await store.CreateAsync(restoredAgentItem);

        var summary = await scheduler.SweepForTestAsync(
            new AgentRestoredEvent(AgentKind.Codex, outageStart, DateTimeOffset.UtcNow));

        Assert.Equal(1, summary.Requeued);
        Assert.Equal(WorkItemState.Failed, (await store.GetAsync(unrelated.Id))!.State);
        Assert.Equal(WorkItemState.Queued, (await store.GetAsync(restoredAgentItem.Id))!.State);
    }

    [Fact]
    public async Task Sweep_IgnoresOldFailedInvolvementWhenTerminalAgentIsCurrent()
    {
        using var store = new SqliteWorkItemStore(_dbPath);
        var queue = new InMemoryTaskQueue();
        var involvement = new InMemoryAgentInvolvementStore();
        var scheduler = NewScheduler(store, queue, enabled: true, involvement: involvement);

        var outageStart = DateTimeOffset.UtcNow.AddMinutes(-40);
        var item = NewItem(
            agent: AgentKind.Claude,
            state: WorkItemState.Failed,
            failureKind: WorkItemFailureKinds.AgentUnavailable,
            updatedAt: outageStart.AddMinutes(30));
        await store.CreateAsync(item);

        var oldInvolvementId = Guid.NewGuid();
        await involvement.RecordStartAsync(new AgentInvolvement(
            Id: oldInvolvementId,
            WorkItemId: item.Id,
            AgentKind: AgentKind.Codex,
            ModelId: null,
            Phase: "audit:llm-review",
            StartedAt: outageStart.AddMinutes(1),
            EndedAt: null,
            Iteration: 1,
            Outcome: null));
        await involvement.FinalizeAsync(oldInvolvementId, outageStart.AddMinutes(2), "failure:auth");

        var codexSummary = await scheduler.SweepForTestAsync(
            new AgentRestoredEvent(AgentKind.Codex, outageStart, DateTimeOffset.UtcNow));
        Assert.Equal(0, codexSummary.Requeued);
        Assert.Equal(WorkItemState.Failed, (await store.GetAsync(item.Id))!.State);

        var claudeSummary = await scheduler.SweepForTestAsync(
            new AgentRestoredEvent(AgentKind.Claude, outageStart, DateTimeOffset.UtcNow));
        Assert.Equal(1, claudeSummary.Requeued);
        Assert.Equal(WorkItemState.Queued, (await store.GetAsync(item.Id))!.State);
    }

    [Fact]
    public async Task Sweep_DoesNotRequeueUncorroboratedStdoutOnlyAuthFailures()
    {
        using var store = new SqliteWorkItemStore(_dbPath);
        var queue = new InMemoryTaskQueue();
        var scheduler = NewScheduler(store, queue, enabled: true);

        var outageStart = DateTimeOffset.UtcNow.AddMinutes(-10);
        var itemLocal = NewItem(
            agent: AgentKind.Claude,
            state: WorkItemState.Failed,
            failureKind: WorkItemFailureKinds.AuthRequired,
            updatedAt: outageStart.AddMinutes(2),
            lastError: "auth required from agent output during work: login prompt matched",
            authFailureScope: WorkItemAuthFailureScope.Item);
        var corroborated = NewItem(
            agent: AgentKind.Claude,
            state: WorkItemState.Failed,
            failureKind: WorkItemFailureKinds.AuthRequired,
            updatedAt: outageStart.AddMinutes(3),
            lastError: "auth required from agent output during work: login prompt matched",
            authFailureScope: WorkItemAuthFailureScope.Fleet);
        var legacyItemLocal = NewItem(
            agent: AgentKind.Claude,
            state: WorkItemState.Failed,
            failureKind: WorkItemFailureKinds.AuthRequired,
            updatedAt: outageStart.AddMinutes(4),
            lastError: "auth required from agent output during work: login prompt matched; stdout accepted for item failure only; forced in-VM smoke probe did not corroborate auth");
        await store.CreateAsync(itemLocal);
        await store.CreateAsync(corroborated);
        await store.CreateAsync(legacyItemLocal);

        var summary = await scheduler.SweepForTestAsync(
            new AgentRestoredEvent(AgentKind.Claude, outageStart, DateTimeOffset.UtcNow));

        Assert.Equal(1, summary.Requeued);
        Assert.Equal(WorkItemState.Failed, (await store.GetAsync(itemLocal.Id))!.State);
        Assert.Equal(WorkItemState.Queued, (await store.GetAsync(corroborated.Id))!.State);
        Assert.Equal(WorkItemState.Failed, (await store.GetAsync(legacyItemLocal.Id))!.State);
    }

    [Fact]
    public async Task Sweep_EmitsSweepLevelWebhookAlert()
    {
        using var store = new SqliteWorkItemStore(_dbPath);
        var queue = new InMemoryTaskQueue();
        var webhooks = new CapturingWebhookDispatcher();
        var scheduler = NewScheduler(store, queue, enabled: true, webhooks: webhooks);

        var outageStart = DateTimeOffset.UtcNow.AddMinutes(-10);
        var item = NewItem(
            agent: AgentKind.Claude,
            state: WorkItemState.Failed,
            failureKind: WorkItemFailureKinds.AgentUnavailable,
            updatedAt: outageStart.AddMinutes(2));
        await store.CreateAsync(item);

        await scheduler.SweepForTestAsync(
            new AgentRestoredEvent(AgentKind.Claude, outageStart, DateTimeOffset.UtcNow));

        var sweep = Assert.Single(webhooks.Events, e => e.Event == "agent.restore_requeue_swept");
        Assert.Equal("agent_restore", Detail(sweep, "reason"));
        Assert.Equal("claude", Detail(sweep, "restoredAgent"));
        Assert.Equal("1", Detail(sweep, "requeued"));
        Assert.Equal("0", Detail(sweep, "skipped"));

        var retry = Assert.Single(webhooks.Events, e => e.Event == "work_item.agent_restore_requeued");
        Assert.Equal("agent_restore", Detail(retry, "reason"));
        Assert.Equal("claude", Detail(retry, "restoredAgent"));
        Assert.Equal(WorkItemState.Queued, retry.WorkItem!.State);
    }

    [Fact]
    public async Task Sweep_AuditLogsSweepAndRequeuedItemCounts()
    {
        using var store = new SqliteWorkItemStore(_dbPath);
        var queue = new InMemoryTaskQueue();
        var scheduler = NewScheduler(store, queue, enabled: true);

        var outageStart = DateTimeOffset.UtcNow.AddMinutes(-10);
        var item = NewItem(
            agent: AgentKind.Claude,
            state: WorkItemState.Failed,
            failureKind: WorkItemFailureKinds.AgentUnavailable,
            updatedAt: outageStart.AddMinutes(2));
        await store.CreateAsync(item);

        await scheduler.SweepForTestAsync(
            new AgentRestoredEvent(AgentKind.Claude, outageStart, DateTimeOffset.UtcNow));

        var sweep = Assert.Single(_sink.Events,
            e => GetScalar<string>(e, "EventName") == "agent.restore_requeue_swept");
        Assert.Equal("claude", GetScalar<string>(sweep, "Agent"));
        Assert.Equal(1, GetScalar<int>(sweep, "Requeued"));
        Assert.Equal(0, GetScalar<int>(sweep, "Skipped"));

        var requeuedItem = Assert.Single(_sink.Events,
            e => GetScalar<string>(e, "EventName") == "agent.restore_requeue_item");
        Assert.Equal(item.Id.ToString(), GetScalar<string>(requeuedItem, "WorkItemId"));
        Assert.Equal("claude", GetScalar<string>(requeuedItem, "Agent"));
        Assert.Equal(WorkItemFailureKinds.AgentUnavailable, GetScalar<string>(requeuedItem, "FailureKind"));
        Assert.Equal("work", GetScalar<string>(requeuedItem, "From"));
    }

    [Fact]
    public async Task OperatorReset_PublishesRestoreAfterInVmSmokeCacheInvalidation()
    {
        var registry = NewRegistry();
        var cache = new InVmSmokeCache(TimeSpan.FromMinutes(30));
        cache.Set(
            AgentKind.Claude,
            "baseline",
            new AgentSmokeResult(true, null, TimeSpan.FromMilliseconds(5), SmokeFailureCategory.None));
        var reset = new AgentAvailabilityReset(registry, cache, registry);
        var observedCacheWasCleared = false;
        ((IAgentRestoreSignal)registry).AgentRestored += _ =>
            observedCacheWasCleared = cache.TryGet(AgentKind.Claude, "baseline") is null;

        registry.MarkSmokeResult(
            AgentKind.Claude,
            new AgentSmokeResult(false, "missing binary", TimeSpan.Zero, SmokeFailureCategory.Persistent));

        reset.Reset(AgentKind.Claude);

        Assert.True(observedCacheWasCleared);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task BreakerResetRestoreEventCarriesOutageWindowAndDrivesRequeue()
    {
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var registry = new AgentAvailabilityRegistry(
            new AvailabilityOptions { MaxConsecutiveFastFails = 1 },
            fakeTime,
            NullLogger<AgentAvailabilityRegistry>.Instance);
        var captured = new List<AgentRestoredEvent>();
        ((IAgentRestoreSignal)registry).AgentRestored += captured.Add;

        var outageStart = fakeTime.GetUtcNow();
        registry.RecordRunOutcome(AgentKind.Claude, success: false, duration: TimeSpan.FromMilliseconds(10));
        Assert.False(registry.GetAvailability(AgentKind.Claude).Available);

        using var store = new SqliteWorkItemStore(_dbPath);
        var queue = new InMemoryTaskQueue();
        var item = NewItem(
            agent: AgentKind.Claude,
            state: WorkItemState.Failed,
            failureKind: WorkItemFailureKinds.AgentUnavailable,
            updatedAt: outageStart.AddMinutes(1));
        await store.CreateAsync(item);

        fakeTime.Advance(TimeSpan.FromMinutes(5));
        NewReset(registry).Reset(AgentKind.Claude);

        var evt = Assert.Single(captured);
        Assert.Equal(outageStart, evt.OutageStartedAt);

        var summary = await NewScheduler(store, queue, enabled: true).SweepForTestAsync(evt);
        Assert.Equal(1, summary.Requeued);
        Assert.Equal(WorkItemState.Queued, (await store.GetAsync(item.Id))!.State);
    }

    [Fact]
    public void RestoreSignal_DoesNotFireUntilAllExclusionSourcesClear()
    {
        var registry = NewRegistry();
        var captured = new List<AgentRestoredEvent>();
        ((IAgentRestoreSignal)registry).AgentRestored += captured.Add;

        registry.MarkSmokeResult(
            AgentKind.Claude,
            new AgentSmokeResult(false, "host auth", TimeSpan.Zero, SmokeFailureCategory.Persistent),
            SmokeExclusionSource.HostSmoke);
        registry.MarkSmokeResult(
            AgentKind.Claude,
            new AgentSmokeResult(false, "vm binary", TimeSpan.Zero, SmokeFailureCategory.Persistent),
            SmokeExclusionSource.InVmSmoke);

        registry.MarkSmokeResult(
            AgentKind.Claude,
            new AgentSmokeResult(true, null, TimeSpan.FromMilliseconds(10), SmokeFailureCategory.None),
            SmokeExclusionSource.HostSmoke);
        Assert.Empty(captured);

        registry.MarkSmokeResult(
            AgentKind.Claude,
            new AgentSmokeResult(true, null, TimeSpan.FromMilliseconds(10), SmokeFailureCategory.None),
            SmokeExclusionSource.InVmSmoke);
        Assert.Single(captured);
    }

    [Fact]
    public void BuildOptions_InvalidLookbackGrace_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            OrchestratorOptionsFactory.BuildAgentRestoreRetryOptions(
                enabled: true, lookbackGrace: "not-a-timespan",
                postRestoreMargin: "00:05:00"));
        Assert.Contains("LookbackGrace", ex.Message);
    }

    [Fact]
    public void BuildOptions_NegativeLookbackGrace_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            OrchestratorOptionsFactory.BuildAgentRestoreRetryOptions(
                enabled: true, lookbackGrace: "-00:01:00",
                postRestoreMargin: "00:05:00"));
        Assert.Contains("LookbackGrace", ex.Message);
    }

    [Fact]
    public void BuildOptions_InvalidPostRestoreMargin_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            OrchestratorOptionsFactory.BuildAgentRestoreRetryOptions(
                enabled: true, lookbackGrace: "00:30:00",
                postRestoreMargin: "not-a-timespan"));
        Assert.Contains("PostRestoreMargin", ex.Message);
    }

    [Fact]
    public void BuildOptions_NegativePostRestoreMargin_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            OrchestratorOptionsFactory.BuildAgentRestoreRetryOptions(
                enabled: true, lookbackGrace: "00:30:00",
                postRestoreMargin: "-00:05:00"));
        Assert.Contains("PostRestoreMargin", ex.Message);
    }

    [Fact]
    public void BuildOptions_Disabled_ReturnsDisabledOptionsWithoutValidating()
    {
        // Enabled=false must short-circuit the validation, so a stale or
        // invalid TimeSpan in operator config doesn't crash startup just
        // because the feature happens to be off.
        var opts = OrchestratorOptionsFactory.BuildAgentRestoreRetryOptions(
            enabled: false, lookbackGrace: "not-a-timespan",
            postRestoreMargin: "not-a-timespan");
        Assert.False(opts.Enabled);
    }

    [Fact]
    public void RestoreSignal_FiresOnOperatorReset()
    {
        var registry = NewRegistry();
        var captured = new List<AgentRestoredEvent>();
        ((IAgentRestoreSignal)registry).AgentRestored += captured.Add;

        // Bench the agent first so reset transitions Excluded → Available.
        registry.MarkSmokeResult(
            AgentKind.Codex,
            new AgentSmokeResult(false, "missing binary", TimeSpan.Zero, SmokeFailureCategory.Persistent));

        NewReset(registry).Reset(AgentKind.Codex);

        Assert.Single(captured);
        Assert.Equal(AgentKind.Codex.Value, captured[0].Agent.Value);
    }

    [Fact]
    public void RestoreSignal_NoOpResetOnHealthyAgent_DoesNotFire()
    {
        var registry = NewRegistry();
        var captured = new List<AgentRestoredEvent>();
        ((IAgentRestoreSignal)registry).AgentRestored += captured.Add;

        // No prior failure recorded — reset of a never-excluded agent must
        // not fabricate a restore event the consumer would then sweep on.
        NewReset(registry).Reset(AgentKind.Gemini);

        Assert.Empty(captured);
    }

    private static AgentAvailabilityRegistry NewRegistry() =>
        new(new AvailabilityOptions(), TimeProvider.System,
            NullLogger<AgentAvailabilityRegistry>.Instance);

    private static AgentAvailabilityReset NewReset(AgentAvailabilityRegistry registry) =>
        new(registry, new InVmSmokeCache(TimeSpan.FromMinutes(15)), registry);

    private static string? Detail(WebhookEvent evt, string propertyName)
        => evt.Details?.GetType().GetProperty(propertyName)?.GetValue(evt.Details)?.ToString();

    private static async Task RecordFailedInvolvementAsync(
        IAgentInvolvementStore involvement,
        WorkItemId workItemId,
        AgentKind agent,
        DateTimeOffset endedAt,
        string outcome = AgentInvolvementOutcomes.FailureInfrastructure)
    {
        var involvementId = Guid.NewGuid();
        await involvement.RecordStartAsync(new AgentInvolvement(
            Id: involvementId,
            WorkItemId: workItemId,
            AgentKind: agent,
            ModelId: null,
            Phase: "work",
            StartedAt: endedAt.AddSeconds(-1),
            EndedAt: null,
            Iteration: 1,
            Outcome: null));
        await involvement.FinalizeAsync(involvementId, endedAt, outcome);
    }

    private static AgentRestoreRetryScheduler NewScheduler(
        IWorkItemStore store,
        InMemoryTaskQueue queue,
        bool enabled,
        ILogger<AgentRestoreRetryScheduler>? schedulerLogger = null,
        IAgentRestoreSignal? signal = null,
        IWebhookDispatcher? webhooks = null,
        IProjectRepository? projects = null,
        IAgentInvolvementStore? involvement = null,
        int maxCandidatesPerSweep = AgentRestoreRetryOptions.DefaultMaxCandidatesPerSweep)
    {
        var retrier = new WorkItemRetrier(
            store,
            queue,
            new NullGitHost(),
            NullLogger<WorkItemRetrier>.Instance);
        var opts = new AgentRestoreRetryOptions
        {
            Enabled = enabled,
            LookbackGrace = TimeSpan.FromMinutes(30),
            PostRestoreMargin = TimeSpan.FromMinutes(5),
            MaxCandidatesPerSweep = maxCandidatesPerSweep,
        };
        return new AgentRestoreRetryScheduler(
            store,
            retrier,
            () => opts,
            schedulerLogger ?? NullLogger<AgentRestoreRetryScheduler>.Instance,
            signal,
            webhooks,
            projects,
            involvement);
    }

    private static WorkItem NewItem(
        AgentKind? agent,
        WorkItemState state,
        string failureKind,
        DateTimeOffset updatedAt,
        string? lastError = "synthetic infra failure",
        WorkItemAuthFailureScope? authFailureScope = null) => new()
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("proj"),
            Title = "test",
            Prompt = "do work",
            Agent = agent,
            State = state,
            FailureKind = failureKind,
            AuthFailureScope = authFailureScope,
            LastError = lastError,
            UpdatedAt = updatedAt,
        };

    private static T? GetScalar<T>(LogEvent evt, string key)
    {
        if (!evt.Properties.TryGetValue(key, out var prop) || prop is not ScalarValue sv)
            return default;
        if (sv.Value is T t)
            return t;
        if (typeof(T) == typeof(int) && sv.Value is long l)
            return (T)(object)(int)l;
        return default;
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
