namespace CodeyBox.Core;

public interface IWebhookDispatcher
{
    Task PublishAsync(WebhookEvent evt, CancellationToken ct);
}
