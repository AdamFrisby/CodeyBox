using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Time-based (ramped) quota floor: the gate's minimum available-quota %
/// is a linear ramp from <see cref="QuotaRouterOptions.StartFloorPct"/>
/// just after the quota window resets down to
/// <see cref="QuotaRouterOptions.EndFloorPct"/> as reset approaches.
/// Falls back to <see cref="QuotaRouterOptions.MinQuotaPct"/> when the
/// probe surfaces no reset or no ramp window is configured. These tests
/// pin the math (start/mid/end), the per-agent override, and the
/// unknown-window fallback — they don't exercise the burn-estimator
/// gate, which sits behind a separate code path.
/// </summary>
public sealed class QuotaRouterRampedFloorTests
{
    private static readonly AgentKind Claude = AgentKind.Claude;
    private static readonly AgentKind Codex = AgentKind.Codex;

    private static readonly DateTimeOffset Now =
        new(2026, 5, 31, 12, 0, 0, TimeSpan.Zero);

    private static AgentClassRouter BuildRouter(
        QuotaRouterOptions opts,
        TimeProvider? timeProvider = null) =>
        new(
            catalog: [new AgentClass { Id = "x", DisplayName = "x", Members = [] }],
            probes: Array.Empty<IAgentQuotaProbe>(),
            opts: opts,
            log: NullLogger<AgentClassRouter>.Instance,
            timeProvider: timeProvider);

    private static QuotaRouterOptions DefaultOpts() => new()
    {
        MinQuotaPct = 10.0,
        StartFloorPct = 25.0,
        EndFloorPct = 3.0,
        RampWindow = TimeSpan.FromDays(7),
    };

    [Fact]
    public void StartOfWindow_FloorIsStartFloor()
    {
        var opts = DefaultOpts();
        var router = BuildRouter(opts);
        var reset = Now + TimeSpan.FromDays(7);

        var floor = router.ComputeEffectiveFloorPct(Claude, reset, Now);

        Assert.Equal(25.0, floor, precision: 6);
    }

    [Fact]
    public void MidWindow_FloorIsHalfwayBetweenStartAndEnd()
    {
        var opts = DefaultOpts();
        var router = BuildRouter(opts);
        // 3.5 days remaining of a 7-day window → fractionElapsed = 0.5
        var reset = Now + TimeSpan.FromDays(3.5);

        var floor = router.ComputeEffectiveFloorPct(Claude, reset, Now);

        // lerp(25, 3, 0.5) = 14.0
        Assert.Equal(14.0, floor, precision: 6);
    }

    [Fact]
    public void EndOfWindow_FloorIsEndFloor()
    {
        var opts = DefaultOpts();
        var router = BuildRouter(opts);
        var reset = Now; // reset is right now → fractionElapsed = 1

        var floor = router.ComputeEffectiveFloorPct(Claude, reset, Now);

        Assert.Equal(3.0, floor, precision: 6);
    }

    [Fact]
    public void PastResetAt_ClampsToEndFloor()
    {
        // resetAt in the past (stale snapshot the retry-scheduler hasn't
        // refreshed yet). fractionElapsed clamps to 1 and the floor sits at
        // EndFloorPct rather than overshooting to a negative number.
        var opts = DefaultOpts();
        var router = BuildRouter(opts);
        var reset = Now - TimeSpan.FromHours(2);

        var floor = router.ComputeEffectiveFloorPct(Claude, reset, Now);

        Assert.Equal(3.0, floor, precision: 6);
    }

    [Fact]
    public void ResetAtFurtherOutThanWindow_ClampsToStartFloor()
    {
        // resetAt past the configured window length (operator misconfig or
        // probe overshoot). fractionElapsed clamps to 0 rather than going
        // negative — the floor stays at StartFloorPct.
        var opts = DefaultOpts();
        var router = BuildRouter(opts);
        var reset = Now + TimeSpan.FromDays(30);

        var floor = router.ComputeEffectiveFloorPct(Claude, reset, Now);

        Assert.Equal(25.0, floor, precision: 6);
    }

    [Fact]
    public void NullResetAt_FallsBackToMinQuotaPct()
    {
        var opts = DefaultOpts();
        var router = BuildRouter(opts);

        var floor = router.ComputeEffectiveFloorPct(Claude, resetAt: null, Now);

        Assert.Equal(opts.MinQuotaPct, floor);
    }

