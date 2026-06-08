using System.Threading.Channels;
using CodeyBox.Core;

namespace CodeyBox.Webhooks;

/// <summary>
/// In-process fan-out for <see cref="WebhookEvent"/>. Every event handed to
/// <see cref="HttpWebhookDispatcher"/> (or any other <see cref="IWebhookDispatcher"/>)
/// also passes through this broadcaster, which assigns a monotonic sequence ID,
/// stores it in two ring buffers (per work item + global) for Last-Event-ID
/// replay, and pushes it to every active subscriber that matches a filter.
///
/// <para>Used by the SSE endpoints to stream live pipeline events to clients
/// without polling. Webhook delivery (push-to-URL) and SSE (pull-over-HTTP)
/// share the same event surface.</para>
/// </summary>
public sealed class WebhookEventBroadcaster
{
    private readonly int _capacity;
    private long _nextId;
    private readonly Queue<BroadcastedEvent> _global;
    private readonly Dictionary<string, Queue<BroadcastedEvent>> _perWorkItem;
    private readonly LinkedList<Subscriber> _subscribers = new();
    private readonly object _lock = new();

    /// <summary>
    /// When true, every <see cref="Publish"/> runs <see cref="EventSchema.ValidateEnvelope"/>
    /// and throws on a missing required field. Off by default so production never
    /// observes the cost; tests set it to fail fast on schema drift. Internal +
    /// <c>InternalsVisibleTo("CodeyBox.Tests")</c> so a loaded plugin assembly
    /// cannot flip it on at runtime.
    /// </summary>
    internal static bool StrictSchemaValidationForTests { get; set; }

    /// <summary>Test-only hook: count of live subscribers.</summary>
    internal int SubscriberCount { get { lock (_lock) return _subscribers.Count; } }

    public WebhookEventBroadcaster(int ringBufferCapacity = 1000)
    {
        if (ringBufferCapacity < 1)
            throw new ArgumentOutOfRangeException(nameof(ringBufferCapacity), "must be >= 1");
        _capacity = ringBufferCapacity;
        _global = new Queue<BroadcastedEvent>(_capacity);
        _perWorkItem = new Dictionary<string, Queue<BroadcastedEvent>>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Records <paramref name="evt"/> and notifies every subscriber whose
    /// filter matches. Thread-safe and non-blocking — subscriber channels
    /// drop the oldest pending event on overflow so a slow consumer cannot
    /// stall publishing.
    /// </summary>
    public BroadcastedEvent Publish(WebhookEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (StrictSchemaValidationForTests)
        {
            var err = EventSchema.ValidateEnvelope(evt);
            if (err is not null)
                throw new InvalidOperationException("Webhook event failed schema validation: " + err);
        }
        var workItemKey = evt.WorkItem?.Id.ToString();

        BroadcastedEvent broadcasted;
        Subscriber[] toNotify;
        lock (_lock)
        {
            var id = ++_nextId;
            broadcasted = new BroadcastedEvent(id, evt);

            AppendCapped(_global, broadcasted, _capacity);
            if (workItemKey is not null)
            {
                if (!_perWorkItem.TryGetValue(workItemKey, out var perItem))
                {
                    perItem = new Queue<BroadcastedEvent>(_capacity);
                    _perWorkItem[workItemKey] = perItem;
                }
                AppendCapped(perItem, broadcasted, _capacity);
            }

            // Snapshot subscribers under the lock so concurrent Subscribe/Dispose
            // doesn't mutate the list while we're iterating it.
            toNotify = _subscribers.Count == 0
                ? []
                : _subscribers.Where(s => s.Filter.Matches(evt)).ToArray();
        }

        foreach (var sub in toNotify)
            sub.Channel.Writer.TryWrite(broadcasted);

        return broadcasted;
    }

    /// <summary>
    /// Registers a subscriber. If <paramref name="lastEventId"/> is set, the
    /// returned <see cref="Subscription"/> first replays any events with
    /// matching filter and id &gt; <paramref name="lastEventId"/> from the
    /// ring buffer, then yields live events going forward.
    /// </summary>
    public Subscription Subscribe(SubscriptionFilter filter, long? lastEventId)
    {
        ArgumentNullException.ThrowIfNull(filter);

        // Bounded with DropOldest: subscribers that fall behind lose stale
        // events rather than backpressuring publishers. Clients can recover
        // by reconnecting with Last-Event-ID (events stay in the ring buffer).
        var channel = Channel.CreateBounded<BroadcastedEvent>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        var subscriber = new Subscriber(filter, channel);

        IReadOnlyList<BroadcastedEvent> replay;
        LinkedListNode<Subscriber> node;
        long snapshotMaxId;
        lock (_lock)
        {
            snapshotMaxId = _nextId;

            // Choose the smallest applicable buffer. The per-workitem buffer
            // exists only for filters scoped to a single id; the global
            // buffer always works.
            var source = filter.WorkItemId is { } wid && _perWorkItem.TryGetValue(wid, out var perItem)
                ? (IEnumerable<BroadcastedEvent>)perItem
                : _global;

            replay = source
                .Where(b => (!lastEventId.HasValue || b.SequenceId > lastEventId.Value)
                    && b.SequenceId <= snapshotMaxId
                    && filter.Matches(b.Event))
                .ToList();

            node = _subscribers.AddLast(subscriber);
        }

        return new Subscription(this, node, channel, replay, snapshotMaxId);
    }

    internal void Unsubscribe(LinkedListNode<Subscriber> node, Channel<BroadcastedEvent> channel)
    {
        lock (_lock)
        {
            if (node.List is not null)
                _subscribers.Remove(node);
        }
        channel.Writer.TryComplete();
    }

    private static void AppendCapped(Queue<BroadcastedEvent> q, BroadcastedEvent evt, int capacity)
    {
        q.Enqueue(evt);
        while (q.Count > capacity)
            q.Dequeue();
    }

    internal sealed class Subscriber
    {
        public SubscriptionFilter Filter { get; }
        public Channel<BroadcastedEvent> Channel { get; }

        public Subscriber(SubscriptionFilter filter, Channel<BroadcastedEvent> channel)
        {
            Filter = filter;
            Channel = channel;
        }
    }
}

/// <summary>
/// A live SSE subscription. Yields buffered replay events (matching the
/// filter and <c>lastEventId</c> passed to <see cref="WebhookEventBroadcaster.Subscribe"/>)
/// first, then live events. Disposing the subscription removes it from the
/// broadcaster.
/// </summary>
public sealed class Subscription : IAsyncDisposable
{
    private readonly WebhookEventBroadcaster _broadcaster;
    private readonly LinkedListNode<WebhookEventBroadcaster.Subscriber> _node;
    private readonly Channel<BroadcastedEvent> _channel;
    private readonly IReadOnlyList<BroadcastedEvent> _replay;
    private readonly long _replayThroughId;
    private int _disposed;

