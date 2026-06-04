using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for the rate-aware budget gate in <see cref="AgentClassRouter"/>.
/// Combines a programmable burn estimator + running-counters fake with the
/// existing FakeProbe so the test can dial AvailablePct, the running count,
/// and the historical avg burn independently.
/// </summary>
public sealed class AgentClassRouterRateAwareTests
{
    private static readonly AgentKind Codex = AgentKind.Codex;
    private static readonly AgentKind Claude = AgentKind.Claude;

    private static WorkItem MakeItem(string classId) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("proj"),
        Title = "t",
        Prompt = "p",
        AgentClassId = classId,
    };

    private static AgentMembership Sub(AgentKind kind) =>
        new() { Agent = kind, Billing = AgentBilling.Subscription, QualityScore = 100 };

    private static AgentClass FrontierClass(params AgentMembership[] members) => new()
    {
        Id = "frontier",
        DisplayName = "Frontier",
        Members = members,
    };

    private static AgentClassRouter BuildRouter(
        IReadOnlyList<AgentClass> catalog,
        IEnumerable<IAgentQuotaProbe> probes,
        IAgentBurnEstimator burnEstimator,
        IAgentRunningCounters runningCounters)
    {
        var opts = new QuotaRouterOptions { MinQuotaPct = 5.0, QuotaRecheckInterval = TimeSpan.FromMinutes(5) };
        return new AgentClassRouter(
            catalog, probes, opts,
            NullLogger<AgentClassRouter>.Instance,
            TimeProvider.System,
            todModifiers: null,
            quotaFailures: null,
            burnEstimator: burnEstimator,
            runningCounters: runningCounters);
    }

    // ── Scenario from the spec: first dispatch allowed; second skipped ────────

    [Fact]
    public async Task ColdStart_NoSamples_FallsBackToFitTwo_AllowsFirstAndSecondDispatch()
    {
        var cls = FrontierClass(Sub(Codex));
        var counters = new FakeCounters();
        var estimator = new FakeBurnEstimator(); // SampleCount=0 for codex → fit=2
        var router = BuildRouter([cls], [new FakeProbe(Codex, 100.0)], estimator, counters);

        // Cold start: nothing running, no samples → first dispatch greenlit.
        var d1 = await router.ResolveAsync(MakeItem("frontier"), null, CancellationToken.None);
        Assert.Equal(Codex, d1.Chosen!.Agent);

        // Simulate the orchestrator booking the slot.
        counters.Increment(Codex);

        // 1 in-flight, fit=2 → second dispatch still allowed.
        var d2 = await router.ResolveAsync(MakeItem("frontier"), null, CancellationToken.None);
        Assert.Equal(Codex, d2.Chosen!.Agent);

        counters.Increment(Codex);

        // 2 in-flight, fit=2 → gate fires.
        var d3 = await router.ResolveAsync(MakeItem("frontier"), null, CancellationToken.None);
        Assert.Null(d3.Chosen);
        Assert.True(d3.ShouldWait);
    }

    [Fact]
    public async Task RateAware_AvgBurn90Pct_GatesSecondDispatchOnTenPercentAvailable()
    {
        var cls = FrontierClass(Sub(Codex));
        var counters = new FakeCounters();
        // Sample data: avg burn = 90% per item, with samples.
        var estimator = new FakeBurnEstimator
        {
            EstimatesByAgent =
            {
                [Codex] = new AgentBurnEstimate { AvgBurnPctPerItem = 90.0, SampleCount = 10 }
            }
        };
        // Probe: 100% available initially, then dialed down to 10% after the first dispatch.
        var probe = new MutableProbe(Codex, 100.0);
        var router = BuildRouter([cls], [probe], estimator, counters);

        // 0 in-flight, available=100%, fit=100/90 ≈ 1.11 → dispatch allowed.
        var d1 = await router.ResolveAsync(MakeItem("frontier"), null, CancellationToken.None);
        Assert.Equal(Codex, d1.Chosen!.Agent);

        // Operator semantics: one codex is now in flight, window dropped to 10%.
        counters.Increment(Codex);
        probe.AvailablePct = 10.0;

        // 1 in-flight, fit = 10/90 ≈ 0.11 → 1 >= 0.11 → gated.
        var d2 = await router.ResolveAsync(MakeItem("frontier"), null, CancellationToken.None);
        Assert.Null(d2.Chosen);
        Assert.True(d2.ShouldWait);
    }

    [Fact]
    public async Task RateAware_NoWindowBudgetWithSamples_FailsOpenInsteadOfColdStartFit()
    {
        var cls = FrontierClass(Sub(Codex));
        var counters = new FakeCounters();
        counters.Increment(Codex);
        counters.Increment(Codex);
        counters.Increment(Codex);

        var estimator = new FakeBurnEstimator
        {
            EstimatesByAgent =
            {
                [Codex] = new AgentBurnEstimate
                {
                    AvgBurnPctPerItem = -1,
                    SampleCount = 10,
                    Status = AgentBurnEstimateStatus.NoWindowBudget,
                },
            }
        };
        var router = BuildRouter([cls], [new FakeProbe(Codex, 81.0)], estimator, counters);

        var decision = await router.ResolveAsync(MakeItem("frontier"), null, CancellationToken.None);

        Assert.Equal(Codex, decision.Chosen!.Agent);
        Assert.False(decision.ShouldWait);
    }

    [Fact]
    public async Task RateAware_BlockedCodex_FallsThroughToClaude()
    {
        // Codex is in-flight and rate-aware gated; claude is freely available.
        // The router should pick claude per the existing class-fallback path,
        // matching the spec's "rate-aware integration with mid-iteration fallback".
        var cls = FrontierClass(Sub(Codex), Sub(Claude));
        var counters = new FakeCounters();
        counters.Increment(Codex); // 1 codex already running

        var estimator = new FakeBurnEstimator
        {
            EstimatesByAgent =
            {
                [Codex]  = new AgentBurnEstimate { AvgBurnPctPerItem = 90.0, SampleCount = 10 },
                [Claude] = new AgentBurnEstimate { AvgBurnPctPerItem = 4.0,  SampleCount = 10 },
            }
        };
        var probes = new IAgentQuotaProbe[]
        {
            new FakeProbe(Codex, 10.0),    // available but rate-gated (1 >= 10/90)
            new FakeProbe(Claude, 100.0),  // fully available
        };
        var router = BuildRouter([cls], probes, estimator, counters);

        // Equal QualityScore but codex appears first in config — the router
        // tries codex first, gates by rate, then falls through to claude.
        var decision = await router.ResolveAsync(MakeItem("frontier"), null, CancellationToken.None);
        Assert.Equal(Claude, decision.Chosen!.Agent);
        Assert.False(decision.ShouldWait);
    }

    [Fact]
    public async Task RateAware_PayPerApiMember_IsNotGatedRegardlessOfRunningCount()
    {
        // Spec calls out the PayPerApi exemption: pay-per-API has no window to
        // overrun, so the rate-aware gate must let it through even when the
        // running count would otherwise trigger the gate.
        var payPerApi = new AgentMembership
        { Agent = Codex, Billing = AgentBilling.PayPerApi, QualityScore = 100 };
        var cls = FrontierClass(payPerApi);

        var counters = new FakeCounters();
        // Pile up running counters far above what would gate a Subscription member.
        for (var i = 0; i < 50; i++) counters.Increment(Codex);

        var estimator = new FakeBurnEstimator
        {
            EstimatesByAgent =
            {
                [Codex] = new AgentBurnEstimate { AvgBurnPctPerItem = 90.0, SampleCount = 10 },
            }
        };
        var router = BuildRouter([cls], [new FakeProbe(Codex, 10.0)], estimator, counters);

        var decision = await router.ResolveAsync(MakeItem("frontier"), null, CancellationToken.None);

        Assert.Equal(Codex, decision.Chosen!.Agent);
        Assert.False(decision.ShouldWait);
    }

    [Fact]
    public async Task RateAware_BurnEstimatorThrows_FallsBackToColdStartFitAndAllowsDispatch()
    {
        // A failing estimator must not lock the dispatcher out. The gate's
        // catch-and-default path keeps fit=ColdStart so first/second dispatches
        // still go through.
        var cls = FrontierClass(Sub(Codex));
        var counters = new FakeCounters();
        var estimator = new ThrowingBurnEstimator();
        var router = BuildRouter([cls], [new FakeProbe(Codex, 100.0)], estimator, counters);

        var d1 = await router.ResolveAsync(MakeItem("frontier"), null, CancellationToken.None);
        Assert.Equal(Codex, d1.Chosen!.Agent);
        counters.Increment(Codex);

        var d2 = await router.ResolveAsync(MakeItem("frontier"), null, CancellationToken.None);
        Assert.Equal(Codex, d2.Chosen!.Agent);
    }

    [Fact]
    public async Task SummariseFitsAsync_UnknownClass_ReturnsEmpty()
    {
        var cls = FrontierClass(Sub(Codex));
        var counters = new FakeCounters();
        var estimator = new FakeBurnEstimator();
        var router = BuildRouter([cls], [new FakeProbe(Codex, 100.0)], estimator, counters);

        var fits = await router.SummariseFitsAsync("does-not-exist");

        Assert.Empty(fits);
    }

    [Fact]
    public async Task SummariseFitsAsync_SkipsPayPerApiMembers()
    {
        // PayPerApi members are not rate-gated, so they should not appear in
        // the fits summary.
        var cls = new AgentClass
        {
            Id = "mixed",
            DisplayName = "mixed",
            Members =
            [
                new AgentMembership { Agent = Codex, Billing = AgentBilling.Subscription, QualityScore = 100 },
                new AgentMembership { Agent = Claude, Billing = AgentBilling.PayPerApi, QualityScore = 100 },
            ],
        };
        var counters = new FakeCounters();
        var estimator = new FakeBurnEstimator();
        var router = BuildRouter(
            [cls],
            [new FakeProbe(Codex, 100.0), new FakeProbe(Claude, 100.0)],
            estimator, counters);

        var fits = await router.SummariseFitsAsync("mixed");

        Assert.Single(fits);
        Assert.Equal(Codex, fits[0].Agent);
    }

    [Fact]
    public async Task SummariseFitsAsync_ColdStartUsesDefaultFit_AndIncludesRunningCount()
    {
        var cls = FrontierClass(Sub(Codex));
        var counters = new FakeCounters();
        counters.Increment(Codex);
        var estimator = new FakeBurnEstimator(); // no samples
        var router = BuildRouter([cls], [new FakeProbe(Codex, 100.0)], estimator, counters);

        var fits = await router.SummariseFitsAsync("frontier");

        var view = Assert.Single(fits);
        Assert.Equal("frontier", view.ClassId);
        Assert.Equal(Codex, view.Agent);
        Assert.Equal(AgentClassRouter.DefaultColdStartFitInWindow, view.FitInWindow);
        Assert.Equal(0, view.SampleCount);
        Assert.Equal(1, view.RunningOnAgent);
    }

    [Fact]
    public async Task SummariseFitsAsync_WithSamples_ReturnsEmpiricalFit()
    {
        var cls = FrontierClass(Sub(Codex));
        var counters = new FakeCounters();
        var estimator = new FakeBurnEstimator
        {
            EstimatesByAgent =
            {
                [Codex] = new AgentBurnEstimate { AvgBurnPctPerItem = 25.0, SampleCount = 8 },
            }
        };
        var router = BuildRouter([cls], [new FakeProbe(Codex, 100.0)], estimator, counters);

        var fits = await router.SummariseFitsAsync("frontier");

        var view = Assert.Single(fits);
        Assert.Equal(4.0, view.FitInWindow); // 100 / 25
        Assert.Equal(8, view.SampleCount);
        Assert.Equal(25.0, view.AvgBurnPctPerItem);
    }

    [Fact]
    public async Task SummariseFitsAsync_NoWindowBudgetWithSamples_ReportsUnboundedFit()
    {
        var cls = FrontierClass(Sub(Codex));
        var counters = new FakeCounters();
        var estimator = new FakeBurnEstimator
        {
            EstimatesByAgent =
            {
                [Codex] = new AgentBurnEstimate
                {
                    AvgBurnPctPerItem = -1,
                    SampleCount = 10,
                    Status = AgentBurnEstimateStatus.NoWindowBudget,
                },
            }
        };
        var router = BuildRouter([cls], [new FakeProbe(Codex, 81.0)], estimator, counters);

        var fits = await router.SummariseFitsAsync("frontier");

        var view = Assert.Single(fits);
        Assert.Equal(10, view.SampleCount);
        Assert.Equal(AgentBurnEstimateStatus.NoWindowBudget, view.BurnEstimateStatus);
        Assert.True(double.IsPositiveInfinity(view.FitInWindow));
    }

    [Fact]
    public async Task SummariseFitsAsync_UnknownAvailability_ReturnsNaNFit()
    {
        // Probe returned AvailablePct = -1 (unknown). With samples present,
        // the divisor is positive but there's no meaningful availability — so
        // the surface uses double.NaN as the sentinel.
        var cls = FrontierClass(Sub(Codex));
        var counters = new FakeCounters();
        var estimator = new FakeBurnEstimator
        {
            EstimatesByAgent =
            {
                [Codex] = new AgentBurnEstimate { AvgBurnPctPerItem = 25.0, SampleCount = 8 },
            }
        };
        var router = BuildRouter([cls], [new FakeProbe(Codex, -1.0)], estimator, counters);

        var fits = await router.SummariseFitsAsync("frontier");

        var view = Assert.Single(fits);
        Assert.True(double.IsNaN(view.FitInWindow));
    }

    [Fact]
    public async Task SummariseFitsAsync_ProbeThrows_OmitsMember()
    {
        // SummariseFitsAsync swallows probe exceptions so the /concurrency
        // endpoint doesn't 5xx on a flaky probe; the affected member is
        // silently absent from the response.
        var cls = FrontierClass(Sub(Codex), Sub(Claude));
        var counters = new FakeCounters();
        var estimator = new FakeBurnEstimator();
        var router = BuildRouter(
            [cls],
            [new ThrowingProbe(Codex), new FakeProbe(Claude, 100.0)],
            estimator, counters);

        var fits = await router.SummariseFitsAsync("frontier");

        Assert.Single(fits);
        Assert.Equal(Claude, fits[0].Agent);
    }

    [Fact]
    public async Task SummariseFitsAsync_BurnEstimatorThrows_ReportsSampleSourceUnavailable()
    {
        var cls = FrontierClass(Sub(Codex));
        var counters = new FakeCounters();
        var router = BuildRouter(
            [cls],
            [new FakeProbe(Codex, 100.0)],
            new ThrowingBurnEstimator(),
            counters);

        var fits = await router.SummariseFitsAsync("frontier");

        var view = Assert.Single(fits);
        Assert.Equal(AgentBurnEstimateStatus.SampleSourceUnavailable, view.BurnEstimateStatus);
        Assert.Equal(0, view.SampleCount);
        Assert.Equal(-1, view.AvgBurnPctPerItem);
        Assert.Equal(AgentClassRouter.DefaultColdStartFitInWindow, view.FitInWindow);
    }

    [Fact]
    public async Task SummariseFitsAsync_NoBurnEstimator_ReturnsEmpty()
    {
        // /concurrency must not crash on a router with no burn estimator wired.
        var cls = FrontierClass(Sub(Codex));
        var opts = new QuotaRouterOptions { MinQuotaPct = 5.0 };
        var router = new AgentClassRouter(
            [cls], [new FakeProbe(Codex, 100.0)], opts,
            NullLogger<AgentClassRouter>.Instance);

        var fits = await router.SummariseFitsAsync("frontier");

        Assert.Empty(fits);
    }

    [Fact]
    public async Task RateAware_NoEstimator_PreservesLegacyBehaviour()
    {
        // No burn estimator and no running counters → rate-aware gate is silent.
        // Even with high in-flight counts, the router greenlights as before.
        var cls = FrontierClass(Sub(Codex));
        var opts = new QuotaRouterOptions { MinQuotaPct = 10.0, QuotaRecheckInterval = TimeSpan.FromMinutes(5) };
        var router = new AgentClassRouter(
            [cls], [new FakeProbe(Codex, 50.0)], opts,
            NullLogger<AgentClassRouter>.Instance);

        var decision = await router.ResolveAsync(MakeItem("frontier"), null, CancellationToken.None);
        Assert.Equal(Codex, decision.Chosen!.Agent);
    }

    [Fact]
    public async Task SummariseFitsAsync_TighterBudget_LowersAvailablePctAndFit()
    {
        // /concurrency surface must reflect the local budget: SummariseFitsAsync
        // calls ApplyBudgetAsync before computing AvailablePct and FitInWindow, so
        // a budget tighter than the probe lowers both. Without that call this test
        // would see the full probe AvailablePct and a higher fit.
        var cls = FrontierClass(Sub(Codex));
        var counters = new FakeCounters();
        var estimator = new FakeBurnEstimator
        {
            EstimatesByAgent =
            {
                [Codex] = new AgentBurnEstimate { AvgBurnPctPerItem = 25.0, SampleCount = 8 },
            }
        };
        var opts = new QuotaRouterOptions { MinQuotaPct = 5.0 };
        var router = new AgentClassRouter(
            [cls], [new FakeProbe(Codex, 100.0)], opts,
            NullLogger<AgentClassRouter>.Instance,
            TimeProvider.System,
            todModifiers: null,
            quotaFailures: null,
            burnEstimator: estimator,
            runningCounters: counters,
            availability: null,
            budgetProvider: new StubBudgetProvider(20.0));

        var fits = await router.SummariseFitsAsync("frontier");

        var view = Assert.Single(fits);
        // Probe 100% MIN budget 20% = 20%; fit = 20 / 25 = 0.8.
        Assert.Equal(20.0, view.AvailablePct, precision: 6);
        Assert.Equal(0.8, view.FitInWindow, precision: 6);
    }

    private sealed class StubBudgetProvider : IAgentBudgetProvider
    {
        private readonly double _pct;
        public StubBudgetProvider(double pct) { _pct = pct; }

        public Task<AgentQuotaSnapshot?> GetBudgetSnapshotAsync(AgentKind agent, string? modelId, CancellationToken ct = default)
            => Task.FromResult<AgentQuotaSnapshot?>(new AgentQuotaSnapshot { AvailablePct = _pct, Notes = "local budget" });

        public Task<IReadOnlyList<AgentBudgetUsageView>> SummariseAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AgentBudgetUsageView>>(Array.Empty<AgentBudgetUsageView>());
    }
}

