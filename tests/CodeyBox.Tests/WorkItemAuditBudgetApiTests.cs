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
}
