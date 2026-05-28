using CodeyBox.Core;

namespace CodeyBox.Agents.Cursor;

/// <summary>
/// Quota probe for Cursor's subscription-auth path.
///
/// <para>Cursor does not currently document a usage/rate-limit endpoint
/// reachable from a subscription token. Rather than fabricate a snapshot, the
/// probe returns <c>AvailablePct=-1</c> (<see cref="AgentQuotaSnapshot.Notes"/>
/// = <c>"no probe endpoint"</c>) and lets the router's
/// <c>UnknownPolicy=UseObservedFailures</c> apply observation-based back-
/// pressure. This is consistent with the operator's stated preference for
/// reactive over speculative-coverage (see <c>feedback-vendor-api-drift</c>):
/// when a Cursor exhaustion does occur, <see cref="CursorQuotaFailureDetector"/>
/// classifies the stderr/stdout and the <c>IQuotaFailureStore</c> + observed-
/// failure circuit-breaker handle dispatch back-pressure without a poll loop.</para>
///
/// <para>If a Cursor usage endpoint surfaces later, model the implementation on
/// <c>CodexQuotaProbe</c> (caches per-token, dedupes missing-model warnings,
/// invalidates on file source <c>TokenUpdated</c>).</para>
///
/// <para><b>FUTURE-IMPLEMENTOR HEADLINE-METRIC NOTE.</b> When a real probe
/// replaces this placeholder against the observed <c>planUsage</c> response
/// shape, compute the headline <see cref="AgentQuotaSnapshot.AvailablePct"/>
/// from spend-vs-limit, NOT from <c>totalPercentUsed</c>:
/// <code>
/// availablePct = (planUsage.remaining / planUsage.limit) * 100
///             // equivalent to: 100 - (planUsage.totalSpend / planUsage.limit * 100)
/// </code>
/// Guard <c>planUsage.limit == 0</c> by returning the existing -1 "unknown"
/// sentinel. The <c>totalPercentUsed</c> / <c>autoPercentUsed</c> /
/// <c>apiPercentUsed</c> fields are normalised against a much larger
/// denominator (likely including usage-based-billing headroom) and DO NOT
/// match what the Cursor web UI shows the operator. Captured live response:
/// <c>totalSpend=1313, limit=2000, remaining=687, totalPercentUsed=6.73</c>;
/// the same response's <c>displayMessage</c> reads
/// "You've used 66% of your included usage" — i.e. 1313/2000 = 65.65%, NOT
/// 6.73%. Picking <c>totalPercentUsed</c> would disagree with the UI by
/// ~60 points and would keep dispatching Cursor when it is near cap. Keep
/// the three percent-used fields as <see cref="AgentQuotaSnapshot.Notes"/>
/// content or <see cref="ModelQuota"/> entries so operators investigating
/// quota state can still see them, but never as the headline.</para>
/// </summary>
public sealed class CursorQuotaProbe : IAgentQuotaProbe
{
    public AgentKind Kind => AgentKind.Cursor;

    public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
        => Task.FromResult(new AgentQuotaSnapshot
        {
            AvailablePct = -1,
            Notes = "no probe endpoint",
        });
}
