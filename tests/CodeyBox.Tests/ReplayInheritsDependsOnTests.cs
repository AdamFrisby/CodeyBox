using System.Net;
using System.Net.Http.Json;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that replays inherit the source's dependsOn list unchanged.
/// The dependency graph of the source applies equally to its replays.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class ReplayInheritsDependsOnTests : IDisposable
{
    private readonly WorkItemApiFactory _factory = new();
    private readonly HttpClient _client;

    public ReplayInheritsDependsOnTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static WorkItem DoneItem(IReadOnlyList<WorkItemId>? dependsOn = null) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "item",
        Prompt = "p",
        Agent = AgentKind.Claude,
        State = WorkItemState.Done,
        DependsOn = dependsOn ?? [],
    };

    [Fact]
    public async Task Replay_NoDependsOn_ReplayAlsoHasNone()
    {
        var source = DoneItem();
        await _factory.Store.CreateAsync(source);

        var resp = await _client.PostAsJsonAsync($"/workitems/{source.Id}/replay", new { });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var dto = await resp.Content.ReadFromJsonAsync<DependsOnResponse>();
        Assert.Empty(dto!.DependsOn);
    }

    [Fact]
    public async Task Replay_WithTwoDependencies_ReplayInheritsBoth()
    {
        var depX = DoneItem();
        var depY = DoneItem();
        await _factory.Store.CreateAsync(depX);
        await _factory.Store.CreateAsync(depY);

        var source = DoneItem([depX.Id, depY.Id]);
        await _factory.Store.CreateAsync(source);

        var resp = await _client.PostAsJsonAsync($"/workitems/{source.Id}/replay", new { });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var dto = await resp.Content.ReadFromJsonAsync<DependsOnResponse>();
        Assert.Equal(2, dto!.DependsOn.Count);
        Assert.Contains(depX.Id.ToString(), dto.DependsOn);
        Assert.Contains(depY.Id.ToString(), dto.DependsOn);
    }

    [Fact]
    public async Task Replay_WithOneDependency_ReplayInheritsIt()
    {
        var dep = DoneItem();
        await _factory.Store.CreateAsync(dep);

        var source = DoneItem([dep.Id]);
        await _factory.Store.CreateAsync(source);

        var resp = await _client.PostAsJsonAsync($"/workitems/{source.Id}/replay", new { });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var dto = await resp.Content.ReadFromJsonAsync<DependsOnResponse>();
        Assert.Single(dto!.DependsOn);
        Assert.Equal(dep.Id.ToString(), dto.DependsOn[0]);
    }

    [Fact]
    public async Task ReplayInherited_IsVerifiedInStore()
    {
        var dep = DoneItem();
        await _factory.Store.CreateAsync(dep);

        var source = DoneItem([dep.Id]);
        await _factory.Store.CreateAsync(source);

        var resp = await _client.PostAsJsonAsync($"/workitems/{source.Id}/replay", new { });
        var dto = await resp.Content.ReadFromJsonAsync<DependsOnResponse>();

        // Verify via the store directly
        var replayId = new WorkItemId(Guid.Parse(dto!.Id));
        var storedReplay = await _factory.Store.GetAsync(replayId);
        Assert.Single(storedReplay!.DependsOn);
        Assert.Equal(dep.Id, storedReplay.DependsOn[0]);
    }

    private sealed record DependsOnResponse(string Id, IReadOnlyList<string> DependsOn);
}
