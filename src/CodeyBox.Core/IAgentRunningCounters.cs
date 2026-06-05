namespace CodeyBox.Core;

/// <summary>
/// Live in-flight worker counts keyed by routed <see cref="AgentKind"/>.
/// Implemented by the orchestrator dispatch loop and consumed by the agent
/// class router to apply the rate-aware concurrent-burn gate.
///
/// <para>
/// Lives in Core (not Orchestrator) so non-orchestrator consumers — the
/// <c>/concurrency</c> endpoint, audit log surfaces, tests — can depend on
/// the abstraction without taking a hard reference on the worker pool.
/// </para>
/// </summary>
public interface IAgentRunningCounters
{
    /// <summary>
    /// Returns the number of workers currently running on
    /// <paramref name="agent"/>. Always returns 0 for an agent kind that has
    /// no recorded slot acquisitions in the current process.
    /// </summary>
    int GetRunning(AgentKind agent);

    /// <summary>
    /// Returns the number of workers currently running on a specific routed
    /// member instance. Implementations that have not opted into instance
    /// accounting fall back to the per-kind count.
    /// </summary>
    int GetRunning(AgentMembership member) => GetRunning(member.Agent);

    /// <summary>
    /// Snapshot of every agent kind that has currently-in-flight items.
    /// Empty when nothing is running.
    /// </summary>
    IReadOnlyDictionary<AgentKind, int> Snapshot();
}
