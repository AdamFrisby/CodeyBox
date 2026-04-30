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
        try { File.Delete(_dbPath); } catch { }
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
    public async Task StaleOrderingDetection_MissingId_SetNotEqual()
    {
        // This test validates the endpoint-level logic: if the caller's ID set
        // does not exactly match the current Queued set, the sets are not equal.
        var a = Queued("A");
        var b = Queued("B");
        await _store.CreateAsync(a);
        await _store.CreateAsync(b);

        var queued = await QueuedOrderAsync();
        var queuedSet = new HashSet<WorkItemId>(queued.Select(i => i.Id));

        // Caller omits b — stale view
        var requestedSet = new HashSet<WorkItemId> { a.Id };
        Assert.False(queuedSet.SetEquals(requestedSet));
    }

    [Fact]
    public async Task StaleOrderingDetection_ExtraId_SetNotEqual()
    {
        var a = Queued("A");
        await _store.CreateAsync(a);

        var queued = await QueuedOrderAsync();
        var queuedSet = new HashSet<WorkItemId>(queued.Select(i => i.Id));

        // Caller adds a phantom ID — stale view
        var phantom = WorkItemId.New();
        var requestedSet = new HashSet<WorkItemId> { a.Id, phantom };
        Assert.False(queuedSet.SetEquals(requestedSet));
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
