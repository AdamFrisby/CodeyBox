using System.Net;
using System.Net.Http.Json;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies the priority surface on POST /workitems, PATCH /workitems/{id}/priority,
/// and the priority field in the work-item DTO responses.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class WorkItemPriorityApiTests : IDisposable
{
    private readonly WorkItemApiFactory _factory;
    private readonly HttpClient _client;

    public WorkItemPriorityApiTests()
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

    private static object CreateBody(string projectId = "test-project", int? priority = null) => new
    {
        projectId,
        title = "t",
        prompt = "p",
        priority,
    };

    [Fact]
    public async Task PostWorkItem_DefaultPriorityIsZero()
    {
        var resp = await _client.PostAsJsonAsync("/workitems", CreateBody());
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var dto = await resp.Content.ReadFromJsonAsync<PriorityDto>();
        Assert.NotNull(dto);
        Assert.Equal(0, dto!.Priority);
    }

    [Fact]
    public async Task PostWorkItem_AcceptsPriorityWithinRange()
    {
        var resp = await _client.PostAsJsonAsync("/workitems", CreateBody(priority: 750));
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var dto = await resp.Content.ReadFromJsonAsync<PriorityDto>();
        Assert.Equal(750, dto!.Priority);
    }

    [Theory]
    [InlineData(1001)]
    [InlineData(-1001)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public async Task PostWorkItem_OutOfRangePriority_Returns400(int priority)
    {
        var resp = await _client.PostAsJsonAsync("/workitems", CreateBody(priority: priority));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task PostWorkItem_ExceedsProjectCap_Returns400()
    {
        var resp = await _client.PostAsJsonAsync("/workitems",
            CreateBody(projectId: "capped-project", priority: 500));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task PostWorkItem_AtProjectCap_Allowed()
    {
        var resp = await _client.PostAsJsonAsync("/workitems",
            CreateBody(projectId: "capped-project", priority: 200));
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }

    // ── PATCH priority ───────────────────────────────────────────────────────

    [Fact]
    public async Task PatchPriority_UpdatesPersistedValue()
    {
        var created = await _client.PostAsJsonAsync("/workitems", CreateBody(priority: 0));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var dto = await created.Content.ReadFromJsonAsync<PriorityDto>();

        var patch = await _client.PatchAsJsonAsync($"/workitems/{dto!.Id}/priority", new { priority = 250 });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        var fetched = await _client.GetFromJsonAsync<PriorityDto>($"/workitems/{dto.Id}");
        Assert.Equal(250, fetched!.Priority);
    }

    [Fact]
    public async Task PatchPriority_OutOfRange_Returns400()
    {
        var created = await _client.PostAsJsonAsync("/workitems", CreateBody());
        var dto = await created.Content.ReadFromJsonAsync<PriorityDto>();

        var patch = await _client.PatchAsJsonAsync($"/workitems/{dto!.Id}/priority", new { priority = 5000 });
        Assert.Equal(HttpStatusCode.BadRequest, patch.StatusCode);

        var fetched = await _client.GetFromJsonAsync<PriorityDto>($"/workitems/{dto.Id}");
        Assert.Equal(0, fetched!.Priority);
    }

    [Fact]
    public async Task PatchPriority_ExceedsProjectCap_Returns400()
    {
        var created = await _client.PostAsJsonAsync("/workitems",
            CreateBody(projectId: "capped-project", priority: 100));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var dto = await created.Content.ReadFromJsonAsync<PriorityDto>();

        // 500 is within the global [-1000, 1000] but above the project's MaxPriority=200.
        var patch = await _client.PatchAsJsonAsync($"/workitems/{dto!.Id}/priority", new { priority = 500 });
        Assert.Equal(HttpStatusCode.BadRequest, patch.StatusCode);

        var fetched = await _client.GetFromJsonAsync<PriorityDto>($"/workitems/{dto.Id}");
        Assert.Equal(100, fetched!.Priority);
    }

    [Fact]
    public async Task PatchPriority_OnNonQueuedItem_StillRecordsButHasNoFlightEffect()
    {
        // The spec says PATCH on a Working/Auditing item is allowed (stored) but doesn't
        // affect the in-flight item. The store is the source of truth for the next
        // pipeline phase; in-flight execution is unaffected.
        var created = await _client.PostAsJsonAsync("/workitems", CreateBody());
        var dto = await created.Content.ReadFromJsonAsync<PriorityDto>();
        var id = WorkItemId.Parse(dto!.Id);
        var item = await _factory.Store.GetAsync(id);
        await _factory.Store.UpdateAsync(item! with { State = WorkItemState.Working });

        var patch = await _client.PatchAsJsonAsync($"/workitems/{id}/priority", new { priority = 999 });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        var refreshed = await _factory.Store.GetAsync(id);
        Assert.Equal(999, refreshed!.Priority);
        Assert.Equal(WorkItemState.Working, refreshed.State);
    }

    [Fact]
    public async Task PatchPriority_NonExistentItem_Returns404()
    {
        var patch = await _client.PatchAsJsonAsync(
            $"/workitems/{Guid.NewGuid()}/priority", new { priority = 100 });
        Assert.Equal(HttpStatusCode.NotFound, patch.StatusCode);
    }

    [Theory]
    [InlineData(WorkItemState.Done)]
    [InlineData(WorkItemState.Failed)]
    [InlineData(WorkItemState.Cancelled)]
    [InlineData(WorkItemState.AuditFailed)]
    public async Task PatchPriority_TerminalState_Returns409(WorkItemState terminalState)
    {
        // Terminal items must not silently mutate; priority cannot affect them
        // and changing closed history records is undesirable (audit/compliance).
        var created = await _client.PostAsJsonAsync("/workitems", CreateBody(priority: 100));
        var dto = await created.Content.ReadFromJsonAsync<PriorityDto>();
        var id = WorkItemId.Parse(dto!.Id);
        var item = await _factory.Store.GetAsync(id);
        await _factory.Store.UpdateAsync(item! with { State = terminalState });

        var patch = await _client.PatchAsJsonAsync($"/workitems/{id}/priority", new { priority = 999 });
        Assert.Equal(HttpStatusCode.Conflict, patch.StatusCode);

        var refreshed = await _factory.Store.GetAsync(id);
        Assert.Equal(100, refreshed!.Priority);
        Assert.Equal(terminalState, refreshed.State);
    }

    [Fact]
    public async Task WorkersStatus_ReportsPersistedQueuedCount()
    {
        // Regression: after the channel became a kick stream, queuedCount must
        // reflect queued rows in the store, not buffered kick signals.
        await _factory.Store.CreateAsync(MakeStoredItem(WorkItemState.Queued));
        await _factory.Store.CreateAsync(MakeStoredItem(WorkItemState.Queued));
        await _factory.Store.CreateAsync(MakeStoredItem(WorkItemState.Working));

        var response = await _client.GetAsync("/workers/status");

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(2, json.GetProperty("queuedCount").GetInt32());
    }

    private static WorkItem MakeStoredItem(WorkItemState state) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "t",
        Prompt = "p",
        State = state,
    };

    private sealed record PriorityDto(string Id, int Priority);
}
