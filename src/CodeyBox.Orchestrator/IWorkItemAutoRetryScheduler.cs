using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Narrow notification port for durable work-item auto-retry policies.
/// </summary>
public interface IWorkItemAutoRetryScheduler
{
    Task NotifyQuotaFailureAsync(WorkItem item, CancellationToken ct = default);

    Task NotifyTransientFailureAsync(WorkItem item, CancellationToken ct = default);
}
