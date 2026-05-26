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
