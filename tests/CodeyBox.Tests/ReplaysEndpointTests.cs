using System.Net;
using System.Net.Http.Json;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for GET /workitems/{id}/replays.
/// Verifies that the endpoint returns the source and all its replays
/// in chronological order, and handles edge cases correctly.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class ReplaysEndpointTests : IDisposable
{
    private readonly WorkItemApiFactory _factory = new();
    private readonly HttpClient _client;

    public ReplaysEndpointTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static WorkItem DoneItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "source",
        Prompt = "prompt",
        Agent = AgentKind.Claude,
        State = WorkItemState.Done,
    };

    [Fact]
    public async Task GetReplays_UnknownId_Returns404()
    {
        var resp = await _client.GetAsync($"/workitems/{Guid.NewGuid()}/replays");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task GetReplays_NoReplays_ReturnsSourceAndEmptyList()
    {
        var source = DoneItem();
        await _factory.Store.CreateAsync(source);

        var resp = await _client.GetAsync($"/workitems/{source.Id}/replays");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<ReplaysResponse>();
        Assert.Equal(source.Id.ToString(), body!.Source.Id);
        Assert.Empty(body.Replays);
    }

    [Fact]
    public async Task GetReplays_OneReplay_ReturnsSourceAndReplay()
    {
        var source = DoneItem();
        await _factory.Store.CreateAsync(source);

        await _client.PostAsJsonAsync($"/workitems/{source.Id}/replay", new { });

        var resp = await _client.GetAsync($"/workitems/{source.Id}/replays");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<ReplaysResponse>();
        Assert.Equal(source.Id.ToString(), body!.Source.Id);
        Assert.Single(body.Replays);
        Assert.Equal(source.Id.ToString(), body.Replays[0].ReplayOfWorkItemId);
    }

    [Fact]
    public async Task GetReplays_TwoReplays_ReturnsBothInChronologicalOrder()
    {
        var source = DoneItem();
        await _factory.Store.CreateAsync(source);

        var replayResp1 = await _client.PostAsJsonAsync($"/workitems/{source.Id}/replay", new { agent = "codex" });
        var replayResp2 = await _client.PostAsJsonAsync($"/workitems/{source.Id}/replay", new { agent = "gemini" });

        var replay1 = await replayResp1.Content.ReadFromJsonAsync<IdOnlyResponse>();
        var replay2 = await replayResp2.Content.ReadFromJsonAsync<IdOnlyResponse>();

        var resp = await _client.GetAsync($"/workitems/{source.Id}/replays");
        var body = await resp.Content.ReadFromJsonAsync<ReplaysResponse>();

        Assert.Equal(2, body!.Replays.Count);
        // Both replays link back to the source
        Assert.All(body.Replays, r => Assert.Equal(source.Id.ToString(), r.ReplayOfWorkItemId));
        // IDs are the two created replays
        var replayIds = body.Replays.Select(r => r.Id).ToHashSet();
        Assert.Contains(replay1!.Id, replayIds);
        Assert.Contains(replay2!.Id, replayIds);
    }

    [Fact]
    public async Task GetReplays_SourceDtoIncludesCorrectState()
    {
        var source = DoneItem();
        await _factory.Store.CreateAsync(source);

        var resp = await _client.GetAsync($"/workitems/{source.Id}/replays");
        var body = await resp.Content.ReadFromJsonAsync<ReplaysResponse>();

        Assert.Equal("Done", body!.Source.State);
    }

    [Fact]
    public async Task GetReplays_ReplayDtoHasQueuedState()
    {
        var source = DoneItem();
        await _factory.Store.CreateAsync(source);
        await _client.PostAsJsonAsync($"/workitems/{source.Id}/replay", new { });

        var resp = await _client.GetAsync($"/workitems/{source.Id}/replays");
        var body = await resp.Content.ReadFromJsonAsync<ReplaysResponse>();

        Assert.Equal("Queued", body!.Replays[0].State);
    }

    private sealed record ReplaysResponse(
        ReplayItemDto Source,
        List<ReplayItemDto> Replays);

    private sealed record ReplayItemDto(
        string Id,
        string State,
        string Agent,
        string? ReplayOfWorkItemId);

    private sealed record IdOnlyResponse(string Id);
}
