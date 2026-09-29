using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using static CodeyBox.Orchestrator.QuotaReservationLedger;

namespace CodeyBox.Tests;

/// <summary>
/// Work-denominated quota: absolute (items) floors and reservations for
/// resetting-window pools, burn-derived per-dispatch reservations, and binding
/// window surfacing. Centred on the opencode-go incident: 6% of the monthly
/// window left, a 5% floor, and a 5.0-point default reservation stranded the
/// whole tail even though one dispatch measured ~0.4-1.1 points.
/// </summary>
public sealed class QuotaMeasuredBurnReservationTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private static AgentMembership Sub(AgentKind agent, string? pool = null) => new()
    {
        Agent = agent,
        Billing = AgentBilling.Subscription,
        QualityScore = 100,
        Pool = pool,
    };

    private static QuotaRouterOptions BaseOptions() => new()
    {
        MinQuotaPct = 5.0,
        StartFloorPct = 5.0,
        EndFloorPct = 5.0,
        RampWindow = TimeSpan.FromDays(7),
        DispatchReservationEstimatePct = 5.0,
        DispatchReservationMinPct = 0.5,
        DispatchReservationMaxPct = 25.0,
        DispatchReservationBurnMultiplier = 1.5,
        DispatchReservationBurnMinSamples = 3,
        QuotaReservationMaxAge = TimeSpan.FromHours(6),
    };

    private static QuotaRouterOptions PoolOptions(Action<QuotaRouterOptions> configure)
    {
        var opts = BaseOptions();
        opts.Pools["opencode-go"] = new QuotaPoolOptions
        {
            Name = "opencode-go",
            Kind = QuotaPoolKind.ResettingWindow,
        };
        configure(opts);
        return opts;
    }

    private static AgentBurnEstimate Measured(double burnPct, int samples = 8) => new()
    {
        AvgBurnPctPerItem = burnPct,
        SampleCount = samples,
        Status = AgentBurnEstimateStatus.Measured,
    };

    private static EffectiveQuota ThreeWindowQuota(double rolling, double weekly, double monthly) => new(
        Math.Min(rolling, Math.Min(weekly, monthly)),
        null,
        null,
        new List<WindowQuota>
        {
            new() { Name = "rolling", AvailablePct = rolling },
            new() { Name = "weekly", AvailablePct = weekly },
            new() { Name = "monthly", AvailablePct = monthly },
        });

    // ── Binding window ────────────────────────────────────────────────────

    [Fact]
    public void BindingWindow_IsScarcestKnownWindow()
    {
        var quota = ThreeWindowQuota(rolling: 100, weekly: 98, monthly: 6);
        Assert.Equal("monthly", quota.BindingWindow);
        Assert.Equal("monthly", QuotaWindowBinding.ResolveBindingWindow(quota.Windows));
    }

    [Fact]
    public void BindingWindow_TieResolvesToFirstWindowInProbeOrder()
    {
        var quota = new EffectiveQuota(10, null, null, new List<WindowQuota>
        {
            new() { Name = "weekly", AvailablePct = 10 },
            new() { Name = "monthly", AvailablePct = 10 },
        });
        Assert.Equal("weekly", quota.BindingWindow);
    }

    [Fact]
    public void BindingWindow_NullWhenAggregateCameFromNonWindowSource()
    {
        // A per-model fallback or budget composite can leave AvailablePct
        // lower than every window's own reading — the scarcest window did not
        // produce the aggregate, so no window is named binding.
        var windows = new List<WindowQuota>
        {
            new() { Name = "weekly", AvailablePct = 60 },
            new() { Name = "monthly", AvailablePct = 30 },
        };
        Assert.Null(new EffectiveQuota(2.0, null, null, windows).BindingWindow);
        Assert.Null(new AgentQuotaSnapshot { AvailablePct = 2.0, Windows = windows }.BindingWindow);
        // Equal readings still resolve normally.
        Assert.Equal("monthly", new EffectiveQuota(30, null, null, windows).BindingWindow);
    }

    [Fact]
    public void BindingWindow_NullWithoutUsableReadings()
    {
        Assert.Null(QuotaWindowBinding.ResolveBindingWindow(null));
        Assert.Null(QuotaWindowBinding.ResolveBindingWindow([]));
        Assert.Null(QuotaWindowBinding.ResolveBindingWindow(
            new List<WindowQuota> { new() { Name = "monthly", AvailablePct = -1 } }));
        Assert.Null(new EffectiveQuota(50, null, null).BindingWindow);
    }

    [Fact]
    public void WindowSummary_MarksBindingScarcestFirst()
    {
        var quota = ThreeWindowQuota(rolling: 100, weekly: 98, monthly: 6);
        Assert.Equal(
            "monthly 6.0% (binding), weekly 98.0%, rolling 100.0%",
            QuotaWindowBinding.FormatWindowSummary(quota.Windows));
    }

    // ── Measured-burn helpers ─────────────────────────────────────────────

    [Theory]
    [InlineData(0.6, 8, 3, true)]
    [InlineData(0.6, 2, 3, false)]   // below the sample threshold
    [InlineData(0.0, 8, 3, false)]   // non-positive burn
    [InlineData(-1.0, 8, 3, false)]  // unknown marker
    public void HasMeasuredBurn_GatesOnSamplesAndValue(double burn, int samples, int min, bool expected)
    {
        var estimate = new AgentBurnEstimate
        {
            AvgBurnPctPerItem = burn,
            SampleCount = samples,
            Status = AgentBurnEstimateStatus.Measured,
        };
        Assert.Equal(expected, estimate.HasMeasuredBurn(min));
    }

    [Fact]
    public void ToItemsPct_ConvertsDispatchesThroughMeasuredBurn()
    {
        var burn = Measured(0.6, samples: 8);
        Assert.Equal(1.2, burn.ToItemsPct(2.0, minSamples: 3)!.Value, precision: 9);
        Assert.Null(burn.ToItemsPct(null, 3));
        Assert.Null(burn.ToItemsPct(0, 3));
        Assert.Null(burn.ToItemsPct(-1, 3));
        Assert.Null(burn.ToItemsPct(2.0, 9)); // not enough samples
        Assert.Null(Measured(0.6, samples: 0).ToItemsPct(2.0, 3));
    }

    // ── Estimate resolution order ─────────────────────────────────────────

    [Fact]
    public void ResolveEstimate_NoHistory_MatchesLegacyChain()
    {
        var opts = BaseOptions();
        var member = Sub(AgentKind.Copilot);
        var resolution = ResolveEstimate(opts, member);
        Assert.Equal(QuotaReservationEstimateSource.GlobalEstimate, resolution.Source);
        Assert.Equal(5.0, resolution.EstimatePct, precision: 9);
    }

    [Fact]
    public void ResolveEstimate_PoolPctBeatsExplicitBeatsAgentBeatsGlobal()
    {
        var opts = BaseOptions();
        opts.Pools["p"] = new QuotaPoolOptions
        {
            Name = "p",
            Kind = QuotaPoolKind.ResettingWindow,
            ReservationEstimate = 4.0,
        };
        opts.DispatchReservationEstimatePctByAgent["copilot"] = 2.0;
        var member = Sub(AgentKind.Copilot, pool: "p");

        Assert.Equal(4.0, ResolveEstimate(opts, member, estimateOverride: 3.0).EstimatePct, precision: 9);
        opts.Pools["p"].ReservationEstimate = null;
        var explicitWins = ResolveEstimate(opts, member, estimateOverride: 3.0);
        Assert.Equal(QuotaReservationEstimateSource.ExplicitOverride, explicitWins.Source);
        Assert.Equal(3.0, explicitWins.EstimatePct, precision: 9);
        var agentWins = ResolveEstimate(opts, member);
        Assert.Equal(QuotaReservationEstimateSource.AgentEstimate, agentWins.Source);
        Assert.Equal(2.0, agentWins.EstimatePct, precision: 9);
    }

    [Fact]
    public void ResolveEstimate_ItemsTiersApplyBelowPctAtSameTier()
    {
        var burn = Measured(0.6);
        var opts = BaseOptions();
        opts.Pools["p"] = new QuotaPoolOptions
        {
            Name = "p",
            Kind = QuotaPoolKind.ResettingWindow,
            ReservationEstimate = 4.0,
            ReservationEstimateItems = 2.0,
        };
        var member = Sub(AgentKind.Copilot, pool: "p");

        var pctWins = ResolveEstimate(opts, member, measuredBurn: burn);
        Assert.Equal(QuotaReservationEstimateSource.PoolEstimate, pctWins.Source);
        Assert.Equal(4.0, pctWins.EstimatePct, precision: 9);

        opts.Pools["p"].ReservationEstimate = null;
        var itemsApply = ResolveEstimate(opts, member, measuredBurn: burn);
        Assert.Equal(QuotaReservationEstimateSource.PoolItemsEstimate, itemsApply.Source);
        Assert.Equal(1.2, itemsApply.EstimatePct, precision: 9);
    }

    [Fact]
    public void ResolveEstimate_MeasuredBurnDisplacesOnlyTheGlobalDefault()
    {
        var burn = Measured(0.6);
        var opts = BaseOptions();
        var member = Sub(AgentKind.Copilot);

        var derived = ResolveEstimate(opts, member, measuredBurn: burn);
        Assert.Equal(QuotaReservationEstimateSource.MeasuredBurn, derived.Source);
        Assert.Equal(0.9, derived.EstimatePct, precision: 9);
        Assert.Equal(0.6, derived.MeasuredBurnPctPerItem!.Value, precision: 9);
        Assert.Equal(8, derived.MeasuredSamples);

        opts.DispatchReservationEstimatePctByAgent["copilot"] = 2.0;
        var agentWins = ResolveEstimate(opts, member, measuredBurn: burn);
        Assert.Equal(QuotaReservationEstimateSource.AgentEstimate, agentWins.Source);
        Assert.Equal(2.0, agentWins.EstimatePct, precision: 9);

        opts.DispatchReservationEstimatePctByAgent.Clear();
        var explicitWins = ResolveEstimate(opts, member, estimateOverride: 3.0, measuredBurn: burn);
        Assert.Equal(QuotaReservationEstimateSource.ExplicitOverride, explicitWins.Source);

        opts.DispatchReservationEstimateItems = 2.0;
        var globalItemsWin = ResolveEstimate(opts, member, measuredBurn: burn);
        Assert.Equal(QuotaReservationEstimateSource.GlobalItemsEstimate, globalItemsWin.Source);
        Assert.Equal(1.2, globalItemsWin.EstimatePct, precision: 9);
    }

    [Fact]
    public void ResolveEstimate_TooFewSamples_FallsBackToConstant()
    {
        var opts = BaseOptions();
        var resolution = ResolveEstimate(opts, Sub(AgentKind.Copilot), measuredBurn: Measured(0.6, samples: 2));
        Assert.Equal(QuotaReservationEstimateSource.GlobalEstimate, resolution.Source);
        Assert.Equal(5.0, resolution.EstimatePct, precision: 9);
    }

    [Fact]
    public void ResolveEstimate_NonPositiveMultiplier_DisablesDerivation()
    {
        var opts = BaseOptions();
        opts.DispatchReservationBurnMultiplier = 0;
        var resolution = ResolveEstimate(opts, Sub(AgentKind.Copilot), measuredBurn: Measured(0.6));
        Assert.Equal(QuotaReservationEstimateSource.GlobalEstimate, resolution.Source);
        Assert.Equal(5.0, resolution.EstimatePct, precision: 9);
    }

    [Theory]
    [InlineData(0.05, 0.5)]  // tiny burns still reserve at least the minimum
    [InlineData(30.0, 25.0)] // runaway burns clamp at the maximum
    public void ResolveEstimate_DerivedValue_IsClamped(double burnPct, double expected)
    {
        var opts = BaseOptions();
        var resolution = ResolveEstimate(opts, Sub(AgentKind.Copilot), measuredBurn: Measured(burnPct));
        Assert.Equal(QuotaReservationEstimateSource.MeasuredBurn, resolution.Source);
        Assert.Equal(expected, resolution.EstimatePct, precision: 9);
    }

    [Fact]
    public void ResolveEstimate_BalancePoolIgnoresItemsAndClampsNothing()
    {
        var opts = BaseOptions();
        opts.Pools["prepaid"] = new QuotaPoolOptions
        {
            Name = "prepaid",
            Kind = QuotaPoolKind.DepletingBalance,
            BalanceUnit = "credits",
            ReservationEstimate = 50,
            ReservationEstimateItems = 2.0,
        };
        var member = Sub(AgentKind.Copilot, pool: "prepaid");
        var resolution = ResolveEstimate(opts, member, measuredBurn: Measured(0.6));
        Assert.Equal(QuotaReservationEstimateSource.PoolEstimate, resolution.Source);
        Assert.Equal(50, resolution.EstimatePct, precision: 9);
    }

    // ── Absolute floors ───────────────────────────────────────────────────

    [Fact]
    public void ComputeFloorPct_PoolItemsFloorCompetesViaMaximum()
    {
        var opts = PoolOptions(o =>
        {
            o.MinQuotaPct = 1.0;
            o.StartFloorPct = 1.0;
            o.EndFloorPct = 1.0;
            o.FloorByPool["opencode-go"] = new QuotaPoolFloorOptions
            {
                MinQuotaPct = 1.0,
                StartFloorPct = 1.0,
                EndFloorPct = 1.0,
                MinQuotaItems = 2.0,
            };
        });
        var member = Sub(AgentKind.Copilot, pool: "opencode-go");
        var quota = ThreeWindowQuota(100, 98, 6);

        Assert.Equal(1.0, QuotaGatePolicy.ComputeFloorPct(opts, member, quota, Now), precision: 9);
        Assert.Equal(
            1.2,
            QuotaGatePolicy.ComputeFloorPct(opts, member, quota, Now, Measured(0.6)),
            precision: 9);
    }

    [Fact]
    public void ComputeFloorPct_PercentageFloorStillWinsWhenHigher()
    {
        var opts = PoolOptions(o =>
        {
            o.FloorByPool["opencode-go"] = new QuotaPoolFloorOptions
            {
                MinQuotaPct = 1.0,
                StartFloorPct = 1.0,
                EndFloorPct = 1.0,
                MinQuotaItems = 2.0,
            };
        });
        var member = Sub(AgentKind.Copilot, pool: "opencode-go");
        var quota = ThreeWindowQuota(100, 98, 6);
        // The agent tier still resolves the global 5% floor, which wins over
        // both the pool's 1% floor and its 1.2-point items floor.
        Assert.Equal(
            5.0,
            QuotaGatePolicy.ComputeFloorPct(opts, member, quota, Now, Measured(0.6)),
            precision: 9);
    }

    [Fact]
    public void ComputeFloorPct_AgentAndGlobalItemsFloors()
    {
        var quota = ThreeWindowQuota(100, 98, 6);

        var agentOpts = BaseOptions();
        agentOpts.MinQuotaPct = 1.0;
        agentOpts.StartFloorPct = 1.0;
        agentOpts.EndFloorPct = 1.0;
        agentOpts.FloorByAgent["copilot"] = new QuotaFloorOverrideOptions { MinQuotaItems = 3.0 };
        Assert.Equal(
            1.8,
            QuotaGatePolicy.ComputeFloorPct(agentOpts, Sub(AgentKind.Copilot), quota, Now, Measured(0.6)),
            precision: 9);

        var globalOpts = BaseOptions();
        // 10 items x 0.6%/item = 6.0 points — deliberately above the 5.0 pct
        // floor so the assertion cannot pass unless the global items tier is
        // consulted.
        globalOpts.MinQuotaItems = 10.0;
        Assert.Equal(
            6.0,
            QuotaGatePolicy.ComputeFloorPct(globalOpts, Sub(AgentKind.Copilot), quota, Now, Measured(0.6)),
            precision: 9);
    }

    // ── Incident regression: 6% left, 5% floor, 5.0 default, ~0.6% burn ────

    [Fact]
    public void Incident_WithoutHistory_ReservationRefusesAsBefore()
    {
        var opts = PoolOptions(o =>
        {
            o.FloorByPool["opencode-go"] = new QuotaPoolFloorOptions
            {
                MinQuotaPct = 5.0,
                StartFloorPct = 5.0,
                EndFloorPct = 5.0,
            };
        });
        var member = Sub(AgentKind.Copilot, pool: "opencode-go");
        var quota = ThreeWindowQuota(rolling: 100, weekly: 98, monthly: 6);

        Assert.Equal("monthly", quota.BindingWindow);
        var floor = QuotaGatePolicy.ComputeFloorPct(opts, member, quota, Now);
        Assert.Equal(5.0, floor, precision: 9);

        var ledger = new QuotaReservationLedger(opts);
        var attempt = ledger.TryReserve(member, availablePct: 6.0, floorPct: floor);
        Assert.False(attempt.Allowed);
        Assert.Equal(
            "quota reservation of 5.0% would breach floor (1.0% < 5.0%; 0.0% already escrowed)",
            attempt.DenyReason);
    }

    [Fact]
    public void Incident_WithMeasuredBurn_ReservationDispatches()
    {
        var opts = PoolOptions(o =>
        {
            o.FloorByPool["opencode-go"] = new QuotaPoolFloorOptions
            {
                MinQuotaPct = 5.0,
                StartFloorPct = 5.0,
                EndFloorPct = 5.0,
            };
        });
        var member = Sub(AgentKind.Copilot, pool: "opencode-go");
        var quota = ThreeWindowQuota(rolling: 100, weekly: 98, monthly: 6);
        var burn = Measured(0.6);

        var floor = QuotaGatePolicy.ComputeFloorPct(opts, member, quota, Now, burn);
        Assert.Equal(5.0, floor, precision: 9);

        var ledger = new QuotaReservationLedger(opts);
        var attempt = ledger.TryReserve(
            member, availablePct: 6.0, floorPct: floor,
            measuredBurn: burn, bindingWindow: quota.BindingWindow);
        Assert.True(attempt.Allowed);
        Assert.NotNull(attempt.Lease);
        Assert.Equal(0.9, ledger.GetOutstandingPct(member), precision: 9);
    }

    [Fact]
    public void Incident_PoolItemsReservation_ReservesOneItemsWorth()
    {
        var opts = PoolOptions(o =>
        {
            o.Pools["opencode-go"].ReservationEstimateItems = 1.0;
            o.FloorByPool["opencode-go"] = new QuotaPoolFloorOptions
            {
                MinQuotaPct = 5.0,
                StartFloorPct = 5.0,
                EndFloorPct = 5.0,
            };
        });
        var member = Sub(AgentKind.Copilot, pool: "opencode-go");
        var burn = Measured(0.6);

        var ledger = new QuotaReservationLedger(opts);
        var attempt = ledger.TryReserve(member, availablePct: 6.0, floorPct: 5.0, measuredBurn: burn);
        Assert.True(attempt.Allowed);
        Assert.Equal(0.6, ledger.GetOutstandingPct(member), precision: 9);
    }

    // ── Refusal surfaces name the binding window ──────────────────────────

    [Fact]
    public void GateRefusal_NamesBindingWindowAndSiblings()
    {
        var opts = BaseOptions();
        var member = Sub(AgentKind.Copilot);
        var quota = ThreeWindowQuota(rolling: 100, weekly: 98, monthly: 4);

        var decision = QuotaGatePolicy.Evaluate(opts, member, quota, Now);
        Assert.False(decision.Allow);
        Assert.Equal("monthly", decision.BindingWindow);
        Assert.Contains("monthly 4.0% (binding)", decision.Reason, StringComparison.Ordinal);
        Assert.Contains("weekly 98.0%", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ReservationRefusal_NamesBindingWindow()
    {
        var opts = BaseOptions();
        var member = Sub(AgentKind.Copilot);
        var ledger = new QuotaReservationLedger(opts);
        var attempt = ledger.TryReserve(member, availablePct: 6.0, floorPct: 5.0, bindingWindow: "monthly");
        Assert.False(attempt.Allowed);
        Assert.EndsWith("; binding window 'monthly')", attempt.DenyReason, StringComparison.Ordinal);
    }

    [Fact]
    public void GateRefusal_SingleWindow_KeepsLegacyReasonText()
    {
        var opts = BaseOptions();
        var member = Sub(AgentKind.Copilot);
        var decision = QuotaGatePolicy.Evaluate(
            opts, member, new EffectiveQuota(4.0, null, null), Now);
        Assert.False(decision.Allow);
        Assert.Null(decision.BindingWindow);
        Assert.Equal("quota below floor (4.0% < 5.0%)", decision.Reason);
    }

    [Fact]
    public void BindingWindow_MaliciousName_IsSanitizedForLogsAndDto()
    {
        var hostile = "weekly\n[forged line]\u001b[31m";
        var windows = new List<WindowQuota>
        {
            new() { Name = "monthly", AvailablePct = 50 },
            new() { Name = hostile, AvailablePct = 4 },
        };
        var binding = QuotaWindowBinding.ResolveBindingWindow(windows);
        Assert.Equal("weekly__forged_line___31m", binding);
        Assert.DoesNotContain("\n", binding, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", binding, StringComparison.Ordinal);
        var summary = QuotaWindowBinding.FormatWindowSummary(windows);
        Assert.NotNull(summary);
        Assert.DoesNotContain("\n", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", summary, StringComparison.Ordinal);
        Assert.Contains("weekly__forged_line___31m 4.0% (binding)", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ReservationRefusal_MaliciousBindingWindow_IsSanitized()
    {
        var opts = BaseOptions();
        var member = Sub(AgentKind.Copilot);
        var ledger = new QuotaReservationLedger(opts);
        var attempt = ledger.TryReserve(
            member, availablePct: 6.0, floorPct: 5.0, bindingWindow: "weekly\n[forged]\u001b[0m");
        Assert.False(attempt.Allowed);
        Assert.DoesNotContain("\n", attempt.DenyReason, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", attempt.DenyReason, StringComparison.Ordinal);
        Assert.EndsWith("; binding window 'weekly__forged___0m')", attempt.DenyReason, StringComparison.Ordinal);
    }

    // ── Validation ────────────────────────────────────────────────────────

    [Fact]
    public void Validate_BalancePoolWithItemsFloor_Throws()
    {
        var opts = BaseOptions();
        opts.Pools["prepaid"] = new QuotaPoolOptions
        {
            Name = "prepaid",
            Kind = QuotaPoolKind.DepletingBalance,
            BalanceUnit = "credits",
        };
        opts.FloorByPool["prepaid"] = new QuotaPoolFloorOptions { MinQuotaItems = 1.0 };
        var ex = Assert.Throws<InvalidOperationException>(() => QuotaPoolValidation.Validate(opts));
        Assert.Contains("prepaid", ex.Message, StringComparison.Ordinal);
        Assert.Contains("MinQuotaItems", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_BalancePoolWithItemsEstimate_Throws()
    {
        var opts = BaseOptions();
        opts.Pools["prepaid"] = new QuotaPoolOptions
        {
            Name = "prepaid",
            Kind = QuotaPoolKind.DepletingBalance,
            BalanceUnit = "credits",
            ReservationEstimate = 50,
            ReservationEstimateItems = 1.0,
        };
        var ex = Assert.Throws<InvalidOperationException>(() => QuotaPoolValidation.Validate(opts));
        Assert.Contains("prepaid", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ReservationEstimateItems", ex.Message, StringComparison.Ordinal);
    }

    // ── Probe DTO ─────────────────────────────────────────────────────────

    [Fact]
    public void Snapshot_BindingWindow_SerializesInProbePayload()
    {
        var snapshot = new AgentQuotaSnapshot
        {
            AvailablePct = 6.0,
            Windows = new List<WindowQuota>
            {
                new() { Name = "rolling", AvailablePct = 100 },
                new() { Name = "weekly", AvailablePct = 98 },
                new() { Name = "monthly", AvailablePct = 6 },
            },
        };
        Assert.Equal("monthly", snapshot.BindingWindow);
        var json = System.Text.Json.JsonSerializer.Serialize(snapshot);
        Assert.Contains("\"BindingWindow\":\"monthly\"", json, StringComparison.Ordinal);
    }

    // ── Config mapping and hot reload ─────────────────────────────────────

    private static QuotaRouterConfig ItemsConfig() => new()
    {
        MinQuotaItems = 2.0,
        DispatchReservationEstimateItems = 1.5,
        DispatchReservationEstimateItemsByAgent =
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["copilot"] = 2.0 },
        DispatchReservationBurnMultiplier = 2.0,
        DispatchReservationBurnMinSamples = 5,
        FloorByAgent =
            new Dictionary<string, QuotaRouterFloorConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["copilot"] = new QuotaRouterFloorConfig { MinQuotaItems = 1.0 },
            },
        Pools =
            new Dictionary<string, QuotaPoolConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["opencode-go"] = new QuotaPoolConfig
                {
                    Kind = "ResettingWindow",
                    ReservationEstimateItems = 1.0,
                },
            },
        FloorByPool =
            new Dictionary<string, QuotaPoolFloorConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["opencode-go"] = new QuotaPoolFloorConfig { MinQuotaItems = 1.0 },
            },
    };

    private static void AssertItemsMapped(QuotaRouterOptions options)
    {
        Assert.Equal(2.0, options.MinQuotaItems);
        Assert.Equal(1.5, options.DispatchReservationEstimateItems);
        Assert.Equal(2.0, options.DispatchReservationEstimateItemsByAgent["copilot"]);
        Assert.Equal(2.0, options.DispatchReservationBurnMultiplier);
        Assert.Equal(5, options.DispatchReservationBurnMinSamples);
        Assert.Equal(1.0, options.FloorByAgent["copilot"].MinQuotaItems);
        Assert.Equal(1.0, options.Pools["opencode-go"].ReservationEstimateItems);
        Assert.Equal(1.0, options.FloorByPool["opencode-go"].MinQuotaItems);
    }

    [Fact]
    public void Mapper_MapsItemsKnobs()
    {
        AssertItemsMapped(QuotaRouterConfigMapper.ToOptions(ItemsConfig()));
    }

    [Fact]
    public void HotReload_AppliesItemsKnobs()
    {
        var live = new QuotaRouterOptions();
        QuotaRouterConfigMapper.ApplyHotReload(live, ItemsConfig());
        AssertItemsMapped(live);
    }

    [Fact]
    public void Mapper_RejectsNonPositivePoolItemsEstimate()
    {
        var config = ItemsConfig();
        config.Pools["opencode-go"]!.ReservationEstimateItems = -1.0;
        var ex = Assert.Throws<InvalidOperationException>(
            () => QuotaRouterConfigMapper.ToOptions(config));
        Assert.Contains("ReservationEstimateItems", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Mapper_RejectsNonPositiveAgentItemsEstimate()
    {
        var config = ItemsConfig();
        config.DispatchReservationEstimateItemsByAgent["copilot"] = 0;
        var ex = Assert.Throws<InvalidOperationException>(
            () => QuotaRouterConfigMapper.ToOptions(config));
        Assert.Contains("DispatchReservationEstimateItemsByAgent", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Mapper_DropsNonPositiveItemsFloors()
    {
        var config = ItemsConfig();
        config.MinQuotaItems = -1.0;
        config.FloorByAgent["copilot"]!.MinQuotaItems = 0;
        config.FloorByPool["opencode-go"]!.MinQuotaItems = double.NaN;
        var options = QuotaRouterConfigMapper.ToOptions(config);
        Assert.Null(options.MinQuotaItems);
        Assert.DoesNotContain("copilot", options.FloorByAgent.Keys);
        Assert.DoesNotContain("opencode-go", options.FloorByPool.Keys);
    }
}
