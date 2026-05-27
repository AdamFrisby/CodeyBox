using CodeyBox.Agents.Opencode;
using CodeyBox.Core;

namespace CodeyBox.Tests;

public sealed class OpencodeQuotaProbeTests
{
    [Fact]
    public async Task GetAvailabilityAsync_ReturnsUnknown()
    {
        // Until opencode's subscription exposes a verified usage endpoint
        // the probe must report Unknown so the router falls onto its
        // QuotaUnknownPolicy=UseObservedFailures behaviour. Anything else
        // would either fail-open onto an exhausted subscription or
        // permanently gate opencode out of the chain.
        var probe = new OpencodeQuotaProbe();
        var member = new AgentMembership
        {
            Agent = AgentKind.Opencode,
            Billing = AgentBilling.Subscription,
            QualityScore = 90,
        };

        var snapshot = await probe.GetAvailabilityAsync(member, CancellationToken.None);

        Assert.Equal(-1, snapshot.AvailablePct);
        Assert.Equal("no probe endpoint", snapshot.Notes);
        Assert.Null(snapshot.ResetAt);
        Assert.Empty(snapshot.PerModel);
    }

    [Fact]
    public void Kind_IsOpencode()
    {
        Assert.Equal(AgentKind.Opencode, new OpencodeQuotaProbe().Kind);
    }
}
