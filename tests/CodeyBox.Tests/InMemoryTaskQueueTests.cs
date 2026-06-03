using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class InMemoryTaskQueueTests
{
    [Fact]
    public async Task RoundTripsItems()
    {
        var q = new InMemoryTaskQueue();
        var id = WorkItemId.New();
        await q.EnqueueAsync(id);
        var got = await q.DequeueAsync();
        Assert.Equal(id, got);
    }

    [Fact]
    public async Task RoundTripsGenericDispatchWake()
    {
        var q = new InMemoryTaskQueue();
        await q.EnqueueDispatchWakeAsync();

        var got = await q.DequeueDispatchAsync();

        Assert.NotNull(got);
        Assert.Equal(TaskQueueDispatchKind.GenericWake, got.Value.Kind);
        Assert.Null(got.Value.WorkItemId);
    }

    [Fact]
    public async Task Dequeue_ReturnsNullForGenericDispatchWake()
    {
        var q = new InMemoryTaskQueue();
        await q.EnqueueDispatchWakeAsync();

        var got = await q.DequeueAsync();

        Assert.Null(got);
    }

    [Fact]
    public async Task Complete_ReturnsNullFromDequeue()
    {
        var q = new InMemoryTaskQueue();
        q.Complete();
        var got = await q.DequeueAsync();
        Assert.Null(got);
    }

    [Fact]
    public async Task Dequeue_HonoursCancellation()
    {
        var q = new InMemoryTaskQueue();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await q.DequeueAsync(cts.Token));
    }
}
