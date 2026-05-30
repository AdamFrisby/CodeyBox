using CodeyBox.Core;

namespace CodeyBox.Notifications;

/// <summary>
/// Evaluates true when the count of permanently failed work items
/// (<see cref="WorkItemState.Failed"/> or <see cref="WorkItemState.AbandonedAfterRecoveryAttempts"/>)
/// has increased since the last evaluation. Transient — fires once per
/// increment, not a persistent state.
/// </summary>
public sealed class WorkItemPermanentlyFailedCondition : ICondition, IDisposable
{
    private readonly IWorkItemStore _store;
    private int _lastKnownFailedCount = -1;

    public string Id => "work_item_permanently_failed";

    public WorkItemPermanentlyFailedCondition(IWorkItemStore store)
    {
        _store = store;
    }

    public async Task<bool> EvaluateAsync(CancellationToken ct)
    {
        var failedCount = await _store.CountByStateAsync(WorkItemState.Failed, ct);
        var abandonedCount = await _store.CountByStateAsync(WorkItemState.AbandonedAfterRecoveryAttempts, ct);
        var total = failedCount + abandonedCount;

        var last = Volatile.Read(ref _lastKnownFailedCount);
        if (last < 0)
        {
            Volatile.Write(ref _lastKnownFailedCount, total);
            return false;
        }

        if (total > last)
        {
            Volatile.Write(ref _lastKnownFailedCount, total);
            return true;
        }

        Volatile.Write(ref _lastKnownFailedCount, total);
        return false;
    }

    public void Dispose() { }
}

/// <summary>
/// Notification builder for the work_item_permanently_failed condition.
/// </summary>
public sealed class WorkItemPermanentlyFailedNotificationBuilder : INotificationBuilder, IConditionAwareBuilder
{
    public string ConditionId => "work_item_permanently_failed";

    public Notification Build(DateTimeOffset evaluatedAt) => new()
    {
        ConditionId = "work_item_permanently_failed",
        Title = "Work item permanently failed",
        Summary = "A work item has entered terminal failure after exhausting recovery attempts.",
        Body = $"At {evaluatedAt:R}, a work item entered the Failed state after exhausting " +
               "all configured recovery attempts. The item will not be retried automatically. " +
               "Review the work item details via the admin dashboard.",
        Severity = NotificationSeverity.Warning,
        Timestamp = evaluatedAt,
    };
}
