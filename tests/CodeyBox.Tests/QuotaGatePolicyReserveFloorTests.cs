using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Fail-closed-on-unknown is driven by the <em>effective</em> floor in force
/// for the agent at evaluation time (<see
/// cref="QuotaGatePolicy.ComputeFloorPct"/>: global defaults, <c>MinQuotaPct</c>
/// fallback, per-agent <see cref="QuotaRouterOptions.FloorByAgent"/> overrides,
/// and the time-based ramp), not by the presence of an override entry. A
/// non-zero effective floor refuses an unknown reading regardless of <see
/// cref="QuotaUnknownPolicy"/>; an effective floor of zero keeps the existing
/// <see cref="QuotaUnknownPolicy"/> behaviour.
/// </summary>
public sealed class QuotaGatePolicyReserveFloorTests
{
    private static readonly AgentKind Claude = AgentKind.Claude;
    private static readonly AgentKind Codex = AgentKind.Codex;
    private static readonly DateTimeOffset Now =
        new(2026, 7, 3, 12, 0, 0, TimeSpan.Zero);

    private static AgentMembership Member(AgentKind agent) => new()
    {
        Agent = agent,
        Billing = AgentBilling.Subscription,
        QualityScore = 100,
    };

    private static QuotaRouterOptions DefaultOpts() => new()
    {
        MinQuotaPct = 10.0,
        StartFloorPct = 25.0,
        EndFloorPct = 3.0,
        RampWindow = TimeSpan.FromDays(7),
        UnknownPolicy = QuotaUnknownPolicy.UseObservedFailures,
    };

    private static QuotaRouterOptions ZeroFloorOpts(QuotaUnknownPolicy policy) => new()
    {
        MinQuotaPct = 0,
        StartFloorPct = 0,
        EndFloorPct = 0,
        RampWindow = TimeSpan.FromDays(7),
        UnknownPolicy = policy,
    };

    private static EffectiveQuota UnknownQuota(DateTimeOffset? resetAt = null) =>
        new(AvailablePct: -1, ResetAt: resetAt, Window: null);

    // ── Unknown + non-zero effective floor → fail CLOSED ───────────────────

