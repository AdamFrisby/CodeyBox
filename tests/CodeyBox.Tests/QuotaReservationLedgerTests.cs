using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Regression tests for the quota reservation ledger: concurrent dispatches
/// sharing one cached probe reading must escrow their estimated cost so the
/// pool cannot be carried below its floor before the next probe observes the
/// consumption. Covers the five verification scenarios (concurrency cap,
/// release on success/failure/cancellation, dead-worker expiry, reconcile +
/// probe supersede, single-dispatch parity) plus estimate bounds.
/// </summary>
public sealed class QuotaReservationLedgerTests
{
    private static readonly DateTimeOffset T0 =
        new(2026, 9, 11, 5, 25, 24, TimeSpan.Zero);

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeTimeProvider(DateTimeOffset start) { _now = start; }
        public void Advance(TimeSpan delta) => _now += delta;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class InfiniteSlotGate : IAgentSlotGate
    {
        public bool TryReserve(AgentKind agent) => true;
        public void Release(AgentKind agent) { }
    }

    private static AgentMembership Sub(AgentKind kind, int score = 100) =>
        new() { Agent = kind, Billing = AgentBilling.Subscription, QualityScore = score };

    private static QuotaRouterOptions ReservationOptions(double estimate = 4.0) => new()
    {
        MinQuotaPct = 10.0,
        QuotaRecheckInterval = TimeSpan.FromMinutes(5),
        CapRetryRecheckInterval = TimeSpan.FromSeconds(15),
        DispatchReservationEstimatePct = estimate,
        DispatchReservationMinPct = 0.5,
        DispatchReservationMaxPct = 25.0,
        QuotaReservationMaxAge = TimeSpan.FromHours(6),
    };

