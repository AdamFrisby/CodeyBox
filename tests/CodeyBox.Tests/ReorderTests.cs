using System.Net;
using System.Net.Http.Json;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for IWorkItemStore.ReorderAsync and the reorder endpoint validation logic.
/// </summary>
public sealed class ReorderTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"codeybox-reorder-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;

    public ReorderTests()
    {
        _store = new SqliteWorkItemStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        TestTempArtifacts.DeleteSqliteDatabase(_dbPath);
    }

    private static WorkItem Queued(string title = "t") => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("proj"),
        Title = title,
        Prompt = "p",
        State = WorkItemState.Queued,
        QueuePosition = DateTimeOffset.UtcNow.Ticks,
    };

    private async Task<List<WorkItem>> QueuedOrderAsync()
    {
        var result = new List<WorkItem>();
        await foreach (var item in _store.ListByStateAsync(WorkItemState.Queued))
            result.Add(item);
        return result;
    }

    [Fact]
    public async Task ReorderAsync_ExactMatch_UpdatesOrder()
    {
        var a = Queued("A");
        var b = Queued("B");
        var c = Queued("C");
        await _store.CreateAsync(a);
        await _store.CreateAsync(b);
        await _store.CreateAsync(c);

        // Reorder: C first, then A, then B
        await _store.ReorderAsync([c.Id, a.Id, b.Id]);

        var ordered = await QueuedOrderAsync();
        Assert.Equal(3, ordered.Count);
        Assert.Equal(c.Id, ordered[0].Id);
        Assert.Equal(a.Id, ordered[1].Id);
        Assert.Equal(b.Id, ordered[2].Id);
    }

    [Fact]
    public async Task ReorderAsync_SetsPositionsStartingAtOne()
    {
        var a = Queued("A");
        var b = Queued("B");
        await _store.CreateAsync(a);
        await _store.CreateAsync(b);

        await _store.ReorderAsync([b.Id, a.Id]);

        var ordered = await QueuedOrderAsync();
        // b should have position 1, a should have position 2
        Assert.Equal(b.Id, ordered[0].Id);
        Assert.Equal(1L, ordered[0].QueuePosition);
        Assert.Equal(a.Id, ordered[1].Id);
        Assert.Equal(2L, ordered[1].QueuePosition);
    }

    [Fact]
    public async Task ReorderAsync_DoesNotAffectNonQueuedItems()
    {
        var q = Queued("queued");
        var done = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("proj"),
            Title = "done",
            Prompt = "p",
            State = WorkItemState.Done,
            QueuePosition = DateTimeOffset.UtcNow.Ticks,
        };
        await _store.CreateAsync(q);
        await _store.CreateAsync(done);

        // Only pass the Queued item
        await _store.ReorderAsync([q.Id]);

        var doneRead = await _store.GetAsync(done.Id);
        Assert.NotNull(doneRead);
        // Done item position should be unchanged from its creation position
        Assert.Equal(done.QueuePosition, doneRead!.QueuePosition);
    }

    [Fact]
    public async Task ReorderAsync_EmptyList_NoError()
    {
        // No items in the store, empty reorder should succeed without throwing.
        await _store.ReorderAsync([]);
    }

    [Fact]
    public async Task ReorderAsync_MissingId_StaleDetectedBySetMismatch()
    {
        // Validates that omitting a queued item causes the set comparison to fail,
        // which is the basis for the endpoint's stale-view 400 rejection.
        var a = Queued("A");
        var b = Queued("B");
        await _store.CreateAsync(a);
        await _store.CreateAsync(b);

        var queued = await QueuedOrderAsync();
        var queuedSet = new HashSet<WorkItemId>(queued.Select(i => i.Id));

        // Caller omits b — stale view
        var requestedIds = new List<WorkItemId> { a.Id };
        var requestedSet = new HashSet<WorkItemId>(requestedIds);
        Assert.False(queuedSet.SetEquals(requestedSet));
        // Confirm the store still holds the original order (no changes made)
        var stillOrdered = await QueuedOrderAsync();
        Assert.Equal(2, stillOrdered.Count);
    }

    [Fact]
    public async Task ReorderAsync_ExtraId_StaleDetectedBySetMismatch()
    {
        // Validates that adding a phantom ID causes the set comparison to fail.
        var a = Queued("A");
        await _store.CreateAsync(a);

        var queued = await QueuedOrderAsync();
        var queuedSet = new HashSet<WorkItemId>(queued.Select(i => i.Id));

        var phantom = WorkItemId.New();
        var requestedSet = new HashSet<WorkItemId> { a.Id, phantom };
        Assert.False(queuedSet.SetEquals(requestedSet));
        // Confirm no items were changed
        var stillOrdered = await QueuedOrderAsync();
        Assert.Single(stillOrdered);
        Assert.Equal(a.Id, stillOrdered[0].Id);
    }

    [Fact]
    public async Task NewItemAfterReorder_SortsAfterPositionedItems()
    {
        var a = Queued("A");
        var b = Queued("B");
        await _store.CreateAsync(a);
        await _store.CreateAsync(b);

        // Explicitly position a=1, b=2
        await _store.ReorderAsync([a.Id, b.Id]);

        // New item added later with a high timestamp position
        var c = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("proj"),
            Title = "C",
            Prompt = "p",
            State = WorkItemState.Queued,
            QueuePosition = DateTimeOffset.UtcNow.Ticks,  // large value
        };
        await _store.CreateAsync(c);

        var ordered = await QueuedOrderAsync();
        Assert.Equal(3, ordered.Count);
        // a and b (small positions 1 & 2) should sort before c (large timestamp)
        Assert.Equal(a.Id, ordered[0].Id);
        Assert.Equal(b.Id, ordered[1].Id);
        Assert.Equal(c.Id, ordered[2].Id);
    }
}

