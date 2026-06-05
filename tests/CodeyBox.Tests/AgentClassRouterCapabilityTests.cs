using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for the new capability-based eligibility gate on
/// <see cref="AgentClassRouter"/>. Verifies that:
/// <list type="bullet">
///   <item>Required capabilities filter eligibility independently of QualityScore.</item>
///   <item>An empty required-capability list keeps the router open to any member.</item>
///   <item>QualityScore remains a routing preference, not an eligibility gate.</item>
///   <item>The legacy <see cref="WorkItem.MinModelScore"/> floor is still honoured
///         alongside capabilities during the transition window.</item>
/// </list>
/// </summary>
public sealed class AgentClassRouterCapabilityTests
{
    private static readonly AgentKind Claude = AgentKind.Claude;
    private static readonly AgentKind Codex = AgentKind.Codex;
    private static readonly AgentKind Gemini = AgentKind.Gemini;

    private static AgentClassRouter BuildRouter(IEnumerable<AgentClass> catalog, IEnumerable<IAgentQuotaProbe> probes)
    {
        var opts = new QuotaRouterOptions { MinQuotaPct = 10.0, QuotaRecheckInterval = TimeSpan.FromMinutes(5) };
        return new AgentClassRouter(catalog.ToList(), probes, opts, NullLogger<AgentClassRouter>.Instance);
    }

    private static AgentMembership Member(
        AgentKind kind,
        int score,
        params string[] capabilities) =>
        new()
        {
            Agent = kind,
            Billing = AgentBilling.Subscription,
            QualityScore = score,
            Capabilities = capabilities,
        };

    private static AgentClass Class(params AgentMembership[] members) => new()
    {
        Id = "cls",
        DisplayName = "cls",
        Members = members,
    };

