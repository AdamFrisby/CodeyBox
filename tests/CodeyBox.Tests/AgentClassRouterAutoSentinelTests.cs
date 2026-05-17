using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for the per-model availability resolution logic in
/// <see cref="AgentClassRouter.ResolveMemberQuota"/>:
///
/// <list type="bullet">
///   <item>ModelId not in PerModel → unknown (-1), gated by QuotaUnknownPolicy.</item>
///   <item>ModelId == "auto" → best-of-fleet (MAX) across PerModel.</item>
///   <item>ModelId == "auto" with all bucket entries exhausted → 0%, MIN reset.</item>
///   <item>Empty PerModel → fall back to snapshot.AvailablePct (no per-model signal).</item>
/// </list>
/// </summary>
public sealed class AgentClassRouterAutoSentinelTests
{
    [Fact]
    public void UnknownModelInPerModel_ReturnsUnknown()
    {
        // Probe returns per-model data for two models, but the membership names a third.
        var snapshot = new AgentQuotaSnapshot
        {
            AvailablePct = 80,
            PerModel = new Dictionary<string, ModelQuota>(StringComparer.OrdinalIgnoreCase)
            {
                ["gemini-2.5-pro"] = new() { AvailablePct = 90 },
                ["gemini-2.5-flash"] = new() { AvailablePct = 10 },
            },
        };
        var member = new AgentMembership
        {
            Agent = AgentKind.Gemini,
            Billing = AgentBilling.Subscription,
            ModelId = "gemini-3-pro-preview",
            QualityScore = 95,
            ReasoningMode = "high",
        };

        var quota = AgentClassRouter.ResolveMemberQuota(snapshot, member);

        // Unknown — must not silently fall back to the overall 80% bucket.
        Assert.Equal(-1, quota.AvailablePct);
    }

    [Fact]
    public void AutoSentinel_ReturnsBestOfFleet()
    {
        var snapshot = new AgentQuotaSnapshot
        {
            AvailablePct = 5, // overall is mostConstrained: 5%
            PerModel = new Dictionary<string, ModelQuota>(StringComparer.OrdinalIgnoreCase)
            {
                ["gemini-2.5-pro"] = new() { AvailablePct = 5 },
                ["gemini-2.5-flash"] = new() { AvailablePct = 100 },
                ["gemini-2.5-flash-lite"] = new() { AvailablePct = 42 },
            },
        };
        var member = new AgentMembership
        {
            Agent = AgentKind.Gemini,
            Billing = AgentBilling.Subscription,
            ModelId = "auto",
            QualityScore = 95,
            ReasoningMode = "high",
        };

        var quota = AgentClassRouter.ResolveMemberQuota(snapshot, member);

        // Best-of-fleet is 100 (flash), not mostConstrained 5.
        Assert.Equal(100, quota.AvailablePct);
    }

    [Fact]
    public void AutoSentinel_IsCaseInsensitive()
    {
        var snapshot = new AgentQuotaSnapshot
        {
            AvailablePct = 5,
            PerModel = new Dictionary<string, ModelQuota>(StringComparer.OrdinalIgnoreCase)
            {
                ["gemini-2.5-pro"] = new() { AvailablePct = 50 },
            },
        };
        var member = new AgentMembership
        {
            Agent = AgentKind.Gemini,
            Billing = AgentBilling.Subscription,
            ModelId = "AUTO",
            QualityScore = 95,
            ReasoningMode = "high",
        };

        var quota = AgentClassRouter.ResolveMemberQuota(snapshot, member);

        Assert.Equal(50, quota.AvailablePct);
    }

    [Fact]
    public void AutoSentinel_PicksEarliestReset()
    {
        var earlier = new DateTimeOffset(2026, 5, 17, 10, 0, 0, TimeSpan.Zero);
        var later = new DateTimeOffset(2026, 5, 17, 14, 0, 0, TimeSpan.Zero);
        var snapshot = new AgentQuotaSnapshot
        {
            AvailablePct = 0,
            PerModel = new Dictionary<string, ModelQuota>(StringComparer.OrdinalIgnoreCase)
            {
                ["gemini-2.5-pro"] = new() { AvailablePct = 0, ResetAt = later },
                ["gemini-2.5-flash"] = new() { AvailablePct = 0, ResetAt = earlier },
            },
        };
        var member = new AgentMembership
        {
            Agent = AgentKind.Gemini,
            Billing = AgentBilling.Subscription,
            ModelId = "auto",
            QualityScore = 95,
            ReasoningMode = "high",
        };

        var quota = AgentClassRouter.ResolveMemberQuota(snapshot, member);

        Assert.Equal(0, quota.AvailablePct);
        Assert.Equal(earlier, quota.ResetAt);
    }

