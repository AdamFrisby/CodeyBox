using CodeyBox.Core;
using CodeyBox.Webhooks;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <see cref="WebhookEventBroadcaster"/>. Exercises the
/// fan-out, replay-from-ring-buffer, filter, and subscriber-cleanup paths
/// that back the SSE endpoints.
/// </summary>
public sealed class WebhookEventBroadcasterTests
{
    private static WebhookEvent Evt(string name = "work_item.working", WorkItemId? id = null, string projectId = "proj") =>
        new()
        {
            Event = name,
            WorkItem = new WorkItem
            {
                Id = id ?? WorkItemId.New(),
                ProjectId = new ProjectId(projectId),
                Title = "t",
                Prompt = "p",
            },
            Project = new Project
            {
                Id = new ProjectId(projectId),
                DisplayName = "Test",
                RepositoryUrl = "https://example.com/repo.git",
            },
        };

    [Fact]
    public async Task Subscribe_ReceivesLivePublishedEvents()
    {
        var bc = new WebhookEventBroadcaster();
        await using var sub = bc.Subscribe(new SubscriptionFilter(), lastEventId: null);

        var produced = new List<BroadcastedEvent>();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reader = Task.Run(async () =>
        {
            await foreach (var e in sub.ReadAsync(cts.Token))
            {
                produced.Add(e);
                if (produced.Count == 2) break;
            }
        });

        bc.Publish(Evt("work_item.working"));
        bc.Publish(Evt("work_item.done"));
        await reader;

        Assert.Equal(2, produced.Count);
        Assert.Equal("work_item.working", produced[0].Event.Event);
        Assert.Equal("work_item.done", produced[1].Event.Event);
        Assert.Equal(1, produced[0].SequenceId);
        Assert.Equal(2, produced[1].SequenceId);
    }

    [Fact]
    public async Task LastEventId_ReplaysOnlyNewerBufferedEvents()
    {
        var bc = new WebhookEventBroadcaster();
        // Publish 3 events before any subscriber exists.
        bc.Publish(Evt("a")); // id 1
        bc.Publish(Evt("b")); // id 2
        bc.Publish(Evt("c")); // id 3

        // Reconnecting client claims it last received id 2 — should get only id 3.
        await using var sub = bc.Subscribe(new SubscriptionFilter(), lastEventId: 2);

        var got = new List<BroadcastedEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        try
        {
            await foreach (var e in sub.ReadAsync(cts.Token))
                got.Add(e);
        }
        catch (OperationCanceledException) { /* stop after replay drains */ }

        var single = Assert.Single(got);
        Assert.Equal(3, single.SequenceId);
        Assert.Equal("c", single.Event.Event);
    }

    [Fact]
    public async Task LastEventId_ZeroReplaysEverythingInBuffer()
    {
        var bc = new WebhookEventBroadcaster();
        bc.Publish(Evt("a"));
        bc.Publish(Evt("b"));

        await using var sub = bc.Subscribe(new SubscriptionFilter(), lastEventId: 0);

        var got = new List<long>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        try
        {
            await foreach (var e in sub.ReadAsync(cts.Token))
                got.Add(e.SequenceId);
        }
        catch (OperationCanceledException) { }

        Assert.Equal([1, 2], got);
    }

    [Fact]
    public async Task RingBuffer_EvictsOldestAtCapacity()
    {
        var bc = new WebhookEventBroadcaster(ringBufferCapacity: 3);
        bc.Publish(Evt("e1"));
        bc.Publish(Evt("e2"));
        bc.Publish(Evt("e3"));
        bc.Publish(Evt("e4"));
        bc.Publish(Evt("e5"));

        await using var sub = bc.Subscribe(new SubscriptionFilter(), lastEventId: 0);

        var got = new List<long>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        try
        {
            await foreach (var e in sub.ReadAsync(cts.Token))
                got.Add(e.SequenceId);
        }
        catch (OperationCanceledException) { }

        // Oldest two (ids 1,2) should be evicted; we get 3,4,5.
        Assert.Equal([3, 4, 5], got);
    }

    [Fact]
    public async Task Subscribe_DoesNotDuplicateBufferAndLiveEvents()
    {
        var bc = new WebhookEventBroadcaster();
        bc.Publish(Evt("a"));
        bc.Publish(Evt("b"));

        await using var sub = bc.Subscribe(new SubscriptionFilter(), lastEventId: 0);

        // Publish more events after subscribing — must not duplicate the replay slice.
        bc.Publish(Evt("c"));
        bc.Publish(Evt("d"));

        var got = new List<long>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        try
        {
            await foreach (var e in sub.ReadAsync(cts.Token))
            {
                got.Add(e.SequenceId);
                if (got.Count == 4) break;
            }
        }
        catch (OperationCanceledException) { }

        Assert.Equal([1, 2, 3, 4], got);
        Assert.Equal(got.Count, got.Distinct().Count());
    }

    [Fact]
    public async Task WorkItemFilter_SkipsEventsForOtherItems()
    {
        var bc = new WebhookEventBroadcaster();
        var wantedId = WorkItemId.New();
        var otherId = WorkItemId.New();

        await using var sub = bc.Subscribe(
            new SubscriptionFilter { WorkItemId = wantedId.ToString() },
            lastEventId: null);

        bc.Publish(Evt("other", id: otherId));
        bc.Publish(Evt("wanted", id: wantedId));

        var got = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        try
        {
            await foreach (var e in sub.ReadAsync(cts.Token))
                got.Add(e.Event.Event);
        }
        catch (OperationCanceledException) { }

        var single = Assert.Single(got);
        Assert.Equal("wanted", single);
    }

    [Fact]
    public async Task ProjectFilter_SkipsEventsForOtherProjects()
    {
        var bc = new WebhookEventBroadcaster();
        await using var sub = bc.Subscribe(
            new SubscriptionFilter { ProjectId = "proj-a" },
            lastEventId: null);

        bc.Publish(Evt("a", projectId: "proj-b"));
        bc.Publish(Evt("b", projectId: "proj-a"));

        var got = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        try
        {
            await foreach (var e in sub.ReadAsync(cts.Token))
                got.Add(e.Event.Event);
        }
        catch (OperationCanceledException) { }

        var single = Assert.Single(got);
        Assert.Equal("b", single);
    }

    [Fact]
    public async Task EventTypeFilter_KeepsOnlyAllowedNames()
    {
        var bc = new WebhookEventBroadcaster();
        await using var sub = bc.Subscribe(
            new SubscriptionFilter { EventTypes = ["work_item.done"] },
            lastEventId: null);

        bc.Publish(Evt("work_item.working"));
        bc.Publish(Evt("work_item.done"));

        var got = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        try
        {
            await foreach (var e in sub.ReadAsync(cts.Token))
                got.Add(e.Event.Event);
        }
        catch (OperationCanceledException) { }

        Assert.Equal(["work_item.done"], got);
    }

    [Fact]
    public async Task DisposeSubscription_RemovesSubscriberFromBroadcaster()
    {
        var bc = new WebhookEventBroadcaster();
        var sub = bc.Subscribe(new SubscriptionFilter(), lastEventId: null);
        Assert.Equal(1, bc.SubscriberCount);

        await sub.DisposeAsync();
        Assert.Equal(0, bc.SubscriberCount);

        // Subsequent publishes must not throw and must not crash on the
        // disposed subscriber. (No assertion needed — absence of throw is the test.)
        bc.Publish(Evt("a"));
    }
}
