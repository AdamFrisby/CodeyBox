using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Focused routing port used by quota auto-retry re-evaluation and dispatch
/// admission. It exposes the router operations needed to decide whether a
/// parked quota item can run now, when exhausted class members are expected to
/// refill, and which routing bucket a lower-priority dispatch candidate would
/// currently consume.
/// </summary>
public interface IQuotaRetryRouter
{
    Task<QuotaRetryRoutingDecision> ResolveQuotaRetryAsync(
        WorkItem item,
        Project? project,
        CancellationToken ct,
        string? requiredCapability = null);

    Task<DateTimeOffset?> ComputeEarliestExhaustedResetAsync(
        WorkItem item,
        Project? project,
        CancellationToken ct,
        string? requiredCapability = null);

    /// <summary>
    /// Returns the static routing buckets that <paramref name="item"/> could use
    /// after class, capability, and score filtering. This describes admission
    /// overlap only; it intentionally does not mean any returned member currently
    /// has quota or dispatch availability.
    /// </summary>
    IReadOnlySet<QuotaRetryAdmissionPoolKey> GetQuotaRetryAdmissionPool(
        WorkItem item,
        Project? project,
        string? requiredCapability = null);

    /// <summary>
    /// Returns the single routing bucket that <paramref name="item"/> would use
    /// right now after applying current quota and availability gates, or
    /// <c>null</c> when the item has no currently routable class member. The
    /// optional <paramref name="requiredCapability"/> is the phase-specific
    /// capability gate, such as audit, and composes with the item's own
    /// <see cref="WorkItem.RequiredCapabilities"/> and
    /// <see cref="WorkItem.MinModelScore"/>.
    /// </summary>
    Task<QuotaRetryAdmissionPoolKey?> ResolveCurrentQuotaRetryAdmissionAsync(
        WorkItem item,
        Project? project,
        CancellationToken ct,
        string? requiredCapability = null);
}

public sealed record QuotaRetryRoutingDecision(
    bool ShouldWait,
    bool NoEligibleMembers,
    string? Reason,
    bool WaitingForPausedAgent = false);

public readonly record struct QuotaRetryAdmissionPoolKey(
    string RouteKey,
    AgentKind Agent,
    string ModelId)
{
    public static QuotaRetryAdmissionPoolKey FromMembership(AgentMembership member) =>
        new(
            NormalizeRouteKey(member.RouteKey),
            member.Agent,
            member.ModelId ?? string.Empty);

    public static QuotaRetryAdmissionPoolKey FromDirectAgent(
        AgentKind agent,
        string routeKey,
        string? modelId) =>
        new(
            NormalizeRouteKey(routeKey),
            agent,
            modelId ?? string.Empty);

    private static string NormalizeRouteKey(string routeKey) =>
        routeKey.Trim().ToLowerInvariant();
}