    private static WorkItem Item(int minScore = 0, params string[] required) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("proj"),
        Title = "t",
        Prompt = "p",
        AgentClassId = "cls",
        MinModelScore = minScore,
        RequiredCapabilities = required,
    };

    // ── Default-open: empty required list ⇒ any member is eligible ───────────

    [Fact]
    public async Task NoRequiredCapabilities_AnyMemberEligible()
    {
        var cls = Class(Member(Claude, 80), Member(Codex, 70));
        var router = BuildRouter([cls], [new FakeProbe(Claude, 50.0), new FakeProbe(Codex, 50.0)]);

        var decision = await router.ResolveAsync(Item(), null, CancellationToken.None);

        Assert.NotNull(decision.Chosen);
        Assert.False(decision.NoEligibleMembers);
        // Quality-score preference picks the strongest free member.
        Assert.Equal(Claude, decision.Chosen!.Agent);
    }

    // ── Capability gates eligibility ──────────────────────────────────────────

    [Fact]
    public async Task RequiredCapability_OnlyMatchingMembersEligible()
    {
        // Codex declares "sensitive", Claude doesn't. Item requires "sensitive".
        // Even though Claude has the higher score, Codex must be chosen.
        var cls = Class(
            Member(Claude, 100),
            Member(Codex, 70, "sensitive"));
        var router = BuildRouter([cls], [new FakeProbe(Claude, 50.0), new FakeProbe(Codex, 50.0)]);

        var decision = await router.ResolveAsync(Item(required: "sensitive"), null, CancellationToken.None);

        Assert.NotNull(decision.Chosen);
        Assert.Equal(Codex, decision.Chosen!.Agent);
    }

    [Fact]
    public async Task RequiredCapability_NoMatchingMembers_NoEligible()
    {
        var cls = Class(Member(Claude, 100), Member(Codex, 100));
        var router = BuildRouter([cls], [new FakeProbe(Claude, 50.0), new FakeProbe(Codex, 50.0)]);

        var decision = await router.ResolveAsync(Item(required: "sensitive"), null, CancellationToken.None);

        Assert.Null(decision.Chosen);
        Assert.True(decision.NoEligibleMembers);
        Assert.False(decision.ShouldWait);
        Assert.Contains("ROUTING_NO_ELIGIBLE", decision.Reason);
    }

    [Fact]
    public async Task RequiredCapability_MatchIsCaseInsensitive()
    {
        var cls = Class(Member(Claude, 100, "Sensitive"));
        var router = BuildRouter([cls], [new FakeProbe(Claude, 50.0)]);

        var decision = await router.ResolveAsync(Item(required: "SENSITIVE"), null, CancellationToken.None);

        Assert.NotNull(decision.Chosen);
        Assert.Equal(Claude, decision.Chosen!.Agent);
    }

    [Fact]
    public async Task RequiredCapability_InheritsAcrossSameKindInstances()
    {
        var tagged = Member(Claude, 100, "audit") with { InstanceId = "acct-a" };
        var sibling = Member(Claude, 99) with { InstanceId = "acct-b" };
        var cls = Class(tagged, sibling);
        var router = BuildRouter([cls], [new InstanceRouteProbe(Claude, new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            [tagged.RouteKey] = 0.0,
            [sibling.RouteKey] = 50.0,
        })]);

        var decision = await router.ResolveAsync(
            Item(required: "audit") with { AgentInstanceId = sibling.RouteKey },
            null,
            CancellationToken.None);
        var pool = router.GetCapabilityPool("cls", "audit");

        Assert.NotNull(decision.Chosen);
        Assert.Equal(sibling.RouteKey, decision.Chosen!.RouteKey);
        Assert.NotNull(pool);
        Assert.Contains(Claude, pool!);
    }

    [Fact]
    public async Task RequiredCapability_MemberMustCoverEveryTag()
    {
        // Claude declares "sensitive" only; item requires both — Claude rejected.
        // Codex declares both — Codex picked even though score is lower.
        var cls = Class(
            Member(Claude, 100, "sensitive"),
            Member(Codex, 80, "sensitive", "architectural"));
        var router = BuildRouter([cls], [new FakeProbe(Claude, 50.0), new FakeProbe(Codex, 50.0)]);

        var decision = await router.ResolveAsync(
            Item(required: ["sensitive", "architectural"]),
            null, CancellationToken.None);

        Assert.NotNull(decision.Chosen);
        Assert.Equal(Codex, decision.Chosen!.Agent);
    }

    // ── QualityScore is preference, NOT eligibility ──────────────────────────

    [Fact]
    public async Task QualityScore_OnlyOrdersAmongEligible_NotAnEligibilityGate()
    {
        // Item has no MinModelScore floor — a low-scoring member with the
        // right capability is still eligible and (being the only one) wins.
        var cls = Class(
            Member(Claude, 100),                  // no capability
            Member(Gemini, 50, "sensitive"));     // matches required tag
        var router = BuildRouter([cls], [new FakeProbe(Claude, 50.0), new FakeProbe(Gemini, 50.0)]);

        var decision = await router.ResolveAsync(Item(required: "sensitive"), null, CancellationToken.None);

        Assert.NotNull(decision.Chosen);
        Assert.Equal(Gemini, decision.Chosen!.Agent);
    }

    [Fact]
    public async Task QualityScore_HighestEligibleMemberWins()
    {
        // Both eligible. Quality-score preference picks Claude over Codex.
        var cls = Class(
            Member(Claude, 100, "sensitive"),
            Member(Codex, 90, "sensitive"));
        var router = BuildRouter([cls], [new FakeProbe(Claude, 50.0), new FakeProbe(Codex, 50.0)]);

        var decision = await router.ResolveAsync(Item(required: "sensitive"), null, CancellationToken.None);

        Assert.Equal(Claude, decision.Chosen!.Agent);
    }

    // ── Legacy MinModelScore still honoured during transition ─────────────────

    [Fact]
    public async Task MinModelScore_StillHonouredAlongsideCapabilityGate()
    {
        // Codex declares the capability but its score (70) is below the floor (90).
        // Claude is above the floor but lacks the capability. Neither is eligible.
        var cls = Class(
            Member(Claude, 100),
            Member(Codex, 70, "sensitive"));
        var router = BuildRouter([cls], [new FakeProbe(Claude, 50.0), new FakeProbe(Codex, 50.0)]);

        var decision = await router.ResolveAsync(
            Item(minScore: 90, required: "sensitive"), null, CancellationToken.None);

        Assert.True(decision.NoEligibleMembers);
        Assert.Null(decision.Chosen);
    }

    [Fact]
    public async Task MinModelScore_OnlyGate_StillWorksWhenNoCapabilityRequired()
    {
        // Legacy behaviour: an item with MinModelScore=95 and no capability requirement
        // is still gated by the floor.
        var cls = Class(Member(Claude, 80));
        var router = BuildRouter([cls], [new FakeProbe(Claude, 50.0)]);

        var decision = await router.ResolveAsync(Item(minScore: 95), null, CancellationToken.None);

        Assert.True(decision.NoEligibleMembers);
        Assert.Null(decision.Chosen);
    }

    // ── OrderedFallbackCandidates also respects the capability gate ──────────

    [Fact]
    public async Task OrderedFallbackCandidates_FiltersByRequiredCapabilities()
    {
        var cls = Class(
            Member(Claude, 100),
            Member(Codex, 70, "sensitive"));
        var router = BuildRouter([cls], [new FakeProbe(Claude, 50.0), new FakeProbe(Codex, 50.0)]);

        var candidates = await router.OrderedFallbackCandidatesAsync(
            Item(required: "sensitive"), null, CancellationToken.None);

        Assert.Single(candidates);
        Assert.Equal(Codex, candidates[0].Agent);
    }

    [Fact]
    public async Task OrderedFallbackCandidates_NoCapabilityRequired_AllEligibleReturned()
    {
        var cls = Class(Member(Claude, 100), Member(Codex, 90));
        var router = BuildRouter([cls], [new FakeProbe(Claude, 50.0), new FakeProbe(Codex, 50.0)]);

        var candidates = await router.OrderedFallbackCandidatesAsync(
            Item(), null, CancellationToken.None);

        Assert.Equal(2, candidates.Count);
        // Preference order: highest score first.
        Assert.Equal(Claude, candidates[0].Agent);
        Assert.Equal(Codex, candidates[1].Agent);
    }

    // ── ComputeEarliestExhaustedReset honours eligibility gate ───────────────

    [Fact]
    public async Task ComputeEarliestExhaustedReset_SkipsMembersFailingRequiredCapabilities()
    {
        // Claude has an earlier reset but lacks the required capability — its
        // reset should be ignored. Codex has a later reset but covers the tag.
        var now = DateTimeOffset.UtcNow;
        var claudeReset = now.AddHours(1);
        var codexReset = now.AddHours(5);

        var cls = Class(
            Member(Claude, 100),                  // no capability — ineligible
            Member(Codex, 80, "sensitive"));      // eligible
        var router = BuildRouter([cls],
        [
            new FakeProbe(Claude, new AgentQuotaSnapshot { AvailablePct = 0, ResetAt = claudeReset }),
            new FakeProbe(Codex, new AgentQuotaSnapshot { AvailablePct = 0, ResetAt = codexReset }),
        ]);

        var earliest = await router.ComputeEarliestExhaustedResetAsync(
            Item(required: "sensitive"), null, CancellationToken.None);

        // Claude's earlier reset must NOT be returned — only Codex is eligible.
        Assert.Equal(codexReset, earliest);
    }

    [Fact]
    public async Task ComputeEarliestExhaustedReset_SkipsMembersBelowMinModelScore()
    {
        // Codex's reset is earlier but its score is below the floor.
        // Claude's later reset is the only eligible one.
        var now = DateTimeOffset.UtcNow;
        var claudeReset = now.AddHours(5);
        var codexReset = now.AddHours(1);

        var cls = Class(
            Member(Claude, 100),
            Member(Codex, 80));
        var router = BuildRouter([cls],
        [
            new FakeProbe(Claude, new AgentQuotaSnapshot { AvailablePct = 0, ResetAt = claudeReset }),
            new FakeProbe(Codex, new AgentQuotaSnapshot { AvailablePct = 0, ResetAt = codexReset }),
        ]);

        var earliest = await router.ComputeEarliestExhaustedResetAsync(
            Item(minScore: 90), null, CancellationToken.None);

        Assert.Equal(claudeReset, earliest);
    }
}
