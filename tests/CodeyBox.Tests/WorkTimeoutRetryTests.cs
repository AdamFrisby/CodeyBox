using System.Net;
using System.Net.Http.Json;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// HTTP-level coverage for raising the work-phase budget after a timeout
/// failure: POST /workitems/{id}/retry accepts <c>workTimeoutMinutes</c> and
/// stamps it in the same atomic conditional write as the Queued transition,
/// so the retry/pickup race that made PATCH unusable on Failed items cannot
/// occur — by the time the retry responds, the new budget is persisted.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class WorkTimeoutRetryTests : IDisposable
{
    private readonly WorkItemApiFactory _factory = new();
    private readonly HttpClient _client;

    public WorkTimeoutRetryTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Retry_FailedTimeoutItem_WithWorkTimeoutMinutes_PersistsBudgetAtomically()
    {
        var item = FailedTimeoutItem() with { WorkTimeout = TimeSpan.FromMilliseconds(250) };
        await _factory.Store.CreateAsync(item);

        var response = await _client.PostAsJsonAsync(
            $"/workitems/{item.Id}/retry",
            new { from = "work", workTimeoutMinutes = 480 });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // Synchronous read-back immediately after the 202: the budget was
        // applied by the retry write itself, not by a follow-up PATCH that a
        // dispatcher pickup could beat. A test that PATCHed after retrying
        // could pass spuriously when the PATCH wins the race; this one cannot.
        var readBack = await _factory.Store.GetAsync(item.Id);
        Assert.NotNull(readBack);
        Assert.Equal(WorkItemState.Queued, readBack!.State);
        Assert.Equal(TimeSpan.FromMinutes(480), readBack.WorkTimeout);
    }

    [Fact]
    public async Task Retry_WithWorkTimeoutMinutes_ClampsAboveMax()
    {
        var item = FailedTimeoutItem();
        await _factory.Store.CreateAsync(item);

        var response = await _client.PostAsJsonAsync(
            $"/workitems/{item.Id}/retry",
            new { from = "work", workTimeoutMinutes = 9999 });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var readBack = await _factory.Store.GetAsync(item.Id);
        Assert.NotNull(readBack);
        Assert.Equal(TimeSpan.FromMinutes(480), readBack!.WorkTimeout);
    }

    [Fact]
    public async Task Retry_WithWorkTimeoutMinutes_ClampsBelowMin()
    {
        var item = FailedTimeoutItem();
        await _factory.Store.CreateAsync(item);

        var response = await _client.PostAsJsonAsync(
            $"/workitems/{item.Id}/retry",
            new { from = "work", workTimeoutMinutes = 0 });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var readBack = await _factory.Store.GetAsync(item.Id);
        Assert.NotNull(readBack);
        Assert.Equal(TimeSpan.FromMinutes(1), readBack!.WorkTimeout);
    }

    [Fact]
    public async Task Retry_WithoutWorkTimeoutMinutes_PreservesExistingBudget()
    {
        var item = FailedTimeoutItem() with { WorkTimeout = TimeSpan.FromMinutes(60) };
        await _factory.Store.CreateAsync(item);

        var response = await _client.PostAsJsonAsync(
            $"/workitems/{item.Id}/retry",
            new { from = "work" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var readBack = await _factory.Store.GetAsync(item.Id);
        Assert.NotNull(readBack);
        Assert.Equal(TimeSpan.FromMinutes(60), readBack!.WorkTimeout);
    }

    [Fact]
    public async Task Retry_WithoutWorkTimeoutMinutes_PreservesInherit()
    {
        // An item that never set a timeout keeps inheriting (NULL round-trips
        // through the retry write rather than being pinned to a default).
        var item = FailedTimeoutItem();
        Assert.Null(item.WorkTimeout);
        await _factory.Store.CreateAsync(item);

        var response = await _client.PostAsJsonAsync(
            $"/workitems/{item.Id}/retry",
            new { from = "work" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var readBack = await _factory.Store.GetAsync(item.Id);
        Assert.NotNull(readBack);
        Assert.Null(readBack!.WorkTimeout);
    }

    [Fact]
    public async Task Patch_FailedItemTimeout_StillReturns409()
    {
        // PATCH stays Queued-only by design: the retry request above is the
        // race-free path for raising a Failed item's budget. Pin the 409 so a
        // future change cannot silently reopen the PATCH-then-pickup race.
        var item = FailedTimeoutItem();
        await _factory.Store.CreateAsync(item);

        var response = await _client.PatchAsJsonAsync(
            $"/workitems/{item.Id}",
            new { workTimeoutMinutes = 480 });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var readBack = await _factory.Store.GetAsync(item.Id);
        Assert.NotNull(readBack);
        Assert.Null(readBack!.WorkTimeout);
    }

    private static WorkItem FailedTimeoutItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "timed out",
        Prompt = "p",
        Agent = AgentKind.Claude,
        State = WorkItemState.Failed,
        FailureKind = "timeout",
        CancellationSource = "timeout:work",
        LastError = "phase 'work' exceeded configured timeout (timeout:work)",
    };
}
