using CodeyBox.Core;

namespace CodeyBox.Webhooks;

/// <summary>
/// No-op dispatcher used when no webhook endpoints are configured.
/// </summary>
public sealed class NullWebhookDispatcher : IWebhookDispatcher
{
    public Task PublishAsync(WebhookEvent evt, CancellationToken ct) => Task.CompletedTask;
}
