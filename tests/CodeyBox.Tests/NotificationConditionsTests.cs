using CodeyBox.Core;
using CodeyBox.Notifications;

namespace CodeyBox.Tests;

public sealed class NotificationConditionsTests
{
    // ── OrchestratorStallCondition ─────────────────────────────────────────

    [Fact]
    public async Task OrchestratorStall_NotStalledWhenNoTransitions()
    {
        var clock = new OrchestratorProgressClock();
        // clock has never been stamped — not stalled.
        var condition = new OrchestratorStallCondition(clock, TimeSpan.FromMinutes(10));
        Assert.False(await condition.EvaluateAsync(CancellationToken.None));
    }

    [Fact]
    public async Task OrchestratorStall_NotStalledWhenRecentTransition()
    {
        var clock = new OrchestratorProgressClock();
        clock.Stamp(DateTimeOffset.UtcNow);
        var condition = new OrchestratorStallCondition(clock, TimeSpan.FromMinutes(10));
        Assert.False(await condition.EvaluateAsync(CancellationToken.None));
    }

    [Fact]
    public async Task OrchestratorStall_StalledWhenThresholdExceeded()
    {
        var clock = new OrchestratorProgressClock();
        clock.Stamp(DateTimeOffset.UtcNow - TimeSpan.FromMinutes(20));
        var condition = new OrchestratorStallCondition(clock, TimeSpan.FromMinutes(10));
        Assert.True(await condition.EvaluateAsync(CancellationToken.None));
    }

    // ── SandboxLeakReapedCondition ─────────────────────────────────────────

    [Fact]
    public async Task SandboxLeakReaped_FiresOnNewLeak()
    {
        var sink = new LeakDetectionSink();
        var condition = new SandboxLeakReapedCondition(sink);

        // Initial state: no leaks.
        Assert.False(await condition.EvaluateAsync(CancellationToken.None));

        // Leak detected.
        sink.Increment();
        Assert.True(await condition.EvaluateAsync(CancellationToken.None));

        // Already consumed the edge.
        Assert.False(await condition.EvaluateAsync(CancellationToken.None));

        // Another leak.
        sink.Increment();
        Assert.True(await condition.EvaluateAsync(CancellationToken.None));
    }

    // ── QueueEmptyCondition ────────────────────────────────────────────────

    [Fact]
    public async Task QueueEmpty_TrueWhenNoWorkingItems()
    {
        var store = new StubWorkItemStore(workingCount: 0);
        var condition = new QueueEmptyCondition(store);
        Assert.True(await condition.EvaluateAsync(CancellationToken.None));
    }

    [Fact]
    public async Task QueueEmpty_FalseWhenItemsWorking()
    {
        var store = new StubWorkItemStore(workingCount: 3);
        var condition = new QueueEmptyCondition(store);
        Assert.False(await condition.EvaluateAsync(CancellationToken.None));
    }

    // ── WorkItemPermanentlyFailedCondition ─────────────────────────────────

    [Fact]
    public async Task WorkItemPermanentlyFailed_FiresWhenCountIncreases()
    {
        var store = new StubWorkItemStore(failedCount: 0, abandonedCount: 0);
        var condition = new WorkItemPermanentlyFailedCondition(store);

        // Prime: no edge yet.
        Assert.False(await condition.EvaluateAsync(CancellationToken.None));

        store.FailedCount = 1;
        Assert.True(await condition.EvaluateAsync(CancellationToken.None));

        // Already fired.
        Assert.False(await condition.EvaluateAsync(CancellationToken.None));

        store.AbandonedCount = 1;
        Assert.True(await condition.EvaluateAsync(CancellationToken.None));
    }
}

// ── Test doubles ───────────────────────────────────────────────────────────

public sealed class StubWorkItemStore : IWorkItemStore
{
    public int WorkingCount { get; set; }
    public int FailedCount { get; set; }
    public int AbandonedCount { get; set; }

    public StubWorkItemStore(int workingCount = 0, int failedCount = 0, int abandonedCount = 0)
    {
        WorkingCount = workingCount;
        FailedCount = failedCount;
        AbandonedCount = abandonedCount;
    }

    public Task<int> CountByStateAsync(WorkItemState state, CancellationToken ct)
        => Task.FromResult(state switch
        {
            WorkItemState.Working => WorkingCount,
            WorkItemState.Failed => FailedCount,
            WorkItemState.AbandonedAfterRecoveryAttempts => AbandonedCount,
            _ => 0,
        });

    // Remaining IWorkItemStore members — throw / return default.
    public Task CreateAsync(WorkItem item, CancellationToken ct = default) => throw new NotImplementedException();
    public Task UpdateAsync(WorkItem item, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<bool> TryUpdateIfStateAsync(WorkItem item, WorkItemState onlyIfState, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<PriorityUpdateResult> UpdatePriorityAsync(WorkItemId id, int priority, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<WorkItem?> GetAsync(WorkItemId id, CancellationToken ct = default) => throw new NotImplementedException();
    public IAsyncEnumerable<WorkItem> ListAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public IAsyncEnumerable<WorkItem> ListByStateAsync(WorkItemState state, CancellationToken ct = default) => throw new NotImplementedException();
    public Task ReorderAsync(IReadOnlyList<WorkItemId> orderedIds, CancellationToken ct = default) => throw new NotImplementedException();
    public IAsyncEnumerable<WorkItem> ListDispatchEligibleByPriorityAsync(IReadOnlySet<WorkItemId> skipIds, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<int> CountStartedInWindowAsync(ProjectId projectId, DateTimeOffset since, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<int> CountInFlightAsync(ProjectId projectId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<WorkItem?> GetByExternalIdAsync(ProjectId projectId, string externalId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<WorkItem?> GetByNamespacedExternalIdAsync(ProjectId projectId, string @namespace, string externalId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<WorkItem?> ReplaceExternalIdsAsync(WorkItemId id, IReadOnlyDictionary<string, string> externalIds, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IReadOnlyList<(string ProjectId, int State, int Count, string MaxUpdatedAt)>> GetFleetStateCountsAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IReadOnlyList<(string ProjectId, int State)>> GetFleetRecentOutcomesAsync(int perProject = 5, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IReadOnlyDictionary<string, bool>> GetFleetPauseStatesAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public IAsyncEnumerable<WorkItem> ListByReplaySourceAsync(WorkItemId sourceId, CancellationToken ct = default) => throw new NotImplementedException();
    public IAsyncEnumerable<WorkItem> ListSuspendedAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IReadOnlySet<string>> GetActiveBaselineImageRefsAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IReadOnlyList<(WorkItemId Id, string Title, WorkItemState State)>> ListWorkItemsForBaselineAsync(string baselineImageRef, CancellationToken ct = default) => throw new NotImplementedException();
    public Task OrphanReplaysAsync(WorkItemId sourceId, CancellationToken ct = default) => throw new NotImplementedException();
    public IAsyncEnumerable<WorkItem> ListByReleaseAsync(ReleaseId releaseId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<PromptReplaceResult> TryReplacePromptAsync(WorkItemId id, string newPrompt, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotImplementedException();
    public Task RecordIterationDispatchAsync(WorkItemId workItemId, int iteration, int promptRevisionAtDispatch, DateTimeOffset dispatchedAt, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IReadOnlyList<WorkItemIteration>> GetIterationsAsync(WorkItemId workItemId, CancellationToken ct = default) => throw new NotImplementedException();
}
