using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Focused routing port used by quota auto-retry re-evaluation. It exposes only
/// the router operations needed to decide whether a parked quota item can run
/// now and when exhausted class members are expected to refill.
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

    IReadOnlySet<QuotaRetryAdmissionPoolKey> GetQuotaRetryAdmissionPool(
        WorkItem item,
        Project? project,
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
