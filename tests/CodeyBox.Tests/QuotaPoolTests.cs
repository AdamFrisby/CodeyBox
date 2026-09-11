using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Verification for pool-keyed quota: readings, floors and reservation escrow
/// are keyed by operator-declared quota pool (one account/subscription), not
/// by agent kind. Covers the ten acceptance behaviours: shared reading/floor,
/// pool-scoped reserves, independent pools per kind, legacy FloorByAgent
/// compatibility, fail-closed unresolvable pools, pool identity on the read
/// surface, resetting/balance coexistence and unit validation, terminal
/// balance exhaustion, and reset-free balance reporting.
/// </summary>
public sealed class QuotaPoolTests
{
    private static readonly AgentKind Claude = AgentKind.Claude;
    private static readonly AgentKind Codex = AgentKind.Codex;
    private static readonly DateTimeOffset Now = new(2026, 7, 3, 12, 0, 0, TimeSpan.Zero);

    private static AgentMembership Sub(
        AgentKind agent,
        int score = 100,
        string? instance = null,
        string? model = null,
        string? pool = null) => new()
        {
            Agent = agent,
            Billing = AgentBilling.Subscription,
            QualityScore = score,
            InstanceId = instance,
            ModelId = model,
            Pool = pool,
        };

    private static AgentClass PoolClass(string id, params AgentMembership[] members) => new()
    {
        Id = id,
        DisplayName = id,
        Members = members,
    };

    private static QuotaRouterOptions PoolOpts(
        Action<QuotaRouterOptions>? configure = null)
    {
        var opts = new QuotaRouterOptions
        {
            MinQuotaPct = 10.0,
            StartFloorPct = 25.0,
            EndFloorPct = 3.0,
            RampWindow = TimeSpan.FromDays(7),
            QuotaRecheckInterval = TimeSpan.FromMinutes(5),
            UnknownPolicy = QuotaUnknownPolicy.UseObservedFailures,
            DispatchReservationEstimatePct = 5.0,
            DispatchReservationMinPct = 0.5,
            DispatchReservationMaxPct = 25.0,
        };
        configure?.Invoke(opts);
        return opts;
    }

    private static AgentClassRouter BuildRouter(
        IEnumerable<AgentClass> catalog,
        IEnumerable<IAgentQuotaProbe> probes,
        QuotaRouterOptions opts,
        QuotaReservationLedger? ledger = null) =>
        new(
            catalog.ToList(),
            probes,
            opts,
            NullLogger<AgentClassRouter>.Instance,
            reservationLedger: ledger);

