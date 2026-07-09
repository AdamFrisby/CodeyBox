using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <see cref="AgentAvailabilityRegistry"/> — covers the two
/// load-bearing signals (smoke probe + fast-fail circuit breaker) and the
/// router-side exclusion that prevents the exit-127 cascade described in
/// the cb-216a2230 bug report (14 Cursor items lost after the binary was
/// missing from the multipass image).
/// </summary>
public sealed class AgentAvailabilityRegistryTests
{
    private static readonly AgentKind Claude = AgentKind.Claude;
    private static readonly AgentKind Codex = AgentKind.Codex;

    private static AgentAvailabilityRegistry NewRegistry(
        int fastFailThreshold = 10,
        int maxConsecutive = 3,
        int maxConsecutiveNoChanges = 3)
    {
        var opts = new AvailabilityOptions
        {
            FastFailThresholdSeconds = fastFailThreshold,
            MaxConsecutiveFastFails = maxConsecutive,
            MaxConsecutiveNoChanges = maxConsecutiveNoChanges,
        };
        return new AgentAvailabilityRegistry(opts, TimeProvider.System, NullLogger<AgentAvailabilityRegistry>.Instance);
    }

    private static WorkItemId NewItem() => new(Guid.NewGuid());

    // ── Smoke probe signal ────────────────────────────────────────────────────

    [Fact]
    public void NewRegistry_AnyAgent_IsAvailable()
    {
        var reg = NewRegistry();
        Assert.True(reg.GetAvailability(Claude).Available);
        Assert.Null(reg.GetAvailability(Claude).Reason);
    }

    [Fact]
    public void SmokeFail_ExcludesAgent()
    {
        var reg = NewRegistry();
        reg.MarkSmokeResult(Claude, new AgentSmokeResult(false, "auth", TimeSpan.FromMilliseconds(5)));

        var av = reg.GetAvailability(Claude);
        Assert.False(av.Available);
        Assert.Contains("auth", av.Reason);
    }

    [Fact]
    public void SmokePass_AfterFail_RestoresAgent()
    {
        var reg = NewRegistry();
        reg.MarkSmokeResult(Claude, new AgentSmokeResult(false, "auth", TimeSpan.Zero));
        reg.MarkSmokeResult(Claude, new AgentSmokeResult(true, null, TimeSpan.Zero));

        Assert.True(reg.GetAvailability(Claude).Available);
    }

    [Fact]
    public void SmokeFail_Transition_IsReported()
    {
        var reg = NewRegistry();
        var first = reg.MarkSmokeResult(Claude, new AgentSmokeResult(false, "auth", TimeSpan.Zero));
        var second = reg.MarkSmokeResult(Claude, new AgentSmokeResult(false, "auth", TimeSpan.Zero));

        Assert.False(first.PreviouslyExcluded);
        Assert.True(first.NowExcluded);
        // Steady-state failure does not look like a transition.
        Assert.True(second.PreviouslyExcluded);
        Assert.True(second.NowExcluded);
    }

    [Fact]
    public void SmokeRecover_Transition_IsReported()
    {
        var reg = NewRegistry();
        reg.MarkSmokeResult(Claude, new AgentSmokeResult(false, "auth", TimeSpan.Zero));
        var transition = reg.MarkSmokeResult(Claude, new AgentSmokeResult(true, null, TimeSpan.Zero));

        Assert.True(transition.PreviouslyExcluded);
        Assert.False(transition.NowExcluded);
    }

    [Fact]
    public void SmokeRecover_EmitsAvailabilityRecoverySignalOnce()
    {
        var reg = NewRegistry();
        var recovered = new List<AgentKind>();
        reg.AgentRecovered += recovered.Add;

        reg.MarkSmokeResult(Codex, new AgentSmokeResult(false, "auth", TimeSpan.Zero));
        reg.MarkSmokeResult(Codex, new AgentSmokeResult(false, "auth", TimeSpan.Zero));
        reg.MarkSmokeResult(Codex, new AgentSmokeResult(true, null, TimeSpan.Zero));
        reg.MarkSmokeResult(Codex, new AgentSmokeResult(true, null, TimeSpan.Zero));

        Assert.Equal([Codex], recovered);
    }

    // ── Fast-fail circuit breaker ─────────────────────────────────────────────

    [Fact]
    public void SingleFastFail_DoesNotExclude()
    {
        var reg = NewRegistry(fastFailThreshold: 10, maxConsecutive: 3);
        reg.RecordRunOutcome(Claude, success: false, duration: TimeSpan.FromSeconds(1));
        Assert.True(reg.GetAvailability(Claude).Available);
    }

