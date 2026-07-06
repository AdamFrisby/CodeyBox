using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Safety fix for the reserve-bypass-on-Unknown defect: when a quota probe
/// cannot produce a reading (<c>AvailablePct &lt; 0</c>) AND the agent has an
/// EXPLICIT per-agent reserve floor configured via
/// <see cref="QuotaRouterOptions.FloorByAgent"/>, the gate must fail CLOSED to
/// protect the reserve rather than letting <see cref="QuotaUnknownPolicy"/>
/// fail open and dispatch below the reserve. Agents WITHOUT an explicit
/// reserve keep the existing UnknownPolicy behaviour.
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

    // ── Unknown + explicit reserve floor → fail CLOSED ───────────────────────

    [Fact]
    public void UnknownQuota_WithExplicitReserveFloor_FailsClosed()
    {
        // Repro of the observed bug: claude has FloorByAgent
        // { StartFloorPct=EndFloorPct=MinQuotaPct=30 } and the probe reports
        // Unknown (availablePct=-1). The default UseObservedFailures policy
        // would fail open (allow), bypassing the 30% reserve. The safety fix
        // fails closed instead.
        var opts = DefaultOpts();
        opts.FloorByAgent[Claude.Value] = new QuotaFloorOverrideOptions
        {
            MinQuotaPct = 30.0,
            StartFloorPct = 30.0,
            EndFloorPct = 30.0,
        };
        var policy = new QuotaGatePolicy(opts);
        var quota = new EffectiveQuota(AvailablePct: -1, ResetAt: null, Window: null);

        var decision = policy.Evaluate(Member(Claude), quota, Now, recentObservedFailure: false);

        Assert.False(decision.Allow);
        Assert.Contains("explicit reserve floor", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownQuota_WithExplicitReserveFloor_FailsClosedUnderFailOpenPolicy()
    {
        // The explicit-reserve fail-closed overrides even FailOpen — the
        // operator's explicit intent to reserve headroom must not be bypassed
        // by an unreadable probe regardless of the configured unknown policy.
        var opts = DefaultOpts();
        opts.UnknownPolicy = QuotaUnknownPolicy.FailOpen;
        opts.FloorByAgent[Claude.Value] = new QuotaFloorOverrideOptions
        {
            MinQuotaPct = 30.0,
        };
        var policy = new QuotaGatePolicy(opts);
        var quota = new EffectiveQuota(AvailablePct: -1, ResetAt: null, Window: null);

        var decision = policy.Evaluate(Member(Claude), quota, Now, recentObservedFailure: false);

        Assert.False(decision.Allow);
    }

    [Fact]
    public void UnknownQuota_WithExplicitReserveFloor_FailsClosedUnderUseObservedFailuresWithoutRecentFailure()
    {
        // The default UseObservedFailures policy would allow when there is no
        // recent observed failure — but the explicit reserve floor intercepts
        // before that switch and fails closed.
        var opts = DefaultOpts();
        opts.UnknownPolicy = QuotaUnknownPolicy.UseObservedFailures;
        opts.FloorByAgent[Claude.Value] = new QuotaFloorOverrideOptions
        {
            MinQuotaPct = 30.0,
        };
        var policy = new QuotaGatePolicy(opts);
        var quota = new EffectiveQuota(AvailablePct: -1, ResetAt: null, Window: null);

        var decision = policy.Evaluate(Member(Claude), quota, Now, recentObservedFailure: false);

        Assert.False(decision.Allow);
    }

    // ── Unknown + NO explicit reserve → existing UnknownPolicy behaviour ──────

    [Fact]
    public void UnknownQuota_WithoutExplicitReserve_AllowsUnderUseObservedFailures()
    {
        // No FloorByAgent override → existing UseObservedFailures behaviour:
        // allow when there is no recent observed failure.
        var opts = DefaultOpts();
        // FloorByAgent deliberately NOT configured for Claude.
        var policy = new QuotaGatePolicy(opts);
        var quota = new EffectiveQuota(AvailablePct: -1, ResetAt: null, Window: null);

        var decision = policy.Evaluate(Member(Claude), quota, Now, recentObservedFailure: false);

        Assert.True(decision.Allow);
    }

    [Fact]
    public void UnknownQuota_WithoutExplicitReserve_BlocksUnderFailCautious()
    {
        // No FloorByAgent override → existing FailCautious behaviour: block.
        var opts = DefaultOpts();
        opts.UnknownPolicy = QuotaUnknownPolicy.FailCautious;
        var policy = new QuotaGatePolicy(opts);
        var quota = new EffectiveQuota(AvailablePct: -1, ResetAt: null, Window: null);

        var decision = policy.Evaluate(Member(Claude), quota, Now, recentObservedFailure: false);

        Assert.False(decision.Allow);
    }

    [Fact]
    public void UnknownQuota_OtherAgentHasReserveButThisOneDoesNot_Allows()
    {
        // Claude has a reserve; Codex does not. An Unknown Codex reading must
        // keep the existing UseObservedFailures behaviour (allow) — the safety
        // fix is per-agent, not global.
        var opts = DefaultOpts();
        opts.FloorByAgent[Claude.Value] = new QuotaFloorOverrideOptions
        {
            MinQuotaPct = 30.0,
        };
        var policy = new QuotaGatePolicy(opts);
        var quota = new EffectiveQuota(AvailablePct: -1, ResetAt: null, Window: null);

        var decision = policy.Evaluate(Member(Codex), quota, Now, recentObservedFailure: false);

        Assert.True(decision.Allow);
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
        var quota = new EffectiveQuota(AvailablePct: -1, ResetAt: null, Window: null);

        var decision = policy.Evaluate(
            Member(Claude), quota, Now,
            recentObservedFailure: true,
            observedFailureReason: "recent observed quota failure");

        Assert.False(decision.Allow);
        Assert.Contains("recent observed", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }
}
