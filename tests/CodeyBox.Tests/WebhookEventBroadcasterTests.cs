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

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Constructor_RejectsRingBufferCapacityLessThanOne(int capacity)
    {
        // Guards against accidentally flipping the comparison in the
        // broadcaster's ctor (or in the DI factory that forwards to it);
        // Queue<>(0) would silently accept the bad value and break replay
        // under load instead of failing fast at startup.
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => new WebhookEventBroadcaster(capacity));
        Assert.Equal("ringBufferCapacity", ex.ParamName);
    }

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
        // Publish 3 events before any subscriber exists. Distinct real event
        // names (must be in EventSchema.KnownEventTypes so strict-mode
        // validation accepts them); only the ordering matters here.
        bc.Publish(Evt("work_item.working")); // id 1
        bc.Publish(Evt("work_item.work_complete")); // id 2
        bc.Publish(Evt("work_item.done")); // id 3

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
        Assert.Equal("work_item.done", single.Event.Event);
    }

    [Fact]
    public async Task LastEventId_ZeroReplaysEverythingInBuffer()
    {
        var bc = new WebhookEventBroadcaster();
        bc.Publish(Evt("work_item.working"));
        bc.Publish(Evt("work_item.done"));

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
        bc.Publish(Evt("work_item.working"));
        bc.Publish(Evt("work_item.work_complete"));
        bc.Publish(Evt("work_item.auditing"));
        bc.Publish(Evt("work_item.audit_iteration"));
        bc.Publish(Evt("work_item.done"));

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
        bc.Publish(Evt("work_item.working"));
        bc.Publish(Evt("work_item.work_complete"));

        await using var sub = bc.Subscribe(new SubscriptionFilter(), lastEventId: 0);

        // Publish more events after subscribing — must not duplicate the replay slice.
        bc.Publish(Evt("work_item.auditing"));
        bc.Publish(Evt("work_item.done"));

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

        bc.Publish(Evt("work_item.working", id: otherId));
        bc.Publish(Evt("work_item.done", id: wantedId));

        var got = new List<WorkItemId?>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        try
        {
            await foreach (var e in sub.ReadAsync(cts.Token))
                got.Add(e.Event.WorkItem?.Id);
        }
        catch (OperationCanceledException) { }

        var single = Assert.Single(got);
        Assert.Equal(wantedId, single);
    }

    [Fact]
    public async Task ProjectFilter_SkipsEventsForOtherProjects()
    {
        var bc = new WebhookEventBroadcaster();
        await using var sub = bc.Subscribe(
            new SubscriptionFilter { ProjectId = "proj-a" },
            lastEventId: null);

        bc.Publish(Evt("work_item.working", projectId: "proj-b"));
        bc.Publish(Evt("work_item.done", projectId: "proj-a"));

        var got = new List<string?>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        try
        {
            await foreach (var e in sub.ReadAsync(cts.Token))
                got.Add(e.Event.Project?.Id.Value);
        }
        catch (OperationCanceledException) { }

        var single = Assert.Single(got);
        Assert.Equal("proj-a", single);
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
        bc.Publish(Evt("work_item.working"));
    }
}
