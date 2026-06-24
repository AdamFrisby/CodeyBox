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
        // Cap reached: at least one over-cap candidate counted in Skipped.
        // The sweep breaks out at the cap rather than iterating to the end —
        // the exact remaining-candidate count is not asserted because the
        // implementation may not enumerate past the break point.
        Assert.True(summary.Skipped >= 1);
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
        registry.Reset(AgentKind.Antigravity);

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
        registry.Reset(AgentKind.Antigravity);
        Assert.Single(capturedRestores);
        Assert.NotNull(capturedRestores[0].OutageStartedAt);

        var summary = await scheduler.SweepForTestAsync(capturedRestores[0]);

        Assert.Equal(1, summary.Requeued);
        Assert.Equal(WorkItemState.Queued, (await store.GetAsync(item.Id))!.State);
    }

    [Fact]
    public async Task Sweep_PerRestoreCap_EmitsSingleWarning_NotPerRowSpam()
    {
        // Regression pin for the cap-reached short-circuit. Pre-fix the loop
        // kept iterating after hitting MaxItemsPerRestore, emitting a WARN
        // for every remaining candidate (potentially thousands per restore).
        // Now it should break out of both state loops once the cap is hit
        // and emit a single summary warning instead.
        using var store = new SqliteWorkItemStore(_dbPath);
        var queue = new InMemoryTaskQueue();
        var log = new ListLogger<AgentRestoreRetryScheduler>();
        var scheduler = NewScheduler(store, queue, enabled: true, maxItemsPerRestore: 2, schedulerLogger: log);

        var outageStart = DateTimeOffset.UtcNow.AddMinutes(-10);
        for (var i = 0; i < 10; i++)
        {
            var item = NewItem(
                agent: AgentKind.Claude,
                state: WorkItemState.Failed,
                failureKind: WorkItemFailureKinds.Infrastructure,
                updatedAt: outageStart.AddMinutes(2 + i));
            await store.CreateAsync(item);
            await store.UpdateAsync(item);
        }

        var summary = await scheduler.SweepForTestAsync(
            new AgentRestoredEvent(AgentKind.Claude, outageStart, DateTimeOffset.UtcNow));

        Assert.Equal(2, summary.Requeued);
        Assert.True(summary.Skipped >= 1);
        var capWarnings = log.Entries.Count(e =>
            e.Level == Microsoft.Extensions.Logging.LogLevel.Warning &&
            e.Message.Contains("per-restore cap", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, capWarnings);
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
    public void BuildOptions_InvalidLookbackGrace_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            OrchestratorOptionsFactory.BuildAgentRestoreRetryOptions(
                enabled: true, lookbackGrace: "not-a-timespan",
                postRestoreMargin: "00:05:00", maxItemsPerRestore: 200));
        Assert.Contains("LookbackGrace", ex.Message);
    }

    [Fact]
    public void BuildOptions_NegativeLookbackGrace_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            OrchestratorOptionsFactory.BuildAgentRestoreRetryOptions(
                enabled: true, lookbackGrace: "-00:01:00",
                postRestoreMargin: "00:05:00", maxItemsPerRestore: 200));
        Assert.Contains("LookbackGrace", ex.Message);
    }

    [Fact]
    public void BuildOptions_InvalidPostRestoreMargin_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            OrchestratorOptionsFactory.BuildAgentRestoreRetryOptions(
                enabled: true, lookbackGrace: "00:30:00",
                postRestoreMargin: "not-a-timespan", maxItemsPerRestore: 200));
        Assert.Contains("PostRestoreMargin", ex.Message);
    }

    [Fact]
    public void BuildOptions_NegativePostRestoreMargin_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            OrchestratorOptionsFactory.BuildAgentRestoreRetryOptions(
                enabled: true, lookbackGrace: "00:30:00",
                postRestoreMargin: "-00:05:00", maxItemsPerRestore: 200));
        Assert.Contains("PostRestoreMargin", ex.Message);
    }

    [Fact]
    public void BuildOptions_NonPositiveMaxItemsPerRestore_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            OrchestratorOptionsFactory.BuildAgentRestoreRetryOptions(
                enabled: true, lookbackGrace: "00:30:00",
                postRestoreMargin: "00:05:00", maxItemsPerRestore: 0));
        Assert.Contains("MaxItemsPerRestore", ex.Message);
    }

    [Fact]
    public void BuildOptions_Disabled_ReturnsDisabledOptionsWithoutValidating()
    {
        // Enabled=false must short-circuit the validation, so a stale or
        // invalid TimeSpan in operator config doesn't crash startup just
        // because the feature happens to be off.
        var opts = OrchestratorOptionsFactory.BuildAgentRestoreRetryOptions(
            enabled: false, lookbackGrace: "not-a-timespan",
            postRestoreMargin: "not-a-timespan", maxItemsPerRestore: 0);
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
        int maxItemsPerRestore = 200,
        ILogger<AgentRestoreRetryScheduler>? schedulerLogger = null)
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
            schedulerLogger ?? NullLogger<AgentRestoreRetryScheduler>.Instance);
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
