namespace CodeyBox.Core;

/// <summary>
/// Focused quota-availability gate for consumers that need to know whether a
/// probed subscription member is currently routable without depending on the
/// full router implementation.
/// </summary>
public interface IAgentQuotaGate
{
    /// <summary>
    /// Synchronous gate check. The caller is responsible for supplying any
    /// recent-observed-failure context — used by the /quota status endpoint
    /// which already has per-(agent, model) failure context in hand.
    /// </summary>
    bool Allows(
        AgentMembership member,
        AgentQuotaSnapshot snapshot,
        DateTimeOffset nowUtc,
        bool recentObservedFailure = false,
        string? observedFailureReason = null);

    /// <summary>
    /// Gate check that resolves recent-observed-failure context internally —
    /// for callers (notifications, watchdogs) that should see the same decision
    /// the dispatch router applies but lack the failure-store dependency.
    /// Implementations consult the failure store when wired; otherwise behave
    /// as <see cref="Allows"/> with no observed failure.
    /// </summary>
    Task<bool> AllowsAsync(
        AgentMembership member,
        AgentQuotaSnapshot snapshot,
        DateTimeOffset nowUtc,
        CancellationToken ct = default);

    /// <summary>
    /// Refusal reason for the same decision <see cref="Allows"/> reports, or
    /// null when the gate allows. Lets status surfaces (e.g. <c>/quota</c>
    /// <c>dispatchReason</c>) show WHY a member is refused — including which
    /// quota window binds — without re-implementing the gate. The default
    /// implementation returns null so existing probes keep compiling; the
    /// router's gate override reports the real reason.
    /// </summary>
    string? GetRefusalReason(
        AgentMembership member,
        AgentQuotaSnapshot snapshot,
        DateTimeOffset nowUtc,
        bool recentObservedFailure = false,
        string? observedFailureReason = null) => null;
}
