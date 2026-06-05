using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for the per-agent concurrency-cap SPILL behaviour in
/// <see cref="AgentClassRouter"/>. When the highest-quality eligible member is
/// at its operator-configured cap, the router must continue to the next
/// eligible member rather than deferring the work item — and the spill must
/// compose with the budget-exhaustion gate, the quota floor, and the
/// observed-failure exclusion.
/// </summary>
public sealed class AgentClassRouterCapSpillTests
{
    private static readonly AgentKind Claude = AgentKind.Claude;
    private static readonly AgentKind Codex = AgentKind.Codex;
    private static readonly AgentKind Gemini = AgentKind.Gemini;

    private static WorkItem MakeItem(string classId) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("proj"),
        Title = "t",
        Prompt = "p",
        AgentClassId = classId,
    };

    private static AgentMembership Sub(AgentKind kind, int score = 100, string? modelId = null) =>
        new()
        {
            Agent = kind,
            Billing = AgentBilling.Subscription,
            QualityScore = score,
            ModelId = modelId,
        };

    private static AgentMembership Api(AgentKind kind, int score = 100, string? modelId = null) =>
        new()
        {
            Agent = kind,
            Billing = AgentBilling.PayPerApi,
            QualityScore = score,
            ModelId = modelId,
        };

    private static AgentClass FrontierClass(params AgentMembership[] members) => new()
    {
        Id = "frontier",
        DisplayName = "Frontier",
        Members = members,
    };

    private static AgentConcurrencySnapshot CapsFor(params (AgentKind Agent, int Max)[] caps)
    {
        var opts = new AgentConcurrencyOptions();
        foreach (var (agent, max) in caps)
            opts.Members[agent.Value] = new AgentConcurrencyEntry { MaxConcurrent = max };
        return new AgentConcurrencySnapshot(opts);
    }

    private static AgentConcurrencySnapshot CapsForRoutes(params (string RouteKey, int Max)[] caps)
    {
        var opts = new AgentConcurrencyOptions();
        foreach (var (routeKey, max) in caps)
            opts.Members[routeKey] = new AgentConcurrencyEntry { MaxConcurrent = max };
        return new AgentConcurrencySnapshot(opts);
    }

    private static AgentClassRouter BuildRouter(
        IReadOnlyList<AgentClass> catalog,
        IEnumerable<IAgentQuotaProbe> probes,
        IAgentRunningCounters runningCounters,
        AgentConcurrencySnapshot concurrencySnapshot,
        QuotaRouterOptions? options = null,
        IAgentBudgetProvider? budgetProvider = null)
    {
        var opts = options ?? new QuotaRouterOptions
        {
            MinQuotaPct = 10.0,
            QuotaRecheckInterval = TimeSpan.FromMinutes(5),
            CapRetryRecheckInterval = TimeSpan.FromSeconds(15),
        };
        return new AgentClassRouter(
            catalog, probes, opts,
            NullLogger<AgentClassRouter>.Instance,
            timeProvider: null,
            todModifiers: null,
            quotaFailures: null,
            burnEstimator: null,
            runningCounters: runningCounters,
            budgetProvider: budgetProvider,
            concurrencySnapshot: concurrencySnapshot);
    }

    // ── Headline behaviour: spill from saturated top member to lower member ──

    [Fact]
    public async Task TopMemberAtCap_LowerMemberFree_SpillsToLower()
    {
        // Claude is the highest-quality member (score 100) but its per-agent cap
        // is at ceiling (running=2, cap=2). Codex (score 99) has a free slot.
        // The router must SPILL to codex rather than defer the work item.
        var cls = FrontierClass(Sub(Claude, score: 100), Sub(Codex, score: 99));
        var counters = new FakeCounters();
        counters.Increment(Claude);
        counters.Increment(Claude);
        var caps = CapsFor((Claude, 2), (Codex, 4));
        var router = BuildRouter(
            [cls],
            [new FakeProbe(Claude, 100.0), new FakeProbe(Codex, 100.0)],
            counters, caps);

        var decision = await router.ResolveAsync(MakeItem("frontier"), null, CancellationToken.None);

        Assert.NotNull(decision.Chosen);
        Assert.Equal(Codex, decision.Chosen!.Agent);
        Assert.False(decision.ShouldWait);
    }

    [Fact]
    public async Task SameKindTopInstanceAtCap_SpillsToSiblingInstance()
    {
        var acctA = Sub(Claude, score: 100) with { InstanceId = "acct-a" };
        var acctB = Sub(Claude, score: 99) with { InstanceId = "acct-b" };
        var cls = FrontierClass(acctA, acctB);
        var counters = new RouteCounters();
        counters.Increment(acctA.RouteKey);
        var caps = CapsForRoutes((acctA.RouteKey, 1), (acctB.RouteKey, 1));
        var router = BuildRouter(
            [cls],
            [new FakeProbe(Claude, 100.0)],
            counters, caps);

        var decision = await router.ResolveAsync(MakeItem("frontier"), null, CancellationToken.None);

        Assert.NotNull(decision.Chosen);
        Assert.Equal(Claude, decision.Chosen!.Agent);
        Assert.Equal(acctB.RouteKey, decision.Chosen.RouteKey);
        Assert.False(decision.ShouldWait);
    }

    [Fact]
    public async Task TopMemberAtCap_LowerMemberAlsoAtCap_FurtherSpillsPastBoth()
    {
        // Claude (100) and codex (99) both saturated; gemini (94, but still
        // eligible) has a free slot. Spill cascades all the way down.
        var cls = FrontierClass(
            Sub(Claude, score: 100),
            Sub(Codex, score: 99),
            Sub(Gemini, score: 94, modelId: "gemini-3.0-pro"));
        var counters = new FakeCounters();
        counters.Increment(Claude);
        counters.Increment(Codex);
        var caps = CapsFor((Claude, 1), (Codex, 1), (Gemini, 2));
        var router = BuildRouter(
            [cls],
            [new FakeProbe(Claude, 100.0), new FakeProbe(Codex, 100.0), new FakeProbe(Gemini, 100.0)],
            counters, caps);

        var decision = await router.ResolveAsync(MakeItem("frontier"), null, CancellationToken.None);

        Assert.Equal(Gemini, decision.Chosen!.Agent);
    }

    // ── Defer fallback when every eligible member is at cap ──────────────────

    [Fact]
    public async Task AllEligibleMembersAtCap_DefersWithCapRetryInterval()
    {
        var cls = FrontierClass(Sub(Claude, score: 100), Sub(Codex, score: 99));
        var counters = new FakeCounters();
        counters.Increment(Claude);
        counters.Increment(Codex);
        var caps = CapsFor((Claude, 1), (Codex, 1));
        var capRetry = TimeSpan.FromSeconds(20);
        var quotaRecheck = TimeSpan.FromMinutes(5);
        var router = BuildRouter(
            [cls],
            [new FakeProbe(Claude, 100.0), new FakeProbe(Codex, 100.0)],
            counters, caps,
            options: new QuotaRouterOptions
            {
                MinQuotaPct = 10.0,
                QuotaRecheckInterval = quotaRecheck,
                CapRetryRecheckInterval = capRetry,
            });

        var decision = await router.ResolveAsync(MakeItem("frontier"), null, CancellationToken.None);

        Assert.Null(decision.Chosen);
        Assert.True(decision.ShouldWait);
        // Cap-retry is much shorter than the quota recheck and is the actually
        // useful wake-up window — a slot opens far sooner than the quota window
        // resets. The router shrinks the interval to capRetry, not the longer
        // quota recheck, when cap is the only blocker.
        Assert.Equal(capRetry, decision.SuggestedRecheckIn);
        Assert.Contains("per-agent concurrency cap", decision.Reason);
    }

    [Fact]
    public async Task AllEligibleMembersAtCap_ChoosesShorterOfQuotaRecheckAndCapRetry()
    {
        // Edge: if the operator configures QuotaRecheckInterval SHORTER than
        // CapRetryInterval, the router keeps the shorter — we still want the
        // soonest plausible retry.
        var cls = FrontierClass(Sub(Claude, score: 100));
        var counters = new FakeCounters();
        counters.Increment(Claude);
        var caps = CapsFor((Claude, 1));
        var quotaRecheck = TimeSpan.FromSeconds(5);
        var capRetry = TimeSpan.FromSeconds(15);
        var router = BuildRouter(
            [cls],
            [new FakeProbe(Claude, 100.0)],
            counters, caps,
            options: new QuotaRouterOptions
            {
                MinQuotaPct = 10.0,
                QuotaRecheckInterval = quotaRecheck,
                CapRetryRecheckInterval = capRetry,
            });

        var decision = await router.ResolveAsync(MakeItem("frontier"), null, CancellationToken.None);

        Assert.True(decision.ShouldWait);
        Assert.Equal(quotaRecheck, decision.SuggestedRecheckIn);
    }

    // ── Composition with budget-exhaustion gate ──────────────────────────────

    [Fact]
    public async Task BudgetExhaustedMember_IsNotChosenAsSpillTarget()
    {
        // Class is PayPerApi-only (no Subscription member). Top member's cap is
        // at ceiling; the next ranked member is below its budget cap. The PayPerApi
        // fire-anyway fallthrough must NOT pick the budget-exhausted member, even
        // though it would otherwise be the next-ranked eligible. Instead it must
        // park — preserving the operator spend cap.
        var cls = FrontierClass(Api(Claude, score: 100), Api(Codex, score: 99));
        var counters = new FakeCounters();
        counters.Increment(Claude);
        var caps = CapsFor((Claude, 1), (Codex, 2));
        var budgetProvider = new PerAgentBudgetProvider(new Dictionary<AgentKind, double>
        {
            [Codex] = 0.0, // codex spend cap fully consumed
        });
        var capRetry = TimeSpan.FromSeconds(15);
        var quotaRecheck = TimeSpan.FromMinutes(5);
        var router = BuildRouter(
            [cls],
            [new FakeProbe(Claude, 100.0), new FakeProbe(Codex, 100.0)],
            counters, caps,
            options: new QuotaRouterOptions
            {
                MinQuotaPct = 10.0,
                QuotaRecheckInterval = quotaRecheck,
                CapRetryRecheckInterval = capRetry,
            },
            budgetProvider: budgetProvider);

        var decision = await router.ResolveAsync(MakeItem("frontier"), null, CancellationToken.None);

        Assert.Null(decision.Chosen);
        Assert.True(decision.ShouldWait);
        // Mixed-blocker park reason: both budget-exhausted AND cap-saturated
        // members exist. The reason string must distinguish both blockers so an
        // operator reading the audit can see WHY the class parked — a regression
        // that drops either branch would mis-attribute the cause.
        Assert.Contains("budget-exhausted", decision.Reason);
        Assert.Contains("concurrency cap", decision.Reason);
        // Park interval shrinks to CapRetryInterval (15s) because the cap is the
        // soonest blocker to clear — a slot freeing is far quicker than waiting
        // out the longer quota recheck window. A regression that omits the cap
        // shrink in the mixed branch would silently park for QuotaRecheckInterval
        // (5 min) — the exact throughput regression this feature prevents.
        Assert.Equal(capRetry, decision.SuggestedRecheckIn);
    }

    [Fact]
    public async Task BudgetExhaustedTopMember_SpillsToLowerNonExhaustedMember()
    {
        // Mirror of the headline test, with the budget gate as the blocker on
        // the top member instead of the concurrency cap. Verifies the post-loop
        // PayPerApi fallthrough's spill-aware filter doesn't accidentally
        // discard a perfectly-fireable lower member.
        var cls = FrontierClass(Api(Claude, score: 100), Api(Codex, score: 99));
        var counters = new FakeCounters();
        var caps = CapsFor((Claude, 4), (Codex, 4));
        var budgetProvider = new PerAgentBudgetProvider(new Dictionary<AgentKind, double>
        {
            [Claude] = 0.0,
            [Codex] = 100.0,
        });
        // Probe AvailablePct is below MinQuotaPct on purpose so the loop falls
        // through to the PayPerApi "fire anyway" branch (matches the production
        // shape where pay-per-API probes return 100 but the budget MIN drives it
        // below threshold for the exhausted member).
        var router = BuildRouter(
            [cls],
            [new FakeProbe(Claude, 100.0), new FakeProbe(Codex, 100.0)],
            counters, caps,
            options: new QuotaRouterOptions { MinQuotaPct = 10.0 },
            budgetProvider: budgetProvider);

        var decision = await router.ResolveAsync(MakeItem("frontier"), null, CancellationToken.None);

        Assert.Equal(Codex, decision.Chosen!.Agent);
    }

    // ── Quota-floor exclusion still honored ──────────────────────────────────

    [Fact]
    public async Task QuotaFloorBelowMin_DoesNotSpillOnQuotaExhaustedMember()
    {
        // Claude is the top Subscription member but its probe reports below the
        // MinQuotaPct floor. Codex is at its cap. With NO eligible member free,
        // the router should still defer — the spill is to slot-free members
        // only, NOT to fail-open the quota gate.
        var cls = FrontierClass(Sub(Claude, score: 100), Sub(Codex, score: 99));
        var counters = new FakeCounters();
        counters.Increment(Codex);
        var caps = CapsFor((Claude, 4), (Codex, 1));
        var router = BuildRouter(
            [cls],
            [new FakeProbe(Claude, 1.0), new FakeProbe(Codex, 100.0)],
            counters, caps);

        var decision = await router.ResolveAsync(MakeItem("frontier"), null, CancellationToken.None);

        Assert.Null(decision.Chosen);
        Assert.True(decision.ShouldWait);
        // Mixed blockers: claude is quota-exhausted, codex is cap-saturated.
        // The router must pick the SHORTER recheck (cap-retry, 15s default)
        // so a freed codex slot is picked up promptly.
        Assert.Equal(TimeSpan.FromSeconds(15), decision.SuggestedRecheckIn);
    }

    [Fact]
    public async Task TopMemberBelowQualityFloor_ExcludedBeforeSpillConsideration()
    {
        // The work item requires MinModelScore=95. Claude scores 90 (excluded
        // pre-loop by the existing floor filter) and is at cap; codex scores
        // 99 and has a free slot. The decision must route to codex without the
        // floor filter accidentally elevating the at-cap claude into the spill.
        var cls = FrontierClass(Sub(Claude, score: 90), Sub(Codex, score: 99));
        var counters = new FakeCounters();
        counters.Increment(Claude);
        var caps = CapsFor((Claude, 1), (Codex, 4));
        var router = BuildRouter(
            [cls],
            [new FakeProbe(Claude, 100.0), new FakeProbe(Codex, 100.0)],
            counters, caps);
        var item = MakeItem("frontier") with { MinModelScore = 95 };

        var decision = await router.ResolveAsync(item, null, CancellationToken.None);

        Assert.Equal(Codex, decision.Chosen!.Agent);
    }

    // ── Composition with observed-failure exclusion ──────────────────────────

    [Fact]
    public async Task ObservedFailureMember_StillExcludedWhenLowerMemberIsCapSaturated()
    {
        // Claude has a fresh observed quota failure (gates it at the breaker).
        // Codex is at cap. No member is fireable — router must defer rather
        // than picking either.
        var now = new DateTimeOffset(2026, 5, 30, 12, 0, 0, TimeSpan.Zero);
        var failures = new InMemoryQuotaFailures();
        failures.Add(Claude, modelId: null, now.AddMinutes(-2));

        var cls = FrontierClass(Sub(Claude, score: 100), Sub(Codex, score: 99));
        var counters = new FakeCounters();
        counters.Increment(Codex);
        var caps = CapsFor((Claude, 4), (Codex, 1));
        var opts = new QuotaRouterOptions
        {
            MinQuotaPct = 10.0,
            QuotaRecheckInterval = TimeSpan.FromMinutes(5),
            ObservedFailureWindow = TimeSpan.FromMinutes(10),
            CapRetryRecheckInterval = TimeSpan.FromSeconds(15),
        };
        var router = new AgentClassRouter(
            [cls],
            [new FakeProbe(Claude, 100.0), new FakeProbe(Codex, 100.0)],
            opts,
            NullLogger<AgentClassRouter>.Instance,
            timeProvider: new FixedTime(now),
            todModifiers: null,
            quotaFailures: failures,
            burnEstimator: null,
            runningCounters: counters,
            budgetProvider: null,
            concurrencySnapshot: caps);

        var decision = await router.ResolveAsync(MakeItem("frontier"), null, CancellationToken.None);

        Assert.Null(decision.Chosen);
        Assert.True(decision.ShouldWait);
        Assert.Equal(TimeSpan.FromSeconds(15), decision.SuggestedRecheckIn);
    }

    [Fact]
    public async Task ObservedFailureOnTopMember_SpillsToCapFreeLowerMember()
    {
        // Claude has a fresh observed quota failure; codex is at its cap but
        // claude's own slot pool is empty. The observed-failure breaker still
        // rejects claude; the SPILL skips past claude (observed-failure reject)
        // AND past codex (cap-saturated) to gemini.
        var now = new DateTimeOffset(2026, 5, 30, 12, 0, 0, TimeSpan.Zero);
        var failures = new InMemoryQuotaFailures();
        failures.Add(Claude, modelId: null, now.AddMinutes(-1));

        var cls = FrontierClass(
            Sub(Claude, score: 100),
            Sub(Codex, score: 99),
            Sub(Gemini, score: 95, modelId: "gemini-3.0-pro"));
        var counters = new FakeCounters();
        counters.Increment(Codex);
        var caps = CapsFor((Claude, 4), (Codex, 1), (Gemini, 4));
        var opts = new QuotaRouterOptions
        {
            MinQuotaPct = 10.0,
            QuotaRecheckInterval = TimeSpan.FromMinutes(5),
            ObservedFailureWindow = TimeSpan.FromMinutes(10),
            CapRetryRecheckInterval = TimeSpan.FromSeconds(15),
        };
        var router = new AgentClassRouter(
            [cls],
            [new FakeProbe(Claude, 100.0), new FakeProbe(Codex, 100.0), new FakeProbe(Gemini, 100.0)],
            opts,
            NullLogger<AgentClassRouter>.Instance,
            timeProvider: new FixedTime(now),
            todModifiers: null,
            quotaFailures: failures,
            burnEstimator: null,
            runningCounters: counters,
            budgetProvider: null,
            concurrencySnapshot: caps);

        var decision = await router.ResolveAsync(MakeItem("frontier"), null, CancellationToken.None);

        Assert.Equal(Gemini, decision.Chosen!.Agent);
    }

    // ── No cap configured ────────────────────────────────────────────────────

    [Fact]
    public async Task NoCapConfiguredForAgent_TopMemberAlwaysChosen()
    {
        // Defence-in-depth: when no concurrency snapshot is provided, or the
        // agent has no entry, IsAtAgentCap returns false and the top member is
        // chosen even with a high in-flight count.
        var cls = FrontierClass(Sub(Claude, score: 100), Sub(Codex, score: 99));
        var counters = new FakeCounters();
        for (var i = 0; i < 10; i++) counters.Increment(Claude);
        // Snapshot exists but has no entry for claude → uncapped.
        var caps = CapsFor((Codex, 4));
        var router = BuildRouter(
            [cls],
            [new FakeProbe(Claude, 100.0), new FakeProbe(Codex, 100.0)],
            counters, caps);

        var decision = await router.ResolveAsync(MakeItem("frontier"), null, CancellationToken.None);

        Assert.Equal(Claude, decision.Chosen!.Agent);
    }

    [Fact]
    public async Task NullConcurrencySnapshot_TopMemberAlwaysChosen()
    {
        var cls = FrontierClass(Sub(Claude, score: 100));
        var counters = new FakeCounters();
        for (var i = 0; i < 10; i++) counters.Increment(Claude);
        var opts = new QuotaRouterOptions { MinQuotaPct = 10.0 };
        var router = new AgentClassRouter(
            [cls],
            [new FakeProbe(Claude, 100.0)],
            opts,
            NullLogger<AgentClassRouter>.Instance,
            timeProvider: null,
            todModifiers: null,
            quotaFailures: null,
            burnEstimator: null,
            runningCounters: counters,
            budgetProvider: null,
            concurrencySnapshot: null);

        var decision = await router.ResolveAsync(MakeItem("frontier"), null, CancellationToken.None);

        Assert.Equal(Claude, decision.Chosen!.Agent);
    }

    // ── PayPerApi fallthrough with cap ───────────────────────────────────────

    [Fact]
    public async Task PayPerApiOnly_AllAtCap_Parks()
    {
        // Class is PayPerApi-only with every member at cap. The fire-anyway
        // fallthrough must NOT pick a cap-saturated member (the orchestrator
        // would just defer the dispatch). The router parks for the cap-retry
        // interval instead.
        var cls = FrontierClass(Api(Claude, score: 100), Api(Codex, score: 99));
        var counters = new FakeCounters();
        counters.Increment(Claude);
        counters.Increment(Codex);
        var caps = CapsFor((Claude, 1), (Codex, 1));
        var capRetry = TimeSpan.FromSeconds(20);
        var quotaRecheck = TimeSpan.FromMinutes(5);
        var router = BuildRouter(
            [cls],
            [new FakeProbe(Claude, 100.0), new FakeProbe(Codex, 100.0)],
            counters, caps,
            options: new QuotaRouterOptions
            {
                MinQuotaPct = 10.0,
                QuotaRecheckInterval = quotaRecheck,
                CapRetryRecheckInterval = capRetry,
            });

        var decision = await router.ResolveAsync(MakeItem("frontier"), null, CancellationToken.None);

        Assert.Null(decision.Chosen);
        Assert.True(decision.ShouldWait);
        Assert.Contains("per-agent concurrency cap", decision.Reason);
        // PayPerApi-only cap-saturated park MUST shrink the recheck to the
        // CapRetryInterval. If the production code drops this shrink for the
        // PayPerApi branch, items would park for the much longer
        // QuotaRecheckInterval (5 min default) — the throughput regression
        // this feature is meant to prevent. Mirrors the Subscription assertion
        // in AllEligibleMembersAtCap_DefersWithCapRetryInterval.
        Assert.Equal(capRetry, decision.SuggestedRecheckIn);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private sealed class FixedTime : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedTime(DateTimeOffset now) { _now = now; }
        public override DateTimeOffset GetUtcNow() => _now;
    }

    /// <summary>
    /// Minimal in-memory IQuotaFailureStore for tests — just enough to verify
    /// the observed-failure breaker excludes a member while the cap-spill
    /// logic considers the rest of the sorted list.
    /// </summary>
    private sealed class InMemoryQuotaFailures : IQuotaFailureStore
    {
        private readonly List<(AgentKind Agent, string? Model, DateTimeOffset At, QuotaFailureKind Kind, ProjectId? Project)> _entries = new();

        public void Add(AgentKind agent, string? modelId, DateTimeOffset at) =>
            _entries.Add((agent, modelId, at, QuotaFailureKind.LimitReached, null));

        public Task RecordAsync(AgentKind agent, string? modelId, QuotaFailureKind kind, DateTimeOffset observedAt, CancellationToken ct = default)
        {
            _entries.Add((agent, modelId, observedAt, kind, null));
            return Task.CompletedTask;
        }

        public Task RecordForProjectAsync(AgentKind agent, string? modelId, ProjectId projectId, QuotaFailureKind kind, DateTimeOffset observedAt, CancellationToken ct = default)
        {
            _entries.Add((agent, modelId, observedAt, kind, projectId));
            return Task.CompletedTask;
        }

        public Task<bool> HasRecentAsync(AgentKind agent, string? modelId, TimeSpan window, DateTimeOffset now, CancellationToken ct = default)
        {
            foreach (var entry in _entries)
            {
                if (entry.Agent != agent) continue;
                if (!string.Equals(entry.Model ?? "", modelId ?? "", StringComparison.Ordinal)) continue;
                if (now - entry.At <= window) return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }

        public Task<DateTimeOffset?> GetMostRecentAsync(AgentKind agent, string? modelId, TimeSpan window, DateTimeOffset now, CancellationToken ct = default)
        {
            DateTimeOffset? mostRecent = null;
            foreach (var entry in _entries)
            {
                if (entry.Agent != agent) continue;
                if (!string.Equals(entry.Model ?? "", modelId ?? "", StringComparison.Ordinal)) continue;
                if (now - entry.At > window) continue;
                if (mostRecent is null || entry.At > mostRecent.Value)
                    mostRecent = entry.At;
            }
            return Task.FromResult(mostRecent);
        }

        public Task<IReadOnlyList<QuotaFailureObservation>> ListRecentAsync(TimeSpan window, DateTimeOffset now, CancellationToken ct = default)
        {
            var results = new List<QuotaFailureObservation>();
            foreach (var entry in _entries)
            {
                if (now - entry.At > window) continue;
                results.Add(new QuotaFailureObservation(entry.Agent, entry.Model, entry.Kind, entry.At, entry.Project));
            }
            return Task.FromResult<IReadOnlyList<QuotaFailureObservation>>(results);
        }

        public Task PruneOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
        {
            _entries.RemoveAll(e => e.At < cutoff);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Budget provider that returns a per-agent fixed AvailablePct. Lets the
    /// spill tests verify that a member tipped below threshold by a configured
    /// budget is excluded from the PayPerApi fire-anyway fallthrough — i.e.
    /// the budget gate is not fail-opened by the new spill path.
    /// </summary>
    private sealed class PerAgentBudgetProvider : IAgentBudgetProvider
    {
        private readonly IReadOnlyDictionary<AgentKind, double> _pctByAgent;
        public PerAgentBudgetProvider(IReadOnlyDictionary<AgentKind, double> pctByAgent)
        {
            _pctByAgent = pctByAgent;
        }

        public Task<AgentQuotaSnapshot?> GetBudgetSnapshotAsync(AgentKind agent, string? modelId, CancellationToken ct = default)
        {
            if (!_pctByAgent.TryGetValue(agent, out var pct))
                return Task.FromResult<AgentQuotaSnapshot?>(null);
            return Task.FromResult<AgentQuotaSnapshot?>(new AgentQuotaSnapshot
            {
                AvailablePct = pct,
                Notes = $"test budget for {agent.Value}",
            });
        }

        public Task<IReadOnlyList<AgentBudgetUsageView>> SummariseAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AgentBudgetUsageView>>(Array.Empty<AgentBudgetUsageView>());
    }

    private sealed class RouteCounters : IAgentRunningCounters
    {
        private readonly Dictionary<string, int> _runningByRoute = new(StringComparer.OrdinalIgnoreCase);

        public void Increment(string routeKey) =>
            _runningByRoute[routeKey] = GetRunning(routeKey) + 1;

        public int GetRunning(AgentKind agent) =>
            _runningByRoute
                .Where(kv => string.Equals(AgentInstanceIds.KindFromRouteKey(kv.Key), agent.Value, StringComparison.OrdinalIgnoreCase))
                .Sum(kv => kv.Value);

        public int GetRunning(AgentMembership member) => GetRunning(member.RouteKey);

        public IReadOnlyDictionary<AgentKind, int> Snapshot() =>
            _runningByRoute
                .GroupBy(kv => AgentInstanceIds.KindFromRouteKey(kv.Key), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => new AgentKind(g.Key), g => g.Sum(kv => kv.Value));

        private int GetRunning(string routeKey) =>
            _runningByRoute.TryGetValue(routeKey, out var n) ? n : 0;
    }
}
