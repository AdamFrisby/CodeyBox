using System.Net;
using System.Net.Http.Json;
using CodeyBox.Core;
using CodeyBox.Tests;

namespace CodeyBox.Tests.Uat.WorkItemApisAndQueueControls;

/// <summary>
/// UAT coverage for work item CRUD and lifecycle endpoints.
/// Plan anchor: docs/uat/00-plan.md#work-item-crud-and-lifecycle-endpoints---creates-lists-patches-retries-cancels-and-uncancels-work-items
/// </summary>
[Collection("GlobalSerilog")]
public sealed class WorkItemCrudAndLifecycleUatTests : IDisposable
{
    private readonly WorkItemApiFactory _factory = new();
    private readonly HttpClient _client;

    public WorkItemCrudAndLifecycleUatTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Create_ValidRequest_ReturnsDtoWithRoutingAndQueueFields()
    {
        var response = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = WorkItemApisAndQueueControlsHelpers.ProjectId,
            title = "Queue a routed item",
            prompt = "make the change",
            agent = "codex",
            agentClassId = "frontier",
            baseBranch = "main",
            workBranch = "feature/uat-routed",
            pushUpstream = false,
            externalId = "UAT-CRUD-1",
            minModelScore = 88,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<WorkItemDto>();

        Assert.NotNull(dto);
        Assert.Equal("UAT-CRUD-1", dto!.ExternalId);
        Assert.Equal("codex", dto.Agent);
        Assert.Equal("frontier", dto.AgentClassId);
        Assert.Equal("main", dto.BaseBranch);
        Assert.Equal("feature/uat-routed", dto.WorkBranch);
        Assert.Equal("Queued", dto.State);
        Assert.Equal(88, dto.MinModelScore);
        Assert.True(dto.QueuePosition > 0);
    }

    [Fact]
    public async Task Create_UnknownProject_ReturnsAvailableProjects()
    {
        var response = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "missing-project",
            title = "unknown project",
            prompt = "p",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.ReadJsonAsync();
        Assert.Contains("missing-project", json.GetProperty("error").GetString());
        Assert.Contains("test-project", json.GetProperty("available").EnumerateArray().Select(v => v.GetString()));
    }

    [Theory]
    [InlineData("title", 201)]
    [InlineData("prompt", 64 * 1024 + 1)]
    public async Task Create_RejectsOversizedTextFields(string field, int length)
    {
        var body = field == "title"
            ? new { projectId = WorkItemApisAndQueueControlsHelpers.ProjectId, title = new string('t', length), prompt = "p" }
            : new { projectId = WorkItemApisAndQueueControlsHelpers.ProjectId, title = "valid title", prompt = new string('p', length) };

        var response = await _client.PostAsJsonAsync("/workitems", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(field, await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Get_ByProjectExternalId_ResolvesSameItemAsUuid()
    {
        var create = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = WorkItemApisAndQueueControlsHelpers.ProjectId,
            title = "external lookup",
            prompt = "p",
            externalId = "UAT-LOOKUP-1",
        });
        create.EnsureSuccessStatusCode();
        var created = await create.Content.ReadFromJsonAsync<WorkItemDto>();

        var byExternal = await _client.GetAsync("/workitems/test-project:UAT-LOOKUP-1");

        byExternal.EnsureSuccessStatusCode();
        var fetched = await byExternal.Content.ReadFromJsonAsync<WorkItemDto>();
        Assert.Equal(created!.Id, fetched!.Id);
        Assert.Equal("UAT-LOOKUP-1", fetched.ExternalId);
    }

    [Fact]
    public async Task Patch_QueuedItem_UpdatesEditableFields()
    {
        var item = WorkItemApisAndQueueControlsHelpers.Item();
        await _factory.Store.CreateAsync(item);

        var response = await _client.PatchAsJsonAsync($"/workitems/{item.Id}", new
        {
            title = "patched title",
            prompt = "patched prompt",
            agent = "gemini",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<WorkItemDto>();
        Assert.Equal("patched title", dto!.Title);
        Assert.Equal("patched prompt", dto.Prompt);
        Assert.Equal("gemini", dto.Agent);

        var stored = await _factory.Store.GetAsync(item.Id);
        Assert.Equal("patched title", stored!.Title);
        Assert.Equal(AgentKind.Gemini, stored.Agent);
    }

    [Fact]
    public async Task Patch_NonQueuedItem_ReturnsConflictWithoutChangingItem()
    {
        var item = WorkItemApisAndQueueControlsHelpers.Item(WorkItemState.Working);
        await _factory.Store.CreateAsync(item);

        var response = await _client.PatchAsJsonAsync($"/workitems/{item.Id}", new
        {
            title = "should not apply",
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var stored = await _factory.Store.GetAsync(item.Id);
        Assert.Equal(item.Title, stored!.Title);
    }

    [Fact]
    public async Task Cancel_CascadesQueuedDependents_AndUncancelRequeuesCascadedChild()
    {
        var parent = WorkItemApisAndQueueControlsHelpers.Item(title: "parent");
        var child = WorkItemApisAndQueueControlsHelpers.Item(title: "child") with
        {
            DependsOn = [parent.Id],
        };
        await _factory.Store.CreateAsync(parent);
        await _factory.Store.CreateAsync(child);

        var cancel = await _client.DeleteAsync($"/workitems/{parent.Id}");

        Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);
        var cancelledParent = await _factory.Store.GetAsync(parent.Id);
        var cancelledChild = await _factory.Store.GetAsync(child.Id);
        Assert.Equal(WorkItemState.Cancelled, cancelledParent!.State);
        Assert.Equal(WorkItemCancellationReason.OperatorRequested, cancelledParent.CancellationReason);
        Assert.Equal(WorkItemState.Cancelled, cancelledChild!.State);
        Assert.Equal(WorkItemCancellationReason.ParentCascaded, cancelledChild.CancellationReason);

        var uncancel = await _client.PostAsJsonAsync($"/workitems/{child.Id}/uncancel", new { });

        Assert.Equal(HttpStatusCode.OK, uncancel.StatusCode);
        var requeuedChild = await _factory.Store.GetAsync(child.Id);
        Assert.Equal(WorkItemState.Queued, requeuedChild!.State);
        Assert.Null(requeuedChild.CancellationReason);
    }

    [Fact]
    public async Task Retry_FailedItemFromWork_RequeuesAndClearsFailure()
    {
        var item = WorkItemApisAndQueueControlsHelpers.Item(WorkItemState.Failed) with
        {
            LastError = "agent failed",
            FailureKind = "agent",
        };
        await _factory.Store.CreateAsync(item);

        var response = await _client.PostAsJsonAsync($"/workitems/{item.Id}/retry", new { from = "work" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var stored = await _factory.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Queued, stored!.State);
        Assert.Null(stored.LastError);
        Assert.Null(stored.FailureKind);
    }
}
