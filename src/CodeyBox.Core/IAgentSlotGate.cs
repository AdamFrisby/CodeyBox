namespace CodeyBox.Core;

/// <summary>
/// Atomic per-agent concurrency slot reservation. The write-side counterpart
/// to <see cref="IAgentRunningCounters"/>: read-side returns "how many are
/// running on this agent"; this gate atomically test-and-increments against
/// the operator-configured cap so the router can spill past a saturated
/// member to a free-and-eligible one without a TOCTOU race against other
/// dispatchers.
///
/// <para>
/// Lives in Core (not Orchestrator) so the router — which composes the gate
/// into its candidate-walk — can depend on the abstraction without taking a
/// hard reference on the worker pool. The orchestrator owns the only
/// production implementation; tests substitute fakes.
/// </para>
/// </summary>
public interface IAgentSlotGate
{
    /// <summary>
    /// Atomically tries to reserve a slot for <paramref name="agent"/>.
    /// Returns true and increments the in-flight count when the agent has no
    /// configured cap or running &lt; cap; returns false when the cap is at
    /// ceiling so the caller can pick a different member.
    ///
    /// <para>
    /// Every successful reservation MUST be paired with a <see cref="Release"/>
    /// call on every exit path of the dispatch. The orchestrator's outer
    /// finally block is the canonical release site.
    /// </para>
    /// </summary>
    bool TryReserve(AgentKind agent);

    /// <summary>
    /// Releases a slot previously reserved via <see cref="TryReserve"/>.
    /// Calling Release without a prior successful TryReserve is undefined
    /// behaviour (and will under-count in-flight workers).
    /// </summary>
    void Release(AgentKind agent);
}
