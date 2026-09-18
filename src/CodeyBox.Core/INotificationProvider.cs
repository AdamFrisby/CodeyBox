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

    /// <summary>Whether this provider can carry interactive actions
    /// (buttons that answer a question) and reflect the decision back into
    /// the channel. Notification-only providers return false and MUST
    /// surface <see cref="Notification.AnswerUrl"/> as a link so the
    /// question stays answerable elsewhere. Default false.</summary>
    bool SupportsInteractions => false;

    /// <summary>Reflect a landed decision back into the channel (e.g. update
    /// the original message with what was decided and by whom). Best-effort:
    /// implementations log and swallow delivery failures. The default is a
    /// no-op for providers whose platform offers no message-update path.</summary>
    Task UpdateDecisionAsync(Notification notification, string decisionSummary, CancellationToken ct) =>
        Task.CompletedTask;
}