    [Fact]
    public void ThreeConsecutiveFastFails_ExcludesAgent()
    {
        var reg = NewRegistry(fastFailThreshold: 10, maxConsecutive: 3);
        for (var i = 0; i < 3; i++)
            reg.RecordRunOutcome(Claude, success: false, duration: TimeSpan.FromSeconds(1));

        var av = reg.GetAvailability(Claude);
        Assert.False(av.Available);
        Assert.Contains("fast-fail circuit breaker", av.Reason);
    }

    [Fact]
    public void SlowFailure_DoesNotCountAsFastFail()
    {
        // The exit-127 reported in cb-216a2230 fired in <5s. A 30-second
        // failure is a real-work failure and must not pollute the breaker.
        var reg = NewRegistry(fastFailThreshold: 10, maxConsecutive: 3);
        for (var i = 0; i < 5; i++)
            reg.RecordRunOutcome(Claude, success: false, duration: TimeSpan.FromSeconds(30));

        Assert.True(reg.GetAvailability(Claude).Available);
    }

    [Fact]
    public void SuccessfulRun_ResetsFastFailCounter()
    {
        var reg = NewRegistry(fastFailThreshold: 10, maxConsecutive: 3);
        reg.RecordRunOutcome(Claude, success: false, duration: TimeSpan.FromSeconds(1));
        reg.RecordRunOutcome(Claude, success: false, duration: TimeSpan.FromSeconds(1));
        reg.RecordRunOutcome(Claude, success: true, duration: TimeSpan.FromSeconds(30));

        // Two more fast fails — counter starts from zero so 2 < 3 means still available.
        reg.RecordRunOutcome(Claude, success: false, duration: TimeSpan.FromSeconds(1));
        reg.RecordRunOutcome(Claude, success: false, duration: TimeSpan.FromSeconds(1));
        Assert.True(reg.GetAvailability(Claude).Available);
    }

    [Fact]
    public void SlowFailure_AfterFastFails_ResetsCounter()
    {
        // A slow-failure run completed real work, so it cancels the breaker
        // even though success=false. Otherwise long-running quota failures
        // would pile up alongside genuine fast-fails.
        var reg = NewRegistry(fastFailThreshold: 10, maxConsecutive: 3);
        reg.RecordRunOutcome(Claude, success: false, duration: TimeSpan.FromSeconds(1));
        reg.RecordRunOutcome(Claude, success: false, duration: TimeSpan.FromSeconds(1));
        reg.RecordRunOutcome(Claude, success: false, duration: TimeSpan.FromSeconds(30));

        reg.RecordRunOutcome(Claude, success: false, duration: TimeSpan.FromSeconds(1));
        reg.RecordRunOutcome(Claude, success: false, duration: TimeSpan.FromSeconds(1));
        Assert.True(reg.GetAvailability(Claude).Available);
    }

    // ── Reset ────────────────────────────────────────────────────────────────

    [Fact]
    public void Reset_ClearsFastFailExclusion()
    {
        var reg = NewRegistry(fastFailThreshold: 10, maxConsecutive: 3);
        for (var i = 0; i < 3; i++)
            reg.RecordRunOutcome(Claude, success: false, duration: TimeSpan.FromSeconds(1));
        Assert.False(reg.GetAvailability(Claude).Available);

        reg.Reset(Claude);
        Assert.True(reg.GetAvailability(Claude).Available);
    }

    [Fact]
    public void Reset_ClearsSmokeExclusion()
    {
        var reg = NewRegistry();
        reg.MarkSmokeResult(Claude, new AgentSmokeResult(false, "auth", TimeSpan.Zero));
        Assert.False(reg.GetAvailability(Claude).Available);

        reg.Reset(Claude);
        Assert.True(reg.GetAvailability(Claude).Available);
    }

    // ── MissingProbe bench (coverage validator) ───────────────────────────────

