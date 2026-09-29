namespace CodeyBox.Core;

/// <summary>
/// Focused quota-availability gate for consumers that need to know whether a
/// probed subscription member is currently routable without depending on the
/// full router implementation.
/// </summary>
public interface IAgentQuotaGate
{
    /// <summary>
    /// Gate check with caller-supplied recent-observed-failure context — used
    /// by status surfaces (e.g. <c>/quota</c>) that already hold per-(agent,
    /// model) failure state. The returned <see cref="AgentQuotaGateVerdict"/>
    /// bundles the allow bit with the refusal reason (which names the binding
    /// quota window) so a caller cannot pair a reason with a stale verdict or
    /// infer allowedness from the reason's nullness.
    /// </summary>
    Task<AgentQuotaGateVerdict> EvaluateAsync(
        AgentMembership member,
        AgentQuotaSnapshot snapshot,
        DateTimeOffset nowUtc,
        bool recentObservedFailure,
        string? observedFailureReason,
        CancellationToken ct = default);

    /// <summary>
    /// Gate check that resolves recent-observed-failure context internally —
    /// for callers (notifications, watchdogs) that should see the same decision
    /// the dispatch router applies but lack the failure-store dependency.
    /// Implementations consult the failure store when wired; otherwise evaluate
    /// with no observed failure.
    /// </summary>
    Task<AgentQuotaGateVerdict> EvaluateAsync(
        AgentMembership member,
        AgentQuotaSnapshot snapshot,
        DateTimeOffset nowUtc,
        CancellationToken ct = default);
}

/// <summary>
/// The quota gate's verdict for one member: whether a dispatch may proceed
/// and, when refused, the human-readable reason — including which quota window
/// binds. Bundled so the allow bit and its explanation are produced by the
/// same evaluation and can never diverge.
/// </summary>
public sealed record AgentQuotaGateVerdict(bool Allow, string? RefusalReason);
