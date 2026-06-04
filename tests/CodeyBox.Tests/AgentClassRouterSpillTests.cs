using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for the quota router's per-agent concurrency cap spill: when the
/// top-ranked eligible member is at its cap, routing falls through to the
/// next eligible-and-free member instead of pinning the work item to a
/// saturated agent and deferring. Spec: see P1 quota-router spill task.
/// </summary>
public sealed class AgentClassRouterSpillTests
{
    private static readonly AgentKind Cursor = new("cursor");
    private static readonly AgentKind Claude = AgentKind.Claude;
    private static readonly AgentKind Codex = AgentKind.Codex;
    private static readonly AgentKind Gemini = new("gemini");

    private static AgentClassRouter BuildRouter(
        AgentClass cls,
        IEnumerable<IAgentQuotaProbe> probes,
        AgentAvailabilityRegistry? registry = null,
        IQuotaFailureStore? failures = null,
        double minQuotaPct = 10.0,
        TimeSpan? capRetry = null)
    {
        var opts = new QuotaRouterOptions
        {
            MinQuotaPct = minQuotaPct,
            QuotaRecheckInterval = TimeSpan.FromMinutes(5),
            CapRetryRecheckInterval = capRetry ?? TimeSpan.FromSeconds(15),
        };
        return new AgentClassRouter(
            [cls],
            probes,
            opts,
            NullLogger<AgentClassRouter>.Instance,
            timeProvider: null,
            todModifiers: null,
            quotaFailures: failures,
            burnEstimator: null,
            runningCounters: null,
            dispatchAvailability: registry is null ? null : new AgentDispatchAvailability(registry));
    }

    private static AgentAvailabilityRegistry NewRegistry()
        => new(new AvailabilityOptions(), TimeProvider.System, NullLogger<AgentAvailabilityRegistry>.Instance);

