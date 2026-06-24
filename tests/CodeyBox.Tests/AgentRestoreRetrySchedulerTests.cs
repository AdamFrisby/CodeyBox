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
    public async Task Sweep_RespectsMaxItemsPerRestoreCap()
    {
        using var store = new SqliteWorkItemStore(_dbPath);
        var queue = new InMemoryTaskQueue();
        var scheduler = NewScheduler(store, queue, enabled: true, maxItemsPerRestore: 2);

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

        Assert.Equal(2, summary.Requeued);
        Assert.Equal(3, summary.Skipped);
        // Two items requeued, three still parked — operator can re-trigger
        // (eg. POST /admin/agent/<name>/reset) to drain the rest in a
        // subsequent burst.
        var queuedCount = 0;
        foreach (var item in items)
        {
            var state = (await store.GetAsync(item.Id))!.State;
            if (state == WorkItemState.Queued) queuedCount++;
        }
        Assert.Equal(2, queuedCount);
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
    public void RestoreSignal_FiresOnOperatorReset()
    {
        var registry = NewRegistry();
        var captured = new List<AgentRestoredEvent>();
        ((IAgentRestoreSignal)registry).AgentRestored += captured.Add;

        // Bench the agent first so reset transitions Excluded → Available.
        registry.MarkSmokeResult(
            AgentKind.Codex,
            new AgentSmokeResult(false, "missing binary", TimeSpan.Zero, SmokeFailureCategory.Persistent));

        registry.Reset(AgentKind.Codex);

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
        registry.Reset(AgentKind.Gemini);

        Assert.Empty(captured);
    }

    private static AgentAvailabilityRegistry NewRegistry() =>
        new(new AvailabilityOptions(), TimeProvider.System,
            NullLogger<AgentAvailabilityRegistry>.Instance);

    private static AgentRestoreRetryScheduler NewScheduler(
        IWorkItemStore store,
        InMemoryTaskQueue queue,
        bool enabled,
        int maxItemsPerRestore = 200)
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
            MaxItemsPerRestore = maxItemsPerRestore,
        };
        return new AgentRestoreRetryScheduler(
            store,
            retrier,
            () => opts,
            NullLogger<AgentRestoreRetryScheduler>.Instance);
    }

    private static WorkItem NewItem(
        AgentKind agent,
        WorkItemState state,
        string failureKind,
        DateTimeOffset updatedAt) => new()
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("proj"),
            Title = "test",
            Prompt = "do work",
            Agent = agent,
            State = state,
            FailureKind = failureKind,
            LastError = "synthetic infra failure",
            UpdatedAt = updatedAt,
        };
}
