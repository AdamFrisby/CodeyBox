using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Narrow notification port for durable work-item auto-retry policies.
/// </summary>
public interface IWorkItemAutoRetryScheduler
{
    Task NotifyQuotaFailureAsync(WorkItem item, CancellationToken ct = default);

    Task<WorkItemAutoRetryScheduleResult> NotifyTransientFailureAsync(WorkItem item, CancellationToken ct = default);
}

public interface IQuotaFailureAutoRetryScheduler
{
    Task NotifyQuotaFailureAsync(WorkItem item, CancellationToken ct = default);
}

public interface IQuotaRetryDispatchPromoter
{
    Task<QuotaRetryDispatchPromotionResult> TryPromoteForDispatchAsync(
        WorkItem item,
        CancellationToken ct = default);
}

public enum QuotaRetryDispatchDisposition
{
    Continue,
    RestartSelection,
    Blocked,
}

public readonly record struct QuotaRetryDispatchPromotionResult(
    bool Promoted,
    string Outcome,
    string? Reason = null,
    QuotaRetryDispatchDisposition Disposition = QuotaRetryDispatchDisposition.Continue);

public interface ITransientFailureAutoRetryScheduler
{
    Task<WorkItemAutoRetryScheduleResult> NotifyTransientFailureAsync(WorkItem item, CancellationToken ct = default);
}

public sealed class WorkItemAutoRetryScheduler : IWorkItemAutoRetryScheduler
{
    private readonly IQuotaFailureAutoRetryScheduler _quota;
    private readonly ITransientFailureAutoRetryScheduler? _transient;

    public WorkItemAutoRetryScheduler(
        IQuotaFailureAutoRetryScheduler quota,
        ITransientFailureAutoRetryScheduler? transient)
    {
        _quota = quota;
        _transient = transient;
    }

    public Task NotifyQuotaFailureAsync(WorkItem item, CancellationToken ct = default) =>
        _quota.NotifyQuotaFailureAsync(item, ct);

    public Task<WorkItemAutoRetryScheduleResult> NotifyTransientFailureAsync(WorkItem item, CancellationToken ct = default) =>
        _transient is null
            ? Task.FromResult(WorkItemAutoRetryScheduleResult.Skipped(item, "transient-scheduler-unavailable"))
            : _transient.NotifyTransientFailureAsync(item, ct);
}

public enum WorkItemAutoRetryScheduleStatus
{
    Scheduled,
    Exhausted,
    Skipped,
}

public sealed record WorkItemAutoRetryScheduleResult(
    WorkItemAutoRetryScheduleStatus Status,
    WorkItem UpdatedItem,
    DateTimeOffset? NextRetryAt = null,
    string? Reason = null)
{
    public static WorkItemAutoRetryScheduleResult Scheduled(
        WorkItem updatedItem,
        DateTimeOffset nextRetryAt) =>
        new(WorkItemAutoRetryScheduleStatus.Scheduled, updatedItem, nextRetryAt);

    public static WorkItemAutoRetryScheduleResult Exhausted(
        WorkItem updatedItem,
        string? reason = null) =>
        new(WorkItemAutoRetryScheduleStatus.Exhausted, updatedItem, updatedItem.NextTransientRetryAt, reason);

    public static WorkItemAutoRetryScheduleResult Skipped(
        WorkItem updatedItem,
        string? reason = null) =>
        new(WorkItemAutoRetryScheduleStatus.Skipped, updatedItem, updatedItem.NextTransientRetryAt, reason);
}