    private static WorkItem MakeItem(string? classId = null) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("proj"),
        Title = "t",
        Prompt = "p",
        AgentClassId = classId,
    };

    /// <summary>Mutable probe so a test can move readings between dispatches.</summary>
    private sealed class MutableProbe : IAgentQuotaProbe
    {
        public MutableProbe(AgentKind kind, AgentQuotaSnapshot snapshot)
        {
            Kind = kind;
            Current = snapshot;
        }

        public AgentKind Kind { get; }
        public AgentQuotaSnapshot Current { get; set; }
        public int CallCount { get; private set; }

        public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(Current);
        }
    }

    private static AgentQuotaSnapshot Pct(double pct, DateTimeOffset? resetAt = null) =>
        new() { AvailablePct = pct, ResetAt = resetAt };

    private static AgentQuotaSnapshot Balance(double remaining, string unit = "credits", DateTimeOffset? resetAt = null) =>
        new() { AvailablePct = -1, BalanceRemaining = remaining, BalanceUnit = unit, ResetAt = resetAt };

    private static void ResettingPool(QuotaRouterOptions opts, string name, double floor) =>
        opts.Pools[name] = new QuotaPoolOptions { Name = name, Kind = QuotaPoolKind.ResettingWindow };

    private static void PoolFloorPct(QuotaRouterOptions opts, string name, double floor) =>
        opts.FloorByPool[name] = new QuotaPoolFloorOptions
        {
            MinQuotaPct = floor,
            StartFloorPct = floor,
            EndFloorPct = floor,
        };

    // ── 1. Shared reading, shared floor, shared escrow ───────────────────────

    [Fact]
    public async Task TwoMembersInOnePool_ShareSingleReadingFloorAndEscrow()
    {
        var opts = PoolOpts(o =>
        {
            ResettingPool(o, "shared", 20);
            PoolFloorPct(o, "shared", 20);
        });
        var ledger = new QuotaReservationLedger(opts);
        var probe = new MutableProbe(Claude, Pct(50));
        var m1 = Sub(Claude, score: 100, model: "model-a", pool: "shared");
        var m2 = Sub(Claude, score: 90, model: "model-b", pool: "shared");
        var router = BuildRouter([PoolClass("c", m1, m2)], [probe], opts, ledger);

        var decision = await router.ResolveAsync(MakeItem("c"), null, CancellationToken.None);

        Assert.NotNull(decision.Chosen);
        Assert.Equal("model-a", decision.Chosen!.ModelId);
        // One pool ⇒ one probe call for the whole dispatch pass.
        Assert.Equal(1, probe.CallCount);
        // Consumption authorised for one member is visible to the other's gate.
        var poolKey = QuotaReservationLedger.PoolReservationKey("shared");
        Assert.Equal(
            ledger.GetOutstandingPct(m1),
            ledger.GetOutstandingPct(m2));
        Assert.True(ledger.GetOutstandingPct(poolKey) > 0);
    }

    [Fact]
    public async Task TwoMembersInOnePool_ShareSingleFloor()
    {
        var opts = PoolOpts(o =>
        {
            ResettingPool(o, "shared", 20);
            PoolFloorPct(o, "shared", 20);
        });
        var probe = new MutableProbe(Claude, Pct(10));
        var m1 = Sub(Claude, score: 100, model: "model-a", pool: "shared");
        var m2 = Sub(Claude, score: 90, model: "model-b", pool: "shared");
        var router = BuildRouter([PoolClass("c", m1, m2)], [probe], opts);

        var decision = await router.ResolveAsync(MakeItem("c"), null, CancellationToken.None);

        Assert.Null(decision.Chosen);
        Assert.True(decision.ShouldWait);
        // One pool ⇒ one shared floor: both members refuse against 20%.
        var policy = new QuotaGatePolicy(opts);
        var now = DateTimeOffset.UtcNow;
        foreach (var member in new[] { m1, m2 })
        {
            var gate = policy.Evaluate(
                member,
                new EffectiveQuota(10, null, null, PoolId: "shared", PoolKind: QuotaPoolKind.ResettingWindow),
                now,
                recentObservedFailure: false);
            Assert.False(gate.Allow);
            Assert.Contains("10.0% < 20.0%", gate.Reason, StringComparison.Ordinal);
            Assert.Equal("shared", gate.PoolId);
        }
    }

    // ── 2. Pool reserve not breached by dispatching both ─────────────────────

    [Fact]
    public async Task ReserveOnSharedPool_NotBreachedByDispatchingBothMembers()
    {
        var opts = PoolOpts(o =>
        {
            ResettingPool(o, "shared", 20);
            PoolFloorPct(o, "shared", 20);
        });
        var ledger = new QuotaReservationLedger(opts);
        // 26% with a 5-point estimate and a 20% floor: exactly one dispatch fits.
        var probe = new MutableProbe(Claude, Pct(26));
        var m1 = Sub(Claude, score: 100, model: "model-a", pool: "shared");
        var m2 = Sub(Claude, score: 90, model: "model-b", pool: "shared");
        var router = BuildRouter([PoolClass("c", m1, m2)], [probe], opts, ledger);

        var first = await router.ResolveAsync(MakeItem("c"), null, CancellationToken.None);
        Assert.NotNull(first.Chosen);

        var second = await router.ResolveAsync(MakeItem("c"), null, CancellationToken.None);
        Assert.Null(second.Chosen);
        Assert.True(second.ShouldWait);
        Assert.Contains("floor", second.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(5.0, ledger.GetOutstandingPct(m2), precision: 9);
    }

    // ── 3. One kind across two pools ⇒ independent readings and floors ───────

    [Fact]
    public async Task OneKindAcrossTwoPools_ResolvesIndependentReadingsAndFloors()
    {
        var opts = PoolOpts(o =>
        {
            ResettingPool(o, "a", 20);
            ResettingPool(o, "b", 20);
            PoolFloorPct(o, "a", 20);
            o.FloorByPool["b"] = new QuotaPoolFloorOptions
            {
                MinQuotaPct = 70,
                StartFloorPct = 70,
                EndFloorPct = 70,
            };
        });
        var ledger = new QuotaReservationLedger(opts);
        var probe = new InstanceRouteProbe(Claude, new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude/acct-a"] = 60,
            ["claude/acct-b"] = 60,
        });
        var mA = Sub(Claude, score: 100, instance: "acct-a", pool: "a");
        var mB = Sub(Claude, score: 100, instance: "acct-b", pool: "b");
        var router = BuildRouter([PoolClass("c", mA, mB)], [probe], opts, ledger);

        // Same 60% reading on both accounts, but pool b's floor is 70%.
        var decision = await router.ResolveAsync(MakeItem("c"), null, CancellationToken.None);

        Assert.NotNull(decision.Chosen);
        Assert.Equal("claude/acct-a", decision.Chosen!.RouteKey);
        // Independent escrows: authorising acct-a leaves pool b untouched.
        Assert.True(ledger.GetOutstandingPct(mA) > 0);
        Assert.Equal(0.0, ledger.GetOutstandingPct(mB));
    }

    // ── 4. FloorByAgent-only config behaves as today ─────────────────────────

    [Fact]
    public void FloorByAgentOnly_ProducesLegacyFloors()
    {
        var opts = PoolOpts(o =>
        {
            o.FloorByAgent[Claude.Value] = new QuotaFloorOverrideOptions
            {
                MinQuotaPct = 30.0,
                StartFloorPct = 30.0,
                EndFloorPct = 30.0,
            };
        });
        var policy = new QuotaGatePolicy(opts);
        var member = Sub(Claude);

        Assert.Equal(30.0, QuotaGatePolicy.ComputeFloorPct(opts, member, new EffectiveQuota(50, null, null), Now));

        var below = policy.Evaluate(member, new EffectiveQuota(25, null, null), Now, recentObservedFailure: false);
        Assert.False(below.Allow);
        Assert.Contains("quota below floor (25.0% < 30.0%)", below.Reason, StringComparison.Ordinal);
        Assert.Null(below.PoolId);
    }

    [Fact]
    public void PoolFloor_AndAgentFloor_HigherWins()
    {
        var opts = PoolOpts(o =>
        {
            ResettingPool(o, "shared", 20);
            PoolFloorPct(o, "shared", 50);
            o.FloorByAgent[Claude.Value] = new QuotaFloorOverrideOptions
            {
                MinQuotaPct = 10.0,
                StartFloorPct = 10.0,
                EndFloorPct = 10.0,
            };
        });
        var member = Sub(Claude, pool: "shared");
        Assert.Equal(50.0, QuotaGatePolicy.ComputeFloorPct(opts, member, new EffectiveQuota(80, null, null), Now));

        opts.FloorByAgent[Claude.Value] = new QuotaFloorOverrideOptions
        {
            MinQuotaPct = 60.0,
            StartFloorPct = 60.0,
            EndFloorPct = 60.0,
        };
        Assert.Equal(60.0, QuotaGatePolicy.ComputeFloorPct(opts, member, new EffectiveQuota(80, null, null), Now));
    }

    // ── 5. Unresolvable pool fails closed, naming member and pool ────────────

    [Fact]
    public async Task MemberWithUnresolvablePool_IsRefusedNamingMemberAndPool()
    {
        var opts = PoolOpts();
        var probe = new MutableProbe(Claude, Pct(90));
        var member = Sub(Claude, pool: "ghost");
        var router = BuildRouter([PoolClass("c", member)], [probe], opts);

        var decision = await router.ResolveAsync(MakeItem("c"), null, CancellationToken.None);

        Assert.Null(decision.Chosen);
        Assert.False(decision.TerminalQuotaExhausted);
        Assert.Contains("claude", decision.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ghost", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void GateWithUnresolvablePool_FailsClosedNamingMemberAndPool()
    {
        var opts = PoolOpts();
        var policy = new QuotaGatePolicy(opts);
        var member = Sub(Claude, pool: "ghost");

        var decision = policy.Evaluate(
            member, new EffectiveQuota(90, null, null), Now, recentObservedFailure: false);

        Assert.False(decision.Allow);
        Assert.Contains("claude", decision.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ghost", decision.Reason, StringComparison.Ordinal);
    }

    // ── 6. Read surface shows pool sharing ───────────────────────────────────

    [Fact]
    public async Task QuotaReadSurface_ShowsWhichMembersShareAPool()
    {
        var opts = PoolOpts(o =>
        {
            ResettingPool(o, "shared", 20);
            PoolFloorPct(o, "shared", 20);
        });
        var probe = new MutableProbe(Claude, Pct(10));
        var m1 = Sub(Claude, score: 100, model: "model-a", pool: "shared");
        var m2 = Sub(Claude, score: 90, model: "model-b", pool: "shared");
        var lone = Sub(Codex, score: 80);
        var codexProbe = new MutableProbe(Codex, Pct(5));
        var router = BuildRouter([PoolClass("c", m1, m2, lone)], [probe, codexProbe], opts);

        // Below the shared floor so every member is evaluated (and recorded),
        // not just the chosen one.
        var decision = await router.ResolveAsync(MakeItem("c"), null, CancellationToken.None);
        Assert.Null(decision.Chosen);

        var pools = router.SnapshotQuotaPools();
        Assert.Contains(pools, r => r.ModelId == "model-a" && r.Pool == "shared");
        Assert.Contains(pools, r => r.ModelId == "model-b" && r.Pool == "shared");
        Assert.Contains(pools, r => r.Agent == Codex && r.Pool is null);
    }

    // ── 7. Resetting and balance pools coexist, gating independently ─────────

    [Fact]
    public async Task ResettingAndBalancePools_CoexistAndGateIndependently()
    {
        var opts = PoolOpts(o =>
        {
            ResettingPool(o, "sub", 20);
            PoolFloorPct(o, "sub", 20);
            o.Pools["credits"] = new QuotaPoolOptions
            {
                Name = "credits",
                Kind = QuotaPoolKind.DepletingBalance,
                BalanceUnit = "credits",
            };
            o.FloorByPool["credits"] = new QuotaPoolFloorOptions { MinBalance = 100 };
        });
        var claudeProbe = new MutableProbe(Claude, Pct(10));
        var codexProbe = new MutableProbe(Codex, Balance(500));
        var mSub = Sub(Claude, score: 100, pool: "sub");
        var mBal = Sub(Codex, score: 90, pool: "credits");
        var router = BuildRouter([PoolClass("c", mSub, mBal)], [claudeProbe, codexProbe], opts);

        // Subscription below its percentage floor; balance healthy ⇒ balance member wins.
        var decision = await router.ResolveAsync(MakeItem("c"), null, CancellationToken.None);
        Assert.NotNull(decision.Chosen);
        Assert.Equal(Codex, decision.Chosen!.Agent);

        // Balance below its absolute floor too, while the subscription refusal
        // is non-terminal ⇒ wait (the subscription window may still reset).
        codexProbe.Current = Balance(50);
        var waiting = await router.ResolveAsync(MakeItem("c"), null, CancellationToken.None);
        Assert.Null(waiting.Chosen);
        Assert.True(waiting.ShouldWait);
        Assert.False(waiting.TerminalQuotaExhausted);
    }

    // ── 8. Unit mismatches rejected at configuration load ────────────────────

    [Fact]
    public void PercentFloorOnBalancePool_RejectedAtLoadNamingPoolAndUnit()
    {
        var qr = new QuotaRouterConfig
        {
            Pools =
            {
                ["b"] = new QuotaPoolConfig { Kind = "DepletingBalance", BalanceUnit = "credits" },
            },
            FloorByPool =
            {
                ["b"] = new QuotaPoolFloorConfig { MinQuotaPct = 10 },
            },
        };

        var ex = Assert.Throws<InvalidOperationException>(() => QuotaRouterConfigMapper.ToOptions(qr));
        Assert.Contains("'b'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("MinBalance", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AbsoluteFloorOnResettingPool_RejectedAtLoadNamingPoolAndUnit()
    {
        var qr = new QuotaRouterConfig
        {
            Pools =
            {
                ["r"] = new QuotaPoolConfig { Kind = "ResettingWindow" },
            },
            FloorByPool =
            {
                ["r"] = new QuotaPoolFloorConfig { MinBalance = 5 },
            },
        };

        var ex = Assert.Throws<InvalidOperationException>(() => QuotaRouterConfigMapper.ToOptions(qr));
        Assert.Contains("'r'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("percent", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FloorEntryNamingUnknownPool_RejectedAtLoad()
    {
        var qr = new QuotaRouterConfig
        {
            FloorByPool =
            {
                ["ghost"] = new QuotaPoolFloorConfig { MinQuotaPct = 10 },
            },
        };

        var ex = Assert.Throws<InvalidOperationException>(() => QuotaRouterConfigMapper.ToOptions(qr));
        Assert.Contains("ghost", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HotReload_CarriesPoolsAndFloors_AndRejectsUnitMismatch()
    {
        var dst = new QuotaRouterOptions();
        var src = new QuotaRouterConfig
        {
            Pools =
            {
                ["p"] = new QuotaPoolConfig { Kind = "DepletingBalance", BalanceUnit = "credits" },
            },
            FloorByPool =
            {
                ["p"] = new QuotaPoolFloorConfig { MinBalance = 10 },
            },
        };

        QuotaRouterConfigMapper.ApplyHotReload(dst, src);

        Assert.Equal(QuotaPoolKind.DepletingBalance, dst.Pools["p"].Kind);
        Assert.Equal("credits", dst.Pools["p"].BalanceUnit);
        Assert.Equal(10, dst.FloorByPool["p"].MinBalance);

        var bad = new QuotaRouterConfig
        {
            Pools =
            {
                ["p"] = new QuotaPoolConfig { Kind = "DepletingBalance" },
            },
            FloorByPool =
            {
                ["p"] = new QuotaPoolFloorConfig { MinQuotaPct = 10 },
            },
        };
        var ex = Assert.Throws<InvalidOperationException>(() => QuotaRouterConfigMapper.ApplyHotReload(dst, bad));
        Assert.Contains("'p'", ex.Message, StringComparison.Ordinal);
        // The failed reload leaves the last good floor in place.
        Assert.Equal(10, dst.FloorByPool["p"].MinBalance);
    }

    // ── 9. Zero balance ⇒ terminal refusal, never a reset park ───────────────

    [Fact]
    public async Task ZeroBalancePool_YieldsTerminalRefusalWithNoResetPark()
    {
        var opts = PoolOpts(o =>
        {
            o.Pools["credits"] = new QuotaPoolOptions
            {
                Name = "credits",
                Kind = QuotaPoolKind.DepletingBalance,
                BalanceUnit = "credits",
            };
            o.FloorByPool["credits"] = new QuotaPoolFloorOptions { MinBalance = 0 };
        });
        var probe = new MutableProbe(Codex, Balance(0));
        var member = Sub(Codex, pool: "credits");
        var router = BuildRouter([PoolClass("c", member)], [probe], opts);
        var policy = new QuotaGatePolicy(opts);

        var gate = policy.Evaluate(
            member,
            new EffectiveQuota(-1, null, null, BalanceRemaining: 0, PoolId: "credits", PoolKind: QuotaPoolKind.DepletingBalance),
            Now,
            recentObservedFailure: false);
        Assert.False(gate.Allow);
        Assert.True(gate.Terminal);
        Assert.Contains("credits", gate.Reason, StringComparison.Ordinal);

        var decision = await router.ResolveAsync(MakeItem("c"), null, CancellationToken.None);
        Assert.Null(decision.Chosen);
        Assert.False(decision.ShouldWait);
        Assert.True(decision.TerminalQuotaExhausted);
        Assert.Contains("credits", decision.Reason, StringComparison.Ordinal);

        // No reset exists to park on: the earliest-reset hint is null.
        Assert.Null(await router.ComputeEarliestExhaustedResetAsync(MakeItem("c"), null, CancellationToken.None));
    }

    // ── 10. Balance pools never report a reset instant ───────────────────────

    [Fact]
    public void BalancePool_NeverReportedWithResetInstant()
    {
        var reset = new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.Zero);
        var opts = PoolOpts(o =>
        {
            o.Pools["credits"] = new QuotaPoolOptions
            {
                Name = "credits",
                Kind = QuotaPoolKind.DepletingBalance,
                BalanceUnit = "credits",
            };
        });
        var policy = new QuotaGatePolicy(opts);
        var member = Sub(Codex, pool: "credits");
        var snapshot = new AgentQuotaSnapshot
        {
            AvailablePct = -1,
            ResetAt = reset,
            BalanceRemaining = 250,
            BalanceUnit = "credits",
            Windows = [new WindowQuota { Name = "w", AvailablePct = -1, ResetAt = reset }],
            PerModel =
                new Dictionary<string, ModelQuota>(StringComparer.OrdinalIgnoreCase)
                {
                    ["m"] = new() { AvailablePct = -1, ResetAt = reset },
                },
        };

        var quota = policy.ResolvePoolQuota(snapshot, member);
        Assert.Null(quota.ResetAt);

        var masked = QuotaPoolMasks.WithoutResetInstants(snapshot);
        Assert.Null(masked.ResetAt);
        Assert.All(masked.Windows, w => Assert.Null(w.ResetAt));
        Assert.All(masked.PerModel.Values, m => Assert.Null(m.ResetAt));
        Assert.Equal(250, masked.BalanceRemaining);

        var gate = policy.Evaluate(member, quota, Now, recentObservedFailure: false);
        Assert.True(gate.Allow);
        Assert.Null(QuotaGatePolicy.ResolveResetHint(quota, gate));
    }
}
