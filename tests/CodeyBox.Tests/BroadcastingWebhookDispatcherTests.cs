using CodeyBox.Core;
using CodeyBox.Webhooks;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <see cref="BroadcastingWebhookDispatcher"/>, the decorator
/// that both publishes events to the in-process broadcaster (for SSE) and
/// delegates to the inner outbound-HTTP dispatcher.
/// </summary>
public sealed class BroadcastingWebhookDispatcherTests
{
    private static WebhookEvent SampleEvent(string name = "work_item.working") => new()
    {
        Event = name,
        WorkItem = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("proj"),
            Title = "t",
            Prompt = "p",
        },
        Project = new Project
        {
            Id = new ProjectId("proj"),
            DisplayName = "Test",
            RepositoryUrl = "https://example.com/repo.git",
        },
    };

    [Fact]
    public async Task PublishAsync_BroadcastsAndDelegatesToInner()
    {
        var bc = new WebhookEventBroadcaster();
        var inner = new RecordingDispatcher();
        var sut = new BroadcastingWebhookDispatcher(bc, inner);

        // Subscribe BEFORE publishing so the broadcaster fans out live.
        await using var sub = bc.Subscribe(new SubscriptionFilter(), lastEventId: null);

        var evt = SampleEvent();
        await sut.PublishAsync(evt, CancellationToken.None);

        // Inner dispatcher received the same instance.
        Assert.Same(evt, Assert.Single(inner.Received));

        // Broadcaster fanned out to the subscriber.
        var received = new List<BroadcastedEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        try
        {
            await foreach (var e in sub.ReadAsync(cts.Token))
            {
                received.Add(e);
                break;
            }
        }
        catch (OperationCanceledException) { }

        var single = Assert.Single(received);
        Assert.Same(evt, single.Event);
    }

    [Fact]
    public async Task PublishAsync_BroadcastsEvenIfInnerThrows()
    {
        // Documents the order: broadcast first, then delegate. A broken
        // outbound webhook must not prevent in-process subscribers from
        // observing the event.
        var bc = new WebhookEventBroadcaster();
        var inner = new ThrowingDispatcher();
        var sut = new BroadcastingWebhookDispatcher(bc, inner);

        await using var sub = bc.Subscribe(new SubscriptionFilter(), lastEventId: null);
        var evt = SampleEvent();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.PublishAsync(evt, CancellationToken.None));

        // Subscriber still saw the event.
        var got = new List<BroadcastedEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        try
        {
            await foreach (var e in sub.ReadAsync(cts.Token))
            {
                got.Add(e);
                break;
            }
        }
        catch (OperationCanceledException) { }

        Assert.Same(evt, Assert.Single(got).Event);
    }

    [Fact]
    public async Task DisposeAsync_ForwardsToInnerWhenInnerIsAsyncDisposable()
    {
        var bc = new WebhookEventBroadcaster();
        var inner = new DisposableDispatcher();
        var sut = new BroadcastingWebhookDispatcher(bc, inner);

        await sut.DisposeAsync();

        Assert.True(inner.Disposed);
    }

    [Fact]
    public async Task DisposeAsync_IsNoOpWhenInnerIsNotAsyncDisposable()
    {
        var bc = new WebhookEventBroadcaster();
        var inner = new RecordingDispatcher(); // does not implement IAsyncDisposable
        var sut = new BroadcastingWebhookDispatcher(bc, inner);

        // Should not throw.
        await sut.DisposeAsync();
    }

    private sealed class RecordingDispatcher : IWebhookDispatcher
    {
        public List<WebhookEvent> Received { get; } = [];

        public Task PublishAsync(WebhookEvent evt, CancellationToken ct)
        {
            Received.Add(evt);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingDispatcher : IWebhookDispatcher
    {
        public Task PublishAsync(WebhookEvent evt, CancellationToken ct)
            => throw new InvalidOperationException("inner failure");
    }

    private sealed class DisposableDispatcher : IWebhookDispatcher, IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public Task PublishAsync(WebhookEvent evt, CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
