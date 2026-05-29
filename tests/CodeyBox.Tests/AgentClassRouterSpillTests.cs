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
    private static readonly AgentKind Opencode = new("opencode");

    private static AgentClassRouter BuildRouter(
        AgentClass cls,
        IEnumerable<IAgentQuotaProbe> probes,
        AgentAvailabilityRegistry? registry = null,
        double minQuotaPct = 10.0)
    {
        var opts = new QuotaRouterOptions { MinQuotaPct = minQuotaPct, QuotaRecheckInterval = TimeSpan.FromMinutes(5) };
        return new AgentClassRouter(
            [cls],
            probes,
            opts,
            NullLogger<AgentClassRouter>.Instance,
            timeProvider: null,
            todModifiers: null,
            quotaFailures: null,
            burnEstimator: null,
            runningCounters: null,
            availability: registry);
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

    /// <summary>
    /// Helper that records each <c>reserveSlot</c> invocation and lets the
    /// test choose which agents succeed. Mirrors the orchestrator's atomic
    /// reserve-or-fail semantics: returning true means the slot was taken.
    /// </summary>
    private sealed class CapReserver
    {
        private readonly Dictionary<AgentKind, int> _caps;
        private readonly Dictionary<AgentKind, int> _running = [];
        public List<AgentKind> ReserveCalls { get; } = [];

        public CapReserver(Dictionary<AgentKind, int> caps) { _caps = caps; }

        public bool TryReserve(AgentMembership member)
        {
            ReserveCalls.Add(member.Agent);
            if (!_caps.TryGetValue(member.Agent, out var cap)) cap = 0;
            var cur = _running.GetValueOrDefault(member.Agent);
            if (cap > 0 && cur >= cap) return false;
            _running[member.Agent] = cur + 1;
            return true;
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
        var reserver = new CapReserver(caps);
        // Pre-fill Cursor to its cap.
        Assert.True(reserver.TryReserve(Sub(Cursor)));
        reserver.ReserveCalls.Clear();

        var decision = await router.ResolveAsync(
            MakeItem(), project: null, CancellationToken.None,
            reserveSlot: reserver.TryReserve);

        Assert.NotNull(decision.Chosen);
        Assert.Equal(Claude, decision.Chosen!.Agent);
        Assert.True(decision.SlotReserved);
        Assert.False(decision.ShouldWait);
        // The router asked for Cursor first (top score) and got refused;
        // then asked for Claude and got accepted.
        Assert.Equal([Cursor, Claude], reserver.ReserveCalls);
        Assert.Equal(1, reserver.Running(Cursor));   // pre-existing
        Assert.Equal(1, reserver.Running(Claude));   // just reserved
    }

    [Fact]
    public async Task AllEligibleMembersAtCap_DefersWithAllMembersAtCapFlag()
    {
        // Every member already at cap → decision must be ShouldWait with
        // AllMembersAtCap=true and the at-cap agents listed.
        var cls = FrontierClass(Sub(Cursor, score: 95), Sub(Claude, score: 90));
        var router = BuildRouter(cls,
            [new FakeProbe(Cursor, 90.0), new FakeProbe(Claude, 90.0)]);

        var reserver = new CapReserver(new()
        {
            [Cursor] = 1,
            [Claude] = 1,
        });
        Assert.True(reserver.TryReserve(Sub(Cursor)));
        Assert.True(reserver.TryReserve(Sub(Claude)));
        reserver.ReserveCalls.Clear();

        var decision = await router.ResolveAsync(
            MakeItem(), project: null, CancellationToken.None,
            reserveSlot: reserver.TryReserve);

        Assert.Null(decision.Chosen);
        Assert.True(decision.ShouldWait);
        Assert.True(decision.AllMembersAtCap);
        Assert.False(decision.SlotReserved);
        Assert.Equal(new[] { Cursor, Claude }, decision.AtCapAgents);
        // Running counts are unchanged — no spill candidate succeeded.
        Assert.Equal(1, reserver.Running(Cursor));
        Assert.Equal(1, reserver.Running(Claude));
    }

    [Fact]
    public async Task BelowFloorMembers_NotConsideredAsSpillTargets()
    {
        // Item requires MinModelScore=95 — only Cursor (95) qualifies. With
        // Cursor at cap, the lower-scored Claude must NOT be used as spill;
        // the item defers with AllMembersAtCap=true.
        var cls = FrontierClass(Sub(Cursor, score: 95), Sub(Claude, score: 90));
        var router = BuildRouter(cls,
            [new FakeProbe(Cursor, 90.0), new FakeProbe(Claude, 90.0)]);

        var reserver = new CapReserver(new() { [Cursor] = 1, [Claude] = 2 });
        Assert.True(reserver.TryReserve(Sub(Cursor)));
        reserver.ReserveCalls.Clear();

        var decision = await router.ResolveAsync(
            MakeItem(minScore: 95), project: null, CancellationToken.None,
            reserveSlot: reserver.TryReserve);

        Assert.Null(decision.Chosen);
        Assert.True(decision.ShouldWait);
        Assert.True(decision.AllMembersAtCap);
        Assert.Equal([Cursor], decision.AtCapAgents);
        // Claude was never asked to reserve — it was filtered out by the floor.
        Assert.DoesNotContain(Claude, reserver.ReserveCalls);
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

        var reserver = new CapReserver(new()
        {
            [Cursor] = 1,
            [Claude] = 2,
            [Codex] = 1,
        });
        Assert.True(reserver.TryReserve(Sub(Cursor)));
        reserver.ReserveCalls.Clear();

        var decision = await router.ResolveAsync(
            MakeItem(), project: null, CancellationToken.None,
            reserveSlot: reserver.TryReserve);

        Assert.NotNull(decision.Chosen);
        Assert.Equal(Codex, decision.Chosen!.Agent);
        Assert.True(decision.SlotReserved);
        // The smoke-failed Claude must never have been asked to reserve.
        Assert.DoesNotContain(Claude, reserver.ReserveCalls);
    }

    [Fact]
    public async Task QuotaExhaustedMembers_NotConsideredAsSpillTargets()
    {
        // Top member (Cursor) at cap. Second (Claude) is quota-exhausted.
        // Third (Codex) is free → choose Codex. Claude's reserveSlot must
        // never be invoked because it fails the quota gate first.
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

        var reserver = new CapReserver(new()
        {
            [Cursor] = 1,
            [Claude] = 2,
            [Codex] = 1,
        });
        Assert.True(reserver.TryReserve(Sub(Cursor)));
        reserver.ReserveCalls.Clear();

        var decision = await router.ResolveAsync(
            MakeItem(), project: null, CancellationToken.None,
            reserveSlot: reserver.TryReserve);

        Assert.NotNull(decision.Chosen);
        Assert.Equal(Codex, decision.Chosen!.Agent);
        Assert.True(decision.SlotReserved);
        Assert.DoesNotContain(Claude, reserver.ReserveCalls);
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

        var reserver = new CapReserver(new() { [Cursor] = 2, [Claude] = 2 });

        var decision = await router.ResolveAsync(
            MakeItem(), project: null, CancellationToken.None,
            reserveSlot: reserver.TryReserve);

        Assert.NotNull(decision.Chosen);
        Assert.Equal(Cursor, decision.Chosen!.Agent);
        Assert.True(decision.SlotReserved);
        // Only Cursor was asked to reserve — Claude never visited.
        Assert.Equal([Cursor], reserver.ReserveCalls);
        Assert.Equal(1, reserver.Running(Cursor));
        Assert.Equal(0, reserver.Running(Claude));
    }

    [Fact]
    public async Task NoReserveSlot_BackwardCompatible_StillRoutesFirstMember()
    {
        // Calling ResolveAsync without the reserveSlot callback preserves the
        // legacy behavior: no cap check, decision.SlotReserved=false.
        var cls = FrontierClass(Sub(Cursor, score: 95), Sub(Claude, score: 90));
        var router = BuildRouter(cls,
            [new FakeProbe(Cursor, 90.0), new FakeProbe(Claude, 90.0)]);

        var decision = await router.ResolveAsync(MakeItem(), project: null, CancellationToken.None);

        Assert.NotNull(decision.Chosen);
        Assert.Equal(Cursor, decision.Chosen!.Agent);
        Assert.False(decision.SlotReserved);
    }

    [Fact]
    public async Task MixedCapAndQuotaRejection_SurfacesAllMembersAtCap()
    {
        // One member quota-rejected (below threshold), one at cap. There is
        // no eligible-and-free member, so the item must defer. The router
        // surfaces AllMembersAtCap=true because at least one eligible
        // (quota-passing) member was blocked solely by the cap — the
        // orchestrator picks the short cap-retry rather than waiting the
        // full quota recheck interval.
        var cls = FrontierClass(Sub(Cursor, score: 95), Sub(Claude, score: 90));
        var router = BuildRouter(cls,
            [
                new FakeProbe(Cursor, 90.0),
                new FakeProbe(Claude, 2.0),   // quota-rejected
            ]);

        var reserver = new CapReserver(new() { [Cursor] = 1, [Claude] = 1 });
        Assert.True(reserver.TryReserve(Sub(Cursor)));
        reserver.ReserveCalls.Clear();

        var decision = await router.ResolveAsync(
            MakeItem(), project: null, CancellationToken.None,
            reserveSlot: reserver.TryReserve);

        Assert.Null(decision.Chosen);
        Assert.True(decision.ShouldWait);
        Assert.True(decision.AllMembersAtCap);
        Assert.Equal([Cursor], decision.AtCapAgents);
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

        var reserver = new CapReserver(new()
        {
            [Cursor] = 1,
            [Claude] = 2,
            [Codex] = 2,
        });
        Assert.True(reserver.TryReserve(Sub(Cursor)));
        reserver.ReserveCalls.Clear();

        var decision = await router.ResolveAsync(
            MakeItem(), project: null, CancellationToken.None,
            reserveSlot: reserver.TryReserve);

        Assert.NotNull(decision.Chosen);
        Assert.Equal(Claude, decision.Chosen!.Agent);
        // Should have visited Cursor (rejected by cap) then Claude (chosen);
        // Codex never visited.
        Assert.Equal([Cursor, Claude], reserver.ReserveCalls);
    }
}
