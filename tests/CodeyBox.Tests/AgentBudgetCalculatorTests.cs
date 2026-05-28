using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class AgentBudgetCalculatorTests
{
    private static readonly AgentKind Opencode = AgentKind.Opencode;

    private static AgentBudgetOptions Opts(params AgentBudgetWindowOptions[] windows)
    {
        var opts = new AgentBudgetOptions();
        opts.Members["opencode"] = new AgentBudgetMemberOptions
        {
            Models = { ["m1"] = new AgentBudgetModelOptions { Windows = windows.ToList() } },
        };
        return opts;
    }

    private static AgentBudgetWindowOptions Rolling(int hours, double limitCents) =>
        new() { Kind = BudgetWindowKind.Rolling, Hours = hours, LimitCents = limitCents };

    private static AgentBudgetCalculator Build(FakeUsageStore store, AgentBudgetOptions opts, TimeProvider? time = null) =>
        new(store, opts, NullLogger<AgentBudgetCalculator>.Instance, time);

    [Fact]
    public async Task NotConfigured_ReturnsNull()
    {
        var calc = Build(new FakeUsageStore(), Opts(Rolling(5, 200)));
        Assert.Null(await calc.GetBudgetSnapshotAsync(AgentKind.Claude, "whatever"));
        Assert.Null(await calc.GetBudgetSnapshotAsync(Opencode, "unknown-model"));
        Assert.Null(await calc.GetBudgetSnapshotAsync(Opencode, null));
    }

    [Fact]
    public async Task EmptyTable_Bootstrap_ReturnsHundredPercent()
    {
        var store = new FakeUsageStore(); // returns zero for everything
        var calc = Build(store, Opts(Rolling(5, 200)));

        var snapshot = await calc.GetBudgetSnapshotAsync(Opencode, "m1");

        Assert.NotNull(snapshot);
        Assert.Equal(100.0, snapshot!.AvailablePct);
    }

    [Fact]
    public async Task SingleWindow_HalfSpent_ReturnsFiftyPercentRemaining()
    {
        // LimitCents 200 → 2_000_000 microcents; 1_000_000 spent = 50% used.
        var store = new FakeUsageStore { DefaultSum = 1_000_000 };
        var calc = Build(store, Opts(Rolling(5, 200)));

        var snapshot = await calc.GetBudgetSnapshotAsync(Opencode, "m1");

        Assert.NotNull(snapshot);
        Assert.Equal(50.0, snapshot!.AvailablePct, precision: 6);
    }

    [Fact]
    public async Task SingleWindow_NinetyPercentSpent_ReturnsTenPercentRemaining()
    {
        // Asymmetric spend so the remaining figure diverges from the used figure:
        // 90% used must yield 10% remaining. The 50% boundary test cannot catch a
        // percentUsed/percentRemaining inversion (both formulas give 50 there);
        // this asymmetric point only passes when AvailablePct == 100 - percentUsed.
        // LimitCents 200 → 2_000_000 microcents; 1_800_000 spent = 90% used.
        var store = new FakeUsageStore { DefaultSum = 1_800_000 };
        var calc = Build(store, Opts(Rolling(5, 200)));

        var snapshot = await calc.GetBudgetSnapshotAsync(Opencode, "m1");

        Assert.NotNull(snapshot);
        Assert.Equal(10.0, snapshot!.AvailablePct, precision: 6);
    }

    [Fact]
    public async Task SumWindowCancelled_PropagatesRatherThanFailClosed()
    {
        // A cancelled token during the usage query (shutdown/abort) is NOT an
        // accounting outage. ComputeAsync's catch filters
        // `when ex is not OperationCanceledException`, so cancellation must
        // propagate so dispatch unwinds cleanly — rather than being swallowed and
        // returning AvailablePct=0, which would park dispatch as fail-closed.
        var store = new FakeUsageStore
        {
            Responder = (_, _, _, _) => throw new OperationCanceledException(),
        };
        var calc = Build(store, Opts(Rolling(5, 200)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => calc.GetBudgetSnapshotAsync(Opencode, "m1"));
    }

    [Fact]
    public async Task Overspent_ClampsToZero()
    {
        var store = new FakeUsageStore { DefaultSum = 9_000_000 }; // way over 2_000_000 cap
        var calc = Build(store, Opts(Rolling(5, 200)));

        var snapshot = await calc.GetBudgetSnapshotAsync(Opencode, "m1");

        Assert.Equal(0.0, snapshot!.AvailablePct);
    }

    [Fact]
    public async Task MultipleWindows_AvailablePctIsMinAcrossWindows()
    {
        // Rolling (span < 1 day) → 50% remaining; Monthly (span >= 27 days) → 90% remaining.
        var store = new FakeUsageStore
        {
            Responder = (_, _, from, to) =>
                (to - from) < TimeSpan.FromDays(1)
                    ? new AgentUsageWindowAggregate(1_000_000, null, 1) // 50% of 200c
                    : new AgentUsageWindowAggregate(200_000, null, 1),  // 10% of 200c → 90% remaining
        };
        var opts = Opts(Rolling(5, 200), new AgentBudgetWindowOptions { Kind = BudgetWindowKind.Monthly, LimitCents = 200 });
        var calc = Build(store, opts);

        var snapshot = await calc.GetBudgetSnapshotAsync(Opencode, "m1");

        Assert.Equal(50.0, snapshot!.AvailablePct, precision: 6);
        Assert.Equal(2, snapshot.Windows.Count);
    }

    [Fact]
    public async Task MultipleWindows_RollingWeeklyMonthly_MinAcrossAll()
    {
        // Exercise all three window kinds together. Distinguish them by query
        // span: rolling 5h (<1d), weekly 7d (<10d), monthly ~1mo (>=28d). The
        // weekly window is the tightest, so it governs the combined AvailablePct.
        var store = new FakeUsageStore
        {
            Responder = (_, _, from, to) =>
            {
                var span = to - from;
                if (span < TimeSpan.FromDays(1)) return new AgentUsageWindowAggregate(1_000_000, null, 1); // 50% rem
                if (span < TimeSpan.FromDays(10)) return new AgentUsageWindowAggregate(1_600_000, null, 1); // 20% rem
                return new AgentUsageWindowAggregate(200_000, null, 1); // 90% rem
            },
        };
        var opts = Opts(
            Rolling(5, 200),
            new AgentBudgetWindowOptions { Kind = BudgetWindowKind.Weekly, LimitCents = 200 },
            new AgentBudgetWindowOptions { Kind = BudgetWindowKind.Monthly, LimitCents = 200 });
        var calc = Build(store, opts);

        var snapshot = await calc.GetBudgetSnapshotAsync(Opencode, "m1");

        // MIN(50, 20, 90) = 20 — the weekly window binds.
        Assert.Equal(20.0, snapshot!.AvailablePct, precision: 6);
        Assert.Equal(3, snapshot.Windows.Count);
    }

    [Fact]
    public async Task WeeklyWindow_QueriesFromMondayMidnightUtc()
    {
        // 2026-05-29 is a Friday. ISO week Monday is 2026-05-25 00:00 UTC.
        var time = new FixedTime(new DateTimeOffset(2026, 5, 29, 13, 45, 0, TimeSpan.Zero));
        var store = new FakeUsageStore();
        var opts = Opts(new AgentBudgetWindowOptions { Kind = BudgetWindowKind.Weekly, LimitCents = 200 });
        var calc = Build(store, opts, time);

        await calc.GetBudgetSnapshotAsync(Opencode, "m1");

        var q = Assert.Single(store.Queries);
        Assert.Equal(new DateTimeOffset(2026, 5, 25, 0, 0, 0, TimeSpan.Zero), q.From);
        Assert.Equal(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero), q.To);
    }

    [Fact]
    public async Task MonthlyWindow_QueriesFromFirstOfMonthUtc()
    {
        var time = new FixedTime(new DateTimeOffset(2026, 5, 29, 13, 45, 0, TimeSpan.Zero));
        var store = new FakeUsageStore();
        var opts = Opts(new AgentBudgetWindowOptions { Kind = BudgetWindowKind.Monthly, LimitCents = 200 });
        var calc = Build(store, opts, time);

        await calc.GetBudgetSnapshotAsync(Opencode, "m1");

        var q = Assert.Single(store.Queries);
        Assert.Equal(new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero), q.From);
        Assert.Equal(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero), q.To);
    }

    [Fact]
    public async Task RollingWindow_ResetIsEarliestEventPlusHours()
    {
        var now = new DateTimeOffset(2026, 5, 29, 12, 0, 0, TimeSpan.Zero);
        var earliest = now.AddHours(-3);
        var store = new FakeUsageStore
        {
            Responder = (_, _, _, _) => new AgentUsageWindowAggregate(500_000, earliest, 1),
        };
        var calc = Build(store, Opts(Rolling(5, 200)), new FixedTime(now));

        var snapshot = await calc.GetBudgetSnapshotAsync(Opencode, "m1");

        Assert.Equal(earliest.AddHours(5), snapshot!.ResetAt);
    }

    [Fact]
    public async Task SummariseAll_ReturnsPerWindowUsageView()
    {
        var store = new FakeUsageStore { DefaultSum = 1_000_000 };
        var calc = Build(store, Opts(Rolling(5, 200)));

        var views = await calc.SummariseAllAsync();

        var view = Assert.Single(views);
        Assert.Equal("opencode", view.Agent);
        Assert.Equal("m1", view.Model);
        var w = Assert.Single(view.Windows);
        Assert.Equal("Rolling", w.Kind);
        Assert.Equal(5, w.Hours);
        Assert.Equal(100, w.UsedCents);   // 1_000_000 microcents = 100 cents
        Assert.Equal(200, w.LimitCents);
        Assert.Equal(50.0, w.PercentRemaining, precision: 6);
    }

    [Fact]
    public async Task ConfigReload_AppliesNewLimits()
    {
        var store = new FakeUsageStore { DefaultSum = 1_000_000 };
        var calc = Build(store, Opts(Rolling(5, 200)));

        var first = await calc.SummariseAllAsync();
        Assert.Equal(50.0, first.Single().Windows.Single().PercentRemaining, precision: 6);

        // Halve the limit → same spend is now 100% used.
        calc.ApplyConfigReload(Opts(Rolling(5, 100)));
        var second = await calc.SummariseAllAsync();
        Assert.Equal(0.0, second.Single().Windows.Single().PercentRemaining, precision: 6);
    }

    [Fact]
    public async Task StoreResolutionFailure_FailsClosed_AvailableZero()
    {
        // A configured budget whose usage store cannot be resolved must NOT
        // silently disable the gate; it fails closed (0% remaining) so the cap
        // still gates dispatch while accounting is unavailable.
        var calc = new AgentBudgetCalculator(
            () => throw new InvalidOperationException("store unavailable"),
            Opts(Rolling(5, 200)),
            NullLogger<AgentBudgetCalculator>.Instance);

        var snapshot = await calc.GetBudgetSnapshotAsync(Opencode, "m1");

        Assert.NotNull(snapshot);
        Assert.Equal(0.0, snapshot!.AvailablePct);
        var w = Assert.Single(snapshot.Windows);
        Assert.Equal(0.0, w.AvailablePct);
    }

    [Fact]
    public async Task PartialWindowFailure_FailedWindowCountsAsExhausted()
    {
        // Rolling (span < 1 day) query throws; Monthly succeeds at 10% used
        // (90% remaining). The failed rolling window must still participate in
        // MIN as exhausted (0%), so the snapshot fails closed rather than
        // reporting the healthier surviving window.
        var store = new FakeUsageStore
        {
            Responder = (_, _, from, to) =>
                (to - from) < TimeSpan.FromDays(1)
                    ? throw new InvalidOperationException("rolling query failed")
                    : new AgentUsageWindowAggregate(200_000, null, 1),
        };
        var opts = Opts(Rolling(5, 200), new AgentBudgetWindowOptions { Kind = BudgetWindowKind.Monthly, LimitCents = 200 });
        var calc = Build(store, opts);

        var snapshot = await calc.GetBudgetSnapshotAsync(Opencode, "m1");

        Assert.NotNull(snapshot);
        Assert.Equal(0.0, snapshot!.AvailablePct);
        Assert.Equal(2, snapshot.Windows.Count);
    }

    [Fact]
    public async Task AllWindowsFail_FailsClosed_NotNull()
    {
        var store = new FakeUsageStore
        {
            Responder = (_, _, _, _) => throw new InvalidOperationException("every query fails"),
        };
        var calc = Build(store, Opts(Rolling(5, 200)));

        var snapshot = await calc.GetBudgetSnapshotAsync(Opencode, "m1");

        Assert.NotNull(snapshot);
        Assert.Equal(0.0, snapshot!.AvailablePct);
    }

    [Fact]
    public async Task ZeroLimit_FailsClosed()
    {
        // A misconfigured LimitCents <= 0 means a zero budget; nothing is
        // available, so the window fails closed instead of disabling the gate.
        var store = new FakeUsageStore();
        var calc = Build(store, Opts(Rolling(5, 0)));

        var snapshot = await calc.GetBudgetSnapshotAsync(Opencode, "m1");

        Assert.NotNull(snapshot);
        Assert.Equal(0.0, snapshot!.AvailablePct);
    }

    [Fact]
    public async Task UnrecognisedWindowKind_FailsClosed_NeverQueries()
    {
        // An out-of-range enum value (e.g. a future BudgetWindowKind left
        // unhandled) must fail closed (0% remaining) rather than fall through to
        // a zero-width query that would report 100% and silently disable the gate.
        var store = new FakeUsageStore { DefaultSum = 0 };
        var opts = Opts(new AgentBudgetWindowOptions { Kind = (BudgetWindowKind)999, LimitCents = 200 });
        var calc = Build(store, opts);

        var snapshot = await calc.GetBudgetSnapshotAsync(Opencode, "m1");

        Assert.NotNull(snapshot);
        Assert.Equal(0.0, snapshot!.AvailablePct);
        var w = Assert.Single(snapshot.Windows);
        Assert.Equal(0.0, w.AvailablePct);
        // The unrecognised kind is rejected before any window query runs.
        Assert.Empty(store.Queries);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(null)]
    public async Task RollingWindow_MissingOrNonPositiveHours_FailsClosed(int? hours)
    {
        // A Rolling window with missing/non-positive Hours is a misconfiguration.
        // It must fail closed (0% remaining) rather than silently collapse to a
        // 1-hour window, which would narrow the cap and overstate remaining budget.
        var store = new FakeUsageStore { DefaultSum = 0 };
        var opts = Opts(new AgentBudgetWindowOptions { Kind = BudgetWindowKind.Rolling, Hours = hours, LimitCents = 200 });
        var calc = Build(store, opts);

        var snapshot = await calc.GetBudgetSnapshotAsync(Opencode, "m1");

        Assert.NotNull(snapshot);
        Assert.Equal(0.0, snapshot!.AvailablePct);
        var w = Assert.Single(snapshot.Windows);
        Assert.Equal(0.0, w.AvailablePct);
        // Failed closed before issuing a window query.
        Assert.Empty(store.Queries);
    }

    [Fact]
    public async Task RollingWindow_QueriesFromNowMinusHours()
    {
        var now = new DateTimeOffset(2026, 5, 29, 12, 0, 0, TimeSpan.Zero);
        var store = new FakeUsageStore();
        var calc = Build(store, Opts(Rolling(5, 200)), new FixedTime(now));

        await calc.GetBudgetSnapshotAsync(Opencode, "m1");

        var q = Assert.Single(store.Queries);
        Assert.Equal(now.AddHours(-5), q.From);
        Assert.Equal(now, q.To);
    }

    [Fact]
    public async Task SummariseAllAsync_RecomputesEveryCall()
    {
        // The visibility path must not cache: a cached "healthy" snapshot would
        // keep /quota reporting pre-outage percentRemaining after an accounting
        // outage begins, contradicting the fail-closed contract. Every call
        // recomputes against the live store.
        var store = new FakeUsageStore { DefaultSum = 1_000_000 };
        var calc = Build(store, Opts(Rolling(5, 200)));

        await calc.SummariseAllAsync();
        await calc.SummariseAllAsync();

        Assert.Equal(2, store.Queries.Count);
    }

    [Fact]
    public async Task SummariseAllAsync_ReflectsOutage_AfterHealthyCall()
    {
        // A prior healthy summary must NOT be served while the store is down: once
        // the accounting outage begins, /quota must read as exhausted (0% / degraded
        // note) rather than the stale healthy snapshot.
        var fail = false;
        var store = new FakeUsageStore
        {
            Responder = (_, _, _, _) => fail
                ? throw new InvalidOperationException("outage")
                : new AgentUsageWindowAggregate(0, null, 0),
        };
        var calc = Build(store, Opts(Rolling(5, 200)));

        var healthy = await calc.SummariseAllAsync();
        Assert.Equal(100.0, healthy.Single().Windows.Single().PercentRemaining);

        fail = true;
        var degraded = await calc.SummariseAllAsync();
        Assert.Equal(0.0, degraded.Single().Windows.Single().PercentRemaining);
    }

    [Fact]
    public async Task GetBudgetSnapshotAsync_NeverServesCache_RecomputesEveryCall()
    {
        // The dispatch gate must recompute every call: a cached "healthy" value
        // would mask an accounting outage, bypassing the documented fail-closed
        // behaviour.
        var store = new FakeUsageStore { DefaultSum = 1_000_000 };
        var calc = Build(store, Opts(Rolling(5, 200)));

        await calc.GetBudgetSnapshotAsync(Opencode, "m1");
        await calc.GetBudgetSnapshotAsync(Opencode, "m1");

        Assert.Equal(2, store.Queries.Count);
    }

    [Fact]
    public async Task GateFailsClosed_AfterHealthyVisibilityCall()
    {
        // A healthy SummariseAllAsync followed by a store outage: the dispatch gate
        // must fail closed immediately rather than reporting the earlier healthy
        // reading.
        var fail = false;
        var store = new FakeUsageStore
        {
            Responder = (_, _, _, _) => fail
                ? throw new InvalidOperationException("outage")
                : new AgentUsageWindowAggregate(0, null, 0),
        };
        var calc = Build(store, Opts(Rolling(5, 200)));

        var summary = await calc.SummariseAllAsync();
        Assert.Equal(100.0, summary.Single().Windows.Single().PercentRemaining);

        // Outage begins.
        fail = true;
        var gate = await calc.GetBudgetSnapshotAsync(Opencode, "m1");
        Assert.Equal(0.0, gate!.AvailablePct);
    }

    [Fact]
    public async Task DegradedComputation_RecoversOnNextCall()
    {
        // First call: every query throws → fails closed. Second call: queries
        // succeed → the gate recovers immediately rather than serving a stale
        // exhausted snapshot.
        var fail = true;
        var store = new FakeUsageStore
        {
            Responder = (_, _, _, _) => fail
                ? throw new InvalidOperationException("transient")
                : new AgentUsageWindowAggregate(0, null, 0),
        };
        var calc = Build(store, Opts(Rolling(5, 200)));

        var first = await calc.GetBudgetSnapshotAsync(Opencode, "m1");
        Assert.Equal(0.0, first!.AvailablePct);

        fail = false;
        var second = await calc.GetBudgetSnapshotAsync(Opencode, "m1");
        Assert.Equal(100.0, second!.AvailablePct);
    }

    [Fact]
    public async Task StoreResolutionFailure_FailsClosed_AvailableZero()
    {
        // A configured budget whose usage store cannot be resolved must NOT
        // silently disable the gate; it fails closed (0% remaining) so the cap
        // still gates dispatch while accounting is unavailable.
        var calc = new AgentBudgetCalculator(
            () => throw new InvalidOperationException("store unavailable"),
            Opts(Rolling(5, 200)),
            NullLogger<AgentBudgetCalculator>.Instance);

        var snapshot = await calc.GetBudgetSnapshotAsync(Opencode, "m1");

        Assert.NotNull(snapshot);
        Assert.Equal(0.0, snapshot!.AvailablePct);
        var w = Assert.Single(snapshot.Windows);
        Assert.Equal(0.0, w.AvailablePct);
    }

    [Fact]
    public async Task PartialWindowFailure_FailedWindowCountsAsExhausted()
    {
        // Rolling (span < 1 day) query throws; Monthly succeeds at 10% used
        // (90% remaining). The failed rolling window must still participate in
        // MIN as exhausted (0%), so the snapshot fails closed rather than
        // reporting the healthier surviving window.
        var store = new FakeUsageStore
        {
            Responder = (_, _, from, to) =>
                (to - from) < TimeSpan.FromDays(1)
                    ? throw new InvalidOperationException("rolling query failed")
                    : new AgentUsageWindowAggregate(200_000, null, 1),
        };
        var opts = Opts(Rolling(5, 200), new AgentBudgetWindowOptions { Kind = BudgetWindowKind.Monthly, LimitCents = 200 });
        var calc = Build(store, opts);

        var snapshot = await calc.GetBudgetSnapshotAsync(Opencode, "m1");

        Assert.NotNull(snapshot);
        Assert.Equal(0.0, snapshot!.AvailablePct);
        Assert.Equal(2, snapshot.Windows.Count);
    }

    [Fact]
    public async Task AllWindowsFail_FailsClosed_NotNull()
    {
        var store = new FakeUsageStore
        {
            Responder = (_, _, _, _) => throw new InvalidOperationException("every query fails"),
        };
        var calc = Build(store, Opts(Rolling(5, 200)));

        var snapshot = await calc.GetBudgetSnapshotAsync(Opencode, "m1");

        Assert.NotNull(snapshot);
        Assert.Equal(0.0, snapshot!.AvailablePct);
    }

    [Fact]
    public async Task ZeroLimit_FailsClosed()
    {
        // A misconfigured LimitCents <= 0 means a zero budget; nothing is
        // available, so the window fails closed instead of disabling the gate.
        var store = new FakeUsageStore();
        var calc = Build(store, Opts(Rolling(5, 0)));

        var snapshot = await calc.GetBudgetSnapshotAsync(Opencode, "m1");

        Assert.NotNull(snapshot);
        Assert.Equal(0.0, snapshot!.AvailablePct);
    }

    [Fact]
    public async Task RollingWindow_QueriesFromNowMinusHours()
    {
        var now = new DateTimeOffset(2026, 5, 29, 12, 0, 0, TimeSpan.Zero);
        var store = new FakeUsageStore();
        var calc = Build(store, Opts(Rolling(5, 200)), new FixedTime(now));

        await calc.GetBudgetSnapshotAsync(Opencode, "m1");

        var q = Assert.Single(store.Queries);
        Assert.Equal(now.AddHours(-5), q.From);
        Assert.Equal(now, q.To);
    }

    [Fact]
    public async Task SecondCallWithinTtl_ReusesCachedResult()
    {
        var store = new FakeUsageStore { DefaultSum = 1_000_000 };
        var opts = Opts(Rolling(5, 200));
        opts.CacheTtl = TimeSpan.FromMinutes(10); // caching on
        var calc = Build(store, opts);

        await calc.GetBudgetSnapshotAsync(Opencode, "m1");
        await calc.GetBudgetSnapshotAsync(Opencode, "m1");

        // One window queried once; the second call must hit the cache.
        Assert.Single(store.Queries);
    }

    [Fact]
    public async Task DegradedComputation_IsNotCached_RecoversOnNextCall()
    {
        // First call: every query throws → fails closed and is NOT cached.
        // Second call: queries succeed → the gate recovers immediately rather
        // than serving a stale exhausted snapshot for CacheTtl.
        var fail = true;
        var store = new FakeUsageStore
        {
            Responder = (_, _, _, _) => fail
                ? throw new InvalidOperationException("transient")
                : new AgentUsageWindowAggregate(0, null, 0),
        };
        var opts = Opts(Rolling(5, 200));
        opts.CacheTtl = TimeSpan.FromMinutes(10);
        var calc = Build(store, opts);

        var first = await calc.GetBudgetSnapshotAsync(Opencode, "m1");
        Assert.Equal(0.0, first!.AvailablePct);

        fail = false;
        var second = await calc.GetBudgetSnapshotAsync(Opencode, "m1");
        Assert.Equal(100.0, second!.AvailablePct);
    }

    private sealed class FixedTime : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedTime(DateTimeOffset now) { _now = now; }
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class FakeUsageStore : IAgentUsageStore
    {
        public long DefaultSum { get; set; }
        public Func<string, string?, DateTimeOffset, DateTimeOffset, AgentUsageWindowAggregate>? Responder { get; set; }
        public List<(string Agent, string? Model, DateTimeOffset From, DateTimeOffset To)> Queries { get; } = new();

        public Task RecordAsync(AgentUsageEvent usage, CancellationToken ct = default) => Task.CompletedTask;

        public Task<AgentUsageWindowAggregate> SumWindowAsync(
            string agentKind, string? modelId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
        {
            Queries.Add((agentKind, modelId, fromUtc, toUtc));
            var result = Responder?.Invoke(agentKind, modelId, fromUtc, toUtc)
                ?? new AgentUsageWindowAggregate(DefaultSum, null, DefaultSum > 0 ? 1 : 0);
            return Task.FromResult(result);
        }

        public Task<int> PruneAsync(DateTimeOffset cutoffUtc, CancellationToken ct = default) => Task.FromResult(0);
    }
}