    [Fact]
    public void MissingProbeExclusion_NotClearedByHostOrInVmSmokePass()
    {
        // An agent benched because it has no in-VM smoke probe can never be
        // un-benched by a smoke pass: there is no probe to ever pass, and a
        // pass from another source must not reach across to lift it. Only an
        // operator Reset (after a probe is registered) clears it.
        var reg = NewRegistry();
        reg.ExcludeForMissingProbe(Claude, "no in-VM smoke probe registered for claude");
        Assert.False(reg.GetAvailability(Claude).Available);

        // A host credential pass: clears HostSmoke only, leaves MissingProbe.
        reg.MarkSmokeResult(Claude, new AgentSmokeResult(true, null, TimeSpan.Zero), SmokeExclusionSource.HostSmoke);
        Assert.False(reg.GetAvailability(Claude).Available);

        // A freshly-executed in-VM pass: clears InVmSmoke + fast-fail, still
        // leaves MissingProbe (a different source).
        reg.MarkSmokeResult(Claude, new AgentSmokeResult(true, null, TimeSpan.Zero),
            SmokeExclusionSource.InVmSmoke, clearsFastFail: true);
        Assert.False(reg.GetAvailability(Claude).Available);
        Assert.Contains("no in-VM smoke probe", reg.GetAvailability(Claude).Reason);

        reg.Reset(Claude);
        Assert.True(reg.GetAvailability(Claude).Available);
    }

    [Fact]
    public void DispatchAvailability_WhenSmokeDisabled_IgnoresSmokeSourcesButKeepsFastFail()
    {
        var reg = NewRegistry(fastFailThreshold: 10, maxConsecutive: 3);
        var dispatchAvailability = new AgentDispatchAvailability(
            reg,
            inVmSmokeGate: null,
            smokeOptions: new SmokeOptionsSnapshot(new SmokeOptions { Enabled = false }));

        reg.MarkSmokeResult(
            Claude,
            new AgentSmokeResult(false, "host smoke failed", TimeSpan.Zero),
            SmokeExclusionSource.HostSmoke);
        reg.MarkSmokeResult(
            Claude,
            new AgentSmokeResult(false, "in-VM smoke failed", TimeSpan.Zero),
            SmokeExclusionSource.InVmSmoke);
        reg.ExcludeForMissingProbe(Claude, "no in-VM smoke probe registered");

        Assert.False(reg.GetAvailability(Claude).Available);
        Assert.True(dispatchAvailability.GetAvailability(Claude)!.Available);

        for (var i = 0; i < 3; i++)
            reg.RecordRunOutcome(Claude, success: false, duration: TimeSpan.FromSeconds(1));

        var av = dispatchAvailability.GetAvailability(Claude)!;
        Assert.False(av.Available);
        Assert.Contains("fast-fail circuit breaker", av.Reason);
        Assert.DoesNotContain("smoke failed", av.Reason);
        Assert.DoesNotContain("no in-VM smoke probe", av.Reason);
    }

    [Fact]
    public void CachedInVmPass_DoesNotClearFastFailExclusion()
    {
        // A cached in-VM verdict re-executed no CLI, so replaying it must NOT
        // lift a fast-fail bench earned from real sub-threshold dispatch
        // failures. Only a freshly executed in-VM probe (clearsFastFail:true)
        // or operator Reset may clear it.
        var reg = NewRegistry(fastFailThreshold: 10, maxConsecutive: 3);
        for (var i = 0; i < 3; i++)
            reg.RecordRunOutcome(Claude, success: false, duration: TimeSpan.FromSeconds(1));
        Assert.False(reg.GetAvailability(Claude).Available);

        // Cache-hit reconciliation path: pass with clearsFastFail:false.
        reg.MarkSmokeResult(Claude, new AgentSmokeResult(true, null, TimeSpan.Zero),
            SmokeExclusionSource.InVmSmoke, clearsFastFail: false);
        Assert.False(reg.GetAvailability(Claude).Available);
        Assert.Contains("fast-fail circuit breaker", reg.GetAvailability(Claude).Reason);

        // A freshly executed in-VM probe clears it.
        reg.MarkSmokeResult(Claude, new AgentSmokeResult(true, null, TimeSpan.Zero),
            SmokeExclusionSource.InVmSmoke, clearsFastFail: true);
        Assert.True(reg.GetAvailability(Claude).Available);
    }

    [Fact]
    public void Reset_UnknownAgent_DoesNotThrow()
    {
        var reg = NewRegistry();
        reg.Reset(Codex);
        Assert.True(reg.GetAvailability(Codex).Available);
    }

    // ── Per-agent isolation ───────────────────────────────────────────────────

    [Fact]
    public void OneAgentExcluded_OtherUnaffected()
    {
        var reg = NewRegistry();
        reg.MarkSmokeResult(Claude, new AgentSmokeResult(false, "auth", TimeSpan.Zero));
        Assert.False(reg.GetAvailability(Claude).Available);
        Assert.True(reg.GetAvailability(Codex).Available);
    }

    // ── Snapshot ──────────────────────────────────────────────────────────────

