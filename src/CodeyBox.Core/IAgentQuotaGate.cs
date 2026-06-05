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
}
