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
    /// bundles the allow bit with the refusal reason (for quota-floor
    /// refusals, naming the binding quota window when the aggregate reading
    /// came from a window) so a caller cannot pair a reason with a stale
    /// verdict or infer allowedness from the reason's nullness.
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
/// binds for window-gated refusals. Constructed only via
/// <see cref="Allowed"/>/<see cref="Denied"/>, so a refusal always carries its
/// reason and an allow never carries a stale one.
/// </summary>
public sealed record AgentQuotaGateVerdict
{
    private AgentQuotaGateVerdict(bool allow, string? refusalReason)
    {
        Allow = allow;
        RefusalReason = refusalReason;
    }

    /// <summary>True when the gate lets the dispatch proceed.</summary>
    public bool Allow { get; }

    /// <summary>
    /// Human-readable refusal reason; null iff <see cref="Allow"/> is true.
    /// </summary>
    public string? RefusalReason { get; }

    /// <summary>A verdict letting the dispatch proceed.</summary>
    public static AgentQuotaGateVerdict Allowed() => new(true, null);

    /// <summary>
    /// A verdict refusing the dispatch; <paramref name="reason"/> explains why.
    /// </summary>
    public static AgentQuotaGateVerdict Denied(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new(false, reason);
    }
}