    [Fact]
    public void ZeroRampWindow_FallsBackToMinQuotaPct()
    {
        var opts = DefaultOpts();
        opts.RampWindow = TimeSpan.Zero;
        var router = BuildRouter(opts);

        var floor = router.ComputeEffectiveFloorPct(Claude, Now + TimeSpan.FromDays(3), Now);

        Assert.Equal(opts.MinQuotaPct, floor);
    }

    [Fact]
    public void PerAgentOverride_UsesAgentWindow()
    {
        // claude pinned to a 24h window; codex on the default 7d window.
        // Both have resetAt 12h out. claude → 12h elapsed of 24h (0.5, mid).
        // codex → 156h elapsed of 168h (~0.93, near end-of-window).
        var opts = DefaultOpts();
        opts.RampWindowByAgent[Claude.Value] = TimeSpan.FromHours(24);
        var router = BuildRouter(opts);
        var reset = Now + TimeSpan.FromHours(12);

        var claudeFloor = router.ComputeEffectiveFloorPct(Claude, reset, Now);
        var codexFloor = router.ComputeEffectiveFloorPct(Codex, reset, Now);

        Assert.Equal(14.0, claudeFloor, precision: 6);
        // codex on 7d window with 12h until reset: lerp(25, 3, 1 - 12/168) ≈ 4.57
        Assert.True(codexFloor > 4.0 && codexFloor < 5.5,
            $"codex floor {codexFloor} should sit near the end of a 7d ramp");
    }

    [Fact]
    public void ConfigOverride_StartAndEndFloorRespected()
    {
        var opts = new QuotaRouterOptions
        {
            MinQuotaPct = 5.0,
            StartFloorPct = 40.0,
            EndFloorPct = 10.0,
            RampWindow = TimeSpan.FromDays(7),
        };
        var router = BuildRouter(opts);
        var reset = Now + TimeSpan.FromDays(3.5);

        var floor = router.ComputeEffectiveFloorPct(Claude, reset, Now);

        // lerp(40, 10, 0.5) = 25
        Assert.Equal(25.0, floor, precision: 6);
    }

    [Fact]
    public void PerAgentFloorOverride_UsesAgentFloorAndOmittedAgentUsesGlobalRamp()
    {
        var opts = DefaultOpts();
        opts.FloorByAgent[Codex.Value] = new QuotaFloorOverrideOptions
        {
            MinQuotaPct = 1.0,
            StartFloorPct = 1.0,
            EndFloorPct = 0.0,
        };
        var router = BuildRouter(opts);
        var reset = Now + TimeSpan.FromDays(3.5);

        var codexFloor = router.ComputeEffectiveFloorPct(Codex, reset, Now);
        var claudeFloor = router.ComputeEffectiveFloorPct(Claude, reset, Now);
        var codexUnknownResetFloor = router.ComputeEffectiveFloorPct(Codex, resetAt: null, Now);

        Assert.Equal(0.5, codexFloor, precision: 6);
        Assert.Equal(14.0, claudeFloor, precision: 6);
        Assert.Equal(1.0, codexUnknownResetFloor, precision: 6);
    }

    [Fact]
    public void WouldAllowWrapper_UsesPerAgentOverrideAndResetAt()
    {
        var opts = DefaultOpts();
        opts.FloorByAgent[Codex.Value] = new QuotaFloorOverrideOptions
        {
            MinQuotaPct = 1.0,
            StartFloorPct = 1.0,
            EndFloorPct = 0.0,
        };
        var reset = Now + TimeSpan.FromDays(7);

        Assert.True(QuotaRouter.WouldAllow(
            Codex,
            availablePct: 1.0,
            recentFailure: false,
            opts,
            resetAt: reset,
            nowUtc: Now));
        Assert.False(QuotaRouter.WouldAllow(
            Claude,
            availablePct: 20.0,
            recentFailure: false,
            opts,
            resetAt: reset,
            nowUtc: Now));
    }

