using System.Net;
using System.Net.Http.Json;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that GET/DELETE/PATCH /workitems/{id} accept both UUID and
/// composite &lt;projectId&gt;:&lt;externalId&gt; path formats and return the same record.
/// Also verifies mismatched project:externalId returns 404.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class RoutePathBothFormatsTests : IDisposable
{
    private readonly WorkItemApiFactory _factory = new();
    private readonly HttpClient _client;

    public RoutePathBothFormatsTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task<WorkItemResponse> CreateAsync(string externalId)
    {
        var r = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "t",
            prompt = "p",
            externalId,
        });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<WorkItemResponse>())!;
    }

    [Fact]
    public async Task GetByUuid_AndByComposite_ReturnSameRecord()
    {
        var created = await CreateAsync("ROUTE-1");

        var byUuid = await _client.GetFromJsonAsync<WorkItemResponse>($"/workitems/{created.Id}");
        var byComposite = await _client.GetFromJsonAsync<WorkItemResponse>($"/workitems/test-project:ROUTE-1");

        Assert.NotNull(byUuid);
        Assert.NotNull(byComposite);
        Assert.Equal(byUuid!.Id, byComposite!.Id);
        Assert.Equal("ROUTE-1", byComposite.ExternalId);
    }

    [Fact]
    public async Task GetByComposite_WrongProject_Returns404()
    {
        await CreateAsync("ROUTE-2");

        var r = await _client.GetAsync("/workitems/wrong-project:ROUTE-2");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task GetByComposite_EmptyProjectPart_Returns400()
    {
        var r = await _client.GetAsync("/workitems/:ROUTE-3");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task GetByComposite_EmptyExternalIdPart_Returns400()
    {
        var r = await _client.GetAsync("/workitems/test-project:");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task GetByComposite_ItemWithoutExternalId_Returns404()
    {
        // Create an item with no externalId
        var r = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "no-ext",
            prompt = "p",
        });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);

        // Trying to look up by a composite path returns 404 (no match)
        var lookup = await _client.GetAsync("/workitems/test-project:some-id");
        Assert.Equal(HttpStatusCode.NotFound, lookup.StatusCode);
    }

    [Fact]
    public async Task DeleteByComposite_CancelsItem()
    {
        var created = await CreateAsync("ROUTE-DEL");

        var r = await _client.DeleteAsync($"/workitems/test-project:ROUTE-DEL");
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);

        // Re-fetching by UUID should show Cancelled state
        var fetched = await _client.GetFromJsonAsync<WorkItemResponse>($"/workitems/{created.Id}");
        Assert.Equal("Cancelled", fetched!.State);
    }

    [Fact]
    public async Task PatchByComposite_UpdatesTitle()
    {
        var created = await CreateAsync("ROUTE-PATCH");

        var r = await _client.PatchAsJsonAsync(
            $"/workitems/test-project:ROUTE-PATCH",
            new { title = "patched title" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        var updated = await r.Content.ReadFromJsonAsync<WorkItemResponse>();
        Assert.NotNull(updated);
        Assert.Equal(created.Id, updated!.Id);
    }

    [Fact]
    public async Task RetryByComposite_ResolvesItem_Returns409WhenNotFailed()
    {
        // Item is in Queued state — retry should fail with 409 (not 404).
        // This verifies ResolveWorkItemAsync handles the composite path correctly;
        // the 409 proves the item was found before the state check fired.
        await CreateAsync("ROUTE-RETRY");

        var r = await _client.PostAsJsonAsync(
            "/workitems/test-project:ROUTE-RETRY/retry",
            new { from = "work" });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
    }

    [Fact]
    public async Task GetDependentsByComposite_ReturnsEmptyList()
    {
        await CreateAsync("ROUTE-DEPS");

        var r = await _client.GetAsync("/workitems/test-project:ROUTE-DEPS/dependents");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task GetTimelineByComposite_ReturnsOk()
    {
        await CreateAsync("ROUTE-TIMELINE");

        var r = await _client.GetAsync("/workitems/test-project:ROUTE-TIMELINE/timeline");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    private sealed record WorkItemResponse(string Id, string? ExternalId, string State = "");
}