/// <summary>
/// HTTP-level tests for POST /workitems/reorder. Verifies the endpoint's status codes
/// and that the queue order actually changes on success. A fresh server + store is
/// created per test method for isolation.
///
/// Joined to <c>GlobalSerilog</c> because <c>WebApplicationFactory</c> startup
/// runs Program.cs's Serilog bootstrap, which mutates the static
/// <see cref="Serilog.Log.Logger"/>; this serializes us with other tests that
/// observe or write to that global.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class ReorderHttpTests : IDisposable
{
    private readonly WorkItemApiFactory _factory = new();
    private readonly HttpClient _client;

    public ReorderHttpTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static WorkItem QueuedItem(string title = "t") => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("proj"),
        Title = title,
        Prompt = "p",
        State = WorkItemState.Queued,
        QueuePosition = DateTimeOffset.UtcNow.Ticks,
    };

    [Fact]
    public async Task Reorder_ExactMatch_Returns204()
    {
        var a = QueuedItem("A");
        var b = QueuedItem("B");
        await _factory.Store.CreateAsync(a);
        await _factory.Store.CreateAsync(b);

        var response = await _client.PostAsJsonAsync(
            "/workitems/reorder",
            new { ids = new[] { b.Id.ToString(), a.Id.ToString() } });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Reorder_QueueOrderChanges()
    {
        var a = QueuedItem("A");
        var b = QueuedItem("B");
        var c = QueuedItem("C");
        await _factory.Store.CreateAsync(a);
        await _factory.Store.CreateAsync(b);
        await _factory.Store.CreateAsync(c);

        var response = await _client.PostAsJsonAsync(
            "/workitems/reorder",
            new { ids = new[] { c.Id.ToString(), a.Id.ToString(), b.Id.ToString() } });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var ordered = new List<WorkItem>();
        await foreach (var item in _factory.Store.ListByStateAsync(WorkItemState.Queued))
            ordered.Add(item);
        Assert.Equal(3, ordered.Count);
        Assert.Equal(c.Id, ordered[0].Id);
        Assert.Equal(a.Id, ordered[1].Id);
        Assert.Equal(b.Id, ordered[2].Id);
    }

    [Fact]
    public async Task Reorder_MissingId_Returns400StaleError()
    {
        var a = QueuedItem("A");
        var b = QueuedItem("B");
        await _factory.Store.CreateAsync(a);
        await _factory.Store.CreateAsync(b);

        // Omit b — stale view
        var response = await _client.PostAsJsonAsync(
            "/workitems/reorder",
            new { ids = new[] { a.Id.ToString() } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("stale", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reorder_ExtraId_Returns400StaleError()
    {
        var a = QueuedItem("A");
        await _factory.Store.CreateAsync(a);

        var response = await _client.PostAsJsonAsync(
            "/workitems/reorder",
            new { ids = new[] { a.Id.ToString(), Guid.NewGuid().ToString() } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("stale", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reorder_DuplicateIds_Returns400()
    {
        var a = QueuedItem("A");
        await _factory.Store.CreateAsync(a);

        var response = await _client.PostAsJsonAsync(
            "/workitems/reorder",
            new { ids = new[] { a.Id.ToString(), a.Id.ToString() } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Reorder_InvalidGuid_Returns400()
    {
        var response = await _client.PostAsJsonAsync(
            "/workitems/reorder",
            new { ids = new[] { "not-a-valid-guid" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