    [Fact]
    public void EmptyPerModel_FallsBackToOverall()
    {
        // No per-model signal at all (e.g. NullQuotaProbe). The existing
        // fall-back to overall is preserved so unrelated probes are unaffected.
        var snapshot = new AgentQuotaSnapshot { AvailablePct = 73 };
        var member = new AgentMembership
        {
            Agent = AgentKind.Gemini,
            Billing = AgentBilling.Subscription,
            ModelId = "gemini-2.5-pro",
            QualityScore = 95,
            ReasoningMode = "high",
        };

        var quota = AgentClassRouter.ResolveMemberQuota(snapshot, member);

        Assert.Equal(73, quota.AvailablePct);
    }

    [Fact]
    public async Task UnknownModel_FailCautious_GatesAtRuntime()
    {
        // End-to-end: an unknown model gated by FailCautious gets skipped in
        // favour of the next member.
        var snapshot = new AgentQuotaSnapshot
        {
            AvailablePct = 80,
            PerModel = new Dictionary<string, ModelQuota>(StringComparer.OrdinalIgnoreCase)
            {
                ["gemini-2.5-pro"] = new() { AvailablePct = 90 },
            },
        };
        var cls = new AgentClass
        {
            Id = "frontier",
            DisplayName = "Frontier",
            Members =
            [
                new AgentMembership
                {
                    Agent = AgentKind.Gemini,
                    Billing = AgentBilling.Subscription,
                    ModelId = "gemini-9-pro-preview", // unknown — not in bucket list
                    QualityScore = 95,
                    ReasoningMode = "high",
                },
                new AgentMembership
                {
                    Agent = AgentKind.Claude,
                    Billing = AgentBilling.Subscription,
                    ModelId = "claude-opus-4-7",
                    QualityScore = 94,
                },
            ],
        };

        var router = new AgentClassRouter(
            [cls],
            [new FakeProbe(AgentKind.Gemini, snapshot), new FakeProbe(AgentKind.Claude, 60.0)],
            new QuotaRouterOptions { MinQuotaPct = 10, UnknownPolicy = QuotaUnknownPolicy.FailCautious },
            NullLogger<AgentClassRouter>.Instance);

        var decision = await router.ResolveAsync(new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("proj"),
            Title = "t",
            Prompt = "p",
            AgentClassId = "frontier",
            MinModelScore = 90, // both members are eligible
        }, null, CancellationToken.None);

        // Gemini was skipped (unknown model + fail-cautious); Claude was picked.
        Assert.Equal(AgentKind.Claude, decision.Chosen!.Agent);
    }

    [Fact]
    public async Task AutoSentinel_NoBucketsExhausted_RoutesToGemini()
    {
        // auto + one bucket entry at 100% → routes to gemini (not skipped).
        var snapshot = new AgentQuotaSnapshot
        {
            AvailablePct = 5, // mostConstrained reading would block this member
            PerModel = new Dictionary<string, ModelQuota>(StringComparer.OrdinalIgnoreCase)
            {
                ["gemini-2.5-pro"] = new() { AvailablePct = 5 },
                ["gemini-2.5-flash"] = new() { AvailablePct = 100 },
            },
        };
        var cls = new AgentClass
        {
            Id = "frontier",
            DisplayName = "Frontier",
            Members =
            [
                new AgentMembership
                {
                    Agent = AgentKind.Gemini,
                    Billing = AgentBilling.Subscription,
                    ModelId = "auto",
                    QualityScore = 95,
                    ReasoningMode = "high",
                },
            ],
        };

        var router = new AgentClassRouter(
            [cls],
            [new FakeProbe(AgentKind.Gemini, snapshot)],
            new QuotaRouterOptions { MinQuotaPct = 10 },
            NullLogger<AgentClassRouter>.Instance);

        var decision = await router.ResolveAsync(new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("proj"),
            Title = "t",
            Prompt = "p",
            AgentClassId = "frontier",
        }, null, CancellationToken.None);

        Assert.Equal(AgentKind.Gemini, decision.Chosen!.Agent);
        Assert.Equal("auto", decision.Chosen!.ModelId);
    }
}
