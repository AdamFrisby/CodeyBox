using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Xunit;

namespace CodeyBox.Tests;

public sealed class QuotaRetryKeyTests
{
    [Fact]
    public void QuotaBucketKeys_NormalizeRouteKeyConsistently()
    {
        var member = new AgentMembership
        {
            Agent = AgentKind.Claude,
            InstanceId = "Acct-A",
            ModelId = "opus",
            Billing = AgentBilling.Subscription,
            QualityScore = 100,
        };

        var quotaMemberKey = AgentQuotaMemberKey.From(member);
        var admissionPoolKey = QuotaRetryAdmissionPoolKey.FromMembership(member);

        Assert.Equal("claude/acct-a", quotaMemberKey.RouteKey);
        Assert.Equal(quotaMemberKey.RouteKey, admissionPoolKey.RouteKey);
        Assert.Equal(quotaMemberKey.Agent, admissionPoolKey.Agent);
        Assert.Equal(quotaMemberKey.ModelId, admissionPoolKey.ModelId);
    }
}
