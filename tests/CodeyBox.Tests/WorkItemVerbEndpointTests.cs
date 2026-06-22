using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.DependencyInjection;

namespace CodeyBox.Tests;

/// <summary>
/// HTTP-level coverage for the work-item verb endpoints added for CLI queue
/// actions. These tests exercise the real endpoint/store behavior; CLI tests
/// only prove the client sends the POST.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class WorkItemVerbEndpointTests : IDisposable
{
    private readonly WorkItemApiFactory _factory;
    private readonly HttpClient _client;

    public WorkItemVerbEndpointTests()
    {
        _factory = new WorkItemApiFactory(projects:
        [
            new Project
            {
                Id = new ProjectId("test-project"),
                DisplayName = "Test Project",
                RepositoryUrl = "https://github.com/test/repo",
            },
            new Project
            {
                Id = new ProjectId("capped-project"),
                DisplayName = "Capped Project",
                RepositoryUrl = "https://github.com/test/capped",
                MaxPriority = 200,
            },
        ]);
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Abandon_QueuedItem_TransitionsAndDeletesStreamSummaries()
    {
        var item = MakeItem(WorkItemState.Queued);
        await _factory.Store.CreateAsync(item);
        var summaries = _factory.Services.GetRequiredService<IAgentStreamSummaryStore>();
        await SeedSummaryAsync(summaries, item.Id);

        var resp = await _client.PostAsync($"/workitems/{item.Id}/abandon", null);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(item.Id.ToString(), body.GetProperty("id").GetString());
        Assert.Equal(WorkItemState.AbandonedAfterRecoveryAttempts.ToString(), body.GetProperty("state").GetString());

        var readBack = await _factory.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.AbandonedAfterRecoveryAttempts, readBack!.State);
        Assert.Equal("abandoned via API", readBack.LastError);
        Assert.Empty(await summaries.GetByWorkItemAsync(item.Id));
    }

    [Fact]
    public async Task Abandon_AlreadyAbandoned_IsIdempotent()
    {
        var originalUpdatedAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var item = MakeItem(WorkItemState.AbandonedAfterRecoveryAttempts) with
        {
            LastError = "exceeded MaxRecoveryAttempts",
            UpdatedAt = originalUpdatedAt,
        };
        await _factory.Store.CreateAsync(item);
        var summaries = _factory.Services.GetRequiredService<IAgentStreamSummaryStore>();
        await SeedSummaryAsync(summaries, item.Id);

        var resp = await _client.PostAsync($"/workitems/{item.Id}/abandon", null);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var readBack = await _factory.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.AbandonedAfterRecoveryAttempts, readBack!.State);
        Assert.Equal("exceeded MaxRecoveryAttempts", readBack.LastError);
        Assert.Equal(originalUpdatedAt, readBack.UpdatedAt);

        var rows = await summaries.GetByWorkItemAsync(item.Id);
        var row = Assert.Single(rows);
        Assert.Equal("work-1-abcdef.jsonl", row.FileName);
    }

    [Theory]
    [InlineData(WorkItemState.Done)]
    [InlineData(WorkItemState.Planning)]
    [InlineData(WorkItemState.PlanReview)]
    [InlineData(WorkItemState.PlanApproved)]
    [InlineData(WorkItemState.Working)]
    [InlineData(WorkItemState.WorkComplete)]
    [InlineData(WorkItemState.Auditing)]
    [InlineData(WorkItemState.Reworking)]
    [InlineData(WorkItemState.AuditPassed)]
    [InlineData(WorkItemState.Merging)]
    [InlineData(WorkItemState.Merged)]
    [InlineData(WorkItemState.UpstreamPushing)]
    [InlineData(WorkItemState.ReworkingForConflict)]
    public async Task Abandon_RejectedStates_Return409AndDoNotMutate(WorkItemState state)
    {
        var item = MakeItem(state);
        await _factory.Store.CreateAsync(item);

        var resp = await _client.PostAsync($"/workitems/{item.Id}/abandon", null);

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var readBack = await _factory.Store.GetAsync(item.Id);
        Assert.Equal(state, readBack!.State);
    }

    [Fact]
    public async Task Abandon_RaceOutOfQueued_Returns409AndPreservesActiveRow()
    {
        using var factory = new WorkItemApiFactory();
        var racingStore = new AbandonRaceStore(factory.Store);
        factory.WorkItemStoreDecorator = _ => racingStore;
        using var client = factory.CreateClient();

        var item = MakeItem(WorkItemState.Queued);
        await factory.Store.CreateAsync(item);
        var summaries = factory.Services.GetRequiredService<IAgentStreamSummaryStore>();
        await SeedSummaryAsync(summaries, item.Id);

        var resp = await client.PostAsync($"/workitems/{item.Id}/abandon", null);

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var readBack = await factory.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Working, readBack!.State);
        Assert.NotNull(readBack.StartedAt);
        Assert.Equal("worker picked up item", readBack.LastError);
        Assert.NotEmpty(await summaries.GetByWorkItemAsync(item.Id));
    }

    [Fact]
    public async Task Promote_QueuedItem_RaisesPriorityToGlobalCapAndEnqueues()
    {
        var item = MakeItem(WorkItemState.Queued, priority: 10);
        await _factory.Store.CreateAsync(item);

        var resp = await _client.PostAsync($"/workitems/{item.Id}/promote", null);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(WorkItemState.Queued.ToString(), body.GetProperty("state").GetString());

        var readBack = await _factory.Store.GetAsync(item.Id);
        Assert.Equal(1000, readBack!.Priority);
        Assert.Equal(WorkItemState.Queued, readBack.State);
        var queue = _factory.Services.GetRequiredService<ITaskQueue>();
        Assert.Equal(1, queue.Count);
        Assert.Equal(item.Id, await queue.DequeueAsync());
    }

    [Fact]
    public async Task Promote_UsesProjectMaxPriorityCap()
    {
        var item = MakeItem(WorkItemState.Queued, projectId: "capped-project", priority: 10);
        await _factory.Store.CreateAsync(item);

        var resp = await _client.PostAsync($"/workitems/{item.Id}/promote", null);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var readBack = await _factory.Store.GetAsync(item.Id);
        Assert.Equal(200, readBack!.Priority);
        var queue = _factory.Services.GetRequiredService<ITaskQueue>();
        Assert.Equal(1, queue.Count);
        Assert.Equal(item.Id, await queue.DequeueAsync());
    }

    [Fact]
    public async Task Promote_AlreadyAtCap_ReturnsOkWithoutEnqueue()
    {
        var item = MakeItem(WorkItemState.Queued, priority: 1000);
        await _factory.Store.CreateAsync(item);

        var resp = await _client.PostAsync($"/workitems/{item.Id}/promote", null);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var readBack = await _factory.Store.GetAsync(item.Id);
        Assert.Equal(1000, readBack!.Priority);
        Assert.Equal(0, _factory.Services.GetRequiredService<ITaskQueue>().Count);
    }

    [Fact]
    public async Task Promote_WorkingItem_Returns409AndDoesNotChangePriority()
    {
        var item = MakeItem(WorkItemState.Working, priority: 10);
        await _factory.Store.CreateAsync(item);

        var resp = await _client.PostAsync($"/workitems/{item.Id}/promote", null);

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var readBack = await _factory.Store.GetAsync(item.Id);
        Assert.Equal(10, readBack!.Priority);
        Assert.Equal(WorkItemState.Working, readBack.State);
        Assert.Equal(0, _factory.Services.GetRequiredService<ITaskQueue>().Count);
    }

    [Fact]
    public async Task Promote_MissingProject_Returns400WithoutChangingPriorityOrEnqueueing()
    {
        var item = MakeItem(WorkItemState.Queued, projectId: "missing-project", priority: 10);
        await _factory.Store.CreateAsync(item);

        var resp = await _client.PostAsync($"/workitems/{item.Id}/promote", null);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("unknown project 'missing-project'", body.GetProperty("error").GetString());

        var readBack = await _factory.Store.GetAsync(item.Id);
        Assert.Equal(10, readBack!.Priority);
        Assert.Equal(WorkItemState.Queued, readBack.State);
        Assert.Equal(0, _factory.Services.GetRequiredService<ITaskQueue>().Count);
    }

    [Fact]
    public async Task Promote_RowDeletedAfterResolve_Returns404WithoutEnqueueing()
    {
        using var factory = new WorkItemApiFactory();
        var racingStore = new PromoteDeletedRowStore(factory.Store);
        factory.WorkItemStoreDecorator = _ => racingStore;
        using var client = factory.CreateClient();

        var item = MakeItem(WorkItemState.Queued, priority: 10);
        await factory.Store.CreateAsync(item);

        var resp = await client.PostAsync($"/workitems/{item.Id}/promote", null);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains($"work item '{item.Id}' no longer exists", body.GetProperty("error").GetString());
        Assert.Null(await factory.Store.GetAsync(item.Id));
        Assert.Equal(0, factory.Services.GetRequiredService<ITaskQueue>().Count);
    }

    [Fact]
    public async Task Promote_RowBecomesTerminalAfterResolve_Returns409WithoutPriorityPatchOrEnqueueing()
    {
        using var factory = new WorkItemApiFactory();
        var racingStore = new PromoteTerminalRaceStore(factory.Store);
        factory.WorkItemStoreDecorator = _ => racingStore;
        using var client = factory.CreateClient();

        var item = MakeItem(WorkItemState.Queued, priority: 10);
        await factory.Store.CreateAsync(item);

        var resp = await client.PostAsync($"/workitems/{item.Id}/promote", null);

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("terminal state 'Done'", body.GetProperty("error").GetString());

        var readBack = await factory.Store.GetAsync(item.Id);
        Assert.Equal(10, readBack!.Priority);
        Assert.Equal(WorkItemState.Done, readBack.State);
        Assert.Equal(0, factory.Services.GetRequiredService<ITaskQueue>().Count);
    }

    [Fact]
    public async Task Promote_RaceOutOfQueued_Returns409DoesNotPatchPriorityOrEnqueue()
    {
        using var factory = new WorkItemApiFactory();
        var racingStore = new PromoteRaceStore(factory.Store);
        factory.WorkItemStoreDecorator = _ => racingStore;
        using var client = factory.CreateClient();

        var item = MakeItem(WorkItemState.Queued, priority: 10);
        await factory.Store.CreateAsync(item);

        var resp = await client.PostAsync($"/workitems/{item.Id}/promote", null);

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var readBack = await factory.Store.GetAsync(item.Id);
        Assert.Equal(10, readBack!.Priority);
        Assert.Equal(WorkItemState.Working, readBack.State);
        Assert.NotNull(readBack.StartedAt);
        Assert.Equal(0, factory.Services.GetRequiredService<ITaskQueue>().Count);
    }

    private static WorkItem MakeItem(
        WorkItemState state,
        string projectId = "test-project",
        int priority = 0) => new()
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId(projectId),
            Title = "t",
            Prompt = "p",
            State = state,
            Priority = priority,
        };

    private static Task SeedSummaryAsync(IAgentStreamSummaryStore summaries, WorkItemId id) =>
        summaries.UpsertAsync(new AgentStreamSummaryRow(
            id,
            "work-1-abcdef.jsonl",
            "work",
            1,
            AgentKind.Claude,
            new AgentStreamSummary(
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(1),
                10,
                20,
                0,
                null,
                [],
                [],
                null),
            DateTimeOffset.UtcNow));

    private sealed class AbandonRaceStore(SqliteWorkItemStore inner) : ForwardingWorkItemStore(inner)
    {
        private int _injected;

        public override async Task<bool> TryUpdateIfStateAndUpdatedAtAsync(
            WorkItem item,
            WorkItemState onlyIfState,
            DateTimeOffset onlyIfUpdatedAt,
            CancellationToken ct = default)
        {
            if (item.State == WorkItemState.AbandonedAfterRecoveryAttempts
                && onlyIfState == WorkItemState.Queued
                && Interlocked.Exchange(ref _injected, 1) == 0)
            {
                var current = await Inner.GetAsync(item.Id, ct);
                Assert.NotNull(current);
                await Inner.UpdateAsync(current! with
                {
                    State = WorkItemState.Working,
                    StartedAt = DateTimeOffset.UtcNow,
                    LastError = "worker picked up item",
                    UpdatedAt = DateTimeOffset.UtcNow,
                }, ct);
            }

            return await Inner.TryUpdateIfStateAndUpdatedAtAsync(item, onlyIfState, onlyIfUpdatedAt, ct);
        }
    }

    private sealed class PromoteRaceStore(SqliteWorkItemStore inner) : ForwardingWorkItemStore(inner)
    {
        private int _injected;

        public override async Task<PriorityUpdateResult> UpdatePriorityIfStateAsync(
            WorkItemId id,
            int priority,
            DateTimeOffset updatedAt,
            WorkItemState onlyIfState,
            CancellationToken ct = default)
        {
            if (onlyIfState == WorkItemState.Queued && Interlocked.Exchange(ref _injected, 1) == 0)
            {
                var current = await Inner.GetAsync(id, ct);
                Assert.NotNull(current);
                await Inner.UpdateAsync(current! with
                {
                    State = WorkItemState.Working,
                    StartedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                }, ct);
            }

            return await Inner.UpdatePriorityIfStateAsync(id, priority, updatedAt, onlyIfState, ct);
        }
    }

    private sealed class PromoteDeletedRowStore(SqliteWorkItemStore inner) : ForwardingWorkItemStore(inner)
    {
        private int _injected;

        public override async Task<PriorityUpdateResult> UpdatePriorityIfStateAsync(
            WorkItemId id,
            int priority,
            DateTimeOffset updatedAt,
            WorkItemState onlyIfState,
            CancellationToken ct = default)
        {
            if (onlyIfState == WorkItemState.Queued && Interlocked.Exchange(ref _injected, 1) == 0)
                await Inner.DeleteRowForTestAsync(id, ct);

            return await Inner.UpdatePriorityIfStateAsync(id, priority, updatedAt, onlyIfState, ct);
        }
    }

    private sealed class PromoteTerminalRaceStore(SqliteWorkItemStore inner) : ForwardingWorkItemStore(inner)
    {
        private int _injected;

        public override async Task<PriorityUpdateResult> UpdatePriorityIfStateAsync(
            WorkItemId id,
            int priority,
            DateTimeOffset updatedAt,
            WorkItemState onlyIfState,
            CancellationToken ct = default)
        {
            if (onlyIfState == WorkItemState.Queued && Interlocked.Exchange(ref _injected, 1) == 0)
            {
                var current = await Inner.GetAsync(id, ct);
                Assert.NotNull(current);
                await Inner.UpdateAsync(current! with
                {
                    State = WorkItemState.Done,
                    UpdatedAt = DateTimeOffset.UtcNow,
                }, ct);
            }

            return await Inner.UpdatePriorityIfStateAsync(id, priority, updatedAt, onlyIfState, ct);
        }
    }

    private abstract class ForwardingWorkItemStore(SqliteWorkItemStore inner) : IWorkItemStore
    {
        protected SqliteWorkItemStore Inner { get; } = inner;

        public virtual Task CreateAsync(WorkItem item, CancellationToken ct = default) => Inner.CreateAsync(item, ct);
        public virtual Task UpdateAsync(WorkItem item, CancellationToken ct = default) => Inner.UpdateAsync(item, ct);
        public virtual Task<bool> TryUpdateIfStateAsync(WorkItem item, WorkItemState onlyIfState, CancellationToken ct = default) =>
            Inner.TryUpdateIfStateAsync(item, onlyIfState, ct);
        public virtual Task<bool> TryUpdateIfStateAndUpdatedAtAsync(WorkItem item, WorkItemState onlyIfState, DateTimeOffset onlyIfUpdatedAt, CancellationToken ct = default) =>
            Inner.TryUpdateIfStateAndUpdatedAtAsync(item, onlyIfState, onlyIfUpdatedAt, ct);
        public virtual Task<PriorityUpdateResult> UpdatePriorityAsync(WorkItemId id, int priority, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            Inner.UpdatePriorityAsync(id, priority, updatedAt, ct);
        public virtual Task<PriorityUpdateResult> UpdatePriorityIfStateAsync(WorkItemId id, int priority, DateTimeOffset updatedAt, WorkItemState onlyIfState, CancellationToken ct = default) =>
            Inner.UpdatePriorityIfStateAsync(id, priority, updatedAt, onlyIfState, ct);
        public virtual Task<DependsOnUpdateResult> UpdateDependsOnAsync(WorkItemId id, IReadOnlyList<WorkItemId> dependsOn, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            Inner.UpdateDependsOnAsync(id, dependsOn, updatedAt, ct);
        public virtual Task<AuditBudgetUpdateResult> UpdateAuditBudgetAsync(WorkItemId id, int? auditMaxIterations, string? auditComplexity, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            Inner.UpdateAuditBudgetAsync(id, auditMaxIterations, auditComplexity, updatedAt, ct);
        public virtual Task<WorkItem?> GetAsync(WorkItemId id, CancellationToken ct = default) => Inner.GetAsync(id, ct);
        public virtual IAsyncEnumerable<WorkItem> ListAsync(CancellationToken ct = default) => Inner.ListAsync(ct);
        public virtual IAsyncEnumerable<WorkItem> ListByStateAsync(WorkItemState state, CancellationToken ct = default) => Inner.ListByStateAsync(state, ct);
        public virtual Task<int> CountByStateAsync(WorkItemState state, CancellationToken ct = default) => Inner.CountByStateAsync(state, ct);
        public virtual Task ReorderAsync(IReadOnlyList<WorkItemId> orderedIds, CancellationToken ct = default) => Inner.ReorderAsync(orderedIds, ct);
        public virtual IAsyncEnumerable<WorkItem> ListDispatchEligibleByPriorityAsync(IReadOnlySet<WorkItemId> skipIds, CancellationToken ct = default) =>
            Inner.ListDispatchEligibleByPriorityAsync(skipIds, ct);
        public virtual Task<int> CountStartedInWindowAsync(ProjectId projectId, DateTimeOffset since, CancellationToken ct = default) =>
            Inner.CountStartedInWindowAsync(projectId, since, ct);
        public virtual Task<int> CountInFlightAsync(ProjectId projectId, CancellationToken ct = default) => Inner.CountInFlightAsync(projectId, ct);
        public virtual Task<(int Refactor, int Other)> CountInFlightSplitByRefactorAsync(ProjectId projectId, CancellationToken ct = default, WorkItemId? excludeId = null) =>
            Inner.CountInFlightSplitByRefactorAsync(projectId, ct, excludeId);
        public virtual Task<WorkItem?> GetByExternalIdAsync(ProjectId projectId, string externalId, CancellationToken ct = default) =>
            Inner.GetByExternalIdAsync(projectId, externalId, ct);
        public virtual Task<WorkItem?> GetByNamespacedExternalIdAsync(ProjectId projectId, string @namespace, string externalId, CancellationToken ct = default) =>
            Inner.GetByNamespacedExternalIdAsync(projectId, @namespace, externalId, ct);
        public virtual Task<WorkItem?> ReplaceExternalIdsAsync(WorkItemId id, IReadOnlyDictionary<string, string> externalIds, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            Inner.ReplaceExternalIdsAsync(id, externalIds, updatedAt, ct);
        public virtual Task<IReadOnlyList<(string ProjectId, int State, int Count, string MaxUpdatedAt)>> GetFleetStateCountsAsync(CancellationToken ct = default) =>
            Inner.GetFleetStateCountsAsync(ct);
        public virtual Task<IReadOnlyList<(string ProjectId, int State)>> GetFleetRecentOutcomesAsync(int perProject = 5, CancellationToken ct = default) =>
            Inner.GetFleetRecentOutcomesAsync(perProject, ct);
        public virtual Task<IReadOnlyDictionary<string, bool>> GetFleetPauseStatesAsync(CancellationToken ct = default) =>
            Inner.GetFleetPauseStatesAsync(ct);
        public virtual IAsyncEnumerable<WorkItem> ListByReplaySourceAsync(WorkItemId sourceId, CancellationToken ct = default) =>
            Inner.ListByReplaySourceAsync(sourceId, ct);
        public virtual IAsyncEnumerable<WorkItem> ListSuspendedAsync(CancellationToken ct = default) => Inner.ListSuspendedAsync(ct);
        public virtual Task<IReadOnlySet<string>> GetActiveBaselineImageRefsAsync(CancellationToken ct = default) =>
            Inner.GetActiveBaselineImageRefsAsync(ct);
        public virtual Task<IReadOnlyList<(WorkItemId Id, string Title, WorkItemState State)>> ListWorkItemsForBaselineAsync(string baselineImageRef, CancellationToken ct = default) =>
            Inner.ListWorkItemsForBaselineAsync(baselineImageRef, ct);
        public virtual Task OrphanReplaysAsync(WorkItemId sourceId, CancellationToken ct = default) => Inner.OrphanReplaysAsync(sourceId, ct);
        public virtual IAsyncEnumerable<WorkItem> ListByReleaseAsync(ReleaseId releaseId, CancellationToken ct = default) => Inner.ListByReleaseAsync(releaseId, ct);
        public virtual Task<PromptReplaceResult> TryReplacePromptAsync(WorkItemId id, string newPrompt, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            Inner.TryReplacePromptAsync(id, newPrompt, updatedAt, ct);
        public virtual Task RecordIterationDispatchAsync(WorkItemId workItemId, int iteration, int promptRevisionAtDispatch, DateTimeOffset dispatchedAt, CancellationToken ct = default) =>
            Inner.RecordIterationDispatchAsync(workItemId, iteration, promptRevisionAtDispatch, dispatchedAt, ct);
        public virtual Task<IReadOnlyList<WorkItemIteration>> GetIterationsAsync(WorkItemId workItemId, CancellationToken ct = default) =>
            Inner.GetIterationsAsync(workItemId, ct);
    }
}
