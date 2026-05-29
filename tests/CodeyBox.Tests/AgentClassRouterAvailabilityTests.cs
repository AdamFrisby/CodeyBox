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

    private static AgentMembership Api(AgentKind kind, int score = 100) =>
        new() { Agent = kind, Billing = AgentBilling.PayPerApi, QualityScore = score };

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

        public Task<AgentAvailability> EnsureAvailableAsync(AgentKind kind, string? baselineRef, CancellationToken ct)
        {
            Probed.Add(kind);
            _onProbe(kind);
            return Task.FromResult(_reg.GetAvailability(kind));
        }

        public Task ProbeAllAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<AgentAvailability?> ForceProbeAsync(AgentKind kind, CancellationToken ct)
        {
            Probed.Add(kind);
            _onProbe(kind);
            return Task.FromResult<AgentAvailability?>(_reg.GetAvailability(kind));
        }
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
        // The wait reason must reflect the smoke bench, not falsely claim the
        // members are below the quota threshold (which would misroute operator
        // attention and imply quota recovery, not a smoke sweep / reset).
        Assert.Contains("smoke gate", decision.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("threshold", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // ── PayPerApi-only fallback must honour the smoke gate (AC#1) ─────────────

    [Fact]
    public async Task PayPerApiOnly_AllSmokeBenched_Waits_DoesNotFireBrokenBinary()
    {
        // The PayPerApi-only fallback fires a member "despite apparent low quota"
        // to cover quota-probe inaccuracy — but it must NEVER fire a member the
        // in-VM smoke gate benched: a smoke bench means the binary exits 127 / can't
        // auth, the exact cascade AC#1 exists to prevent. With the sole PayPerApi
        // member benched, the router must wait for the sweep / operator reset, not
        // route to the broken CLI.
        var cls = FrontierClass(Api(Cursor, score: 100));
        var reg = NewRegistry();
        reg.MarkSmokeResult(Cursor,
            new AgentSmokeResult(false, "exit 127", TimeSpan.Zero), SmokeExclusionSource.InVmSmoke);

        var router = BuildRouter(cls, [], reg);

        var decision = await router.ResolveAsync(MakeItem(), project: null, CancellationToken.None);

        Assert.Null(decision.Chosen);
        Assert.True(decision.ShouldWait);
        Assert.Contains("smoke gate", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PayPerApiOnly_HigherScoredBenched_FallbackSkipsItAndFiresNextMember()
    {
        // Reach the PayPerApi-only fallback with a benched higher-scored member and
        // a routable lower-scored one. PayPerApi members never fail the main-loop
        // quota gate, so the lower-scored member is forced to be skipped there via
        // in-process exhaustion (a quota signal the fallback is allowed to override);
        // the fallback must then pick the first NON-benched member
        // (sorted.FirstOrDefault(!smokeExcluded)), not sorted[0] — the benched
        // Cursor. A regression restoring sorted[0] would route to the broken binary
        // and reproduce the exit-127 cascade AC#1 targets.
        var cls = FrontierClass(Api(Cursor, score: 150), Api(Claude, score: 100));
        var reg = NewRegistry();
        reg.MarkSmokeResult(Cursor,
            new AgentSmokeResult(false, "exit 127", TimeSpan.Zero), SmokeExclusionSource.InVmSmoke);

        var router = BuildRouter(cls, [], reg);
        // Quota-exhaust the healthy member so the main loop skips it and the
        // PayPerApi-only fallback block is the path that selects a member.
        router.MarkExhausted(Api(Claude), TimeSpan.FromMinutes(5));

        var decision = await router.ResolveAsync(MakeItem(), project: null, CancellationToken.None);

        Assert.NotNull(decision.Chosen);
        Assert.Equal(Claude, decision.Chosen!.Agent); // not the benched, higher-scored Cursor
        Assert.Equal(AgentBilling.PayPerApi, decision.Chosen.Billing);
        Assert.False(decision.ShouldWait);
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

    // ── Quota-availability snapshot (drives the OTel observable gauge) ────────

    [Fact]
    public async Task SnapshotQuotaAvailability_ReflectsProbedHeadroom_AfterResolve()
    {
        // The codeybox.agent.quota.available_pct gauge reads the IAgentQuotaAvailabilitySnapshot
        // contract. Resolving routes through the probes, which records each
        // member's observed headroom; the snapshot must then surface it without
        // re-probing.
        var cls = FrontierClass(Sub(Cursor, score: 150), Sub(Claude, score: 100));
        var reg = NewRegistry();
        var router = BuildRouter(cls, [new FakeProbe(Cursor, 64.0), new FakeProbe(Claude, 90.0)], reg);

        // Before any probe runs the snapshot is empty.
        Assert.Empty(router.SnapshotQuotaAvailability());

        await router.ResolveAsync(MakeItem(), project: null, CancellationToken.None);

        var snap = router.SnapshotQuotaAvailability();
        // Cursor is highest-quality and at/above the floor, so it is probed first
        // and chosen; its observed headroom is recorded.
        Assert.Contains(snap, s => s.Agent == Cursor && s.ModelId is null && Math.Abs(s.AvailablePct - 64.0) < 1e-9);
    }
}