    [Fact]
    public void UnknownQuota_WithExplicitReserveFloor_FailsClosed()
    {
        // Repro of the observed bug: claude has FloorByAgent
        // { StartFloorPct=EndFloorPct=MinQuotaPct=30 } and the probe reports
        // Unknown (availablePct=-1). The default UseObservedFailures policy
        // would fail open (allow), bypassing the 30% reserve. The safety fix
        // fails closed instead, naming the effective floor in the reason.
        var opts = DefaultOpts();
        opts.FloorByAgent[Claude.Value] = new QuotaFloorOverrideOptions
        {
            MinQuotaPct = 30.0,
            StartFloorPct = 30.0,
            EndFloorPct = 30.0,
        };
        var policy = new QuotaGatePolicy(opts);
        var quota = UnknownQuota();

        var decision = policy.Evaluate(Member(Claude), quota, Now, recentObservedFailure: false);

        Assert.False(decision.Allow);
        Assert.Contains("fail-closed", decision.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("30.0%", decision.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(30.0, decision.FloorPct);
    }

    [Fact]
    public void UnknownQuota_WithExplicitReserveFloor_FailsClosedUnderFailOpenPolicy()
    {
        // The effective-floor fail-closed overrides even FailOpen — the
        // operator's intent to reserve headroom must not be bypassed
        // by an unreadable probe regardless of the configured unknown policy.
        var opts = DefaultOpts();
        opts.UnknownPolicy = QuotaUnknownPolicy.FailOpen;
        opts.FloorByAgent[Claude.Value] = new QuotaFloorOverrideOptions
        {
            MinQuotaPct = 30.0,
        };
        var policy = new QuotaGatePolicy(opts);
        var quota = UnknownQuota();

        var decision = policy.Evaluate(Member(Claude), quota, Now, recentObservedFailure: false);

        Assert.False(decision.Allow);
        Assert.Contains("fail-closed", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownQuota_WithExplicitReserveFloor_FailsClosedUnderUseObservedFailuresWithoutRecentFailure()
    {
        // The default UseObservedFailures policy would allow when there is no
        // recent observed failure — but the non-zero effective floor intercepts
        // before that switch and fails closed.
        var opts = DefaultOpts();
        opts.UnknownPolicy = QuotaUnknownPolicy.UseObservedFailures;
        opts.FloorByAgent[Claude.Value] = new QuotaFloorOverrideOptions
        {
            MinQuotaPct = 30.0,
        };
        var policy = new QuotaGatePolicy(opts);
        var quota = UnknownQuota();

        var decision = policy.Evaluate(Member(Claude), quota, Now, recentObservedFailure: false);

        Assert.False(decision.Allow);
    }

    [Fact]
    public void UnknownQuota_WithGlobalDefaultFloorAndNoOverride_FailsClosedUnderFailOpen()
    {
        // No FloorByAgent entry at all — the non-zero global default floor
        // (MinQuotaPct=10, ResetAt unknown so the ramp falls back to it)
        // still protects the reserve under FailOpen.
        var opts = DefaultOpts();
        opts.UnknownPolicy = QuotaUnknownPolicy.FailOpen;
        var policy = new QuotaGatePolicy(opts);
        var quota = UnknownQuota();

        var decision = policy.Evaluate(Member(Claude), quota, Now, recentObservedFailure: false);

        Assert.False(decision.Allow);
        Assert.Contains("10.0%", decision.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(10.0, decision.FloorPct);
    }

    [Fact]
    public void UnknownQuota_GlobalDefaultFloorProtectsEveryAgentWithoutOverride()
    {
        // The global default floor is not per-agent: codex with no override
        // entry is refused exactly like an agent with an override, because
        // the effective floor in force is what matters, not how it was
        // expressed.
        var opts = DefaultOpts();
        opts.UnknownPolicy = QuotaUnknownPolicy.FailOpen;
        opts.FloorByAgent[Claude.Value] = new QuotaFloorOverrideOptions
        {
            MinQuotaPct = 10.0,
            StartFloorPct = 10.0,
            EndFloorPct = 10.0,
        };
        var policy = new QuotaGatePolicy(opts);
        var quota = UnknownQuota();

        var viaOverride = policy.Evaluate(Member(Claude), quota, Now, recentObservedFailure: false);
        var viaGlobal = policy.Evaluate(Member(Codex), quota, Now, recentObservedFailure: false);

        Assert.False(viaOverride.Allow);
        Assert.False(viaGlobal.Allow);
        Assert.Equal(viaOverride.Allow, viaGlobal.Allow);
        Assert.Equal(viaOverride.FloorPct, viaGlobal.FloorPct);
    }

    [Fact]
    public void UnknownQuota_SameFloorAsOverrideOrGlobalDefault_YieldsSameDecision()
    {
        // Expressing the same 30% floor as a per-agent override and as the
        // global default yields the same decision — protection depends on
        // what the floor is, not how it was expressed.
        var viaOverrideOpts = DefaultOpts();
        viaOverrideOpts.UnknownPolicy = QuotaUnknownPolicy.FailOpen;
        viaOverrideOpts.FloorByAgent[Claude.Value] = new QuotaFloorOverrideOptions
        {
            MinQuotaPct = 30.0,
            StartFloorPct = 30.0,
            EndFloorPct = 30.0,
        };
        var viaGlobalOpts = DefaultOpts();
        viaGlobalOpts.UnknownPolicy = QuotaUnknownPolicy.FailOpen;
        viaGlobalOpts.MinQuotaPct = 30.0;
        viaGlobalOpts.StartFloorPct = 30.0;
        viaGlobalOpts.EndFloorPct = 30.0;

        var quota = UnknownQuota();
        var viaOverride = new QuotaGatePolicy(viaOverrideOpts).Evaluate(
            Member(Claude), quota, Now, recentObservedFailure: false);
        var viaGlobal = new QuotaGatePolicy(viaGlobalOpts).Evaluate(
            Member(Claude), quota, Now, recentObservedFailure: false);

        Assert.False(viaOverride.Allow);
        Assert.False(viaGlobal.Allow);
        Assert.Equal(viaOverride.Allow, viaGlobal.Allow);
        Assert.Equal(viaOverride.FloorPct, viaGlobal.FloorPct);
        Assert.Equal(viaOverride.Reason, viaGlobal.Reason);
    }

    [Fact]
    public void UnknownQuota_RampedFloorCurrentlyNonZero_FailsClosed()
    {
        // Start=20, End=0 over 7d with the reset a full window out: the ramp
        // is at its start (20%) right now, so unknown fails closed.
        var opts = DefaultOpts();
        opts.UnknownPolicy = QuotaUnknownPolicy.FailOpen;
        opts.MinQuotaPct = 20.0;
        opts.StartFloorPct = 20.0;
        opts.EndFloorPct = 0.0;
        var policy = new QuotaGatePolicy(opts);
        var quota = UnknownQuota(resetAt: Now + TimeSpan.FromDays(7));

        Assert.Equal(20.0, policy.ComputeEffectiveFloorPct(Claude, quota, Now));

        var decision = policy.Evaluate(Member(Claude), quota, Now, recentObservedFailure: false);

        Assert.False(decision.Allow);
        Assert.Contains("20.0%", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownQuota_RampedFloorAtZero_FallsThroughToUnknownPolicy()
    {
        // Same configuration, evaluated when the window is about to reset:
        // the ramp has reached EndFloorPct=0, so there is no reserve to
        // protect and FailOpen admits.
        var opts = DefaultOpts();
        opts.UnknownPolicy = QuotaUnknownPolicy.FailOpen;
        opts.MinQuotaPct = 20.0;
        opts.StartFloorPct = 20.0;
        opts.EndFloorPct = 0.0;
        var policy = new QuotaGatePolicy(opts);
        var quota = UnknownQuota(resetAt: Now);

        Assert.Equal(0.0, policy.ComputeEffectiveFloorPct(Claude, quota, Now));

        var decision = policy.Evaluate(Member(Claude), quota, Now, recentObservedFailure: false);

        Assert.True(decision.Allow);
        Assert.Contains("fail-open", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // ── Unknown + zero effective floor → existing UnknownPolicy behaviour ───

    [Fact]
    public void UnknownQuota_WithZeroEffectiveFloor_AllowsUnderFailOpen()
    {
        // An effective floor of zero means no reserve to protect: the
        // existing FailOpen behaviour applies.
        var policy = new QuotaGatePolicy(ZeroFloorOpts(QuotaUnknownPolicy.FailOpen));
        var quota = UnknownQuota();

        var decision = policy.Evaluate(Member(Claude), quota, Now, recentObservedFailure: false);

        Assert.True(decision.Allow);
        Assert.Contains("fail-open", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownQuota_WithZeroEffectiveFloor_AllowsUnderUseObservedFailures()
    {
        // No reserve in force → existing UseObservedFailures behaviour:
        // allow when there is no recent observed failure.
        var policy = new QuotaGatePolicy(ZeroFloorOpts(QuotaUnknownPolicy.UseObservedFailures));
        var quota = UnknownQuota();

        var decision = policy.Evaluate(Member(Claude), quota, Now, recentObservedFailure: false);

        Assert.True(decision.Allow);
    }

    [Fact]
    public void UnknownQuota_WithZeroEffectiveFloor_BlocksUnderFailCautious()
    {
        // No reserve in force → existing FailCautious behaviour: block.
        var policy = new QuotaGatePolicy(ZeroFloorOpts(QuotaUnknownPolicy.FailCautious));
        var quota = UnknownQuota();

        var decision = policy.Evaluate(Member(Claude), quota, Now, recentObservedFailure: false);

        Assert.False(decision.Allow);
        Assert.Contains("fail-cautious", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // ── Known readings → floor enforced normally (unchanged) ──────────────────

    [Fact]
    public void KnownQuota_AboveFloor_Allows_EvenWithExplicitReserve()
    {
        // A known reading above the floor still dispatches — the safety fix
        // only intercepts the Unknown branch.
        var opts = DefaultOpts();
        opts.FloorByAgent[Claude.Value] = new QuotaFloorOverrideOptions
        {
            MinQuotaPct = 30.0,
            StartFloorPct = 30.0,
            EndFloorPct = 30.0,
            RampWindow = TimeSpan.FromDays(7),
        };
        var policy = new QuotaGatePolicy(opts);
        var reset = Now + TimeSpan.FromDays(3.5);
        var quota = new EffectiveQuota(AvailablePct: 50.0, ResetAt: reset, Window: null);

        var decision = policy.Evaluate(Member(Claude), quota, Now, recentObservedFailure: false);

        Assert.True(decision.Allow);
    }

    [Fact]
    public void KnownQuota_BelowFloor_Blocks_EvenWithExplicitReserve()
    {
        // A known reading below the floor still blocks — unchanged behaviour.
        var opts = DefaultOpts();
        opts.FloorByAgent[Claude.Value] = new QuotaFloorOverrideOptions
        {
            MinQuotaPct = 30.0,
            StartFloorPct = 30.0,
            EndFloorPct = 30.0,
            RampWindow = TimeSpan.FromDays(7),
        };
        var policy = new QuotaGatePolicy(opts);
        var reset = Now + TimeSpan.FromDays(3.5);
        var quota = new EffectiveQuota(AvailablePct: 5.0, ResetAt: reset, Window: null);

        var decision = policy.Evaluate(Member(Claude), quota, Now, recentObservedFailure: false);

        Assert.False(decision.Allow);
        Assert.Contains("below floor", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // ── Recent observed failure still blocks (top of Evaluate, unchanged) ─────

    [Fact]
    public void UnknownQuota_WithExplicitReserve_AndRecentFailure_Blocks()
    {
        // A recent observed failure blocks at the top of Evaluate regardless of
        // the reserve check — both paths return false, so this is a belt-and-
        // braces pin that the reserve fix doesn't accidentally shadow the
        // observed-failure gate.
        var opts = DefaultOpts();
        opts.FloorByAgent[Claude.Value] = new QuotaFloorOverrideOptions
        {
            MinQuotaPct = 30.0,
        };
        var policy = new QuotaGatePolicy(opts);
        var quota = UnknownQuota();

        var decision = policy.Evaluate(
            Member(Claude), quota, Now,
            recentObservedFailure: true,
            observedFailureReason: "recent observed quota failure");

        Assert.False(decision.Allow);
        Assert.Contains("recent observed", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // ── Transient retains a recent good reading; Permanent discards it ────────

    [Fact]
    public async Task TransientUnknown_WithRecentGoodReadingAboveFloor_Admits()
    {
        // The last-known-good layer retains the 50% reading across a Transient
        // blip, so the gate sees a known 50% ≥ 10% floor and admits — even
        // under FailOpen with a non-zero floor in force.
        var clock = new ManualClock(Now);
        var inner = new StubProbe { Next = new AgentQuotaSnapshot { AvailablePct = 50 } };
        var lkg = new LastKnownGoodQuotaProbe(
            inner,
            () => new LastKnownGoodQuotaOptions { MaxStaleness = TimeSpan.FromMinutes(5) },
            NullLogger<LastKnownGoodQuotaProbe>.Instance,
            clock);
        var member = Member(Claude);
        await lkg.GetAvailabilityAsync(member, CancellationToken.None);

        inner.Next = AgentQuotaSnapshot.UnknownSnapshot(QuotaUnknownReason.Transient, "5xx");
        clock.Advance(TimeSpan.FromMinutes(1));
        var snapshot = await lkg.GetAvailabilityAsync(member, CancellationToken.None);

        Assert.True(snapshot.IsKnown);
        var opts = DefaultOpts();
        opts.UnknownPolicy = QuotaUnknownPolicy.FailOpen;
        var policy = new QuotaGatePolicy(opts);
        var decision = policy.Evaluate(
            member, QuotaGatePolicy.ResolveMemberQuota(snapshot, member), clock.GetUtcNow());

        Assert.True(decision.Allow);
    }

    [Fact]
    public async Task PermanentUnknown_WithSameCachedReading_Refuses()
    {
        // The same 50% cached reading must NOT stand in for a Permanent
        // unknown: the last-known-good layer discards it, the gate sees
        // unknown with a 10% effective floor in force, and refuses — even
        // under FailOpen.
        var clock = new ManualClock(Now);
        var inner = new StubProbe { Next = new AgentQuotaSnapshot { AvailablePct = 50 } };
        var lkg = new LastKnownGoodQuotaProbe(
            inner,
            () => new LastKnownGoodQuotaOptions { MaxStaleness = TimeSpan.FromMinutes(5) },
            NullLogger<LastKnownGoodQuotaProbe>.Instance,
            clock);
        var member = Member(Claude);
        await lkg.GetAvailabilityAsync(member, CancellationToken.None);

        inner.Next = AgentQuotaSnapshot.UnknownSnapshot(QuotaUnknownReason.Permanent, "401");
        clock.Advance(TimeSpan.FromMinutes(1));
        var snapshot = await lkg.GetAvailabilityAsync(member, CancellationToken.None);

        Assert.False(snapshot.IsKnown);
        Assert.Equal(QuotaUnknownReason.Permanent, snapshot.Unknown);
        var opts = DefaultOpts();
        opts.UnknownPolicy = QuotaUnknownPolicy.FailOpen;
        var policy = new QuotaGatePolicy(opts);
        var decision = policy.Evaluate(
            member, QuotaGatePolicy.ResolveMemberQuota(snapshot, member), clock.GetUtcNow());

        Assert.False(decision.Allow);
        Assert.Contains("fail-closed", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class StubProbe : IAgentQuotaProbe
    {
        public AgentKind Kind => AgentKind.Claude;
        public AgentQuotaSnapshot Next { get; set; } = new() { AvailablePct = 100 };

        public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct) =>
            Task.FromResult(Next);
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }
}
