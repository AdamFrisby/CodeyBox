using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Verification for executor-reported quota: pools whose credential lives on
/// an executor host are metered from that host's reported readings instead of
/// a direct orchestrator probe. Covers the six acceptance behaviours:
/// orchestrator-held pools behave exactly as today; executor-held pools gate
/// identically to the same reading obtained directly; reports from
/// non-holders are rejected without mutating the stored reading; silence
/// becomes unknown within the staleness bound (fail-closed on a non-zero
/// floor); out-of-range or reset-inconsistent readings are rejected without
/// being stored; and admission is decided by the orchestrator in both modes.
/// </summary>
public sealed class ExecutorQuotaReportTests
{
    private static readonly AgentKind Claude = AgentKind.Claude;
    private static readonly DateTimeOffset T0 = new(2026, 7, 3, 12, 0, 0, TimeSpan.Zero);

    private sealed class AdvancingClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }

    private sealed class MutableProbe(AgentKind kind, AgentQuotaSnapshot snapshot) : IAgentQuotaProbe
    {
        public AgentKind Kind { get; } = kind;
        public AgentQuotaSnapshot Current { get; set; } = snapshot;
        public int CallCount { get; private set; }

        public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(Current);
        }
    }

    private static AgentMembership Sub(AgentKind agent, string? pool, int score = 100) => new()
    {
        Agent = agent,
        Billing = AgentBilling.Subscription,
        QualityScore = score,
        Pool = pool,
    };

    private static AgentClass SoloClass(string id, AgentMembership member) => new()
    {
        Id = id,
        DisplayName = id,
        Members = [member],
    };

    private static WorkItem MakeItem(string? classId = null) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("proj"),
        Title = "t",
        Prompt = "p",
        AgentClassId = classId,
    };

    private static QuotaRouterOptions BaseOpts() => new()
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

    private static void ResettingPool(
        QuotaRouterOptions opts,
        string name,
        double floor,
        QuotaProbeSource source = QuotaProbeSource.OrchestratorDirect,
        string[]? holders = null,
        TimeSpan? maxAge = null)
    {
        opts.Pools[name] = new QuotaPoolOptions
        {
            Name = name,
            Kind = QuotaPoolKind.ResettingWindow,
            ProbeSource = source,
            ReportedReadingMaxAge = maxAge ?? QuotaRouterDefaults.DefaultReportedReadingMaxAge,
            HolderHostIds = holders is null ? [] : [.. holders],
        };
        opts.FloorByPool[name] = new QuotaPoolFloorOptions
        {
            MinQuotaPct = floor,
            StartFloorPct = floor,
            EndFloorPct = floor,
        };
    }

    private static void BalancePool(
        QuotaRouterOptions opts,
        string name,
        double floor,
        QuotaProbeSource source = QuotaProbeSource.OrchestratorDirect,
        string[]? holders = null)
    {
        opts.Pools[name] = new QuotaPoolOptions
        {
            Name = name,
            Kind = QuotaPoolKind.DepletingBalance,
            BalanceUnit = "credits",
            ProbeSource = source,
            HolderHostIds = holders is null ? [] : [.. holders],
        };
        opts.FloorByPool[name] = new QuotaPoolFloorOptions { MinBalance = floor };
    }

    private static AgentClassRouter BuildRouter(
        AgentClass catalog,
        IEnumerable<IAgentQuotaProbe> probes,
        QuotaRouterOptions opts,
        AdvancingClock clock,
        ExecutorQuotaReportStore? store) =>
        new(
            [catalog],
            probes,
            opts,
            NullLogger<AgentClassRouter>.Instance,
            timeProvider: clock,
            reportStore: store);

    private static ExecutorQuotaReport PctReport(string pool, double pct, DateTimeOffset observed) => new()
    {
        PoolName = pool,
        AvailablePct = pct,
        ObservedAt = observed,
    };

    // ── 1. Orchestrator-held pools behave exactly as today ───────────────────

    [Fact]
    public async Task OrchestratorDirectPool_ProbesDirectlyAndRejectsExecutorReports()
    {
        var clock = new AdvancingClock(T0);
        var opts = BaseOpts();
        ResettingPool(opts, "local", floor: 20);
        var probe = new MutableProbe(Claude, new AgentQuotaSnapshot { AvailablePct = 50 });
        var store = new ExecutorQuotaReportStore(opts, clock);
        var router = BuildRouter(SoloClass("c", Sub(Claude, "local")), [probe], opts, clock, store);

        var decision = await router.ResolveAsync(MakeItem("c"), null, CancellationToken.None);

        Assert.NotNull(decision.Chosen);
        Assert.Equal(1, probe.CallCount);

        Assert.False(store.TryReport("exec-1", PctReport("local", 90, clock.GetUtcNow()), out var reason));
        Assert.Contains("orchestrator-probed", reason, StringComparison.OrdinalIgnoreCase);
        Assert.False(store.TryGetStored("local", out _, out _));

        var again = await router.ResolveAsync(MakeItem("c"), null, CancellationToken.None);
        Assert.NotNull(again.Chosen);
        Assert.Equal(2, probe.CallCount);
    }

    // ── 2. Executor-held pools gate identically to the same direct reading ───

    [Fact]
    public async Task ExecutorReportedPool_GateMatchesDirectProbeForSameReading()
    {
        foreach (var pct in new[] { 80.0, 10.0 })
        {
            var clock = new AdvancingClock(T0);

            var directOpts = BaseOpts();
            ResettingPool(directOpts, "pool", floor: 20);
            var directProbe = new MutableProbe(Claude, new AgentQuotaSnapshot { AvailablePct = pct });
            var directRouter = BuildRouter(
                SoloClass("c", Sub(Claude, "pool")), [directProbe], directOpts, clock, null);
            var direct = await directRouter.ResolveAsync(MakeItem("c"), null, CancellationToken.None);

            var execOpts = BaseOpts();
            ResettingPool(execOpts, "pool", floor: 20,
                source: QuotaProbeSource.ExecutorReported, holders: ["exec-1"]);
            var execProbe = new MutableProbe(Claude, new AgentQuotaSnapshot { AvailablePct = -1 });
            var execStore = new ExecutorQuotaReportStore(execOpts, clock);
            Assert.True(execStore.TryReport("exec-1", PctReport("pool", pct, clock.GetUtcNow()), out _));
            var execRouter = BuildRouter(
                SoloClass("c", Sub(Claude, "pool")), [execProbe], execOpts, clock, execStore);
            var reported = await execRouter.ResolveAsync(MakeItem("c"), null, CancellationToken.None);

            Assert.Equal(direct.Chosen?.RouteKey, reported.Chosen?.RouteKey);
            Assert.Equal(direct.ShouldWait, reported.ShouldWait);
            Assert.Equal(0, execProbe.CallCount);
            if (pct >= 20)
                Assert.NotNull(reported.Chosen);
            else
            {
                Assert.Null(reported.Chosen);
                Assert.True(reported.ShouldWait);
            }
        }
    }

    [Fact]
    public async Task ExecutorReportedBalancePool_GateMatchesDirectProbeForSameBalance()
    {
        foreach (var (balance, expectChosen) in new[] { (1000.0, true), (100.0, false) })
        {
            var clock = new AdvancingClock(T0);

            var directOpts = BaseOpts();
            BalancePool(directOpts, "prepaid", floor: 500);
            var directProbe = new MutableProbe(Claude,
                new AgentQuotaSnapshot { AvailablePct = -1, BalanceRemaining = balance, BalanceUnit = "credits" });
            var directRouter = BuildRouter(
                SoloClass("c", Sub(Claude, "prepaid")), [directProbe], directOpts, clock, null);
            var direct = await directRouter.ResolveAsync(MakeItem("c"), null, CancellationToken.None);

            var execOpts = BaseOpts();
            BalancePool(execOpts, "prepaid", floor: 500,
                source: QuotaProbeSource.ExecutorReported, holders: ["exec-1"]);
            var execProbe = new MutableProbe(Claude,
                AgentQuotaSnapshot.UnknownSnapshot(QuotaUnknownReason.NoCredential, "no local credential"));
            var execStore = new ExecutorQuotaReportStore(execOpts, clock);
            Assert.True(execStore.TryReport("exec-1",
                new ExecutorQuotaReport
                {
                    PoolName = "prepaid",
                    BalanceRemaining = balance,
                    ObservedAt = clock.GetUtcNow(),
                }, out _));
            var execRouter = BuildRouter(
                SoloClass("c", Sub(Claude, "prepaid")), [execProbe], execOpts, clock, execStore);
            var reported = await execRouter.ResolveAsync(MakeItem("c"), null, CancellationToken.None);

            Assert.Equal(direct.Chosen?.RouteKey, reported.Chosen?.RouteKey);
            Assert.Equal(direct.ShouldWait, reported.ShouldWait);
            Assert.Equal(expectChosen, reported.Chosen is not null);
            Assert.Equal(0, execProbe.CallCount);
        }
    }

    // ── 3. Non-holder reports are rejected; the stored reading is unchanged ──

    [Fact]
    public void ReportFromUndeclaredHost_IsRejectedAndStoredReadingUnchanged()
    {
        var clock = new AdvancingClock(T0);
        var opts = BaseOpts();
        ResettingPool(opts, "pool", floor: 20,
            source: QuotaProbeSource.ExecutorReported, holders: ["exec-1"]);
        var store = new ExecutorQuotaReportStore(opts, clock);
        Assert.True(store.TryReport("exec-1", PctReport("pool", 80, clock.GetUtcNow()), out _));

        foreach (var impostor in new[] { "exec-2", "exec-1x", "EXEC-1" })
        {
            Assert.False(store.TryReport(impostor, PctReport("pool", 5, clock.GetUtcNow()), out var reason));
            Assert.Contains("not declared as holding", reason, StringComparison.OrdinalIgnoreCase);
        }

        foreach (var empty in new[] { "", "  " })
        {
            Assert.False(store.TryReport(empty, PctReport("pool", 5, clock.GetUtcNow()), out var reason));
            Assert.Contains("host id is required", reason, StringComparison.OrdinalIgnoreCase);
        }

        Assert.True(store.TryGetStored("pool", out var stored, out var holder));
        Assert.NotNull(stored);
        Assert.Equal(80, stored!.AvailablePct);
        Assert.Equal("exec-1", holder);
        Assert.Equal(80, store.GetSnapshot("pool").AvailablePct);
    }

    // ── 4. Silence becomes unknown within the staleness bound ────────────────

    [Fact]
    public async Task SilentExecutor_BecomesUnknownWithinStalenessBound_AndFailClosed()
    {
        var clock = new AdvancingClock(T0);
        var opts = BaseOpts();
        ResettingPool(opts, "pool", floor: 20,
            source: QuotaProbeSource.ExecutorReported, holders: ["exec-1"],
            maxAge: TimeSpan.FromMinutes(5));
        var probe = new MutableProbe(Claude,
            AgentQuotaSnapshot.UnknownSnapshot(QuotaUnknownReason.NoCredential, "no local credential"));
        var store = new ExecutorQuotaReportStore(opts, clock);
        var router = BuildRouter(SoloClass("c", Sub(Claude, "pool")), [probe], opts, clock, store);

        Assert.True(store.TryReport("exec-1", PctReport("pool", 80, clock.GetUtcNow()), out _));
        var healthy = await router.ResolveAsync(MakeItem("c"), null, CancellationToken.None);
        Assert.NotNull(healthy.Chosen);

        clock.Advance(TimeSpan.FromMinutes(5).Add(TimeSpan.FromSeconds(1)));
        var stale = store.GetSnapshot("pool");
        Assert.False(stale.IsKnown);
        Assert.Equal(QuotaUnknownReason.Transient, stale.Unknown);

        var denied = await router.ResolveAsync(MakeItem("c"), null, CancellationToken.None);
        Assert.Null(denied.Chosen);
        Assert.True(denied.ShouldWait);
        Assert.Contains("floor", denied.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, probe.CallCount);
    }

    [Fact]
    public async Task NeverReportedExecutorPool_ReadsUnknownAndFailClosedOnFloor()
    {
        var clock = new AdvancingClock(T0);
        var opts = BaseOpts();
        ResettingPool(opts, "pool", floor: 20,
            source: QuotaProbeSource.ExecutorReported, holders: ["exec-1"]);
        var probe = new MutableProbe(Claude,
            AgentQuotaSnapshot.UnknownSnapshot(QuotaUnknownReason.NoCredential, "no local credential"));
        var store = new ExecutorQuotaReportStore(opts, clock);
        var router = BuildRouter(SoloClass("c", Sub(Claude, "pool")), [probe], opts, clock, store);

        var missing = store.GetSnapshot("pool");
        Assert.False(missing.IsKnown);
        Assert.Equal(QuotaUnknownReason.Transient, missing.Unknown);

        var denied = await router.ResolveAsync(MakeItem("c"), null, CancellationToken.None);
        Assert.Null(denied.Chosen);
        Assert.True(denied.ShouldWait);
    }

    [Fact]
    public void FreshExecutorUnknown_PreservesReasonLikeDirectProbe()
    {
        var clock = new AdvancingClock(T0);
        var opts = BaseOpts();
        ResettingPool(opts, "pool", floor: 20,
            source: QuotaProbeSource.ExecutorReported, holders: ["exec-1"]);
        var store = new ExecutorQuotaReportStore(opts, clock);

        foreach (var reason in new[] { QuotaUnknownReason.Transient, QuotaUnknownReason.Permanent, QuotaUnknownReason.NoCredential })
        {
            Assert.True(store.TryReport("exec-1",
                new ExecutorQuotaReport
                {
                    PoolName = "pool",
                    ObservedAt = clock.GetUtcNow(),
                    Unknown = reason,
                }, out _));
            var snapshot = store.GetSnapshot("pool");
            Assert.False(snapshot.IsKnown);
            Assert.Equal(reason, snapshot.Unknown);
        }
    }

    // ── 5. Out-of-range / reset-inconsistent readings are rejected ───────────

    [Theory]
    [InlineData(101.0)]
    [InlineData(-0.5)]
    public void OutOfRangePercentage_IsRejectedWithoutStoring(double pct)
    {
        RejectPct(pct);
    }

    [Fact]
    public void NonFinitePercentage_IsRejectedWithoutStoring()
    {
        RejectPct(double.NaN);
        RejectPct(double.PositiveInfinity);
        RejectPct(double.NegativeInfinity);
    }

    private static void RejectPct(double pct)
    {
        var clock = new AdvancingClock(T0);
        var opts = BaseOpts();
        ResettingPool(opts, "pool", floor: 20,
            source: QuotaProbeSource.ExecutorReported, holders: ["exec-1"]);
        var store = new ExecutorQuotaReportStore(opts, clock);
        Assert.True(store.TryReport("exec-1", PctReport("pool", 60, clock.GetUtcNow()), out _));

        Assert.False(store.TryReport("exec-1",
            new ExecutorQuotaReport { PoolName = "pool", AvailablePct = pct, ObservedAt = clock.GetUtcNow() },
            out var reason));
        Assert.Contains("0-100", reason, StringComparison.Ordinal);

        Assert.True(store.TryGetStored("pool", out var stored, out _));
        Assert.Equal(60, stored!.AvailablePct);
    }

    [Fact]
    public void MissingPercentageOnResettingPool_IsRejectedWithoutStoring()
    {
        var clock = new AdvancingClock(T0);
        var opts = BaseOpts();
        ResettingPool(opts, "pool", floor: 20,
            source: QuotaProbeSource.ExecutorReported, holders: ["exec-1"]);
        var store = new ExecutorQuotaReportStore(opts, clock);
        Assert.True(store.TryReport("exec-1", PctReport("pool", 60, clock.GetUtcNow()), out _));

        Assert.False(store.TryReport("exec-1",
            new ExecutorQuotaReport { PoolName = "pool", ObservedAt = clock.GetUtcNow() },
            out _));

        Assert.True(store.TryGetStored("pool", out var stored, out _));
        Assert.Equal(60, stored!.AvailablePct);
    }

    [Fact]
    public void InvalidBalance_IsRejectedWithoutStoring()
    {
        RejectBalance(-1.0);
        RejectBalance(double.NaN);
        RejectBalance(double.PositiveInfinity);
    }

    private static void RejectBalance(double balance)
    {
        var clock = new AdvancingClock(T0);
        var opts = BaseOpts();
        BalancePool(opts, "prepaid", floor: 100,
            source: QuotaProbeSource.ExecutorReported, holders: ["exec-1"]);
        var store = new ExecutorQuotaReportStore(opts, clock);
        Assert.True(store.TryReport("exec-1",
            new ExecutorQuotaReport
            {
                PoolName = "prepaid",
                BalanceRemaining = 1000,
                ObservedAt = clock.GetUtcNow(),
            }, out _));

        Assert.False(store.TryReport("exec-1",
            new ExecutorQuotaReport
            {
                PoolName = "prepaid",
                BalanceRemaining = balance,
                ObservedAt = clock.GetUtcNow(),
            }, out var reason));
        Assert.Contains("balance", reason, StringComparison.OrdinalIgnoreCase);

        Assert.True(store.TryGetStored("prepaid", out var stored, out _));
        Assert.Equal(1000, stored!.BalanceRemaining);
    }

    [Fact]
    public void ResetOnBalancePool_IsRejectedWithoutStoring()
    {
        var clock = new AdvancingClock(T0);
        var opts = BaseOpts();
        BalancePool(opts, "prepaid", floor: 100,
            source: QuotaProbeSource.ExecutorReported, holders: ["exec-1"]);
        var store = new ExecutorQuotaReportStore(opts, clock);
        Assert.True(store.TryReport("exec-1",
            new ExecutorQuotaReport
            {
                PoolName = "prepaid",
                BalanceRemaining = 1000,
                ObservedAt = clock.GetUtcNow(),
            }, out _));

        Assert.False(store.TryReport("exec-1",
            new ExecutorQuotaReport
            {
                PoolName = "prepaid",
                BalanceRemaining = 900,
                ResetAt = clock.GetUtcNow().AddDays(7),
                ObservedAt = clock.GetUtcNow(),
            }, out var reason));
        Assert.Contains("reset", reason, StringComparison.OrdinalIgnoreCase);

        Assert.True(store.TryGetStored("prepaid", out var stored, out _));
        Assert.Equal(1000, stored!.BalanceRemaining);
        Assert.Null(store.GetSnapshot("prepaid").ResetAt);
    }

    [Fact]
    public void ResetOnResettingPool_IsAccepted()
    {
        var clock = new AdvancingClock(T0);
        var opts = BaseOpts();
        ResettingPool(opts, "pool", floor: 20,
            source: QuotaProbeSource.ExecutorReported, holders: ["exec-1"]);
        var store = new ExecutorQuotaReportStore(opts, clock);
        var reset = clock.GetUtcNow().AddDays(7);

        Assert.True(store.TryReport("exec-1",
            new ExecutorQuotaReport
            {
                PoolName = "pool",
                AvailablePct = 60,
                ResetAt = reset,
                ObservedAt = clock.GetUtcNow(),
            }, out _));
        Assert.Equal(reset, store.GetSnapshot("pool").ResetAt);
    }

    [Fact]
    public void FutureObservedBeyondSkew_AndUnknownPool_AreRejected()
    {
        var clock = new AdvancingClock(T0);
        var opts = BaseOpts();
        ResettingPool(opts, "pool", floor: 20,
            source: QuotaProbeSource.ExecutorReported, holders: ["exec-1"]);
        var store = new ExecutorQuotaReportStore(opts, clock);

        Assert.False(store.TryReport("exec-1",
            PctReport("pool", 60, clock.GetUtcNow().AddHours(1)), out var futureReason));
        Assert.Contains("future", futureReason, StringComparison.OrdinalIgnoreCase);

        Assert.False(store.TryReport("exec-1",
            PctReport("ghost", 60, clock.GetUtcNow()), out var ghostReason));
        Assert.Contains("no configured quota pool", ghostReason, StringComparison.OrdinalIgnoreCase);

        Assert.False(store.TryGetStored("pool", out _, out _));
    }

    // ── 6. Admission is decided by the orchestrator in both modes ────────────

    [Fact]
    public async Task HealthyExecutorReport_DoesNotAdmitPastOrchestratorFloor()
    {
        var clock = new AdvancingClock(T0);
        var opts = BaseOpts();
        ResettingPool(opts, "pool", floor: 20,
            source: QuotaProbeSource.ExecutorReported, holders: ["exec-1"]);
        var probe = new MutableProbe(Claude,
            AgentQuotaSnapshot.UnknownSnapshot(QuotaUnknownReason.NoCredential, "no local credential"));
        var store = new ExecutorQuotaReportStore(opts, clock);
        Assert.True(store.TryReport("exec-1", PctReport("pool", 80, clock.GetUtcNow()), out _));
        var router = BuildRouter(SoloClass("c", Sub(Claude, "pool")), [probe], opts, clock, store);

        var admitted = await router.ResolveAsync(MakeItem("c"), null, CancellationToken.None);
        Assert.NotNull(admitted.Chosen);

        opts.FloorByPool["pool"] = new QuotaPoolFloorOptions
        {
            MinQuotaPct = 90,
            StartFloorPct = 90,
            EndFloorPct = 90,
        };
        var denied = await router.ResolveAsync(MakeItem("c"), null, CancellationToken.None);
        Assert.Null(denied.Chosen);
        Assert.True(denied.ShouldWait);
        Assert.Equal(80, store.GetSnapshot("pool").AvailablePct);
    }

    [Fact]
    public void ReportStore_ExposesNoAdmissionDecision()
    {
        var admissionLike = typeof(ExecutorQuotaReportStore)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(m => m.Name.Contains("Allow", StringComparison.OrdinalIgnoreCase)
                || m.Name.Contains("Admit", StringComparison.OrdinalIgnoreCase)
                || m.Name.Contains("Gate", StringComparison.OrdinalIgnoreCase)
                || m.Name.Contains("Decide", StringComparison.OrdinalIgnoreCase)
                || m.ReturnType == typeof(QuotaGateDecision))
            .Select(m => m.Name)
            .ToArray();
        Assert.Empty(admissionLike);
    }

    // ── Configuration mapping ────────────────────────────────────────────────

    [Fact]
    public void PoolConfig_MapsProbeSourceHoldersAndStaleness()
    {
        var config = new QuotaRouterConfig
        {
            Pools =
            {
                ["remote"] = new QuotaPoolConfig
                {
                    Kind = "ResettingWindow",
                    ProbeSource = "ExecutorReported",
                    ReportedReadingMaxAgeSeconds = 120,
                    HolderHostIds = ["exec-1", "exec-2"],
                },
                ["local"] = new QuotaPoolConfig { Kind = "ResettingWindow" },
            },
        };

        var mapped = QuotaRouterConfigMapper.ToOptions(config);

        Assert.Equal(QuotaProbeSource.ExecutorReported, mapped.Pools["remote"].ProbeSource);
        Assert.Equal(TimeSpan.FromSeconds(120), mapped.Pools["remote"].ReportedReadingMaxAge);
        Assert.Equal(new[] { "exec-1", "exec-2" }, mapped.Pools["remote"].HolderHostIds);
        Assert.Equal(QuotaProbeSource.OrchestratorDirect, mapped.Pools["local"].ProbeSource);
        Assert.Equal(
            QuotaRouterDefaults.DefaultReportedReadingMaxAge,
            mapped.Pools["local"].ReportedReadingMaxAge);
        Assert.Empty(mapped.Pools["local"].HolderHostIds);
    }

    [Theory]
    [InlineData("executorreported")]
    [InlineData("EXECUTORREPORTED")]
    [InlineData(" ExecutorReported ")]
    public void PoolConfig_ProbeSourceParsesCaseInsensitively(string source)
    {
        var config = new QuotaRouterConfig
        {
            Pools = { ["p"] = new QuotaPoolConfig { Kind = "ResettingWindow", ProbeSource = source } },
        };
        Assert.Equal(QuotaProbeSource.ExecutorReported, QuotaRouterConfigMapper.ToOptions(config).Pools["p"].ProbeSource);
    }

    [Fact]
    public void PoolConfig_RejectsUnknownProbeSourceNegativeStalenessAndEmptyHolder()
    {
        Assert.Throws<InvalidOperationException>(() => QuotaRouterConfigMapper.ToOptions(new QuotaRouterConfig
        {
            Pools = { ["p"] = new QuotaPoolConfig { Kind = "ResettingWindow", ProbeSource = "SomewhereElse" } },
        }));
        Assert.Throws<InvalidOperationException>(() => QuotaRouterConfigMapper.ToOptions(new QuotaRouterConfig
        {
            Pools =
            {
                ["p"] = new QuotaPoolConfig
                {
                    Kind = "ResettingWindow",
                    ProbeSource = "ExecutorReported",
                    ReportedReadingMaxAgeSeconds = -5,
                },
            },
        }));
        Assert.Throws<InvalidOperationException>(() => QuotaRouterConfigMapper.ToOptions(new QuotaRouterConfig
        {
            Pools =
            {
                ["p"] = new QuotaPoolConfig
                {
                    Kind = "ResettingWindow",
                    ProbeSource = "ExecutorReported",
                    HolderHostIds = ["exec-1", ""],
                },
            },
        }));
    }

    // ── Bounded resets, bounded notes, kind-change safety ────────────────────

    [Fact]
    public void FarFutureReset_IsRejectedWithoutStoring()
    {
        var clock = new AdvancingClock(T0);
        var opts = BaseOpts();
        ResettingPool(opts, "pool", floor: 20,
            source: QuotaProbeSource.ExecutorReported, holders: ["exec-1"]);
        var store = new ExecutorQuotaReportStore(opts, clock);
        Assert.True(store.TryReport("exec-1", PctReport("pool", 60, clock.GetUtcNow()), out _));

        Assert.False(store.TryReport("exec-1",
            new ExecutorQuotaReport
            {
                PoolName = "pool",
                AvailablePct = 60,
                ResetAt = clock.GetUtcNow().AddDays(30),
                ObservedAt = clock.GetUtcNow(),
            }, out var reason));
        Assert.Contains("horizon", reason, StringComparison.OrdinalIgnoreCase);

        Assert.True(store.TryGetStored("pool", out var stored, out _));
        Assert.Equal(60, stored!.AvailablePct);
        Assert.Null(stored.ResetAt);
    }

    [Fact]
    public void LongPastReset_IsRejectedWithoutStoring()
    {
        var clock = new AdvancingClock(T0);
        var opts = BaseOpts();
        ResettingPool(opts, "pool", floor: 20,
            source: QuotaProbeSource.ExecutorReported, holders: ["exec-1"]);
        var store = new ExecutorQuotaReportStore(opts, clock);
        Assert.True(store.TryReport("exec-1", PctReport("pool", 60, clock.GetUtcNow()), out _));

        Assert.False(store.TryReport("exec-1",
            new ExecutorQuotaReport
            {
                PoolName = "pool",
                AvailablePct = 60,
                ResetAt = clock.GetUtcNow().AddDays(-2),
                ObservedAt = clock.GetUtcNow(),
            }, out var reason));
        Assert.Contains("observed", reason, StringComparison.OrdinalIgnoreCase);

        Assert.True(store.TryGetStored("pool", out var stored, out _));
        Assert.Equal(60, stored!.AvailablePct);
    }

    [Fact]
    public void OversizedNotes_AreRejectedWithoutStoring()
    {
        var clock = new AdvancingClock(T0);
        var opts = BaseOpts();
        ResettingPool(opts, "pool", floor: 20,
            source: QuotaProbeSource.ExecutorReported, holders: ["exec-1"]);
        var store = new ExecutorQuotaReportStore(opts, clock);
        Assert.True(store.TryReport("exec-1", PctReport("pool", 60, clock.GetUtcNow()), out _));

        Assert.False(store.TryReport("exec-1",
            new ExecutorQuotaReport
            {
                PoolName = "pool",
                AvailablePct = 60,
                ObservedAt = clock.GetUtcNow(),
                Notes = new string('n', ExecutorQuotaReportStore.MaxReportNotesLength + 1),
            }, out var reason));
        Assert.Contains("notes", reason, StringComparison.OrdinalIgnoreCase);

        Assert.True(store.TryGetStored("pool", out var stored, out _));
        Assert.Equal(60, stored!.AvailablePct);
    }

    [Fact]
    public void NotesWithControlCharacters_AreRejectedWithoutStoring()
    {
        var clock = new AdvancingClock(T0);
        var opts = BaseOpts();
        ResettingPool(opts, "pool", floor: 20,
            source: QuotaProbeSource.ExecutorReported, holders: ["exec-1"]);
        var store = new ExecutorQuotaReportStore(opts, clock);
        Assert.True(store.TryReport("exec-1", PctReport("pool", 60, clock.GetUtcNow()), out _));

        Assert.False(store.TryReport("exec-1",
            new ExecutorQuotaReport
            {
                PoolName = "pool",
                AvailablePct = 60,
                ObservedAt = clock.GetUtcNow(),
                Notes = "probe ok\u0000injected",
            }, out var reason));
        Assert.Contains("control", reason, StringComparison.OrdinalIgnoreCase);

        Assert.True(store.TryGetStored("pool", out var stored, out _));
        Assert.Equal(60, stored!.AvailablePct);
    }

    [Fact]
    public void BalanceReportFollowedByKindHotReload_ReadsTransientUnknownInsteadOfThrowing()
    {
        var clock = new AdvancingClock(T0);
        var opts = BaseOpts();
        BalancePool(opts, "prepaid", floor: 100,
            source: QuotaProbeSource.ExecutorReported, holders: ["exec-1"]);
        var store = new ExecutorQuotaReportStore(opts, clock);
        Assert.True(store.TryReport("exec-1",
            new ExecutorQuotaReport
            {
                PoolName = "prepaid",
                BalanceRemaining = 1000,
                ObservedAt = clock.GetUtcNow(),
            }, out _));

        opts.Pools["prepaid"].Kind = QuotaPoolKind.ResettingWindow;

        var snapshot = store.GetSnapshot("prepaid");
        Assert.False(snapshot.IsKnown);
        Assert.Equal(QuotaUnknownReason.Transient, snapshot.Unknown);
    }
}
