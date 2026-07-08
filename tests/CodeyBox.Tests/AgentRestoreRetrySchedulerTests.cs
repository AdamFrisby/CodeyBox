using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Behavioural tests for <see cref="AgentRestoreRetryScheduler"/>. Covers the
/// four acceptance points called out in the brief: restore trigger,
/// infra-vs-real filter, idempotency, and window bounding. Each test isolates
/// a single bound so a regression in any one is unambiguous.
/// </summary>
public sealed class AgentRestoreRetrySchedulerTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(),
        $"codeybox-agent-restore-retry-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task Sweep_RequeuesInfraFailedItemPinnedToRestoredAgent_InWindow()
    {
        using var store = new SqliteWorkItemStore(_dbPath);
        var queue = new InMemoryTaskQueue();
        var scheduler = NewScheduler(store, queue, enabled: true);

        var outageStart = DateTimeOffset.UtcNow.AddMinutes(-15);
        var item = NewItem(
            agent: AgentKind.Claude,
            state: WorkItemState.Failed,
            failureKind: WorkItemFailureKinds.Infrastructure,
            updatedAt: outageStart.AddMinutes(5));
        await store.CreateAsync(item);
        await store.UpdateAsync(item);

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
        var scheduler = NewScheduler(store, queue, enabled: true);

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
            updatedAt: outageStart.AddMinutes(5));
        var agentUnavailableItem = NewItem(
            agent: AgentKind.Claude,
            state: WorkItemState.Failed,
            failureKind: WorkItemFailureKinds.AgentUnavailable,
            updatedAt: outageStart.AddMinutes(5));

        foreach (var item in new[] { infraItem, buildItem, agentInternalItem, configItem, authRequiredItem, agentUnavailableItem })
        {
            await store.CreateAsync(item);
            await store.UpdateAsync(item);
        }

        var summary = await scheduler.SweepForTestAsync(
            new AgentRestoredEvent(AgentKind.Claude, outageStart, DateTimeOffset.UtcNow));

        // infrastructure + auth_required + agent_unavailable = 3 candidates.
        // build / agent / configuration are deterministic and must NOT requeue.
        Assert.Equal(3, summary.Requeued);

        Assert.Equal(WorkItemState.Queued, (await store.GetAsync(infraItem.Id))!.State);
        Assert.Equal(WorkItemState.Queued, (await store.GetAsync(authRequiredItem.Id))!.State);
        Assert.Equal(WorkItemState.Queued, (await store.GetAsync(agentUnavailableItem.Id))!.State);
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
            failureKind: WorkItemFailureKinds.Infrastructure,
            updatedAt: outageStart.AddMinutes(2));
        await store.CreateAsync(item);
        await store.UpdateAsync(item);

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
            failureKind: WorkItemFailureKinds.Infrastructure,
            updatedAt: outageStart.AddHours(-2));
        await store.CreateAsync(ancientFailure);
        await store.UpdateAsync(ancientFailure);

        // Failure 10 min before outage start IS in window (within the 30-min grace).
        var withinGrace = NewItem(
            agent: AgentKind.Claude,
            state: WorkItemState.Failed,
            failureKind: WorkItemFailureKinds.Infrastructure,
            updatedAt: outageStart.AddMinutes(-10));
        await store.CreateAsync(withinGrace);
        await store.UpdateAsync(withinGrace);

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
            failureKind: WorkItemFailureKinds.Infrastructure,
            updatedAt: outageStart.AddMinutes(5));
        var codexItem = NewItem(
            agent: AgentKind.Codex,
            state: WorkItemState.Failed,
            failureKind: WorkItemFailureKinds.Infrastructure,
            updatedAt: outageStart.AddMinutes(5));
        await store.CreateAsync(claudeItem);
        await store.UpdateAsync(claudeItem);
        await store.CreateAsync(codexItem);
        await store.UpdateAsync(codexItem);

        var summary = await scheduler.SweepForTestAsync(
            new AgentRestoredEvent(AgentKind.Claude, outageStart, DateTimeOffset.UtcNow));

        Assert.Equal(1, summary.Requeued);
        Assert.Equal(WorkItemState.Queued, (await store.GetAsync(claudeItem.Id))!.State);
        Assert.Equal(WorkItemState.Failed, (await store.GetAsync(codexItem.Id))!.State);
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
            failureKind: WorkItemFailureKinds.Infrastructure,
            updatedAt: DateTimeOffset.UtcNow.AddMinutes(-10));
        await store.CreateAsync(item);
        await store.UpdateAsync(item);

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
        var scheduler = NewScheduler(store, queue, enabled: true);

        var outageStart = DateTimeOffset.UtcNow.AddMinutes(-15);
        var item = NewItem(
            agent: AgentKind.Claude,
            state: WorkItemState.MergeConflictResolutionFailed,
            failureKind: WorkItemFailureKinds.Infrastructure,
            updatedAt: outageStart.AddMinutes(5));
        await store.CreateAsync(item);
        await store.UpdateAsync(item);

        var summary = await scheduler.SweepForTestAsync(
            new AgentRestoredEvent(AgentKind.Claude, outageStart, DateTimeOffset.UtcNow));

        Assert.Equal(1, summary.Requeued);
        Assert.Equal(WorkItemState.Queued, (await store.GetAsync(item.Id))!.State);
    }

    [Fact]
    public async Task Sweep_RequeuesEveryMatchingCandidate()
    {
        using var store = new SqliteWorkItemStore(_dbPath);
        var queue = new InMemoryTaskQueue();
        var scheduler = NewScheduler(store, queue, enabled: true);

        var outageStart = DateTimeOffset.UtcNow.AddMinutes(-10);
        var items = new List<WorkItem>();
        for (var i = 0; i < 5; i++)
        {
            var item = NewItem(
                agent: AgentKind.Claude,
                state: WorkItemState.Failed,
                failureKind: WorkItemFailureKinds.Infrastructure,
                updatedAt: outageStart.AddMinutes(2 + i));
            await store.CreateAsync(item);
            await store.UpdateAsync(item);
            items.Add(item);
        }

        var summary = await scheduler.SweepForTestAsync(
            new AgentRestoredEvent(AgentKind.Claude, outageStart, DateTimeOffset.UtcNow));

        Assert.Equal(5, summary.Requeued);
        Assert.Equal(0, summary.Skipped);
        var queuedCount = 0;
        foreach (var item in items)
        {
            var state = (await store.GetAsync(item.Id))!.State;
            if (state == WorkItemState.Queued) queuedCount++;
        }
        Assert.Equal(5, queuedCount);
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
            updatedAt: fakeTime.GetUtcNow());
        await store.CreateAsync(item);
        await store.UpdateAsync(item);

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
            failureKind: WorkItemFailureKinds.Infrastructure,
            updatedAt: restoredAt.AddMinutes(4));
        var afterMargin = NewItem(
            agent: AgentKind.Claude,
            state: WorkItemState.Failed,
            failureKind: WorkItemFailureKinds.Infrastructure,
            updatedAt: restoredAt.AddMinutes(6));
        await store.CreateAsync(insideMargin);
        await store.UpdateAsync(insideMargin);
        await store.CreateAsync(afterMargin);
        await store.UpdateAsync(afterMargin);

        var summary = await scheduler.SweepForTestAsync(
            new AgentRestoredEvent(AgentKind.Claude, outageStart, restoredAt));

        Assert.Equal(1, summary.Requeued);
        Assert.Equal(WorkItemState.Queued, (await store.GetAsync(insideMargin.Id))!.State);
        Assert.Equal(WorkItemState.Failed, (await store.GetAsync(afterMargin.Id))!.State);
    }

    [Fact]
    public async Task Sweep_NullOutageStartedAt_StillReturnsSummaryForObservability()
    {
        // The audit-log emission for the null-window no-op case is verified by
        // inspection of SweepAsync; asserting it here via the global Serilog
        // sink would race with the rest of the test suite (the static
        // Log.Logger is shared across non-GlobalSerilog collections). Instead
        // we pin the observable behavior: the summary is returned with
        // Requeued=0 so callers can distinguish "feature disabled" (no call)
        // from "no candidates matched / null window" (call returning zeros).
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
                failureKind: WorkItemFailureKinds.Infrastructure,
                updatedAt: DateTimeOffset.UtcNow);
            await store.CreateAsync(item);
            await store.UpdateAsync(item);

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
    public async Task Sweep_RequeuesDefaultAgentRowsWhenPersistedAgentIsUnset()
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
        await store.UpdateAsync(item);

        var summary = await scheduler.SweepForTestAsync(
            new AgentRestoredEvent(AgentKind.Claude, outageStart, DateTimeOffset.UtcNow));

        Assert.Equal(1, summary.Requeued);
        Assert.Equal(WorkItemState.Queued, (await store.GetAsync(item.Id))!.State);
    }

    [Fact]
    public async Task Sweep_UsesLatestFailedInvolvementWhenWorkItemAgentIsStale()
    {
        using var store = new SqliteWorkItemStore(_dbPath);
        var queue = new InMemoryTaskQueue();
        var involvement = new InMemoryAgentInvolvementStore();
        var scheduler = NewScheduler(store, queue, enabled: true, involvement: involvement);

        var outageStart = DateTimeOffset.UtcNow.AddMinutes(-10);
        var item = NewItem(
            agent: AgentKind.Claude,
            state: WorkItemState.Failed,
            failureKind: WorkItemFailureKinds.AuthRequired,
            updatedAt: outageStart.AddMinutes(2),
            lastError: "auth required from agent output during audit: login prompt matched");
        await store.CreateAsync(item);
        await store.UpdateAsync(item);

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
        await involvement.FinalizeAsync(involvementId, outageStart.AddMinutes(2), "failure:auth");

        var summary = await scheduler.SweepForTestAsync(
            new AgentRestoredEvent(AgentKind.Codex, outageStart, DateTimeOffset.UtcNow));

        Assert.Equal(1, summary.Requeued);
        Assert.Equal(WorkItemState.Queued, (await store.GetAsync(item.Id))!.State);
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
            failureKind: WorkItemFailureKinds.Infrastructure,
            updatedAt: outageStart.AddMinutes(30));
        await store.CreateAsync(item);
        await store.UpdateAsync(item);

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
        await store.UpdateAsync(itemLocal);
        await store.CreateAsync(corroborated);
        await store.UpdateAsync(corroborated);
        await store.CreateAsync(legacyItemLocal);
        await store.UpdateAsync(legacyItemLocal);

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
            failureKind: WorkItemFailureKinds.Infrastructure,
            updatedAt: outageStart.AddMinutes(2));
        await store.CreateAsync(item);
        await store.UpdateAsync(item);

        await scheduler.SweepForTestAsync(
            new AgentRestoredEvent(AgentKind.Claude, outageStart, DateTimeOffset.UtcNow));

        var sweep = Assert.Single(webhooks.Events, e => e.Event == "agent.restore_requeue_swept");
        Assert.Equal("agent_restore", Detail(sweep, "reason"));
        Assert.Equal("claude", Detail(sweep, "restoredAgent"));
        Assert.Equal("1", Detail(sweep, "requeued"));
        Assert.Equal("0", Detail(sweep, "skipped"));

        var retry = Assert.Single(webhooks.Events, e => e.Event == "work_item.auto_retry");
        Assert.Equal("agent_restore", Detail(retry, "reason"));
        Assert.Equal("claude", Detail(retry, "restoredAgent"));
        Assert.Equal(WorkItemState.Queued, retry.WorkItem!.State);
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
            failureKind: WorkItemFailureKinds.Infrastructure,
            updatedAt: outageStart.AddMinutes(1));
        await store.CreateAsync(item);
        await store.UpdateAsync(item);

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

    private static AgentRestoreRetryScheduler NewScheduler(
        IWorkItemStore store,
        InMemoryTaskQueue queue,
        bool enabled,
        ILogger<AgentRestoreRetryScheduler>? schedulerLogger = null,
        IAgentRestoreSignal? signal = null,
        IWebhookDispatcher? webhooks = null,
        IProjectRepository? projects = null,
        IAgentInvolvementStore? involvement = null)
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

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class ListLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();
        IDisposable? Microsoft.Extensions.Logging.ILogger.BeginScope<TState>(TState state) => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }

}
