namespace CodeyBox.Core;

/// <summary>
/// Represents a single pipeline event to be dispatched to configured webhook endpoints.
/// Constructed by the pipeline and published via <see cref="IWebhookDispatcher"/>.
///
/// <para><see cref="WorkItem"/> and <see cref="Project"/> are null for agent-level
/// events (e.g. <c>agent.smoke_failed</c>) that have no associated work item.</para>
/// </summary>
public sealed record WebhookEvent
{
    public Guid DeliveryId { get; init; } = Guid.NewGuid();

    /// <summary>Dot-separated event name, e.g. "work_item.audit_passed".</summary>
    public required string Event { get; init; }

    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Null for agent-level events that have no associated work item.</summary>
    public WorkItem? WorkItem { get; init; }

    /// <summary>Null for agent-level events that have no associated project.</summary>
    public Project? Project { get; init; }

    /// <summary>Optional event-specific payload; serialised as-is into the "details" field.</summary>
    public object? Details { get; init; }

    /// <summary>
    /// Set for release-scoped events (event names starting with "release.").
    /// Null for work-item and agent events.
    /// </summary>
    public Release? Release { get; init; }
}
