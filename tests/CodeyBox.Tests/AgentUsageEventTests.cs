using CodeyBox.Core;

namespace CodeyBox.Tests;

public sealed class AgentUsageEventTests
{
    [Fact]
    public void UsdToMicroCents_RoundTripsOneUsdThroughDocumentedDivisor()
    {
        var stored = AgentUsageEvent.UsdToMicroCents(1.00m);

        Assert.Equal(1_000_000L, stored);
        Assert.Equal(1.00m, AgentUsageEvent.MicroCentsToUsd(1_000_000L));
    }
}
