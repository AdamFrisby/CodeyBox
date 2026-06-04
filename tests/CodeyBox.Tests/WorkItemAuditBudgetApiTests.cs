using System.Net;
using System.Net.Http.Json;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class WorkItemAuditBudgetApiTests : IDisposable
{
    private readonly WorkItemApiFactory _factory = new();
    private readonly HttpClient _client;

    public WorkItemAuditBudgetApiTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task PostWorkItem_AuditBudgetFields_PersistAndNormalize()
    {
        var response = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "t",
            prompt = "p",
            auditMaxIterations = 12,
            auditComplexity = "  hard  ",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<AuditBudgetDto>();
        Assert.Equal(12, dto!.AuditMaxIterations);
        Assert.Equal("hard", dto.AuditComplexity);

        var stored = await _factory.Store.GetAsync(WorkItemId.Parse(dto.Id));
        Assert.Equal(12, stored!.AuditMaxIterations);
        Assert.Equal("hard", stored.AuditComplexity);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task PostWorkItem_AuditMaxIterations_NonPositive_Returns400(int auditMaxIterations)
    {
        var response = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "t",
            prompt = "p",
            auditMaxIterations,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostWorkItem_AuditMaxIterations_AboveHardCap_Returns400()
    {
        var response = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "t",
            prompt = "p",
            auditMaxIterations = ProjectAudit.MaxIterationBudget + 1,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostWorkItem_AuditComplexity_TooLong_Returns400()
    {
        var response = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "t",
            prompt = "p",
            auditComplexity = new string('x', 65),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("--flag")]
    [InlineData("hard\nmode")]
    public async Task PostWorkItem_AuditComplexity_OptionLikeOrControl_Returns400(string auditComplexity)
    {
        var response = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "t",
            prompt = "p",
            auditComplexity,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("auditComplexity", body);
    }

    [Fact]
    public async Task PatchAuditBudget_OnWorkingItem_PersistsWithoutClobberingRuntimeColumns()
    {
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var item = NewStoredItem() with
        {
            State = WorkItemState.Working,
            StartedAt = startedAt,
            AgentLogPath = "/logs/current.jsonl",
            FailureKind = "quota",
            QuotaRetryAttempts = 2,
        };
        await _factory.Store.CreateAsync(item);

        var response = await _client.PatchAsJsonAsync($"/workitems/{item.Id}", new
        {
            auditMaxIterations = 15,
            auditComplexity = "  very-hard  ",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<AuditBudgetDto>();
        Assert.Equal(15, dto!.AuditMaxIterations);
        Assert.Equal("very-hard", dto.AuditComplexity);

        var stored = await _factory.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Working, stored!.State);
        Assert.Equal(startedAt, stored.StartedAt);
        Assert.Equal("/logs/current.jsonl", stored.AgentLogPath);
        Assert.Equal("quota", stored.FailureKind);
        Assert.Equal(2, stored.QuotaRetryAttempts);
        Assert.Equal(15, stored.AuditMaxIterations);
        Assert.Equal("very-hard", stored.AuditComplexity);
    }

    [Fact]
    public async Task PatchAuditBudget_WithDependsOn_OnWorkingItem_PersistsBothPartialUpdates()
    {
        var dep = NewStoredItem() with { State = WorkItemState.Done, Title = "dep" };
        var item = NewStoredItem() with { State = WorkItemState.Auditing, Title = "target" };
        await _factory.Store.CreateAsync(dep);
        await _factory.Store.CreateAsync(item);

        var response = await _client.PatchAsJsonAsync($"/workitems/{item.Id}", new
        {
            auditMaxIterations = 9,
            dependsOn = new[] { dep.Id.ToString() },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = await _factory.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Auditing, stored!.State);
        Assert.Equal(9, stored.AuditMaxIterations);
        Assert.Equal([dep.Id], stored.DependsOn);
    }

    [Fact]
    public async Task PatchAuditBudget_WithQueuedOnlyField_OnQueuedItem_PersistsBothWrites()
    {
        var item = NewStoredItem() with { State = WorkItemState.Queued, Title = "old title" };
        await _factory.Store.CreateAsync(item);

        var response = await _client.PatchAsJsonAsync($"/workitems/{item.Id}", new
        {
            title = "new title",
            auditMaxIterations = 11,
            auditComplexity = "  hard  ",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await _factory.Store.GetAsync(item.Id);
        Assert.Equal("new title", stored!.Title);
        Assert.Equal(11, stored.AuditMaxIterations);
        Assert.Equal("hard", stored.AuditComplexity);
    }

    [Fact]
    public async Task PatchAuditBudget_OnNeedsOperatorInputItem_Persists()
    {
        var item = NewStoredItem() with
        {
            State = WorkItemState.NeedsOperatorInput,
            LastError = "audit reached max iterations",
        };
        await _factory.Store.CreateAsync(item);

        var response = await _client.PatchAsJsonAsync($"/workitems/{item.Id}", new
        {
            auditMaxIterations = 17,
            auditComplexity = "  very-hard  ",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await _factory.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.NeedsOperatorInput, stored!.State);
        Assert.Equal("audit reached max iterations", stored.LastError);
        Assert.Equal(17, stored.AuditMaxIterations);
        Assert.Equal("very-hard", stored.AuditComplexity);
    }

    [Theory]
    [InlineData(AuditBudgetUpdateOutcome.NotFound, HttpStatusCode.NotFound)]
    [InlineData(AuditBudgetUpdateOutcome.TerminalState, HttpStatusCode.Conflict)]
    public async Task PatchAuditBudget_ConcurrentStoreOutcome_MapsResponseStatus(
        AuditBudgetUpdateOutcome outcome,
        HttpStatusCode expectedStatus)
    {
        using var factory = new WorkItemApiFactory
        {
            WorkItemStoreDecorator = store => new AuditBudgetRaceStore(store, outcome),
        };
        using var client = factory.CreateClient();
        var item = NewStoredItem() with { State = WorkItemState.Working };
        await factory.Store.CreateAsync(item);

        var response = await client.PatchAsJsonAsync(
            $"/workitems/{item.Id}",
            new { auditMaxIterations = 8 });

        Assert.Equal(expectedStatus, response.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task PatchAuditBudget_AuditMaxIterations_NonPositive_Returns400(int auditMaxIterations)
    {
        var item = NewStoredItem() with { State = WorkItemState.Auditing };
        await _factory.Store.CreateAsync(item);

        var response = await _client.PatchAsJsonAsync($"/workitems/{item.Id}", new { auditMaxIterations });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var stored = await _factory.Store.GetAsync(item.Id);
        Assert.Null(stored!.AuditMaxIterations);
    }

    [Fact]
    public async Task PatchAuditBudget_AuditMaxIterations_AboveHardCap_Returns400()
    {
        var item = NewStoredItem() with { State = WorkItemState.Auditing };
        await _factory.Store.CreateAsync(item);

        var response = await _client.PatchAsJsonAsync(
            $"/workitems/{item.Id}",
            new { auditMaxIterations = ProjectAudit.MaxIterationBudget + 1 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("--flag")]
    [InlineData("hard\nmode")]
    public async Task PatchAuditBudget_AuditComplexity_OptionLikeOrControl_Returns400(string auditComplexity)
    {
        var item = NewStoredItem() with { State = WorkItemState.Auditing };
        await _factory.Store.CreateAsync(item);

        var response = await _client.PatchAsJsonAsync($"/workitems/{item.Id}", new { auditComplexity });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var stored = await _factory.Store.GetAsync(item.Id);
        Assert.Null(stored!.AuditComplexity);
    }

    [Fact]
    public async Task PatchAuditBudget_OnTerminalItem_Returns409()
    {
        var item = NewStoredItem() with
        {
            State = WorkItemState.Done,
            AuditMaxIterations = 4,
            AuditComplexity = "hard",
        };
        await _factory.Store.CreateAsync(item);

        var response = await _client.PatchAsJsonAsync(
            $"/workitems/{item.Id}",
            new { auditMaxIterations = 8, auditComplexity = "very-hard" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var stored = await _factory.Store.GetAsync(item.Id);
        Assert.Equal(4, stored!.AuditMaxIterations);
        Assert.Equal("hard", stored.AuditComplexity);
    }

    [Fact]
    public async Task SqliteUpdateAuditBudget_MissingRow_ReturnsNotFound()
    {
        var result = await _factory.Store.UpdateAuditBudgetAsync(
            WorkItemId.New(),
            auditMaxIterations: 7,
            auditComplexity: "hard",
            DateTimeOffset.UtcNow);

        Assert.Equal(AuditBudgetUpdateOutcome.NotFound, result.Outcome);
        Assert.Null(result.Item);
    }

    [Fact]
    public async Task SqliteUpdateAuditBudget_TerminalRow_ReturnsTerminalStateAndDoesNotUpdate()
    {
        var item = NewStoredItem() with
        {
            State = WorkItemState.Done,
            AuditMaxIterations = 4,
            AuditComplexity = "hard",
        };
        await _factory.Store.CreateAsync(item);

        var result = await _factory.Store.UpdateAuditBudgetAsync(
            item.Id,
            auditMaxIterations: 8,
            auditComplexity: "very-hard",
            DateTimeOffset.UtcNow);

        Assert.Equal(AuditBudgetUpdateOutcome.TerminalState, result.Outcome);
        Assert.Equal(WorkItemState.Done, result.Item!.State);
        Assert.Equal(4, result.Item.AuditMaxIterations);
        Assert.Equal("hard", result.Item.AuditComplexity);

        var stored = await _factory.Store.GetAsync(item.Id);
        Assert.Equal(4, stored!.AuditMaxIterations);
        Assert.Equal("hard", stored.AuditComplexity);
    }

    private static WorkItem NewStoredItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "t",
        Prompt = "p",
        Agent = AgentKind.Claude,
        State = WorkItemState.Queued,
    };

    private sealed record AuditBudgetDto(
        string Id,
        string State,
        int? AuditMaxIterations,
        string? AuditComplexity);

    private sealed class AuditBudgetRaceStore(
        SqliteWorkItemStore inner,
        AuditBudgetUpdateOutcome outcome) : IWorkItemStore
    {
        public Task CreateAsync(WorkItem item, CancellationToken ct = default) =>
            inner.CreateAsync(item, ct);

        public Task UpdateAsync(WorkItem item, CancellationToken ct = default) =>
            inner.UpdateAsync(item, ct);

        public Task<bool> TryUpdateIfStateAsync(WorkItem item, WorkItemState onlyIfState, CancellationToken ct = default) =>
            inner.TryUpdateIfStateAsync(item, onlyIfState, ct);

        public Task<PriorityUpdateResult> UpdatePriorityAsync(WorkItemId id, int priority, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            inner.UpdatePriorityAsync(id, priority, updatedAt, ct);

        public Task<DependsOnUpdateResult> UpdateDependsOnAsync(
            WorkItemId id,
            IReadOnlyList<WorkItemId> dependsOn,
            DateTimeOffset updatedAt,
            CancellationToken ct = default) =>
            inner.UpdateDependsOnAsync(id, dependsOn, updatedAt, ct);

        public async Task<AuditBudgetUpdateResult> UpdateAuditBudgetAsync(
            WorkItemId id,
            int? auditMaxIterations,
            string? auditComplexity,
            DateTimeOffset updatedAt,
            CancellationToken ct = default)
        {
            if (outcome == AuditBudgetUpdateOutcome.NotFound)
                return new AuditBudgetUpdateResult(AuditBudgetUpdateOutcome.NotFound, null);

            if (outcome == AuditBudgetUpdateOutcome.TerminalState)
            {
                var current = await inner.GetAsync(id, ct);
                var baseline = current ?? NewStoredItem() with { Id = id };
                var terminal = baseline with
                {
                    State = WorkItemState.Done,
                    UpdatedAt = updatedAt,
                };
                if (current is not null)
                    await inner.UpdateAsync(terminal, ct);
                return new AuditBudgetUpdateResult(AuditBudgetUpdateOutcome.TerminalState, terminal);
            }

            return await inner.UpdateAuditBudgetAsync(id, auditMaxIterations, auditComplexity, updatedAt, ct);
        }

        public Task<WorkItem?> GetAsync(WorkItemId id, CancellationToken ct = default) =>
            inner.GetAsync(id, ct);

        public IAsyncEnumerable<WorkItem> ListAsync(CancellationToken ct = default) =>
            inner.ListAsync(ct);

        public IAsyncEnumerable<WorkItem> ListByStateAsync(WorkItemState state, CancellationToken ct = default) =>
            inner.ListByStateAsync(state, ct);

        public Task<int> CountByStateAsync(WorkItemState state, CancellationToken ct = default) =>
            inner.CountByStateAsync(state, ct);

        public Task ReorderAsync(IReadOnlyList<WorkItemId> orderedIds, CancellationToken ct = default) =>
            inner.ReorderAsync(orderedIds, ct);

        public IAsyncEnumerable<WorkItem> ListDispatchEligibleByPriorityAsync(IReadOnlySet<WorkItemId> skipIds, CancellationToken ct = default) =>
            inner.ListDispatchEligibleByPriorityAsync(skipIds, ct);

        public Task<int> CountStartedInWindowAsync(ProjectId projectId, DateTimeOffset since, CancellationToken ct = default) =>
            inner.CountStartedInWindowAsync(projectId, since, ct);

        public Task<int> CountInFlightAsync(ProjectId projectId, CancellationToken ct = default) =>
            inner.CountInFlightAsync(projectId, ct);

        public Task<WorkItem?> GetByExternalIdAsync(ProjectId projectId, string externalId, CancellationToken ct = default) =>
            inner.GetByExternalIdAsync(projectId, externalId, ct);

        public Task<WorkItem?> GetByNamespacedExternalIdAsync(ProjectId projectId, string @namespace, string externalId, CancellationToken ct = default) =>
            inner.GetByNamespacedExternalIdAsync(projectId, @namespace, externalId, ct);

        public Task<WorkItem?> ReplaceExternalIdsAsync(
            WorkItemId id,
            IReadOnlyDictionary<string, string> externalIds,
            DateTimeOffset updatedAt,
            CancellationToken ct = default) =>
            inner.ReplaceExternalIdsAsync(id, externalIds, updatedAt, ct);

        public Task<IReadOnlyList<(string ProjectId, int State, int Count, string MaxUpdatedAt)>> GetFleetStateCountsAsync(CancellationToken ct = default) =>
            inner.GetFleetStateCountsAsync(ct);

        public Task<IReadOnlyList<(string ProjectId, int State)>> GetFleetRecentOutcomesAsync(int perProject = 5, CancellationToken ct = default) =>
            inner.GetFleetRecentOutcomesAsync(perProject, ct);

        public Task<IReadOnlyDictionary<string, bool>> GetFleetPauseStatesAsync(CancellationToken ct = default) =>
            inner.GetFleetPauseStatesAsync(ct);

        public IAsyncEnumerable<WorkItem> ListByReplaySourceAsync(WorkItemId sourceId, CancellationToken ct = default) =>
            inner.ListByReplaySourceAsync(sourceId, ct);

        public IAsyncEnumerable<WorkItem> ListSuspendedAsync(CancellationToken ct = default) =>
            inner.ListSuspendedAsync(ct);

        public Task<IReadOnlySet<string>> GetActiveBaselineImageRefsAsync(CancellationToken ct = default) =>
            inner.GetActiveBaselineImageRefsAsync(ct);

        public Task<IReadOnlyList<(WorkItemId Id, string Title, WorkItemState State)>> ListWorkItemsForBaselineAsync(
            string baselineImageRef,
            CancellationToken ct = default) =>
            inner.ListWorkItemsForBaselineAsync(baselineImageRef, ct);

        public Task OrphanReplaysAsync(WorkItemId sourceId, CancellationToken ct = default) =>
            inner.OrphanReplaysAsync(sourceId, ct);

        public IAsyncEnumerable<WorkItem> ListByReleaseAsync(ReleaseId releaseId, CancellationToken ct = default) =>
            inner.ListByReleaseAsync(releaseId, ct);

        public Task<PromptReplaceResult> TryReplacePromptAsync(
            WorkItemId id,
            string newPrompt,
            DateTimeOffset updatedAt,
            CancellationToken ct = default) =>
            inner.TryReplacePromptAsync(id, newPrompt, updatedAt, ct);

        public Task RecordIterationDispatchAsync(
            WorkItemId workItemId,
            int iteration,
            int promptRevisionAtDispatch,
            DateTimeOffset dispatchedAt,
            CancellationToken ct = default) =>
            inner.RecordIterationDispatchAsync(workItemId, iteration, promptRevisionAtDispatch, dispatchedAt, ct);

        public Task<IReadOnlyList<WorkItemIteration>> GetIterationsAsync(WorkItemId workItemId, CancellationToken ct = default) =>
            inner.GetIterationsAsync(workItemId, ct);
    }
}
