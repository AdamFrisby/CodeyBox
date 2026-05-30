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
}

public enum NotificationSeverity
{
    Information,
    Warning,
    Critical,
}
