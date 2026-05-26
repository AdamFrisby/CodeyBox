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
