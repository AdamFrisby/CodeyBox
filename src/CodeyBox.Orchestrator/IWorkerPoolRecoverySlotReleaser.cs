using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Lets dead-worker recovery release a worker-pool lease when it claims a
/// registry row for a worker that this process is still accounting as active.
/// </summary>
public interface IWorkerPoolRecoverySlotReleaser
{
    ValueTask<bool> TryReleaseRecoveredWorkerSlotAsync(
        string workerId,
        WorkItemId? workItemId,
        string reason,
        bool wakeDispatcher,
        CancellationToken ct = default);
}
