using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for the in-process exhaustion cache that backs the pipeline's
/// mid-iteration quota fallback loop. The router exposes
/// <c>OrderedFallbackCandidates</c> + <c>MarkExhausted</c> so the pipeline can
/// swap agents on a live <see cref="AgentFailureKind.QuotaExhausted"/> result
/// without a probe round-trip.
/// </summary>
public sealed class AgentClassRouterFallbackTests
{
    private static readonly AgentKind Claude = AgentKind.Claude;
    private static readonly AgentKind Codex = AgentKind.Codex;
    private static readonly AgentKind Gemini = AgentKind.Gemini;

    private static AgentClassRouter Build(AgentClass cls, params IAgentQuotaProbe[] probes) =>
        new(
            [cls],
            probes,
            new QuotaRouterOptions { MinQuotaPct = 10.0 },
            NullLogger<AgentClassRouter>.Instance);

    private static AgentClass Frontier(params AgentMembership[] members) => new()
    {
        Id = "frontier",
        DisplayName = "Frontier",
        Members = members,
    };

    private static AgentMembership Sub(AgentKind kind, int score = 100, string? modelId = null) => new()
    {
        Agent = kind,
        Billing = AgentBilling.Subscription,
        QualityScore = score,
        ModelId = modelId,
    };

    private static WorkItem Item(string classId = "frontier", int minScore = 95) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("proj"),
        Title = "t",
        Prompt = "p",
        AgentClassId = classId,
        MinModelScore = minScore,
    };

    [Fact]
    public async Task OrderedFallbackCandidates_ReturnsByEffectiveScore_HighestFirst()
    {
        var cls = Frontier(Sub(Codex, score: 100), Sub(Claude, score: 100), Sub(Gemini, score: 95));
        var router = Build(cls);

        var candidates = await router.OrderedFallbackCandidatesAsync(Item(), project: null, CancellationToken.None);

        // Tied scores fall back to config order; Gemini (95) ranks below the 100s.
        Assert.Equal([Codex, Claude, Gemini], candidates.Select(c => c.Agent).ToArray());
    }

    [Fact]
    public async Task OrderedFallbackCandidates_FiltersByMinModelScore()
    {
        var cls = Frontier(Sub(Codex, score: 100), Sub(Gemini, score: 70));
        var router = Build(cls);

        var candidates = await router.OrderedFallbackCandidatesAsync(Item(minScore: 95), project: null, CancellationToken.None);

        Assert.Single(candidates);
        Assert.Equal(Codex, candidates[0].Agent);
    }

    [Fact]
    public async Task MarkExhausted_DropsMember_FromSubsequentOrdering()
    {
        var cls = Frontier(Sub(Codex), Sub(Claude), Sub(Gemini, score: 95));
        var router = Build(cls);

        router.MarkExhausted(Sub(Codex), TimeSpan.FromMinutes(30));
        var candidates = await router.OrderedFallbackCandidatesAsync(Item(), project: null, CancellationToken.None);

        Assert.Equal([Claude, Gemini], candidates.Select(c => c.Agent).ToArray());
    }

    [Fact]
    public async Task MarkExhausted_RespectsResetAt_WhenSoonerThanTtl()
    {
        var cls = Frontier(Sub(Codex), Sub(Claude));
        var router = Build(cls);

        // Reset is in the past — exhaustion should expire immediately.
        router.MarkExhausted(Sub(Codex), TimeSpan.FromHours(1), resetAt: DateTimeOffset.UtcNow.AddSeconds(-1));
        var candidates = await router.OrderedFallbackCandidatesAsync(Item(), project: null, CancellationToken.None);

        Assert.Equal([Codex, Claude], candidates.Select(c => c.Agent).ToArray());
    }

    [Fact]
    public async Task MarkExhausted_PerModel_OnlyDropsMatchingModel()
    {
        var cls = Frontier(
            Sub(Claude, modelId: "claude-opus-4-7"),
            Sub(Claude, modelId: "claude-sonnet-4-6"));
        var router = Build(cls);

        router.MarkExhausted(Sub(Claude, modelId: "claude-opus-4-7"), TimeSpan.FromMinutes(30));
        var candidates = await router.OrderedFallbackCandidatesAsync(Item(), project: null, CancellationToken.None);

        Assert.Single(candidates);
        Assert.Equal("claude-sonnet-4-6", candidates[0].ModelId);
    }

    [Fact]
    public async Task OrderedFallbackCandidates_ReturnsEmpty_WhenNoClassConfigured()
    {
        var cls = Frontier(Sub(Claude));
        var router = Build(cls);

        var candidates = await router.OrderedFallbackCandidatesAsync(Item(classId: null!), project: null, CancellationToken.None);
        Assert.Empty(candidates);
    }

    [Fact]
    public async Task ResolveAsync_SkipsExhaustedMember_AndPicksNext()
    {
        var cls = Frontier(Sub(Codex), Sub(Claude));
        var router = Build(cls,
            new FakeProbe(Codex, 80.0),  // would normally win on tie + config order
            new FakeProbe(Claude, 50.0));

        router.MarkExhausted(Sub(Codex), TimeSpan.FromMinutes(30));

        var decision = await router.ResolveAsync(Item(), project: null, CancellationToken.None);

        Assert.NotNull(decision.Chosen);
        Assert.Equal(Claude, decision.Chosen!.Agent);
    }
}