    [Fact]
    public void Snapshot_EmitsAllTrackedAgents()
    {
        var reg = NewRegistry();
        reg.MarkSmokeResult(Claude, new AgentSmokeResult(true, null, TimeSpan.Zero));
        reg.MarkSmokeResult(Codex, new AgentSmokeResult(false, "auth", TimeSpan.Zero));

        var snap = reg.Snapshot();
        Assert.Equal(2, snap.Count);
        var c = snap.Single(s => s.Agent == Claude);
        var x = snap.Single(s => s.Agent == Codex);
        Assert.False(c.Excluded);
        Assert.True(x.Excluded);
    }

    // ── No-changes circuit breaker ───────────────────────────────────────────
    // Cause-agnostic backstop for silently-broken agents that exit 0 but leave
    // the working tree unchanged (auth collapse, capability collapse, unknown
    // failure modes). The fast-fail breaker only counts non-zero exits, so a
    // silently-broken agent would never trip it. The brief: count CONSECUTIVE
    // DISTINCT work items, not retries of the same item — so a single hard
    // task can't trip the breaker on its own.

    [Fact]
    public void SingleNoChanges_DoesNotExclude()
    {
        // An isolated no-change is not evidence of a broken agent — a single
        // work item could legitimately have nothing to do (rare but possible).
        var reg = NewRegistry(maxConsecutiveNoChanges: 3);
        reg.RecordNoChangesOutcome(Claude, NewItem());
        Assert.True(reg.GetAvailability(Claude).Available);
    }

    [Fact]
    public void ThreeConsecutiveDistinctNoChanges_ExcludesAgent()
    {
        // The headline acceptance: N distinct items in a row produce no
        // changes → agent excluded + reason names the breaker.
        var reg = NewRegistry(maxConsecutiveNoChanges: 3);
        var t1 = reg.RecordNoChangesOutcome(Claude, NewItem());
        var t2 = reg.RecordNoChangesOutcome(Claude, NewItem());
        var t3 = reg.RecordNoChangesOutcome(Claude, NewItem());

        Assert.False(t1.NowExcluded);
        Assert.False(t2.NowExcluded);
        Assert.False(t3.PreviouslyExcluded);
        Assert.True(t3.NowExcluded);
        Assert.True(t3.SourceChanged);

        var av = reg.GetAvailability(Claude);
        Assert.False(av.Available);
        Assert.Contains("no-changes circuit breaker", av.Reason);
    }

    [Fact]
    public void NoChangesTripReportsSourceChanged_WhenAgentAlreadyExcludedByAnotherSource()
    {
        var reg = NewRegistry(maxConsecutiveNoChanges: 2);
        reg.MarkSmokeResult(
            Claude,
            new AgentSmokeResult(false, "host smoke failed", TimeSpan.Zero),
            SmokeExclusionSource.HostSmoke);

        var t1 = reg.RecordNoChangesOutcome(Claude, NewItem());
        var t2 = reg.RecordNoChangesOutcome(Claude, NewItem());

        Assert.True(t1.PreviouslyExcluded);
        Assert.False(t1.SourceChanged);
        Assert.True(t2.PreviouslyExcluded);
        Assert.True(t2.NowExcluded);
        Assert.True(t2.SourceChanged);
        Assert.Contains("host smoke failed", reg.GetAvailability(Claude).Reason);
        Assert.Contains("no-changes circuit breaker", reg.GetAvailability(Claude).Reason);
    }

    [Fact]
    public void RepeatedNoChangesOnSameItem_DoesNotTrip()
    {
        // Distinct-item counting: a single hard item retried in place must not
        // advance the counter. Otherwise one legitimately-empty task on a
        // healthy agent would still trip the breaker on its 3rd retry.
        var reg = NewRegistry(maxConsecutiveNoChanges: 3);
        var item = NewItem();
        reg.RecordNoChangesOutcome(Claude, item);
        reg.RecordNoChangesOutcome(Claude, item);
        reg.RecordNoChangesOutcome(Claude, item);
        reg.RecordNoChangesOutcome(Claude, item);

        Assert.True(reg.GetAvailability(Claude).Available);
        var snap = reg.Snapshot().Single(s => s.Agent == Claude);
        Assert.Equal(1, snap.ConsecutiveNoChanges);
    }

    [Fact]
    public void NoChangesInterleavedWithSuccess_DoesNotTrip()
    {
        // Interleaving non-trip: a real "produced changes" run clears the
        // streak, so [no, no, success, no, no] is two streaks of length 2 —
        // never reaches the threshold of 3.
        var reg = NewRegistry(maxConsecutiveNoChanges: 3);
        reg.RecordNoChangesOutcome(Claude, NewItem());
        reg.RecordNoChangesOutcome(Claude, NewItem());
        reg.RecordChangesProduced(Claude);
        reg.RecordNoChangesOutcome(Claude, NewItem());
        reg.RecordNoChangesOutcome(Claude, NewItem());

        Assert.True(reg.GetAvailability(Claude).Available);
    }