    [Fact]
    public void PerAgentFloorOverride_UsesPerAgentRampWindowBeforeOtherWindows()
    {
        var opts = DefaultOpts();
        opts.RampWindowByAgent[Codex.Value] = TimeSpan.FromDays(2);
        opts.FloorByAgent[Codex.Value] = new QuotaFloorOverrideOptions
        {
            StartFloorPct = 21.0,
            EndFloorPct = 1.0,
            RampWindow = TimeSpan.FromDays(1),
        };
        var router = BuildRouter(opts);
        var reset = Now + TimeSpan.FromHours(12);

        var floor = router.ComputeEffectiveFloorPct(Codex, reset, Now);

        // 12h remaining in the FloorByAgent 24h window => midpoint of 21..1.
        Assert.Equal(11.0, floor, precision: 6);
    }

    [Fact]
    public void PartialPerAgentFloorOverride_InheritsMissingGlobalRampFields()
    {
        var opts = DefaultOpts();
        opts.FloorByAgent[Codex.Value] = new QuotaFloorOverrideOptions
        {
            EndFloorPct = 0.0,
        };
        opts.FloorByAgent[AgentKind.Opencode.Value] = new QuotaFloorOverrideOptions
        {
            MinQuotaPct = 2.0,
            StartFloorPct = 5.0,
        };
        var router = BuildRouter(opts);
        var reset = Now + TimeSpan.FromDays(3.5);

        var codexFloor = router.ComputeEffectiveFloorPct(Codex, reset, Now);
        var opencodeFloor = router.ComputeEffectiveFloorPct(AgentKind.Opencode, reset, Now);
        var codexUnknownResetFloor = router.ComputeEffectiveFloorPct(Codex, resetAt: null, Now);
        var opencodeUnknownResetFloor = router.ComputeEffectiveFloorPct(AgentKind.Opencode, resetAt: null, Now);

        Assert.Equal(12.5, codexFloor, precision: 6);
        Assert.Equal(4.0, opencodeFloor, precision: 6);
        Assert.Equal(10.0, codexUnknownResetFloor, precision: 6);
        Assert.Equal(2.0, opencodeUnknownResetFloor, precision: 6);
    }

    [Fact]
    public void WindowsAware_ClaudeShape_RampKeysOffLongWindowNotShorterBindingWindow()
    {
        // Repro for the operator's "ramp collapsed to EndFloor" bug: when the
        // binding window is shorter than RampWindow, ramping against
        // quota.ResetAt (= the binding short window's reset) makes
        // untilReset << RampWindow every cycle, so fractionElapsed clamps to
        // ~1 and the floor collapses to ~EndFloor. With the windows-aware
        // overload, the long (seven_day) reset is selected and the early-week
        // oversight reservation StartFloorPct is honoured.
        var opts = new QuotaRouterOptions
        {
            MinQuotaPct = 5.0,
            StartFloorPct = 40.0,
            EndFloorPct = 5.0,
            RampWindow = TimeSpan.FromDays(7),
        };
        var policy = new QuotaGatePolicy(opts);
        var fiveHourReset = Now + TimeSpan.FromHours(1);
        var sevenDayReset = Now + TimeSpan.FromDays(4);
        var quota = new EffectiveQuota(
            AvailablePct: 9.0,
            ResetAt: fiveHourReset,
            Window: "five_hour",
            Windows: new[]
            {
                new WindowQuota { Name = "five_hour", AvailablePct = 9.0, ResetAt = fiveHourReset },
                new WindowQuota { Name = "seven_day", AvailablePct = 29.0, ResetAt = sevenDayReset },
            });

        var floor = policy.ComputeEffectiveFloorPct(Claude, quota, Now);

        // lerp(40, 5, 1 - 4/7) = 40 + (5 - 40) * 3/7 = 40 - 15 = 25.0
        Assert.Equal(25.0, floor, precision: 6);
        // And to make the bug-fix posture explicit: the old behaviour would
        // have collapsed to ~EndFloorPct.
        Assert.True(floor > opts.EndFloorPct + 5.0,
            $"expected mid-ramp floor well above EndFloorPct={opts.EndFloorPct}, got {floor}");
    }