/// <summary>Programmable in-process running counters for router tests.</summary>
internal sealed class FakeCounters : IAgentRunningCounters
{
    private readonly Dictionary<AgentKind, int> _counts = new();

    public void Increment(AgentKind agent) =>
        _counts[agent] = _counts.GetValueOrDefault(agent) + 1;

    public void Decrement(AgentKind agent)
    {
        if (_counts.TryGetValue(agent, out var n) && n > 0) _counts[agent] = n - 1;
    }

    public int GetRunning(AgentKind agent) => _counts.GetValueOrDefault(agent);

    public IReadOnlyDictionary<AgentKind, int> Snapshot() =>
        _counts.Where(kv => kv.Value > 0).ToDictionary(kv => kv.Key, kv => kv.Value);
}

/// <summary>Programmable burn estimator for router tests.</summary>
internal sealed class FakeBurnEstimator : IAgentBurnEstimator
{
    public Dictionary<AgentKind, AgentBurnEstimate> EstimatesByAgent { get; } = new();

    public Task<AgentBurnEstimate> GetEstimateAsync(AgentKind agent, CancellationToken ct = default)
    {
        if (EstimatesByAgent.TryGetValue(agent, out var est)) return Task.FromResult(est);
        // No samples → router uses cold-start fit = 2.
        return Task.FromResult(new AgentBurnEstimate { AvgBurnPctPerItem = -1, SampleCount = 0 });
    }
}

/// <summary>Burn estimator that throws on every call — used by the
/// rate-aware gate fallback tests.</summary>
internal sealed class ThrowingBurnEstimator : IAgentBurnEstimator
{
    public Task<AgentBurnEstimate> GetEstimateAsync(AgentKind agent, CancellationToken ct = default) =>
        throw new InvalidOperationException("burn estimator failure (simulated)");
}

/// <summary>Probe that throws on every probe — used by SummariseFitsAsync
/// resiliency tests.</summary>
internal sealed class ThrowingProbe : IAgentQuotaProbe
{
    public ThrowingProbe(AgentKind kind) { Kind = kind; }
    public AgentKind Kind { get; }
    public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct) =>
        throw new InvalidOperationException("probe failure (simulated)");
}

/// <summary>Probe whose AvailablePct can be re-assigned between dispatches.</summary>
internal sealed class MutableProbe : IAgentQuotaProbe
{
    public MutableProbe(AgentKind kind, double availablePct)
    {
        Kind = kind;
        AvailablePct = availablePct;
    }

    public AgentKind Kind { get; }
    public double AvailablePct { get; set; }

    public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct) =>
        Task.FromResult(new AgentQuotaSnapshot { AvailablePct = AvailablePct });
}