    private static WorkItem MakeItem(int minScore = 0) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("proj"),
        Title = "t",
        Prompt = "p",
        AgentClassId = "frontier",
        MinModelScore = minScore,
    };

    private static AgentClass FrontierClass(params AgentMembership[] members) => new()
    {
        Id = "frontier",
        DisplayName = "Frontier",
        Members = members,
    };

    private static AgentMembership Sub(AgentKind kind, int score = 100) =>
        new() { Agent = kind, Billing = AgentBilling.Subscription, QualityScore = score };

    private static AgentMembership PayPerApi(AgentKind kind, int score = 100) =>
        new() { Agent = kind, Billing = AgentBilling.PayPerApi, QualityScore = score };

    /// <summary>
    /// Implements <see cref="IAgentSlotGate"/> for the spill tests, modelling
    /// the orchestrator's atomic per-agent counters. Records every TryReserve
    /// invocation so tests can assert the candidate walk.
    /// </summary>
    private sealed class FakeSlotGate : IAgentSlotGate
    {
        private readonly Dictionary<AgentKind, int> _caps;
        private readonly Dictionary<AgentKind, int> _running = [];
        public List<AgentKind> ReserveCalls { get; } = [];

        public FakeSlotGate(Dictionary<AgentKind, int> caps) { _caps = caps; }

        public bool TryReserve(AgentKind agent)
        {
            ReserveCalls.Add(agent);
            if (!_caps.TryGetValue(agent, out var cap)) cap = 0;
            var cur = _running.GetValueOrDefault(agent);
            if (cap > 0 && cur >= cap) return false;
            _running[agent] = cur + 1;
            return true;
        }

        public void Release(AgentKind agent)
        {
            if (_running.TryGetValue(agent, out var cur) && cur > 0)
                _running[agent] = cur - 1;
        }

        public int Running(AgentKind agent) => _running.GetValueOrDefault(agent);
    }

    // ── Acceptance: spill to next eligible member when top is at cap ─────────

    [Fact]
    public async Task TopMemberAtCap_SpillsToNextEligibleMember()
    {
        // Acceptance scenario: A(QS=95, cap=1) saturated, B(QS=90, cap=2)
        // free → item routes to B instead of deferring on A. We model A as
        // already at cap by pre-running its only slot.
        var cls = FrontierClass(Sub(Cursor, score: 95), Sub(Claude, score: 90));
        var router = BuildRouter(cls,
            [new FakeProbe(Cursor, 90.0), new FakeProbe(Claude, 90.0)]);

        var caps = new Dictionary<AgentKind, int>
        {
            [Cursor] = 1,
            [Claude] = 2,
        };
        var gate = new FakeSlotGate(caps);
        // Pre-fill Cursor to its cap.
        Assert.True(gate.TryReserve(Cursor));
        gate.ReserveCalls.Clear();

        var decision = await router.ResolveAsync(
            MakeItem(), project: null, CancellationToken.None,
            slotGate: gate);

        Assert.NotNull(decision.Chosen);
        Assert.Equal(Claude, decision.Chosen!.Agent);
        Assert.True(decision.SlotReserved);
        Assert.False(decision.ShouldWait);
        // The router asked for Cursor first (top score) and got refused;
        // then asked for Claude and got accepted.
        Assert.Equal([Cursor, Claude], gate.ReserveCalls);
        Assert.Equal(1, gate.Running(Cursor));   // pre-existing
        Assert.Equal(1, gate.Running(Claude));   // just reserved
    }

    [Fact]
    public async Task AllEligibleMembersAtCap_DefersWithAnyMemberAtCapFlag()
    {
        // Every member already at cap → decision must be ShouldWait with
        // AnyMemberAtCap=true and the at-cap agents listed.
        var cls = FrontierClass(Sub(Cursor, score: 95), Sub(Claude, score: 90));
        var router = BuildRouter(cls,
            [new FakeProbe(Cursor, 90.0), new FakeProbe(Claude, 90.0)]);

        var gate = new FakeSlotGate(new()
        {
            [Cursor] = 1,
            [Claude] = 1,
        });
        Assert.True(gate.TryReserve(Cursor));
        Assert.True(gate.TryReserve(Claude));
        gate.ReserveCalls.Clear();

        var decision = await router.ResolveAsync(
            MakeItem(), project: null, CancellationToken.None,
            slotGate: gate);

        Assert.Null(decision.Chosen);
        Assert.True(decision.ShouldWait);
        Assert.True(decision.AnyMemberAtCap);
        Assert.False(decision.SlotReserved);
        Assert.Equal(new[] { Cursor, Claude }, decision.AtCapAgents);
        // Cap-retry interval surfaced (not the longer quota recheck).
        Assert.Equal(TimeSpan.FromSeconds(15), decision.SuggestedRecheckIn);
        // Running counts are unchanged — no spill candidate succeeded.
        Assert.Equal(1, gate.Running(Cursor));
        Assert.Equal(1, gate.Running(Claude));
    }

    [Fact]
    public async Task BelowFloorMembers_NotConsideredAsSpillTargets()
    {
        // Item requires MinModelScore=95 — only Cursor (95) qualifies. With
        // Cursor at cap, the lower-scored Claude must NOT be used as spill;
        // the item defers with AnyMemberAtCap=true.
        var cls = FrontierClass(Sub(Cursor, score: 95), Sub(Claude, score: 90));
        var router = BuildRouter(cls,
            [new FakeProbe(Cursor, 90.0), new FakeProbe(Claude, 90.0)]);

        var gate = new FakeSlotGate(new() { [Cursor] = 1, [Claude] = 2 });
        Assert.True(gate.TryReserve(Cursor));
        gate.ReserveCalls.Clear();

        var decision = await router.ResolveAsync(
            MakeItem(minScore: 95), project: null, CancellationToken.None,
            slotGate: gate);

        Assert.Null(decision.Chosen);
        Assert.True(decision.ShouldWait);
        Assert.True(decision.AnyMemberAtCap);
        Assert.Equal([Cursor], decision.AtCapAgents);
        // Claude was never asked to reserve — it was filtered out by the floor.
        Assert.DoesNotContain(Claude, gate.ReserveCalls);
    }

    [Fact]
    public async Task ExcludedMembers_NotConsideredAsSpillTargets()
    {
        // Top member (Cursor) at cap. Second member (Claude) is excluded by
        // the smoke gate. Third member (Codex) is free → choose Codex.
        var cls = FrontierClass(
            Sub(Cursor, score: 95),
            Sub(Claude, score: 90),
            Sub(Codex, score: 80));
        var reg = NewRegistry();
        reg.MarkSmokeResult(Claude, new AgentSmokeResult(false, "binary not found", TimeSpan.Zero));
        var router = BuildRouter(cls,
            [new FakeProbe(Cursor, 90.0), new FakeProbe(Claude, 90.0), new FakeProbe(Codex, 90.0)],
            reg);

        var gate = new FakeSlotGate(new()
        {
            [Cursor] = 1,
            [Claude] = 2,
            [Codex] = 1,
        });
        Assert.True(gate.TryReserve(Cursor));
        gate.ReserveCalls.Clear();

        var decision = await router.ResolveAsync(
            MakeItem(), project: null, CancellationToken.None,
            slotGate: gate);

        Assert.NotNull(decision.Chosen);
        Assert.Equal(Codex, decision.Chosen!.Agent);
        Assert.True(decision.SlotReserved);
        // The smoke-failed Claude must never have been asked to reserve.
        Assert.DoesNotContain(Claude, gate.ReserveCalls);
    }

    [Fact]
    public async Task ObservedFailureMembers_NotConsideredAsSpillTargets()
    {
        // Top member (Cursor) at cap. Second member (Claude) has a recent
        // observed quota failure (circuit breaker tripped). Third member
        // (Codex) is free → choose Codex. Claude's slot must never be
        // reserved because the observed-failure breaker skips it before
        // the cap gate is consulted.
        var cls = FrontierClass(
            Sub(Cursor, score: 95),
            Sub(Claude, score: 90),
            Sub(Codex, score: 80));
        var dbPath = Path.Combine(Path.GetTempPath(), $"codeybox-spill-failures-{Guid.NewGuid():N}.db");
        using var failures = new SqliteQuotaFailureStore(dbPath);
        try
        {
            await failures.RecordAsync(Claude, modelId: null,
                QuotaFailureKind.LimitReached, DateTimeOffset.UtcNow);
            var router = BuildRouter(cls,
                [new FakeProbe(Cursor, 90.0), new FakeProbe(Claude, 90.0), new FakeProbe(Codex, 90.0)],
                failures: failures);

            var gate = new FakeSlotGate(new()
            {
                [Cursor] = 1,
                [Claude] = 2,
                [Codex] = 1,
            });
            Assert.True(gate.TryReserve(Cursor));
            gate.ReserveCalls.Clear();

            var decision = await router.ResolveAsync(
                MakeItem(), project: null, CancellationToken.None,
                slotGate: gate);

            Assert.NotNull(decision.Chosen);
            Assert.Equal(Codex, decision.Chosen!.Agent);
            Assert.True(decision.SlotReserved);
            // Observed-failure Claude was skipped before the cap gate — never reserved.
            Assert.DoesNotContain(Claude, gate.ReserveCalls);
        }
        finally
        {
            try { File.Delete(dbPath); } catch { }
        }
    }

    [Fact]
    public async Task QuotaExhaustedMembers_NotConsideredAsSpillTargets()
    {
        // Top member (Cursor) at cap. Second (Claude) is quota-exhausted.
        // Third (Codex) is free → choose Codex. Claude's slot must never
        // be reserved because it fails the quota gate first.
        var cls = FrontierClass(
            Sub(Cursor, score: 95),
            Sub(Claude, score: 90),
            Sub(Codex, score: 80));
        var router = BuildRouter(cls,
            [
                new FakeProbe(Cursor, 90.0),
                new FakeProbe(Claude, 2.0),   // below the 10% MinQuotaPct
                new FakeProbe(Codex, 90.0),
            ]);

        var gate = new FakeSlotGate(new()
        {
            [Cursor] = 1,
            [Claude] = 2,
            [Codex] = 1,
        });
        Assert.True(gate.TryReserve(Cursor));
        gate.ReserveCalls.Clear();

        var decision = await router.ResolveAsync(
            MakeItem(), project: null, CancellationToken.None,
            slotGate: gate);

        Assert.NotNull(decision.Chosen);
        Assert.Equal(Codex, decision.Chosen!.Agent);
        Assert.True(decision.SlotReserved);
        Assert.DoesNotContain(Claude, gate.ReserveCalls);
    }

    [Fact]
    public async Task Spill_DoesNotReserveSlotForLosers()
    {
        // Acceptance: the per-agent in-flight count must only increment for
        // the actually-chosen member. The router must not leak a reservation
        // for a candidate that loses to a higher-priority winner.
        var cls = FrontierClass(Sub(Cursor, score: 95), Sub(Claude, score: 90));
        var router = BuildRouter(cls,
            [new FakeProbe(Cursor, 90.0), new FakeProbe(Claude, 90.0)]);

        var gate = new FakeSlotGate(new() { [Cursor] = 2, [Claude] = 2 });

        var decision = await router.ResolveAsync(
            MakeItem(), project: null, CancellationToken.None,
            slotGate: gate);

        Assert.NotNull(decision.Chosen);
        Assert.Equal(Cursor, decision.Chosen!.Agent);
        Assert.True(decision.SlotReserved);
        // Only Cursor was asked to reserve — Claude never visited.
        Assert.Equal([Cursor], gate.ReserveCalls);
        Assert.Equal(1, gate.Running(Cursor));
        Assert.Equal(0, gate.Running(Claude));
    }

    [Fact]
    public async Task NoSlotGate_BackwardCompatible_StillRoutesFirstMember()
    {
        // Calling ResolveAsync without a slot gate preserves the legacy
        // behaviour: no cap check, decision.SlotReserved=false.
        var cls = FrontierClass(Sub(Cursor, score: 95), Sub(Claude, score: 90));
        var router = BuildRouter(cls,
            [new FakeProbe(Cursor, 90.0), new FakeProbe(Claude, 90.0)]);

        var decision = await router.ResolveAsync(MakeItem(), project: null, CancellationToken.None);

        Assert.NotNull(decision.Chosen);
        Assert.Equal(Cursor, decision.Chosen!.Agent);
        Assert.False(decision.SlotReserved);
    }

    [Fact]
    public async Task MixedCapAndQuotaRejection_SurfacesAnyMemberAtCap()
    {
        // One member quota-rejected (below threshold), one at cap. There is
        // no eligible-and-free member, so the item must defer. The router
        // surfaces AnyMemberAtCap=true and uses the short cap-retry interval
        // because waiting for a cap to clear is much faster than the quota
        // recheck cadence — the Reason text reports the mixed-cause cleanly
        // so audit-log readers don't mistake it for a pure cap stall.
        var cls = FrontierClass(Sub(Cursor, score: 95), Sub(Claude, score: 90));
        var router = BuildRouter(cls,
            [
                new FakeProbe(Cursor, 90.0),
                new FakeProbe(Claude, 2.0),   // quota-rejected
            ]);

        var gate = new FakeSlotGate(new() { [Cursor] = 1, [Claude] = 1 });
        Assert.True(gate.TryReserve(Cursor));
        gate.ReserveCalls.Clear();

        var decision = await router.ResolveAsync(
            MakeItem(), project: null, CancellationToken.None,
            slotGate: gate);

        Assert.Null(decision.Chosen);
        Assert.True(decision.ShouldWait);
        Assert.True(decision.AnyMemberAtCap);
        Assert.Equal([Cursor], decision.AtCapAgents);
        Assert.Contains("mixed defer", decision.Reason);
        Assert.Equal(TimeSpan.FromSeconds(15), decision.SuggestedRecheckIn);
    }

    [Fact]
    public async Task QuotaOnlyDefer_DoesNotSetAnyMemberAtCap()
    {
        // All eligible members quota-exhausted, none at cap. The decision
        // must NOT set AnyMemberAtCap (so the caller uses the full quota
        // recheck interval rather than the cap-retry short delay).
        var cls = FrontierClass(Sub(Cursor, score: 95), Sub(Claude, score: 90));
        var router = BuildRouter(cls,
            [
                new FakeProbe(Cursor, 2.0),  // quota-rejected
                new FakeProbe(Claude, 2.0),  // quota-rejected
            ]);

        var gate = new FakeSlotGate(new() { [Cursor] = 5, [Claude] = 5 });

        var decision = await router.ResolveAsync(
            MakeItem(), project: null, CancellationToken.None,
            slotGate: gate);

        Assert.Null(decision.Chosen);
        Assert.True(decision.ShouldWait);
        Assert.False(decision.AnyMemberAtCap);
        Assert.Empty(decision.AtCapAgents);
        // Quota recheck interval, not the cap-retry short delay.
        Assert.Equal(TimeSpan.FromMinutes(5), decision.SuggestedRecheckIn);
        // Slot gate was never asked to reserve a quota-rejected member.
        Assert.Empty(gate.ReserveCalls);
    }

    [Fact]
    public async Task Spill_PreservesScoreOrder_ChoosesHighestFreeMember()
    {
        // Three members ordered by score: A(95) at cap, B(90) free, C(85) free.
        // Spill must pick B (next-highest free), not C.
        var cls = FrontierClass(
            Sub(Cursor, score: 95),
            Sub(Claude, score: 90),
            Sub(Codex, score: 85));
        var router = BuildRouter(cls,
            [
                new FakeProbe(Cursor, 90.0),
                new FakeProbe(Claude, 90.0),
                new FakeProbe(Codex, 90.0),
            ]);

        var gate = new FakeSlotGate(new()
        {
            [Cursor] = 1,
            [Claude] = 2,
            [Codex] = 2,
        });
        Assert.True(gate.TryReserve(Cursor));
        gate.ReserveCalls.Clear();

        var decision = await router.ResolveAsync(
            MakeItem(), project: null, CancellationToken.None,
            slotGate: gate);

        Assert.NotNull(decision.Chosen);
        Assert.Equal(Claude, decision.Chosen!.Agent);
        // Should have visited Cursor (rejected by cap) then Claude (chosen);
        // Codex never visited.
        Assert.Equal([Cursor, Claude], gate.ReserveCalls);
    }

    [Fact]
    public async Task Spill_MultiHop_SkipsTwoCappedMembersAndChoosesThird()
    {
        // Four members ordered by score: A(95), B(90), C(85), D(80).
        // A and B are at cap; C is free. Spill must walk past two saturated
        // members to reach C — guards against an off-by-one or premature
        // exit in the spill loop.
        var cls = FrontierClass(
            Sub(Cursor, score: 95),
            Sub(Claude, score: 90),
            Sub(Codex, score: 85),
            Sub(Gemini, score: 80));
        var router = BuildRouter(cls,
            [
                new FakeProbe(Cursor, 90.0),
                new FakeProbe(Claude, 90.0),
                new FakeProbe(Codex, 90.0),
                new FakeProbe(Gemini, 90.0),
            ]);

        var gate = new FakeSlotGate(new()
        {
            [Cursor] = 1,
            [Claude] = 1,
            [Codex] = 2,
            [Gemini] = 2,
        });
        // Saturate Cursor and Claude.
        Assert.True(gate.TryReserve(Cursor));
        Assert.True(gate.TryReserve(Claude));
        gate.ReserveCalls.Clear();

        var decision = await router.ResolveAsync(
            MakeItem(), project: null, CancellationToken.None,
            slotGate: gate);

        Assert.NotNull(decision.Chosen);
        Assert.Equal(Codex, decision.Chosen!.Agent);
        // The walk visited Cursor → Claude → Codex (chosen). Gemini never visited.
        Assert.Equal([Cursor, Claude, Codex], gate.ReserveCalls);
        Assert.Equal(0, gate.Running(Gemini));
    }

    [Fact]
    public async Task PayPerApiOnly_AtCap_DefersWithAnyMemberAtCap()
    {
        // PayPerApi-only class with the sole member at cap. Goes through
        // the main loop's spill defer path (not the unreachable fallback),
        // and the orchestrator should see AnyMemberAtCap=true so it picks
        // the short cap-retry interval.
        var cls = FrontierClass(PayPerApi(Cursor, score: 95));
        var router = BuildRouter(cls, [new PayPerApiQuotaProbe()]);

        var gate = new FakeSlotGate(new() { [Cursor] = 1 });
        Assert.True(gate.TryReserve(Cursor));
        gate.ReserveCalls.Clear();

        var decision = await router.ResolveAsync(
            MakeItem(), project: null, CancellationToken.None,
            slotGate: gate);

        Assert.Null(decision.Chosen);
        Assert.True(decision.ShouldWait);
        Assert.True(decision.AnyMemberAtCap);
        Assert.Equal([Cursor], decision.AtCapAgents);
        // Cap-retry interval (15s), not the longer quota recheck (5m).
        Assert.Equal(TimeSpan.FromSeconds(15), decision.SuggestedRecheckIn);
    }

    [Fact]
    public async Task PayPerApiOnly_BelowCap_ReservesAndChoosesMember()
    {
        // PayPerApi-only class with cap available: the router reserves
        // through the gate via the main loop and stamps SlotReserved=true.
        var cls = FrontierClass(PayPerApi(Cursor, score: 95));
        var router = BuildRouter(cls, [new PayPerApiQuotaProbe()]);

        var gate = new FakeSlotGate(new() { [Cursor] = 2 });

        var decision = await router.ResolveAsync(
            MakeItem(), project: null, CancellationToken.None,
            slotGate: gate);

        Assert.NotNull(decision.Chosen);
        Assert.Equal(Cursor, decision.Chosen!.Agent);
        Assert.True(decision.SlotReserved);
        Assert.Equal(1, gate.Running(Cursor));
    }

    [Fact]
    public async Task PayPerApiMixedWithExhaustedSub_HonoursCap()
    {
        // Mixed class: a Sub member that fails quota + a PayPerApi member.
        // The PayPerApi takes the win (Sub is below threshold) — the gate
        // reserves the PayPerApi slot atomically. When the PayPerApi is at
        // cap, the main loop spills (no more candidates) and defers with
        // AnyMemberAtCap=true.
        var cls = FrontierClass(
            Sub(Claude, score: 95),
            PayPerApi(Cursor, score: 90));
        var router = BuildRouter(cls,
            [new FakeProbe(Claude, 2.0), new PayPerApiQuotaProbe()]);

        // Below-cap case: PayPerApi wins.
        var gateFree = new FakeSlotGate(new() { [Claude] = 2, [Cursor] = 2 });
        var freeDecision = await router.ResolveAsync(
            MakeItem(), project: null, CancellationToken.None, slotGate: gateFree);
        Assert.NotNull(freeDecision.Chosen);
        Assert.Equal(Cursor, freeDecision.Chosen!.Agent);

        // At-cap case: PayPerApi is at cap, Sub fails quota → defer with cap flag.
        var gateCapped = new FakeSlotGate(new() { [Claude] = 2, [Cursor] = 1 });
        Assert.True(gateCapped.TryReserve(Cursor));
        var cappedDecision = await router.ResolveAsync(
            MakeItem(), project: null, CancellationToken.None, slotGate: gateCapped);
        Assert.Null(cappedDecision.Chosen);
        Assert.True(cappedDecision.AnyMemberAtCap);
        Assert.Equal([Cursor], cappedDecision.AtCapAgents);
        // Reason text reports the mixed cause cleanly.
        Assert.Contains("mixed defer", cappedDecision.Reason);
    }
}
