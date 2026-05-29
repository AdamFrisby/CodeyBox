using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Tests covering acceptance criteria from cb-216a2230 (missing binary →
/// no exit-127 cascade): the router must skip agents marked unavailable
/// by <see cref="AgentAvailabilityRegistry"/>, both at fresh pickup
/// (<see cref="AgentClassRouter.ResolveAsync"/>) and during mid-iteration
/// fallback (<see cref="AgentClassRouter.OrderedFallbackCandidatesAsync"/>).
/// </summary>
public sealed class AgentClassRouterAvailabilityTests
{
    private static readonly AgentKind Cursor = new("cursor");
    private static readonly AgentKind Claude = AgentKind.Claude;

    private static AgentClassRouter BuildRouter(
        AgentClass cls,
        IEnumerable<IAgentQuotaProbe> probes,
        AgentAvailabilityRegistry registry)
    {
        var opts = new QuotaRouterOptions { MinQuotaPct = 10.0, QuotaRecheckInterval = TimeSpan.FromMinutes(5) };
        return new AgentClassRouter(
            [cls],
            probes,
            opts,
            NullLogger<AgentClassRouter>.Instance,
            timeProvider: null,
            todModifiers: null,
            quotaFailures: null,
            burnEstimator: null,
            runningCounters: null,
            availability: registry);
    }

    private static AgentAvailabilityRegistry NewRegistry()
        => new(new AvailabilityOptions(), TimeProvider.System, NullLogger<AgentAvailabilityRegistry>.Instance);

    private static WorkItem MakeItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("proj"),
        Title = "t",
        Prompt = "p",
        AgentClassId = "frontier",
    };

    private static AgentClass FrontierClass(params AgentMembership[] members) => new()
    {
        Id = "frontier",
        DisplayName = "Frontier",
        Members = members,
    };

    private static AgentMembership Sub(AgentKind kind, int score = 100) =>
        new() { Agent = kind, Billing = AgentBilling.Subscription, QualityScore = score };

    // ── Acceptance criterion 1: missing binary → smoke fails → excluded ──────

    [Fact]
    public async Task SmokeFailedAgent_ExcludedFromResolve_FallsBackToNext()
    {
        // Cursor has high quality score (would normally win), but its smoke
        // probe failed (binary missing) so the router must skip it and pick
        // the next member (Claude).
        var cls = FrontierClass(Sub(Cursor, score: 150), Sub(Claude, score: 100));
        var reg = NewRegistry();
        reg.MarkSmokeResult(Cursor, new AgentSmokeResult(false, "binary not found", TimeSpan.Zero));

        var router = BuildRouter(cls,
            [new FakeProbe(Cursor, 90.0), new FakeProbe(Claude, 90.0)],
            reg);

        var decision = await router.ResolveAsync(MakeItem(), project: null, CancellationToken.None);

        Assert.NotNull(decision.Chosen);
        Assert.Equal(Claude, decision.Chosen!.Agent);
        Assert.False(decision.ShouldWait);
    }

    [Fact]
    public async Task SmokeFailedAgent_ExcludedFromFallbackCandidates()
    {
        var cls = FrontierClass(Sub(Cursor, score: 150), Sub(Claude, score: 100));
        var reg = NewRegistry();
        reg.MarkSmokeResult(Cursor, new AgentSmokeResult(false, "binary not found", TimeSpan.Zero));

        var router = BuildRouter(cls, [new FakeProbe(Cursor, 90.0), new FakeProbe(Claude, 90.0)], reg);

        var candidates = await router.OrderedFallbackCandidatesAsync(MakeItem(), null, CancellationToken.None);
        Assert.Single(candidates);
        Assert.Equal(Claude, candidates[0].Agent);
    }

    [Fact]
    public async Task FallbackCandidates_ApplyInVmGate_AndDropAgentBenchedByFirstProbe()
    {
        // The fallback path (mid-iteration quota / audit / rebase reroute) must
        // apply the same in-VM smoke gate as ResolveAsync: a member that looks
        // Available only because it was never probed must be gated on first
        // selection, and dropped if that probe benches it (exit 127 / auth drift).
        var cls = FrontierClass(Sub(Cursor, score: 150), Sub(Claude, score: 100));
        var reg = NewRegistry();
        var gate = new FakeInVmSmokeGate(reg, kind =>
        {
            if (kind == Cursor)
                reg.MarkSmokeResult(Cursor,
                    new AgentSmokeResult(false, "exit 127", TimeSpan.Zero), SmokeExclusionSource.InVmSmoke);
        });
        var router = new AgentClassRouter(
            [cls],
            [new FakeProbe(Cursor, 90.0), new FakeProbe(Claude, 90.0)],
            new QuotaRouterOptions { MinQuotaPct = 10.0 },
            NullLogger<AgentClassRouter>.Instance,
            availability: reg,
            inVmSmokeGate: gate);

        var candidates = await router.OrderedFallbackCandidatesAsync(MakeItem(), null, CancellationToken.None);

        Assert.Contains(Cursor, gate.Probed); // the gate was actually invoked
        Assert.Single(candidates);
        Assert.Equal(Claude, candidates[0].Agent);
    }

    private sealed class FakeInVmSmokeGate : IInVmSmokeGate
    {
        private readonly AgentAvailabilityRegistry _reg;
        private readonly Action<AgentKind> _onProbe;
        public List<AgentKind> Probed { get; } = [];
        public FakeInVmSmokeGate(AgentAvailabilityRegistry reg, Action<AgentKind> onProbe)
        {
            _reg = reg;
            _onProbe = onProbe;
        }

        public bool Enabled => true;

        public Task<AgentAvailability> EnsureAvailableAsync(AgentKind kind, CancellationToken ct)
        {
            Probed.Add(kind);
            _onProbe(kind);
            return Task.FromResult(_reg.GetAvailability(kind));
        }

        public Task ProbeAllAsync(CancellationToken ct) => Task.CompletedTask;
    }

    // ── Acceptance criterion 4: smoke-pass but fast-fail-127 also excludes ──

    [Fact]
    public async Task FastFailedAgent_ExcludedFromResolve_AfterThreeConsecutive()
    {
        // The recorded scenario from the bug report: Cursor's smoke probe is
        // over-permissive (it only checks credential presence) and passes
        // even when the binary is missing. The first three pickups each
        // exit 127 in <1s; the breaker excludes Cursor from the fourth onward.
        var cls = FrontierClass(Sub(Cursor, score: 150), Sub(Claude, score: 100));
        var reg = NewRegistry();
        for (var i = 0; i < 3; i++)
            reg.RecordRunOutcome(Cursor, success: false, duration: TimeSpan.FromMilliseconds(500));

        var router = BuildRouter(cls, [new FakeProbe(Cursor, 90.0), new FakeProbe(Claude, 90.0)], reg);

        var decision = await router.ResolveAsync(MakeItem(), project: null, CancellationToken.None);

        Assert.NotNull(decision.Chosen);
        Assert.Equal(Claude, decision.Chosen!.Agent);
    }

    [Fact]
    public async Task FastFailedAgent_ResetAllowsRouting()
    {
        // After the operator installs the missing binary and calls
        // /admin/agent/cursor/reset, Cursor must rejoin the chain immediately.
        var cls = FrontierClass(Sub(Cursor, score: 150), Sub(Claude, score: 100));
        var reg = NewRegistry();
        for (var i = 0; i < 3; i++)
            reg.RecordRunOutcome(Cursor, success: false, duration: TimeSpan.FromMilliseconds(500));

        reg.Reset(Cursor);

        var router = BuildRouter(cls, [new FakeProbe(Cursor, 90.0), new FakeProbe(Claude, 90.0)], reg);
        var decision = await router.ResolveAsync(MakeItem(), project: null, CancellationToken.None);

        Assert.NotNull(decision.Chosen);
        // Cursor's quality is higher and breaker is cleared, so Cursor wins again.
        Assert.Equal(Cursor, decision.Chosen!.Agent);
    }

    [Fact]
    public async Task AllAgentsExcluded_RoutingFails()
    {
        // When every member is excluded the router falls through "no eligible"
        // because the only members are subscription. ShouldWait would be the
        // legitimate response if the exclusions were quota-shaped, but smoke /
        // fast-fail exclusions are NOT quota — operator intervention is needed.
        var cls = FrontierClass(Sub(Cursor, score: 100), Sub(Claude, score: 100));
        var reg = NewRegistry();
        reg.MarkSmokeResult(Cursor, new AgentSmokeResult(false, "x", TimeSpan.Zero));
        reg.MarkSmokeResult(Claude, new AgentSmokeResult(false, "y", TimeSpan.Zero));

        var router = BuildRouter(cls, [new FakeProbe(Cursor, 90.0), new FakeProbe(Claude, 90.0)], reg);

        var decision = await router.ResolveAsync(MakeItem(), project: null, CancellationToken.None);

        // With no quota-eligible-subscription member found and exclusions
        // counted as rejections, the router signals "wait and retry later" —
        // the periodic sweep + operator reset is what unblocks it.
        Assert.Null(decision.Chosen);
        Assert.True(decision.ShouldWait);
    }

    // ── Recovery path ────────────────────────────────────────────────────────

    [Fact]
    public async Task AfterSmokeRecovers_AgentRejoinsRouting()
    {
        var cls = FrontierClass(Sub(Cursor, score: 150), Sub(Claude, score: 100));
        var reg = NewRegistry();
        reg.MarkSmokeResult(Cursor, new AgentSmokeResult(false, "binary not found", TimeSpan.Zero));
        // PeriodicSmokeProbeService later finds the binary present.
        reg.MarkSmokeResult(Cursor, new AgentSmokeResult(true, null, TimeSpan.Zero));

        var router = BuildRouter(cls, [new FakeProbe(Cursor, 90.0), new FakeProbe(Claude, 90.0)], reg);

        var decision = await router.ResolveAsync(MakeItem(), project: null, CancellationToken.None);
        Assert.Equal(Cursor, decision.Chosen!.Agent);
    }
}
