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
