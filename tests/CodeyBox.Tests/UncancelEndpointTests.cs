using System.Net;
using System.Net.Http.Json;
using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.DependencyInjection;

namespace CodeyBox.Tests;

/// <summary>
/// HTTP-level tests for POST /workitems/{id}/uncancel.
/// Covers the three precondition branches (wrong state, operator-requested, allowed)
/// and verifies the item is reset to Queued and enqueued.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class UncancelEndpointTests : IDisposable
{
    private readonly WorkItemApiFactory _factory = new();
    private readonly HttpClient _client;

    public UncancelEndpointTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static WorkItem CancelledItem(WorkItemCancellationReason? reason, string? error = null) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "t",
        Prompt = "p",
        State = WorkItemState.Cancelled,
        LastError = error,
        CancellationReason = reason,
    };

    // ── 409 when item is not Cancelled ────────────────────────────────────────

    [Theory]
    [InlineData(WorkItemState.Queued)]
    [InlineData(WorkItemState.Working)]
    [InlineData(WorkItemState.Done)]
    public async Task Uncancel_NonCancelledState_Returns409(WorkItemState state)
    {
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "t",
            Prompt = "p",
            State = state,
        };
        await _factory.Store.CreateAsync(item);

        var resp = await _client.PostAsync($"/workitems/{item.Id}/uncancel", null);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);

        // State must not have changed
        var readBack = await _factory.Store.GetAsync(item.Id);
        Assert.Equal(state, readBack!.State);
    }

    // ── 409 when cancellation was operator-requested ───────────────────────────

    [Fact]
    public async Task Uncancel_OperatorRequested_Returns409()
    {
        var item = CancelledItem(WorkItemCancellationReason.OperatorRequested, "cancelled via API");
        await _factory.Store.CreateAsync(item);

        var resp = await _client.PostAsync($"/workitems/{item.Id}/uncancel", null);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);

        var readBack = await _factory.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Cancelled, readBack!.State);
    }

    // ── 200 when cancellation was parent-cascaded ─────────────────────────────

    [Fact]
    public async Task Uncancel_ParentCascaded_ResetsToQueued()
    {
        var item = CancelledItem(WorkItemCancellationReason.ParentCascaded, "parent dependency cancelled");
        await _factory.Store.CreateAsync(item);

        var resp = await _client.PostAsync($"/workitems/{item.Id}/uncancel", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var readBack = await _factory.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Queued, readBack!.State);
        Assert.Null(readBack.CancellationReason);
    }

    // ── 200 for legacy ambiguous items (reason = null, error = "cancelled") ───

    [Fact]
    public async Task Uncancel_LegacyAmbiguousItem_ResetsToQueued()
    {
        var item = CancelledItem(reason: null, error: "cancelled");
        await _factory.Store.CreateAsync(item);

        var resp = await _client.PostAsync($"/workitems/{item.Id}/uncancel", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var readBack = await _factory.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Queued, readBack!.State);
    }

    // ── RecoveryAttempts is reset on uncancel ─────────────────────────────────

    [Fact]
    public async Task Uncancel_ResetsRecoveryAttempts()
    {
        // Item accumulated two recovery attempts before being cascade-cancelled.
        // After uncancel the counter must be zeroed so the next host-shutdown
        // recovery does not immediately abandon it.
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "t",
            Prompt = "p",
            State = WorkItemState.Cancelled,
            CancellationReason = WorkItemCancellationReason.ParentCascaded,
            RecoveryAttempts = 2,
        };
        await _factory.Store.CreateAsync(item);

        var resp = await _client.PostAsync($"/workitems/{item.Id}/uncancel", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var readBack = await _factory.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Queued, readBack!.State);
        Assert.Equal(0, readBack.RecoveryAttempts);
    }

    [Fact]
    public async Task Uncancel_DeletesCachedAgentStreamSummaries()
    {
        var item = CancelledItem(WorkItemCancellationReason.ParentCascaded, "parent dependency cancelled");
        await _factory.Store.CreateAsync(item);
        var summaries = _factory.Services.GetRequiredService<IAgentStreamSummaryStore>();
        await summaries.UpsertAsync(new AgentStreamSummaryRow(
            item.Id,
            "work-1-abcdef.jsonl",
            "work",
            1,
            AgentKind.Claude,
            new AgentStreamSummary(
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(1),
                10,
                20,
                0,
                null,
                [],
                [],
                null),
            DateTimeOffset.UtcNow));

        var resp = await _client.PostAsync($"/workitems/{item.Id}/uncancel", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var rows = await summaries.GetByWorkItemAsync(item.Id);
        Assert.Empty(rows);
    }

    // ── 404 for unknown ID ────────────────────────────────────────────────────

    [Fact]
    public async Task Uncancel_UnknownId_Returns404()
    {
        var resp = await _client.PostAsync($"/workitems/{WorkItemId.New()}/uncancel", null);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
