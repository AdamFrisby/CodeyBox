using System.Net;
using System.Net.Http.Json;
using CodeyBox.Core;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CodeyBox.Tests;

/// <summary>
/// HTTP-level coverage for the no-action-required terminal outcome: the
/// recorded determination is retrievable, the item is retryable when the
/// precondition later holds, and cancelling it is refused so the reasoning
/// is preserved. Guards a silent regression where a refactor drops
/// NoActionRequired from the retry allowlist or treats it as live work.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class NoActionRequiredEndpointTests : IDisposable
{
    private readonly WorkItemApiFactory _factory = new();
    private readonly HttpClient _client;

    public NoActionRequiredEndpointTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Retry_NoActionRequired_TransitionsToQueuedAndEnqueues()
    {
        var item = NoActionItem();
        await _factory.Store.CreateAsync(item);

        var queue = _factory.Services.GetRequiredService<ITaskQueue>();
        Assert.Equal(0, queue.Count);

        var resp = await _client.PostAsJsonAsync($"/workitems/{item.Id}/retry", new { from = "work" });
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        Assert.Equal(1, queue.Count);

        var readBack = await _factory.Store.GetAsync(item.Id);
        Assert.NotNull(readBack);
        Assert.Equal(WorkItemState.Queued, readBack!.State);
    }

    [Fact]
    public async Task Get_NoActionRequired_SurfacesStateAndReasoning()
    {
        var item = NoActionItem();
        await _factory.Store.CreateAsync(item);

        var resp = await _client.GetAsync($"/workitems/{item.Id}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var doc = System.Text.Json.JsonDocument.Parse(
            await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        var state = root.TryGetProperty("state", out var stateEl)
            ? stateEl.ValueKind == System.Text.Json.JsonValueKind.String
                ? stateEl.GetString()
                : stateEl.GetInt32().ToString()
            : root.GetProperty("State").GetString();
        Assert.Equal(WorkItemState.NoActionRequired.ToString(), state);
        var lastError = root.TryGetProperty("lastError", out var errEl)
            ? errEl.GetString()
            : root.GetProperty("LastError").GetString();
        Assert.Contains("No reset-TRIGGER endpoint exists", lastError);
    }

    [Fact]
    public async Task Delete_NoActionRequired_IsRefusedToPreserveDetermination()
    {
        var item = NoActionItem();
        await _factory.Store.CreateAsync(item);

        var resp = await _client.DeleteAsync($"/workitems/{item.Id}");
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);

        var readBack = await _factory.Store.GetAsync(item.Id);
        Assert.NotNull(readBack);
        Assert.Equal(WorkItemState.NoActionRequired, readBack!.State);
        Assert.Contains("No reset-TRIGGER endpoint exists", readBack.LastError);
    }

    private static WorkItem NoActionItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "gated quota reset advisor",
        Prompt = "do the gated step only when the trigger exists",
        State = WorkItemState.NoActionRequired,
        LastError = "no action required: No reset-TRIGGER endpoint exists anywhere in the API surface. (precondition checked: a POST reset-trigger endpoint)",
        StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
    };
}
