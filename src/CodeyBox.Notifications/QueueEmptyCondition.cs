using CodeyBox.Core;

namespace CodeyBox.Notifications;

/// <summary>
/// Evaluates true when the work-item queue has no active items (nothing in
/// <c>Working</c> state). Clears when an item transitions into Working.
/// </summary>
public sealed class QueueEmptyCondition : ICondition, IDisposable
{
    private readonly IWorkItemStore _store;

    public string Id => "queue_empty";

    public QueueEmptyCondition(IWorkItemStore store)
    {
        _store = store;
    }

    public async Task<bool> EvaluateAsync(CancellationToken ct)
    {
        var active = await _store.CountByStateAsync(WorkItemState.Working, ct);
        return active == 0;
    }

    public void Dispose() { }
}

/// <summary>
/// Notification builder for the queue_empty condition.
/// </summary>
public sealed class QueueEmptyNotificationBuilder : INotificationBuilder, IConditionAwareBuilder
{
    public string ConditionId => "queue_empty";

    public Notification Build(DateTimeOffset evaluatedAt) => new()
    {
        ConditionId = "queue_empty",
        Title = "Queue is empty",
        Summary = "No active work items are currently being processed.",
        Body = $"The work-item queue has no active items as of {evaluatedAt:R}. " +
               "The orchestrator is idle and waiting for new work to be enqueued.",
        Severity = NotificationSeverity.Information,
        Timestamp = evaluatedAt,
    };
}
