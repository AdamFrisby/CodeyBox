namespace CodeyBox.Core;

/// <summary>
/// Platform-neutral binding of a <see cref="Notification"/> to its owning
/// work item, shared by notification provider plugins. Lives in Core (like
/// <see cref="NotificationCorrelation"/>, whose token it parses) so every
/// provider resolves the same work item — never a per-plugin fork.
/// </summary>
public static class NotificationBinding
{
    /// <summary>
    /// The work item this notification belongs to: the first offered
    /// action's <see cref="NotificationAction.WorkItemId"/> when present,
    /// else the id recovered from <see cref="Notification.CorrelationToken"/>.
    /// Null when neither binds.
    /// </summary>
    public static string? WorkItemIdFor(Notification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (notification.Actions is { Count: > 0 })
        {
            var first = notification.Actions[0];
            if (!string.IsNullOrWhiteSpace(first.WorkItemId))
                return first.WorkItemId;
        }
        if (!string.IsNullOrWhiteSpace(notification.CorrelationToken)
            && NotificationCorrelation.TryParse(notification.CorrelationToken, out var workItemId, out _))
            return workItemId;
        return null;
    }
}
