using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

public static class QuotaRouter
{
    public static bool WouldAllow(
        AgentKind agent,
        double availablePct,
        bool recentFailure,
        QuotaRouterOptions options,
        DateTimeOffset? resetAt = null,
        DateTimeOffset? nowUtc = null)
    {
        return WouldAllow(
            new AgentMembership
            {
                Agent = agent,
                Billing = AgentBilling.Subscription,
                QualityScore = 100,
            },
            new EffectiveQuota(availablePct, resetAt, null),
            recentFailure,
            options,
            nowUtc);
    }

    public static bool WouldAllow(
        AgentMembership member,
        EffectiveQuota quota,
        bool recentFailure,
        QuotaRouterOptions options,
        DateTimeOffset? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(member);
        return QuotaGatePolicy.Evaluate(
            options,
            member,
            quota,
            nowUtc ?? DateTimeOffset.UtcNow,
            recentFailure,
            observedFailureReason: "recent observed quota failure").Allow;
    }
}