    internal Subscription(
        WebhookEventBroadcaster broadcaster,
        LinkedListNode<WebhookEventBroadcaster.Subscriber> node,
        Channel<BroadcastedEvent> channel,
        IReadOnlyList<BroadcastedEvent> replay,
        long replayThroughId)
    {
        _broadcaster = broadcaster;
        _node = node;
        _channel = channel;
        _replay = replay;
        _replayThroughId = replayThroughId;
    }

    /// <summary>
    /// Yields the replay slice (already snapshotted at <see cref="WebhookEventBroadcaster.Subscribe"/>
    /// time), then live events from the channel. The same event is never
    /// yielded twice — channel events with id &lt;= the replay snapshot are
    /// suppressed because they were already returned in the replay slice.
    /// </summary>
    public async IAsyncEnumerable<BroadcastedEvent> ReadAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // Drain the in-memory replay slice without checking cancellation between
        // yields: it's a bounded snapshot the client explicitly asked for, and a
        // mid-replay cancellation leaves the caller with a partial slice plus a
        // confused Last-Event-ID. Cancellation kicks in below at the channel
        // wait. (Without this, a slow CI scheduler could race a short test
        // timeout against the foreach resumption between yields.)
        foreach (var evt in _replay)
        {
            yield return evt;
        }

        while (await _channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            while (_channel.Reader.TryRead(out var evt))
            {
                if (evt.SequenceId <= _replayThroughId)
                    continue;
                yield return evt;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _broadcaster.Unsubscribe(_node, _channel);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// What a subscriber wants. <see cref="WorkItemId"/> scopes to a single
/// work item (the per-work-item replay buffer is used); the project and
/// event-type filters narrow the global stream.
/// </summary>
public sealed record SubscriptionFilter
{
    public string? WorkItemId { get; init; }
    public string? ProjectId { get; init; }
    public IReadOnlyList<string>? EventTypes { get; init; }

    public bool Matches(WebhookEvent evt)
    {
        if (WorkItemId is not null)
        {
            if (evt.WorkItem is null || !string.Equals(evt.WorkItem.Id.ToString(), WorkItemId, StringComparison.Ordinal))
                return false;
        }
        if (ProjectId is not null)
        {
            if (evt.Project is null || !string.Equals(evt.Project.Id.Value, ProjectId, StringComparison.Ordinal))
                return false;
        }
        if (EventTypes is { Count: > 0 } types
            && !types.Contains(evt.Event, StringComparer.Ordinal))
            return false;
        return true;
    }
}

/// <summary>
/// A <see cref="WebhookEvent"/> tagged with the monotonic server-assigned
/// sequence id used as the SSE <c>id:</c> field and the Last-Event-ID
/// resume cursor.
/// </summary>
public sealed record BroadcastedEvent(long SequenceId, WebhookEvent Event);
