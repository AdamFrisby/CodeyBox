using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Lets dead-worker recovery release a worker-pool lease when it claims a
/// registry row for a worker that this process is still accounting as active,
/// but recovery decides the item will not be re-dispatched.
/// </summary>
public interface IWorkerPoolRecoverySlotReleaser
{
    bool TryReleaseRecoveredWorkerSlot(string workerId, WorkItemId? workItemId, string reason);
}
