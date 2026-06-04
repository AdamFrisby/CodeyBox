using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies the router takes MIN of (real probe AvailablePct, local budget
/// AvailablePct) when an <see cref="IAgentBudgetProvider"/> is wired.
/// </summary>
public sealed class AgentClassRouterBudgetTests
{
    private static readonly AgentKind Claude = AgentKind.Claude;

    private static AgentClassRouter Build(double probePct, double? budgetPct, double minQuotaPct = 10.0)
    {
        var cls = new AgentClass
        {
            Id = "frontier",
            DisplayName = "Frontier",
            Members = [new AgentMembership { Agent = Claude, Billing = AgentBilling.Subscription, QualityScore = 100 }],
        };
        var opts = new QuotaRouterOptions
        {
            MinQuotaPct = minQuotaPct,
            UnknownPolicy = QuotaUnknownPolicy.FailCautious,
        };
        var budget = budgetPct is { } p ? new FakeBudgetProvider(p) : null;
        return new AgentClassRouter(
            [cls],
            [new FakeProbe(Claude, probePct)],
            opts,
            NullLogger<AgentClassRouter>.Instance,
            budgetProvider: budget);
    }

    private static WorkItem Item() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("proj"),
        Title = "t",
        Prompt = "p",
        AgentClassId = "frontier",
    };

    [Fact]
    public async Task BudgetExhausted_GatesEvenWhenProbeHealthy()
    {
        var router = Build(probePct: 80.0, budgetPct: 5.0);
        var decision = await router.ResolveAsync(Item(), null, CancellationToken.None);

        Assert.Null(decision.Chosen);
        Assert.True(decision.ShouldWait);
    }

    [Fact]
    public async Task ProbeExhausted_GatesEvenWhenBudgetHealthy()
    {
        var router = Build(probePct: 5.0, budgetPct: 80.0);
        var decision = await router.ResolveAsync(Item(), null, CancellationToken.None);

        Assert.Null(decision.Chosen);
        Assert.True(decision.ShouldWait);
    }

    [Fact]
    public async Task BothHealthy_Allows()
    {
        var router = Build(probePct: 80.0, budgetPct: 80.0);
        var decision = await router.ResolveAsync(Item(), null, CancellationToken.None);

        Assert.NotNull(decision.Chosen);
        Assert.Equal(Claude, decision.Chosen!.Agent);
    }

    [Fact]
    public async Task ProbeUnknown_BudgetStandsAlone_Allows()
    {
        // Probe -1 (unknown) + FailCautious would normally deny, but a healthy
        // budget supplies a concrete percentage, so the member is allowed.
        var router = Build(probePct: -1.0, budgetPct: 80.0);
        var decision = await router.ResolveAsync(Item(), null, CancellationToken.None);

        Assert.NotNull(decision.Chosen);
        Assert.Equal(Claude, decision.Chosen!.Agent);
    }

    [Fact]
    public async Task ProbeUnknown_BudgetExhausted_Gates()
    {
        var router = Build(probePct: -1.0, budgetPct: 5.0);
        var decision = await router.ResolveAsync(Item(), null, CancellationToken.None);

        Assert.Null(decision.Chosen);
        Assert.True(decision.ShouldWait);
    }

    [Fact]
    public async Task NoBudgetConfigured_FallsBackToProbeOnly()
    {
        // budgetPct null → FakeBudgetProvider absent; probe 80 governs alone.
        var router = Build(probePct: 80.0, budgetPct: null);
        var decision = await router.ResolveAsync(Item(), null, CancellationToken.None);

        Assert.NotNull(decision.Chosen);
    }

    [Fact]
    public async Task ProviderThrows_FailsClosed_Gates()
    {
        // A throwing budget provider (as opposed to a configured-but-degraded
        // budget, which the provider itself reports as 0%) must fail closed in
        // the router so a transient error does not silently drop the budget gate.
        var cls = new AgentClass
        {
            Id = "frontier",
            DisplayName = "Frontier",
            Members = [new AgentMembership { Agent = Claude, Billing = AgentBilling.Subscription, QualityScore = 100 }],
        };
        var router = new AgentClassRouter(
            [cls],
            [new FakeProbe(Claude, 80.0)],
            new QuotaRouterOptions { MinQuotaPct = 10.0, UnknownPolicy = QuotaUnknownPolicy.FailCautious },
            NullLogger<AgentClassRouter>.Instance,
            budgetProvider: new ThrowingBudgetProvider());

        var decision = await router.ResolveAsync(Item(), null, CancellationToken.None);

        Assert.Null(decision.Chosen);
        Assert.True(decision.ShouldWait);
    }

    [Fact]
    public async Task ProbeThrows_HealthyBudget_FailCautious_FallsBackToBudgetAndAllows()
    {
        // A transient probe exception must be treated as unknown (-1) and still
        // apply MIN(probe, budget): a healthy configured budget supplies a
        // concrete percentage, so dispatch is allowed even though FailCautious
        // would deny a bare unknown. Mirrors the audit-path contract so dispatch
        // and audit behave identically when the subscription probe API blips.
        var cls = new AgentClass
        {
            Id = "frontier",
            DisplayName = "Frontier",
            Members = [new AgentMembership { Agent = Claude, Billing = AgentBilling.Subscription, QualityScore = 100 }],
        };
        var router = new AgentClassRouter(
            [cls],
            [new ThrowingProbe(Claude)],
            new QuotaRouterOptions { MinQuotaPct = 10.0, UnknownPolicy = QuotaUnknownPolicy.FailCautious },
            NullLogger<AgentClassRouter>.Instance,
            budgetProvider: new FakeBudgetProvider(80.0));

        var decision = await router.ResolveAsync(Item(), null, CancellationToken.None);

        Assert.NotNull(decision.Chosen);
        Assert.Equal(Claude, decision.Chosen!.Agent);
    }

    [Fact]
    public async Task ProbeThrows_ExhaustedBudget_Gates()
    {
        // Probe throws (unknown) and the local budget is exhausted: the budget
        // must still gate. A probe blip cannot fail-open the operator spend cap.
        var cls = new AgentClass
        {
            Id = "frontier",
            DisplayName = "Frontier",
            Members = [new AgentMembership { Agent = Claude, Billing = AgentBilling.Subscription, QualityScore = 100 }],
        };
        var router = new AgentClassRouter(
            [cls],
            [new ThrowingProbe(Claude)],
            new QuotaRouterOptions { MinQuotaPct = 10.0, UnknownPolicy = QuotaUnknownPolicy.FailCautious },
            NullLogger<AgentClassRouter>.Instance,
            budgetProvider: new FakeBudgetProvider(5.0));

        var decision = await router.ResolveAsync(Item(), null, CancellationToken.None);

        Assert.Null(decision.Chosen);
        Assert.True(decision.ShouldWait);
    }

    [Theory]
    [InlineData(QuotaUnknownPolicy.FailOpen, true)]
    [InlineData(QuotaUnknownPolicy.FailCautious, false)]
    public async Task FallbackCandidates_ProbeThrows_AppliesUnknownPolicy(
        QuotaUnknownPolicy policy,
        bool expectedAllowed)
    {
        var cls = new AgentClass
        {
            Id = "frontier",
            DisplayName = "Frontier",
            Members = [new AgentMembership { Agent = Claude, Billing = AgentBilling.Subscription, QualityScore = 100 }],
        };
        var router = new AgentClassRouter(
            [cls],
            [new ThrowingProbe(Claude)],
            new QuotaRouterOptions { MinQuotaPct = 10.0, UnknownPolicy = policy },
            NullLogger<AgentClassRouter>.Instance);

        var candidates = await router.OrderedFallbackCandidatesAsync(Item(), null, CancellationToken.None);

        if (expectedAllowed)
        {
            var candidate = Assert.Single(candidates);
            Assert.Equal(Claude, candidate.Agent);
        }
        else
        {
            Assert.Empty(candidates);
        }
    }

    [Fact]
    public async Task FallbackCandidates_BudgetExhausted_DropsCandidate()
    {
        var cls = new AgentClass
        {
            Id = "frontier",
            DisplayName = "Frontier",
            Members = [new AgentMembership { Agent = Claude, Billing = AgentBilling.Subscription, QualityScore = 100 }],
        };
        var router = new AgentClassRouter(
            [cls],
            [new FakeProbe(Claude, 80.0)],
            new QuotaRouterOptions { MinQuotaPct = 10.0, UnknownPolicy = QuotaUnknownPolicy.FailCautious },
            NullLogger<AgentClassRouter>.Instance,
            budgetProvider: new FakeBudgetProvider(5.0));

        var candidates = await router.OrderedFallbackCandidatesAsync(Item(), null, CancellationToken.None);

        Assert.Empty(candidates);
    }

    [Fact]
    public async Task FallbackCandidates_BudgetProviderThrows_DropsCandidate()
    {
        var cls = new AgentClass
        {
            Id = "frontier",
            DisplayName = "Frontier",
            Members = [new AgentMembership { Agent = Claude, Billing = AgentBilling.Subscription, QualityScore = 100 }],
        };
        var router = new AgentClassRouter(
            [cls],
            [new FakeProbe(Claude, 80.0)],
            new QuotaRouterOptions { MinQuotaPct = 10.0, UnknownPolicy = QuotaUnknownPolicy.FailCautious },
            NullLogger<AgentClassRouter>.Instance,
            budgetProvider: new ThrowingBudgetProvider());

        var candidates = await router.OrderedFallbackCandidatesAsync(Item(), null, CancellationToken.None);

        Assert.Empty(candidates);
    }

    [Fact]
    public async Task EarliestExhaustedReset_PrefersEarlierBudgetReset()
    {
        // Both probe and budget are exhausted; the budget's reset is sooner.
        // ApplyBudgetAsync must merge ResetAt to the earlier of the two so the
        // retry scheduler wakes at the soonest opportunity.
        var probeReset = new DateTimeOffset(2026, 5, 29, 15, 0, 0, TimeSpan.Zero);
        var budgetReset = new DateTimeOffset(2026, 5, 29, 14, 0, 0, TimeSpan.Zero);
        var cls = new AgentClass
        {
            Id = "frontier",
            DisplayName = "Frontier",
            Members = [new AgentMembership { Agent = Claude, Billing = AgentBilling.Subscription, QualityScore = 100 }],
        };
        var router = new AgentClassRouter(
            [cls],
            [new FakeProbe(Claude, new AgentQuotaSnapshot { AvailablePct = 5.0, ResetAt = probeReset })],
            new QuotaRouterOptions { MinQuotaPct = 10.0, UnknownPolicy = QuotaUnknownPolicy.FailCautious },
            NullLogger<AgentClassRouter>.Instance,
            budgetProvider: new ResetBudgetProvider(5.0, budgetReset));

        var reset = await router.ComputeEarliestExhaustedResetAsync(Item(), null, CancellationToken.None);

        Assert.Equal(budgetReset, reset);
    }

    [Fact]
    public async Task EarliestExhaustedReset_PrefersEarlierProbeReset()
    {
        // Mirror image of the previous test: the probe reset is sooner, so the
        // merge must keep it rather than the later budget reset. Guards against an
        // inverted comparison in ApplyBudgetAsync's ResetAt merge.
        var probeReset = new DateTimeOffset(2026, 5, 29, 14, 0, 0, TimeSpan.Zero);
        var budgetReset = new DateTimeOffset(2026, 5, 29, 15, 0, 0, TimeSpan.Zero);
        var cls = new AgentClass
        {
            Id = "frontier",
            DisplayName = "Frontier",
            Members = [new AgentMembership { Agent = Claude, Billing = AgentBilling.Subscription, QualityScore = 100 }],
        };
        var router = new AgentClassRouter(
            [cls],
            [new FakeProbe(Claude, new AgentQuotaSnapshot { AvailablePct = 5.0, ResetAt = probeReset })],
            new QuotaRouterOptions { MinQuotaPct = 10.0, UnknownPolicy = QuotaUnknownPolicy.FailCautious },
            NullLogger<AgentClassRouter>.Instance,
            budgetProvider: new ResetBudgetProvider(5.0, budgetReset));

        var reset = await router.ComputeEarliestExhaustedResetAsync(Item(), null, CancellationToken.None);

        Assert.Equal(probeReset, reset);
    }

    private static AgentClassRouter BuildPayPerApiOnly(double? budgetPct, double minQuotaPct = 10.0)
    {
        var cls = new AgentClass
        {
            Id = "frontier",
            DisplayName = "Frontier",
            Members = [new AgentMembership { Agent = Claude, Billing = AgentBilling.PayPerApi, QualityScore = 100 }],
        };
        var budget = budgetPct is { } p ? new FakeBudgetProvider(p) : null;
        return new AgentClassRouter(
            [cls],
            [],
            new QuotaRouterOptions { MinQuotaPct = minQuotaPct, UnknownPolicy = QuotaUnknownPolicy.FailCautious },
            NullLogger<AgentClassRouter>.Instance,
            budgetProvider: budget);
    }

    [Fact]
    public async Task PayPerApiOnly_BudgetExhausted_Waits_DoesNotFire()
    {
        // PayPerApi probes always report 100%, so before budgets the no-Subscription
        // fallthrough fired the first member regardless of quota. With an exhausted
        // operator budget that path would fail-open the spend cap; it must park instead.
        var router = BuildPayPerApiOnly(budgetPct: 5.0);
        var decision = await router.ResolveAsync(Item(), null, CancellationToken.None);

        Assert.Null(decision.Chosen);
        Assert.True(decision.ShouldWait);
    }

    [Fact]
    public async Task PayPerApiOnly_BudgetHealthy_Fires()
    {
        var router = BuildPayPerApiOnly(budgetPct: 80.0);
        var decision = await router.ResolveAsync(Item(), null, CancellationToken.None);

        Assert.NotNull(decision.Chosen);
        Assert.Equal(Claude, decision.Chosen!.Agent);
        Assert.False(decision.ShouldWait);
    }

    [Fact]
    public async Task PayPerApiOnly_NoBudget_FiresAnyway()
    {
        // No budget configured: PayPerApi keeps its 100% probe and fires normally,
        // preserving the legacy fire-anyway behaviour for unmetered classes.
        var router = BuildPayPerApiOnly(budgetPct: null);
        var decision = await router.ResolveAsync(Item(), null, CancellationToken.None);

        Assert.NotNull(decision.Chosen);
        Assert.False(decision.ShouldWait);
    }

    [Fact]
    public async Task PayPerApiOnly_BudgetExhausted_ParksUntilSoonerBudgetReset()
    {
        // When every PayPerApi member is budget-exhausted, the wait should be bounded
        // by the soonest budget reset rather than the (coarser) recheck interval.
        var reset = new DateTimeOffset(2026, 5, 29, 14, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 5, 29, 13, 30, 0, TimeSpan.Zero));
        var cls = new AgentClass
        {
            Id = "frontier",
            DisplayName = "Frontier",
            Members = [new AgentMembership { Agent = Claude, Billing = AgentBilling.PayPerApi, QualityScore = 100 }],
        };
        var router = new AgentClassRouter(
            [cls],
            [],
            new QuotaRouterOptions
            {
                MinQuotaPct = 10.0,
                UnknownPolicy = QuotaUnknownPolicy.FailCautious,
                QuotaRecheckInterval = TimeSpan.FromHours(2),
            },
            NullLogger<AgentClassRouter>.Instance,
            timeProvider: time,
            budgetProvider: new ResetBudgetProvider(5.0, reset));

        var decision = await router.ResolveAsync(Item(), null, CancellationToken.None);

        Assert.Null(decision.Chosen);
        Assert.True(decision.ShouldWait);
        Assert.Equal(TimeSpan.FromMinutes(30), decision.SuggestedRecheckIn);
    }

    private sealed class FakeBudgetProvider : IAgentBudgetProvider
    {
        private readonly double _pct;
        public FakeBudgetProvider(double pct) { _pct = pct; }

        public Task<AgentQuotaSnapshot?> GetBudgetSnapshotAsync(AgentKind agent, string? modelId, CancellationToken ct = default)
            => Task.FromResult<AgentQuotaSnapshot?>(new AgentQuotaSnapshot { AvailablePct = _pct, Notes = "local budget" });

        public Task<IReadOnlyList<AgentBudgetUsageView>> SummariseAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AgentBudgetUsageView>>(Array.Empty<AgentBudgetUsageView>());
    }

    private sealed class ResetBudgetProvider : IAgentBudgetProvider
    {
        private readonly double _pct;
        private readonly DateTimeOffset _resetAt;
        public ResetBudgetProvider(double pct, DateTimeOffset resetAt) { _pct = pct; _resetAt = resetAt; }

        public Task<AgentQuotaSnapshot?> GetBudgetSnapshotAsync(AgentKind agent, string? modelId, CancellationToken ct = default)
            => Task.FromResult<AgentQuotaSnapshot?>(new AgentQuotaSnapshot { AvailablePct = _pct, ResetAt = _resetAt, Notes = "local budget" });

        public Task<IReadOnlyList<AgentBudgetUsageView>> SummariseAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AgentBudgetUsageView>>(Array.Empty<AgentBudgetUsageView>());
    }

    private sealed class ThrowingProbe : IAgentQuotaProbe
    {
        public ThrowingProbe(AgentKind kind) { Kind = kind; }
        public AgentKind Kind { get; }
        public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
            => throw new InvalidOperationException("probe transient error");
    }

    private sealed class ThrowingBudgetProvider : IAgentBudgetProvider
    {
        public Task<AgentQuotaSnapshot?> GetBudgetSnapshotAsync(AgentKind agent, string? modelId, CancellationToken ct = default)
            => throw new InvalidOperationException("budget provider failed");

        public Task<IReadOnlyList<AgentBudgetUsageView>> SummariseAllAsync(CancellationToken ct = default)
            => throw new InvalidOperationException("budget provider failed");
    }
}
