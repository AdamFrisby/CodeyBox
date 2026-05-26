using CodeyBox.Core;

namespace CodeyBox.Agents.Opencode;

/// <summary>
/// Placeholder quota probe for opencode. Always returns Unknown.
///
/// <para>opencode's subscription-tier ("opencode Go") IS metered, but no
/// programmatic usage/credits endpoint has been verified against the live
/// service in this environment. Per the operator's
/// <c>feedback-vendor-api-drift</c> rule, we do not speculate on the
/// response shape; this probe ships as a no-op so the router falls onto its
/// <c>QuotaUnknownPolicy=UseObservedFailures</c> behaviour for opencode
/// members until a real probe endpoint is confirmed.</para>
///
/// <para>To wire a real endpoint later: replace
/// <see cref="GetAvailabilityAsync"/> with an <c>HttpClient</c> call mirroring
/// <c>CodexQuotaProbe</c> (named client <c>agent-quota</c>, never log the
/// authorization header, never log the response body), and add the parsing
/// logic for whichever shape opencode actually returns.</para>
/// </summary>
public sealed class OpencodeQuotaProbe : IAgentQuotaProbe
{
    public AgentKind Kind => AgentKind.Opencode;

    public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
    {
        _ = member;
        return Task.FromResult(new AgentQuotaSnapshot
        {
            AvailablePct = -1,
            Notes = "no probe endpoint",
        });
    }
}