    [Fact]
    public void WindowsAware_CodexShape_OnlyWeeklyWindow_BehaviourUnchanged()
    {
        // Codex's binding window IS the weekly. Both overall.ResetAt and the
        // single window entry resolve to the same reset, so the windows-aware
        // overload picks the same point the legacy resetAt overload always
        // did — no behaviour drift for codex.
        var opts = new QuotaRouterOptions
        {
            MinQuotaPct = 5.0,
            StartFloorPct = 10.0,
            EndFloorPct = 5.0,
            RampWindow = TimeSpan.FromDays(7),
        };
        var policy = new QuotaGatePolicy(opts);
        var weeklyReset = Now + TimeSpan.FromDays(4.2);
        var quota = new EffectiveQuota(
            AvailablePct: 6.0,
            ResetAt: weeklyReset,
            Window: "weekly",
            Windows: new[]
            {
                new WindowQuota { Name = "weekly", AvailablePct = 6.0, ResetAt = weeklyReset },
            });

        var floor = policy.ComputeEffectiveFloorPct(Codex, quota, Now);

        // lerp(10, 5, 1 - 4.2/7) = 10 + (5 - 10) * 0.4 = 8.0
        Assert.Equal(8.0, floor, precision: 6);
    }

    [Fact]
    public void WindowsAware_NoWindowsAndNoOverallReset_FallsBackToMinQuotaPct()
    {
        // Probe surfaced nothing window-shaped and no overall reset either —
        // ramp cannot be computed, so the gate falls back to MinQuotaPct just
        // like the resetAt-only overload (preserves the original fixed-floor
        // behaviour).
        var opts = DefaultOpts();
        var policy = new QuotaGatePolicy(opts);
        var quota = new EffectiveQuota(
            AvailablePct: 50.0,
            ResetAt: null,
            Window: null);

        var floor = policy.ComputeEffectiveFloorPct(Claude, quota, Now);

        Assert.Equal(opts.MinQuotaPct, floor, precision: 6);
    }

    [Fact]
    public void WindowsAware_OverallResetButNoWindowEntries_UsesOverallReset()
    {
        // Snapshots from sources that don't decompose into per-window readings
        // (e.g. budget-derived quotas in PipelineRunner) still get the
        // resetAt-based ramp.
        var opts = DefaultOpts();
        var policy = new QuotaGatePolicy(opts);
        var weeklyReset = Now + TimeSpan.FromDays(3.5);
        var quota = new EffectiveQuota(
            AvailablePct: 50.0,
            ResetAt: weeklyReset,
            Window: null);

        var floor = policy.ComputeEffectiveFloorPct(Claude, quota, Now);

        // lerp(25, 3, 0.5) = 14.0 (same number the resetAt-only API returns mid-week)
        Assert.Equal(14.0, floor, precision: 6);
    }

    [Fact]
    public void HotReloadOfOptions_TakesEffectOnNextCall()
    {
        // The router holds the QuotaRouterOptions singleton by reference and
        // reads its properties on every gate decision; mutating the singleton
        // is how AgentConfigHotReload propagates config edits without rebuilds.
        var opts = DefaultOpts();
        var router = BuildRouter(opts);
        var reset = Now + TimeSpan.FromDays(3.5);

        Assert.Equal(14.0, router.ComputeEffectiveFloorPct(Claude, reset, Now), precision: 6);

        opts.StartFloorPct = 50.0;
        opts.EndFloorPct = 10.0;

        // lerp(50, 10, 0.5) = 30
        Assert.Equal(30.0, router.ComputeEffectiveFloorPct(Claude, reset, Now), precision: 6);
    }

    [Fact]
    public async Task ResolveAndFallback_PerAgentNearZeroFloorAdmitsBurnAgentWhileReservedAgentKeepsFloor()
    {
        var opts = DefaultOpts();
        opts.FloorByAgent[Codex.Value] = new QuotaFloorOverrideOptions
        {
            MinQuotaPct = 1.0,
            StartFloorPct = 1.0,
            EndFloorPct = 0.0,
        };
        var reset = Now + TimeSpan.FromDays(7);
        var frontier = new AgentClass
        {
            Id = "frontier",
            DisplayName = "frontier",
            Members = [
                new AgentMembership { Agent = Claude, Billing = AgentBilling.Subscription, QualityScore = 100 },
                new AgentMembership { Agent = Codex, Billing = AgentBilling.Subscription, QualityScore = 100 },
            ],
        };
        var claudeProbe = new FakeProbe(Claude, new AgentQuotaSnapshot
        {
            AvailablePct = 20.0,
            ResetAt = reset,
        });
        var codexProbe = new FakeProbe(Codex, new AgentQuotaSnapshot
        {
            AvailablePct = 1.0,
            ResetAt = reset,
        });
        var router = new AgentClassRouter(
            catalog: [frontier],
            probes: [claudeProbe, codexProbe],
            opts: opts,
            log: NullLogger<AgentClassRouter>.Instance,
            timeProvider: new FakeTimeProvider(Now));
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("proj"),
            Title = "t",
            Prompt = "p",
            AgentClassId = "frontier",
        };

