using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Locks in the GET <c>/workitems/{id}</c> contract for the new
/// <c>iterations[]</c> array. The acceptance criteria require the per-iteration
/// dispatch ledger to be surfaced on the read endpoint so trackers can render
/// "iteration 1 ran against revision 1; iteration 2 ran against revision 2".
/// </summary>
[Collection("GlobalSerilog")]
public sealed class WorkItemIterationsEndpointTests : IDisposable
{
    private readonly WorkItemApiFactory _factory = new();
    private readonly HttpClient _client;

    public WorkItemIterationsEndpointTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static WorkItem MakeItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "T",
        Prompt = "p",
        State = WorkItemState.Working,
        PromptRevision = 2,
    };

    [Fact]
    public async Task Get_WithNoIterations_OmitsOrNullsIterationsField()
    {
        // No dispatch rows recorded yet — the iterations[] field should be
        // absent or null, never an empty array (which would be a misleading
        // "we ran zero iterations" signal).
        var item = MakeItem();
        await _factory.Store.CreateAsync(item);

        var resp = await _client.GetAsync($"/workitems/{item.Id}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();

        if (json.TryGetProperty("iterations", out var iterations))
            Assert.Equal(JsonValueKind.Null, iterations.ValueKind);
    }

    [Fact]
    public async Task Get_WithRecordedIterations_ExposesArrayOfTuples()
    {
        var item = MakeItem();
        await _factory.Store.CreateAsync(item);

        var t1 = DateTimeOffset.UtcNow.AddMinutes(-10);
        var t2 = DateTimeOffset.UtcNow.AddMinutes(-5);
        await _factory.Store.RecordIterationDispatchAsync(item.Id, iteration: 1, promptRevisionAtDispatch: 1, dispatchedAt: t1);
        await _factory.Store.RecordIterationDispatchAsync(item.Id, iteration: 2, promptRevisionAtDispatch: 2, dispatchedAt: t2);

        var resp = await _client.GetAsync($"/workitems/{item.Id}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(json.TryGetProperty("iterations", out var iterations));
        Assert.Equal(JsonValueKind.Array, iterations.ValueKind);
        var rows = iterations.EnumerateArray().ToList();
        Assert.Equal(2, rows.Count);

        // Each row carries (iteration, promptRevision, dispatchedAt) — the
        // shape JobTrack iterates over to render the dispatch ledger.
        var first = rows.First(r => r.GetProperty("iteration").GetInt32() == 1);
        Assert.Equal(1, first.GetProperty("promptRevision").GetInt32());
        Assert.True(first.TryGetProperty("dispatchedAt", out var d1));
        Assert.True(DateTimeOffset.TryParse(d1.GetString(), out _));

        var second = rows.First(r => r.GetProperty("iteration").GetInt32() == 2);
        Assert.Equal(2, second.GetProperty("promptRevision").GetInt32());
    }

    [Fact]
    public async Task Get_ExposesCurrentPromptRevisionAtTopLevel()
    {
        // Independent of iterations[], the work item's current PromptRevision
        // must also be on the DTO so callers can compare "current" vs "what
        // the last iteration ran against."
        var item = MakeItem() with { PromptRevision = 5 };
        await _factory.Store.CreateAsync(item);

        var resp = await _client.GetAsync($"/workitems/{item.Id}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(5, json.GetProperty("promptRevision").GetInt32());
    }
}
