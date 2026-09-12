using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Composes a single, bounded, testable convergence brief from a work item's persisted history.
/// Performs no dispatch, creates no sandbox, and changes no state.
/// </summary>
public interface IConvergenceBriefComposer
{
    /// <summary>
    /// Loads the persisted history for <paramref name="workItemId"/> across all stores
    /// and composes a bounded, redacted convergence brief for a fresh agent.
    /// </summary>
    Task<string> ComposeAsync(WorkItemId workItemId, CancellationToken ct = default);
}
