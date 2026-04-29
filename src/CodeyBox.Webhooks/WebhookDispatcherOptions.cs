namespace CodeyBox.Webhooks;

/// <summary>
/// Top-level options for <see cref="HttpWebhookDispatcher"/>. Passed directly
/// from the CodeyBox:Webhooks config array via DI.
/// </summary>
public sealed record WebhookDispatcherOptions
{
    public IReadOnlyList<WebhookEndpointConfig> Endpoints { get; init; } = [];
}