        var decision = await router.ResolveAsync(item, project: null, CancellationToken.None);

        Assert.Equal(Codex, decision.Chosen!.Agent);
        Assert.Equal(1, claudeProbe.CallCount);
        Assert.Equal(1, codexProbe.CallCount);

        router.MarkExhausted(decision.Chosen, TimeSpan.FromMinutes(30), reset);

        var reservedCandidates = await router.OrderedFallbackCandidatesAsync(
            item, project: null, CancellationToken.None);

        Assert.Empty(reservedCandidates);

        claudeProbe.SetSnapshot(new AgentQuotaSnapshot
        {
            AvailablePct = 30.0,
            ResetAt = reset,
        });

        var availableCandidates = await router.OrderedFallbackCandidatesAsync(
            item, project: null, CancellationToken.None);

        var fallback = Assert.Single(availableCandidates);
        Assert.Equal(Claude, fallback.Agent);
        Assert.Equal(3, claudeProbe.CallCount);
        Assert.Equal(1, codexProbe.CallCount);
    }

    [Fact]
    public async Task EvaluateGate_RampedFloor_RejectsBelow_AdmitsAtOrAbove()
    {
        // End-to-end through ResolveAsync: at the end of the window the floor
        // is 3 % so a 4 % member admits where the legacy fixed-10 % floor would
        // have rejected; at the start of the window the floor is 25 % so a
        // 20 % member is rejected where the legacy gate would have admitted.
        var opts = DefaultOpts();
        var fakeTime = new FakeTimeProvider(Now);

        var endOfWindowReset = Now + TimeSpan.FromMinutes(1);
        var draining = new AgentClass
        {
            Id = "draining",
            DisplayName = "draining",
            Members = [
                new AgentMembership
                {
                    Agent = Claude, Billing = AgentBilling.Subscription, QualityScore = 100,
                },
            ],
        };
        var drainProbe = new FakeProbe(Claude, new AgentQuotaSnapshot
        {
            AvailablePct = 4.0,
            ResetAt = endOfWindowReset,
        });
        var router = new AgentClassRouter(
            catalog: [draining],
            probes: [drainProbe],
            opts: opts,
            log: NullLogger<AgentClassRouter>.Instance,
            timeProvider: fakeTime);

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("proj"),
            Title = "t",
            Prompt = "p",
            AgentClassId = "draining",
        };
        var endDecision = await router.ResolveAsync(item, project: null, CancellationToken.None);
        Assert.Equal(Claude, endDecision.Chosen!.Agent);

        // Same router, but probe now reports 20 % and the reset is far out —
        // start-of-window floor is 25 % so the member is rejected.
        var startReset = Now + TimeSpan.FromDays(7);
        drainProbe.SetSnapshot(new AgentQuotaSnapshot
        {
            AvailablePct = 20.0,
            ResetAt = startReset,
        });
        var startDecision = await router.ResolveAsync(item, project: null, CancellationToken.None);
        Assert.Null(startDecision.Chosen);
        Assert.True(startDecision.ShouldWait);
    }

    /// <summary>
    /// FakeProbe variant that lets the test swap its snapshot mid-router.
    /// Mirrors the existing test-only <c>FakeProbe</c> in
    /// AgentClassRouterTests.cs but adds a setter so a single router instance
    /// can observe before-and-after behaviour without rebuilding the catalog.
    /// </summary>
    private sealed class FakeProbe : IAgentQuotaProbe
    {
        private AgentQuotaSnapshot _snapshot;
        public AgentKind Kind { get; }

        public FakeProbe(AgentKind kind, AgentQuotaSnapshot snapshot)
        {
            Kind = kind;
            _snapshot = snapshot;
        }

        public int CallCount { get; private set; }

        public void SetSnapshot(AgentQuotaSnapshot s) => _snapshot = s;

        public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(_snapshot);
        }
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeTimeProvider(DateTimeOffset start) { _now = start; }
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
