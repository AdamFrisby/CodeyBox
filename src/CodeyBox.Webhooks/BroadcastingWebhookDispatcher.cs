using CodeyBox.Core;

namespace CodeyBox.Webhooks;

/// <summary>
/// Decorator that publishes every <see cref="WebhookEvent"/> to a
/// <see cref="WebhookEventBroadcaster"/> first (for SSE subscribers and
/// future in-process consumers), then delegates to the underlying
/// <see cref="IWebhookDispatcher"/> for outbound HTTP delivery.
///
/// <para>Broadcast is synchronous and cheap (lock + ring-buffer append +
/// non-blocking channel writes); it never blocks the outbound webhook
/// path.</para>
/// </summary>
public sealed class BroadcastingWebhookDispatcher : IWebhookDispatcher, IAsyncDisposable
{
    private readonly WebhookEventBroadcaster _broadcaster;
    private readonly IWebhookDispatcher _inner;

    public BroadcastingWebhookDispatcher(WebhookEventBroadcaster broadcaster, IWebhookDispatcher inner)
    {
        _broadcaster = broadcaster;
        _inner = inner;
    }

    public Task PublishAsync(WebhookEvent evt, CancellationToken ct)
    {
        _broadcaster.Publish(evt);
        return _inner.PublishAsync(evt, ct);
    }

    public ValueTask DisposeAsync()
    {
        return _inner is IAsyncDisposable d ? d.DisposeAsync() : ValueTask.CompletedTask;
    }
}
