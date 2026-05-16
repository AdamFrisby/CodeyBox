using System.Net;
using System.Net.Http.Json;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CodeyBox.Tests;

/// <summary>
/// HTTP-level coverage for the documented operator escape hatch: retrying a
/// WaitingForQuotaReset item via POST /workitems/{id}/retry should bypass the
/// QuotaRetryScheduler and re-enqueue immediately. Guards a silent regression
/// where a refactor drops WaitingForQuotaReset from the allowlist and operators
/// who follow the documented procedure hit a 409.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class RetryWaitingForQuotaResetEndpointTests : IDisposable
{
    private readonly WorkItemApiFactory _factory = new();
    private readonly HttpClient _client;

    public RetryWaitingForQuotaResetEndpointTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Retry_WaitingForQuotaReset_TransitionsToQueuedAndEnqueues()
    {
        // The operator override path: an item parked in WaitingForQuotaReset
        // should accept POST /retry?from=work and end up back on the queue
        // for the worker pool. A regression that drops WaitingForQuotaReset
        // from the allowlist would return 409 instead.
        var item = WaitingItem();
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
    public async Task Retry_WaitingForQuotaReset_ClearsQuotaFields()
    {
        // The .With(WorkItemState.Queued) path inside WorkItemRetrier must
        // clear FailureKind / QuotaResetAt / NextQuotaRetryAt — otherwise the
        // QuotaRetryScheduler would observe a stale "still parked" record on
        // the next pickup and try to re-arm the timer for an already-Queued
        // item. The transition logic is unit-tested in WaitingForQuotaResetTests,
        // but this asserts the operator-override path actually hits it.
        var item = WaitingItem();
        await _factory.Store.CreateAsync(item);

        var resp = await _client.PostAsJsonAsync($"/workitems/{item.Id}/retry", new { from = "work" });
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        var readBack = await _factory.Store.GetAsync(item.Id);
        Assert.NotNull(readBack);
        Assert.Null(readBack!.FailureKind);
        Assert.Null(readBack.QuotaResetAt);
        Assert.Null(readBack.NextQuotaRetryAt);
    }

    [Fact]
    public async Task Retry_WaitingForQuotaReset_PreservesQuotaRetryAttempts()
    {
        // Operator-triggered retries (trigger="manual" inside RetryAsync) must
        // NOT increment QuotaRetryAttempts — that counter is reserved for the
        // auto-retry scheduler so it can stop after MaxAutoRetriesPerWorkItem.
        // A regression that always increments would let one operator click
        // exhaust the auto-retry budget the scheduler may later need.
        var item = WaitingItem() with { QuotaRetryAttempts = 1 };
        await _factory.Store.CreateAsync(item);

        var resp = await _client.PostAsJsonAsync($"/workitems/{item.Id}/retry", new { from = "work" });
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        var readBack = await _factory.Store.GetAsync(item.Id);
        Assert.Equal(1, readBack!.QuotaRetryAttempts);
    }

    [Fact]
    public async Task Retry_AcceptedResponseBody_ReportsQueuedResumeState()
    {
        // The Accepted response carries `{ id, from, state }` — the state
        // field must reflect the resume state (Queued for `from=work`).
        // Operators rely on this echo to confirm the override took effect
        // without an immediate GET.
        var item = WaitingItem();
        await _factory.Store.CreateAsync(item);

        var resp = await _client.PostAsJsonAsync($"/workitems/{item.Id}/retry", new { from = "work" });
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<RetryAcceptedBody>();
        Assert.NotNull(body);
        Assert.Equal(item.Id.ToString(), body!.Id);
        Assert.Equal("work", body.From);
        Assert.Equal(WorkItemState.Queued.ToString(), body.State);
    }

    private static WorkItem WaitingItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "parked",
        Prompt = "p",
        State = WorkItemState.WaitingForQuotaReset,
        FailureKind = "quota",
        QuotaResetAt = DateTimeOffset.UtcNow.AddHours(1),
        NextQuotaRetryAt = DateTimeOffset.UtcNow.AddMinutes(30),
    };

    private sealed record RetryAcceptedBody(string Id, string From, string State);
}
