namespace CodeyBox.Core;

/// <summary>
/// Delivers a <see cref="Notification"/> to a target (email inbox, Slack
/// channel, etc.). Implementations must be thread-safe and should not throw
/// on transient delivery failures — log and swallow.
/// </summary>
public interface INotificationProvider
{
    /// <summary>Human-readable provider name, e.g. "email", "slack".</summary>
    string Name { get; }

    /// <summary>Send one notification. Called by the dispatcher after rules
    /// evaluation. Implementations must be safe for concurrent calls.</summary>
    Task SendAsync(Notification notification, CancellationToken ct);
}