    private static WorkItem MakeItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("proj"),
        Title = "t",
        Prompt = "p",
        AgentClassId = "frontier",
        MinModelScore = 0,
    };

    // ── 1. Concurrent dispatches authorise only what the estimate allows ──

    [Fact]
    public void ConcurrentReserves_AuthoriseOnlyWhatEstimateAllows()
    {
        var clock = new FakeTimeProvider(T0);
        var ledger = new QuotaReservationLedger(ReservationOptions(), clock);
        var member = Sub(AgentKind.Claude);

        const int n = 5;
        var barrier = new Barrier(n);
        var bag = new ConcurrentBag<QuotaReservationAttempt>();
        Parallel.For(0, n, _ =>
        {
            barrier.SignalAndWait(TimeSpan.FromSeconds(30));
            bag.Add(ledger.TryReserve(member, availablePct: 20.0, floorPct: 10.0));
        });

        // Headroom is 10 points; each dispatch escrows 4 → exactly 2 fit.
        Assert.Equal(2, bag.Count(a => a.Allowed));
        Assert.Equal(3, bag.Count(a => !a.Allowed));
        Assert.All(bag.Where(a => !a.Allowed), a => Assert.NotNull(a.DenyReason));
        Assert.Equal(8.0, ledger.GetOutstandingPct(member), precision: 5);
        Assert.Equal(2, ledger.GetReservationCount(member));
    }

    [Fact]
    public void GateWithoutLedger_AllowsEveryConcurrentEvaluation()
    {
        // The defect shape: a stateless gate on the raw reading passes all N.
        var opts = ReservationOptions();
        var member = Sub(AgentKind.Claude);
        var quota = new EffectiveQuota(AvailablePct: 20.0, ResetAt: null, Window: null);

        var allowed = 0;
        Parallel.For(0, 5, _ =>
        {
            if (QuotaGatePolicy.Evaluate(opts, member, quota, T0).Allow)
                Interlocked.Increment(ref allowed);
        });

        Assert.Equal(5, allowed);
    }

    [Fact]
    public void GateWithOutstanding_RefusesOnlyWhatHeadroomForbids()
    {
        var opts = ReservationOptions();
        var member = Sub(AgentKind.Claude);
        var quota = new EffectiveQuota(AvailablePct: 20.0, ResetAt: null, Window: null);

        Assert.True(QuotaGatePolicy.Evaluate(opts, member, quota, T0, outstandingPct: 8.0).Allow);
        var denied = QuotaGatePolicy.Evaluate(opts, member, quota, T0, outstandingPct: 12.0);
        Assert.False(denied.Allow);
        Assert.Contains("outstanding reservations", denied.Reason);
    }

    // ── 2. Gate behaviour unchanged with a single dispatch in flight ──

    [Fact]
    public void SingleDispatch_GateMatchesLegacyBehaviour()
    {
        var opts = ReservationOptions();
        var member = Sub(AgentKind.Claude);
        var quota = new EffectiveQuota(AvailablePct: 20.0, ResetAt: null, Window: null);

        var legacy = QuotaGatePolicy.Evaluate(opts, member, quota, T0);
        var escrowed = QuotaGatePolicy.Evaluate(opts, member, quota, T0, outstandingPct: 0);
        Assert.Equal(legacy.Allow, escrowed.Allow);
        Assert.Equal(legacy.Reason, escrowed.Reason);

        var clock = new FakeTimeProvider(T0);
        var ledger = new QuotaReservationLedger(opts, clock);
        var attempt = ledger.TryReserve(member, availablePct: 20.0, floorPct: 10.0);
        Assert.True(attempt.Allowed);
        Assert.NotNull(attempt.Lease);
        Assert.Equal(4.0, ledger.GetOutstandingPct(member), precision: 5);
    }

    // ── 3. Reservation released on success, failure, and cancellation ──

    [Theory]
    [InlineData(null)]   // success path with no measurable usage rows
    [InlineData(0.0)]    // phase ran but extracted zero tokens
    [InlineData(-1.0)]   // unusable observation must not pin the estimate
    public void Complete_WithoutPositiveUsage_Releases(double? observed)
    {
        var clock = new FakeTimeProvider(T0);
        var ledger = new QuotaReservationLedger(ReservationOptions(), clock);
        var member = Sub(AgentKind.Claude);

        var attempt = ledger.TryReserve(member, 20.0, 10.0);
        Assert.True(attempt.Allowed);
        Assert.True(ledger.Complete(attempt.Lease, observed));
        Assert.Equal(0.0, ledger.GetOutstandingPct(member), precision: 5);
    }

    [Fact]
    public void Release_IsIdempotentAndSafeWithAnything()
    {
        var clock = new FakeTimeProvider(T0);
        var ledger = new QuotaReservationLedger(ReservationOptions(), clock);
        var member = Sub(AgentKind.Claude);

        Assert.False(ledger.Release((QuotaReservationLease?)null));
        Assert.False(ledger.Release(Guid.NewGuid()));
        Assert.False(ledger.Complete(Guid.NewGuid(), 1.0));

        var attempt = ledger.TryReserve(member, 20.0, 10.0);
        Assert.True(ledger.Release(attempt.Lease));
        Assert.False(ledger.Release(attempt.Lease));
        Assert.Equal(0.0, ledger.GetOutstandingPct(member), precision: 5);
    }

    // ── 4. A dead worker leaves no permanent reservation ──

    [Fact]
    public void SweepExpired_ReapsReservationsPastMaxAge()
    {
        var clock = new FakeTimeProvider(T0);
        var ledger = new QuotaReservationLedger(ReservationOptions(), clock);
        var member = Sub(AgentKind.Claude);

        var attempt = ledger.TryReserve(member, 20.0, 10.0);
        Assert.True(attempt.Allowed);
        Assert.Equal(0, ledger.SweepExpired());
        Assert.Equal(4.0, ledger.GetOutstandingPct(member), precision: 5);

        clock.Advance(TimeSpan.FromHours(6) + TimeSpan.FromMinutes(1));
        Assert.Equal(1, ledger.SweepExpired());
        Assert.Equal(0.0, ledger.GetOutstandingPct(member), precision: 5);
    }

    // ── 5. Reconcile replaces the estimate; a newer probe supersedes ──

    [Fact]
    public void Complete_ReplacesEstimateWithObservedUsage()
    {
        var clock = new FakeTimeProvider(T0);
        var ledger = new QuotaReservationLedger(ReservationOptions(), clock);
        var member = Sub(AgentKind.Claude);

        var attempt = ledger.TryReserve(member, 20.0, 10.0);
        Assert.True(attempt.Allowed);
        Assert.True(ledger.Complete(attempt.Lease, 1.5));
        Assert.Equal(1.5, ledger.GetOutstandingPct(member), precision: 5);
        Assert.Equal(1, ledger.GetReservationCount(member));
    }

    [Fact]
    public void ProbeReading_SupersedesOnlyCompletedEntriesItPostdates()
    {
        var clock = new FakeTimeProvider(T0);
        var ledger = new QuotaReservationLedger(ReservationOptions(), clock);
        var member = Sub(AgentKind.Claude);

        // Still-running dispatches survive even a fresh reading.
        var running = ledger.TryReserve(member, 20.0, 10.0);
        Assert.True(running.Allowed);
        ledger.NoteProbeReading(member, 18.0, T0 + TimeSpan.FromMinutes(1));
        Assert.Equal(4.0, ledger.GetOutstandingPct(member), precision: 5);

        // Reconcile, then a stale reading must not retire the observed tail.
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True(ledger.Complete(running.Lease, 1.5));
        ledger.NoteProbeReading(member, 18.0, T0 + TimeSpan.FromMinutes(1));
        Assert.Equal(1.5, ledger.GetOutstandingPct(member), precision: 5);

        // A reading observed after completion retires it: the provider number
        // already reflects the actuals, so keeping the escrow would double-count.
        ledger.NoteProbeReading(member, 16.5, T0 + TimeSpan.FromMinutes(3));
        Assert.Equal(0.0, ledger.GetOutstandingPct(member), precision: 5);

        // Unknown readings supersede nothing.
        var second = ledger.TryReserve(member, 20.0, 10.0);
        Assert.True(second.Allowed);
        Assert.True(ledger.Complete(second.Lease, 2.0));
        ledger.NoteProbeReading(member, -1.0, T0 + TimeSpan.FromMinutes(5));
        Assert.Equal(2.0, ledger.GetOutstandingPct(member), precision: 5);
    }

    // ── Settlement without extracted usage retains the estimate ──

    [Fact]
    public void Complete_WithoutExtractedUsage_RetainsEstimate()
    {
        // A phase whose cost row has has_extracted_token_usage = 0 and zero
        // tokens settles at the reserved estimate, not at zero.
        var clock = new FakeTimeProvider(T0);
        var ledger = new QuotaReservationLedger(ReservationOptions(), clock);
        var member = Sub(AgentKind.Copilot);

        var attempt = ledger.TryReserve(member, 20.0, 10.0);
        Assert.True(attempt.Allowed);
        Assert.NotNull(attempt.Lease);
        Assert.True(ledger.Complete(attempt.Lease, observedPct: 0, hasObservedUsage: false));
        Assert.Equal(4.0, ledger.GetOutstandingPct(member), precision: 5);
        Assert.Equal(1, ledger.GetReservationCount(member));

        var stats = ledger.GetSettlementStats(member);
        Assert.Equal(0, stats.SettledFromActuals);
        Assert.Equal(1, stats.RetainedAtEstimate);
        Assert.Equal(4.0, stats.RetainedAtEstimatePct, precision: 5);
        Assert.Equal(0.0, stats.SettledFromActualsPct, precision: 5);
    }

    [Fact]
    public void Complete_WithExtractedUsage_SettlesAtObserved()
    {
        var clock = new FakeTimeProvider(T0);
        var ledger = new QuotaReservationLedger(ReservationOptions(), clock);
        var member = Sub(AgentKind.Claude);

        var attempt = ledger.TryReserve(member, 20.0, 10.0);
        Assert.True(attempt.Allowed);
        Assert.True(ledger.Complete(attempt.Lease, observedPct: 1.5, hasObservedUsage: true));
        Assert.Equal(1.5, ledger.GetOutstandingPct(member), precision: 5);

        var stats = ledger.GetSettlementStats(member);
        Assert.Equal(1, stats.SettledFromActuals);
        Assert.Equal(0, stats.RetainedAtEstimate);
        Assert.Equal(1.5, stats.SettledFromActualsPct, precision: 5);
    }

    [Fact]
    public void Complete_RetainedEstimate_SupersededByNewerProbeReading()
    {
        // A retained estimate is still-unobserved consumption: a newer probe
        // reading retires it exactly like a reconciled tail.
        var clock = new FakeTimeProvider(T0);
        var ledger = new QuotaReservationLedger(ReservationOptions(), clock);
        var member = Sub(AgentKind.Copilot);

        var attempt = ledger.TryReserve(member, 20.0, 10.0);
        Assert.True(attempt.Allowed);
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True(ledger.Complete(attempt.Lease, observedPct: null, hasObservedUsage: false));
        Assert.Equal(4.0, ledger.GetOutstandingPct(member), precision: 5);

        ledger.NoteProbeReading(member, 16.0, T0 + TimeSpan.FromMinutes(3));
        Assert.Equal(0.0, ledger.GetOutstandingPct(member), precision: 5);
    }

    [Fact]
    public void Complete_MixedSequence_SettlesAtLeastReleasingEverything()
    {
        // Over a sequence mixing measured and unmeasured runs, the cumulative
        // settled cost (retained estimates plus observed reconciliations) is
        // never less than releasing every reservation — the pre-fix outcome
        // for unmeasured runs, which recorded real runs as free.
        var clock = new FakeTimeProvider(T0);
        var ledger = new QuotaReservationLedger(ReservationOptions(estimate: 4.0), clock);
        var member = Sub(AgentKind.Copilot);

        const int measured = 3;
        const int unmeasured = 4;
        for (var i = 0; i < measured; i++)
        {
            var attempt = ledger.TryReserve(member, 100.0, 0.0);
            Assert.True(attempt.Allowed);
            Assert.True(ledger.Complete(attempt.Lease, observedPct: 1.5, hasObservedUsage: true));
        }
        for (var i = 0; i < unmeasured; i++)
        {
            var attempt = ledger.TryReserve(member, 100.0, 0.0);
            Assert.True(attempt.Allowed);
            Assert.NotNull(attempt.Lease);
            Assert.Equal(4.0, attempt.Lease!.ReservedPct, precision: 5);
            Assert.True(ledger.Complete(attempt.Lease, observedPct: 0, hasObservedUsage: false));
        }

        var expected = (measured * 1.5) + (unmeasured * 4.0);
        Assert.Equal(expected, ledger.GetOutstandingPct(member), precision: 5);
        Assert.True(expected >= measured * 1.5);

        var stats = ledger.GetSettlementStats(member);
        Assert.Equal(measured, stats.SettledFromActuals);
        Assert.Equal(unmeasured, stats.RetainedAtEstimate);
        Assert.Equal(measured * 1.5, stats.SettledFromActualsPct, precision: 5);
        Assert.Equal(unmeasured * 4.0, stats.RetainedAtEstimatePct, precision: 5);
    }

    [Fact]
    public void SettlementStats_ArePerPool()
    {
        var clock = new FakeTimeProvider(T0);
        var ledger = new QuotaReservationLedger(ReservationOptions(), clock);
        var claude = Sub(AgentKind.Claude);
        var copilot = Sub(AgentKind.Copilot);

        var a = ledger.TryReserve(claude, 100.0, 0.0);
        Assert.True(a.Allowed);
        Assert.True(ledger.Complete(a.Lease, observedPct: 1.5, hasObservedUsage: true));
        var b = ledger.TryReserve(copilot, 100.0, 0.0);
        Assert.True(b.Allowed);
        Assert.True(ledger.Complete(b.Lease, observedPct: 0, hasObservedUsage: false));
        var c = ledger.TryReserve(copilot, 100.0, 0.0);
        Assert.True(c.Allowed);
        Assert.True(ledger.Complete(c.Lease, observedPct: null, hasObservedUsage: false));

        var claudeStats = ledger.GetSettlementStats(claude);
        Assert.Equal(1, claudeStats.SettledFromActuals);
        Assert.Equal(0, claudeStats.RetainedAtEstimate);

        var copilotStats = ledger.GetSettlementStats("copilot");
        Assert.Equal(0, copilotStats.SettledFromActuals);
        Assert.Equal(2, copilotStats.RetainedAtEstimate);

        var all = ledger.GetAllSettlementStats();
        Assert.Equal(2, all.Count);
        Assert.Equal(1, all["claude"].SettledFromActuals);
        Assert.Equal(2, all["copilot"].RetainedAtEstimate);
    }

    // ── Estimate bounds: missing/zero never silently reserves nothing ──

    [Theory]
    [InlineData(null, 5.0, 0.5, 25.0, 5.0)]   // missing → configured default
    [InlineData(0.0, 5.0, 0.5, 25.0, 5.0)]    // zero → default, not nothing
    [InlineData(-3.0, 5.0, 0.5, 25.0, 5.0)]   // negative → default
    [InlineData(4.0, 5.0, 0.5, 25.0, 4.0)]    // sane value passes through
    [InlineData(0.1, 5.0, 0.5, 25.0, 0.5)]    // below min → clamped up
    [InlineData(90.0, 5.0, 0.5, 25.0, 25.0)]  // above max → clamped down
    public void ResolveEstimatePct_BoundsEstimates(
        double? raw, double fallback, double min, double max, double expected)
    {
        Assert.Equal(expected, QuotaReservationLedger.ResolveEstimatePct(raw, fallback, min, max), precision: 5);
    }

    [Fact]
    public void TryReserve_MissingEstimate_ReservesMinimum()
    {
        var clock = new FakeTimeProvider(T0);
        var opts = ReservationOptions(estimate: 0.0);
        var ledger = new QuotaReservationLedger(opts, clock);
        var member = Sub(AgentKind.Claude);

        var attempt = ledger.TryReserve(member, 20.0, 10.0);
        Assert.True(attempt.Allowed);
        Assert.Equal(0.5, ledger.GetOutstandingPct(member), precision: 5);
    }

    [Fact]
    public void TryReserve_PerAgentOverride_WinsOverGlobal()
    {
        var clock = new FakeTimeProvider(T0);
        var opts = ReservationOptions(estimate: 4.0);
        opts.DispatchReservationEstimatePctByAgent["claude"] = 2.0;
        var ledger = new QuotaReservationLedger(opts, clock);

        var attempt = ledger.TryReserve(Sub(AgentKind.Claude), 20.0, 10.0);
        Assert.True(attempt.Allowed);
        Assert.Equal(2.0, ledger.GetOutstandingPct(Sub(AgentKind.Claude)), precision: 5);
    }

    [Fact]
    public void TryReserve_UnknownReading_RecordsNothing()
    {
        var clock = new FakeTimeProvider(T0);
        var ledger = new QuotaReservationLedger(ReservationOptions(), clock);
        var member = Sub(AgentKind.Claude);

        var attempt = ledger.TryReserve(member, availablePct: -1.0, floorPct: 10.0);
        Assert.True(attempt.Allowed);
        Assert.Null(attempt.Lease);
        Assert.Equal(0, ledger.GetReservationCount(member));
    }

    // ── Observed-usage conversion ──

    [Fact]
    public void ToObservedPct_ConvertsTokensAgainstBudget()
    {
        var total = new WorkItemUsageTotal(
            TokensInput: 10000, TokensOutput: 4000, TokensReasoning: 1000,
            TokensCached: 2000, CostUsd: 0.1, ElapsedMs: 1000);
        Assert.Equal(15.0, QuotaReservationLedger.ToObservedPct(total, 100_000)!.Value, precision: 5);
        Assert.Null(QuotaReservationLedger.ToObservedPct(total, 0));
        Assert.Null(QuotaReservationLedger.ToObservedPct(null, 100_000));
    }

    // ── Router integration: N concurrent dispatches, one cached reading ──

    [Fact]
    public async Task Router_ConcurrentDispatches_RespectLedger()
    {
        var clock = new FakeTimeProvider(T0);
        var opts = ReservationOptions(estimate: 4.0);
        var ledger = new QuotaReservationLedger(opts, clock);
        var cls = new AgentClass
        {
            Id = "frontier",
            DisplayName = "Frontier",
            Members = [Sub(AgentKind.Claude)],
        };
        var router = new AgentClassRouter(
            [cls],
            [new FakeProbe(AgentKind.Claude, 20.0)],
            opts,
            NullLogger<AgentClassRouter>.Instance,
            timeProvider: clock,
            reservationLedger: ledger);
        var gate = new InfiniteSlotGate();

        const int n = 5;
        var barrier = new Barrier(n);
        var tasks = Enumerable.Range(0, n).Select(_ => Task.Run(async () =>
        {
            barrier.SignalAndWait(TimeSpan.FromSeconds(30));
            return await router.ResolveAsync(MakeItem(), project: null, CancellationToken.None, slotGate: gate);
        })).ToArray();
        var decisions = await Task.WhenAll(tasks);

        var chosen = decisions.Where(d => d.Chosen is not null).ToList();
        Assert.Equal(2, chosen.Count);
        Assert.All(chosen, d => Assert.NotNull(d.QuotaReservation));

        // The refused dispatches hold nothing; the authorised two escrow 4 each.
        Assert.Equal(8.0, ledger.GetOutstandingPct(Sub(AgentKind.Claude)), precision: 5);

        foreach (var decision in decisions)
            ledger.Release(decision.QuotaReservation);
        Assert.Equal(0.0, ledger.GetOutstandingPct(Sub(AgentKind.Claude)), precision: 5);
    }

    [Fact]
    public async Task Router_ExhaustedHeadroom_DefersWithoutLeaking()
    {
        var clock = new FakeTimeProvider(T0);
        var opts = ReservationOptions(estimate: 4.0);
        var ledger = new QuotaReservationLedger(opts, clock);
        var member = Sub(AgentKind.Claude);

        // Pre-escrow 8 of the 20 points: the gate hint still passes
        // (effective 12 >= floor 10) but no full 4-point estimate fits, so the
        // atomic commit denies and the walk defers.
        Assert.True(ledger.TryReserve(member, 20.0, 10.0).Allowed);
        Assert.True(ledger.TryReserve(member, 20.0, 10.0).Allowed);
        Assert.False(ledger.TryReserve(member, 20.0, 10.0).Allowed);

        var cls = new AgentClass
        {
            Id = "frontier",
            DisplayName = "Frontier",
            Members = [member],
        };
        var router = new AgentClassRouter(
            [cls],
            [new FakeProbe(AgentKind.Claude, 20.0)],
            opts,
            NullLogger<AgentClassRouter>.Instance,
            timeProvider: clock,
            reservationLedger: ledger);

        var decision = await router.ResolveAsync(
            MakeItem(), project: null, CancellationToken.None, slotGate: new InfiniteSlotGate());

        Assert.Null(decision.Chosen);
        Assert.True(decision.ShouldWait);
        Assert.Null(decision.QuotaReservation);
        Assert.Equal(2, ledger.GetReservationCount(member));
        Assert.Equal(8.0, ledger.GetOutstandingPct(member), precision: 5);
    }
}