    [Fact]
    public void NoChangesBreaker_NotTrippedByDifferentAgent()
    {
        // Per-agent isolation: claude's no-changes streak must not bench codex.
        var reg = NewRegistry(maxConsecutiveNoChanges: 3);
        for (var i = 0; i < 3; i++)
            reg.RecordNoChangesOutcome(Claude, NewItem());

        Assert.False(reg.GetAvailability(Claude).Available);
        Assert.True(reg.GetAvailability(Codex).Available);
    }

    [Fact]
    public void Reset_ClearsNoChangesExclusion()
    {
        // Recovery via the existing reset path — never bench permanently. The
        // operator runs POST /admin/agent/{name}/reset after diagnosing the
        // root cause and the agent is routable again.
        var reg = NewRegistry(maxConsecutiveNoChanges: 3);
        for (var i = 0; i < 3; i++)
            reg.RecordNoChangesOutcome(Claude, NewItem());
        Assert.False(reg.GetAvailability(Claude).Available);

        reg.Reset(Claude);
        Assert.True(reg.GetAvailability(Claude).Available);

        // After reset the counter starts fresh — a single no-change does
        // not immediately re-trip.
        reg.RecordNoChangesOutcome(Claude, NewItem());
        Assert.True(reg.GetAvailability(Claude).Available);
    }

    [Fact]
    public void RecordChangesProduced_DoesNotLiftExistingExclusion()
    {
        // By design: an already-excluded agent never receives dispatch, so the
        // signal would not fire in practice anyway — but if it ever does
        // (e.g. concurrent in-flight runs), it must not silently lift the
        // bench. Recovery is operator-only.
        var reg = NewRegistry(maxConsecutiveNoChanges: 3);
        for (var i = 0; i < 3; i++)
            reg.RecordNoChangesOutcome(Claude, NewItem());
        Assert.False(reg.GetAvailability(Claude).Available);

        reg.RecordChangesProduced(Claude);
        Assert.False(reg.GetAvailability(Claude).Available);
        Assert.Contains("no-changes circuit breaker", reg.GetAvailability(Claude).Reason);
    }

    [Fact]
    public void NoChangesBreaker_DisabledWhenThresholdIsZero()
    {
        // Disable knob: operator sets MaxConsecutiveNoChanges <= 0 to opt out
        // of the breaker entirely (e.g. while diagnosing a flapping signal)
        // without unwiring the rest of the registry.
        var reg = NewRegistry(maxConsecutiveNoChanges: 0);
        for (var i = 0; i < 10; i++)
            reg.RecordNoChangesOutcome(Claude, NewItem());

        Assert.True(reg.GetAvailability(Claude).Available);
    }

    [Fact]
    public void NoChangesBreaker_DoesNotAffectFastFailCounter()
    {
        // The two breakers are orthogonal: a clean-exit no-changes outcome
        // must not increment ConsecutiveFastFails, and vice versa.
        var reg = NewRegistry(maxConsecutive: 3, maxConsecutiveNoChanges: 3);
        reg.RecordNoChangesOutcome(Claude, NewItem());
        reg.RecordNoChangesOutcome(Claude, NewItem());

        var snap = reg.Snapshot().Single(s => s.Agent == Claude);
        Assert.Equal(0, snap.ConsecutiveFastFails);
        Assert.Equal(2, snap.ConsecutiveNoChanges);
    }

    [Fact]
    public void SteadyStateNoChangesAfterTrip_StillReportsTransitionOnce()
    {
        // Webhook firing semantics: only the !PreviouslyExcluded -> NowExcluded
        // edge fires the operator alert. Subsequent calls on the still-broken
        // agent must report the steady-state transition, not a re-trip.
        var reg = NewRegistry(maxConsecutiveNoChanges: 3);
        reg.RecordNoChangesOutcome(Claude, NewItem());
        reg.RecordNoChangesOutcome(Claude, NewItem());
        var trip = reg.RecordNoChangesOutcome(Claude, NewItem());
        var steady = reg.RecordNoChangesOutcome(Claude, NewItem());

        Assert.False(trip.PreviouslyExcluded);
        Assert.True(trip.NowExcluded);
        Assert.True(steady.PreviouslyExcluded);
        Assert.True(steady.NowExcluded);
    }
}
