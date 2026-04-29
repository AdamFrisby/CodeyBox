namespace CodeyBox.Core;

/// <summary>
/// Represents a single pipeline event to be dispatched to configured webhook endpoints.
/// Constructed by the pipeline and published via <see cref="IWebhookDispatcher"/>.
/// </summary>
public sealed record WebhookEvent
{
    public Guid DeliveryId { get; init; } = Guid.NewGuid();

    /// <summary>Dot-separated event name, e.g. "work_item.audit_passed".</summary>
    public required string Event { get; init; }

    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;

    public required WorkItem WorkItem { get; init; }

    public required Project Project { get; init; }

    /// <summary>Optional event-specific payload; serialised as-is into the "details" field.</summary>
    public object? Details { get; init; }
}
