namespace CodeyBox.Core;

/// <summary>
/// A human/systems-oriented notification driven by a condition evaluation.
/// Unlike <see cref="WebhookEvent"/> which POSTs structured machine events
/// to subscribers, <c>Notification</c> is designed to alert people when
/// something they should know about happens (queue idle, quotas exhausted, etc.).
/// </summary>
public sealed record Notification
{
    /// <summary>Stable identifier for the condition that produced this notification.</summary>
    public required string ConditionId { get; init; }

    /// <summary>One-line subject / title suitable for an email subject line.</summary>
    public required string Title { get; init; }

    /// <summary>Human-readable body text describing what happened and why.</summary>
    public string? Summary { get; init; }

    /// <summary>Full body payload with structured detail. Providers may render
    /// this differently (plain-text email body, Slack block kit, etc.).</summary>
    public string? Body { get; init; }

    /// <summary>Severity level for triage and display.</summary>
    public NotificationSeverity Severity { get; init; } = NotificationSeverity.Information;

    /// <summary>UTC timestamp when the condition was evaluated true.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Arbitrary structured fields the provider may render. Keys
    /// should be stable across evaluations of the same condition so email
    /// clients can thread correctly.</summary>
    public IReadOnlyDictionary<string, string>? Fields { get; init; }

    /// <summary>Recipient addresses or channel identifiers from the matching
    /// rule. When non-empty providers SHOULD deliver to these recipients;
    /// when null or empty the provider's default applies.</summary>
    public IReadOnlyList<string>? Recipients { get; init; }

    /// <summary>Actions offered alongside this notification (e.g. buttons in
    /// chat that answer an operator question). Additive: a provider that
    /// ignores actions renders the notification exactly as it would without
    /// them. Null or empty means "no actions offered".</summary>
    public IReadOnlyList<NotificationAction>? Actions { get; init; }

    /// <summary>URL where the decision behind <see cref="Actions"/> can be
    /// made when the delivering provider cannot carry interactions itself
    /// (notification-only providers surface this as a link so the question
    /// is still answerable elsewhere). Null when not applicable.</summary>
    public string? AnswerUrl { get; init; }

    /// <summary>Opaque token binding this notification to the thing being
    /// decided. Echoed back by inbound interactions so a stale message
    /// cannot answer a superseded question. Null when not applicable.</summary>
    public string? CorrelationToken { get; init; }
}

/// <summary>
/// One offered action on a <see cref="Notification"/>: a human-readable
/// label, the answer value submitted when the action is taken, and the
/// binding to the work item question being decided.
/// </summary>
public sealed record NotificationAction
{
    /// <summary>Human-readable label rendered on the button or link.</summary>
    public required string Label { get; init; }

    /// <summary>Answer value recorded when this action is taken.</summary>
    public required string Value { get; init; }

    /// <summary>Work item that owns the question being decided.</summary>
    public required string WorkItemId { get; init; }

    /// <summary>Agent-supplied stable question identifier (e.g. "q-001").</summary>
    public required string QuestionId { get; init; }
}

public enum NotificationSeverity
{
    Information,
    Warning,
    Critical,
}
