using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Serilog;
using Serilog.Events;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for the per-agent dispatch failure circuit breaker: the pure
/// <see cref="CircuitBreakerLogic"/> state machine, the stateful
/// <see cref="AgentCircuitBreaker"/> holder (per-key isolation, disabled
/// no-op), <see cref="AgentCircuitBreakerOptions"/> resolution/clamping, and
/// the <see cref="AgentClassRouter"/> gate composition (a benched member is
/// excluded and routing spills to the next healthy member).
///
/// <para>
/// Joined to the <see cref="GlobalSerilogCollection"/>: opening/half-opening/
/// closing a breaker emits an <c>agent_circuit_breaker.transition</c> audit
/// event through the static <see cref="Serilog.Log.Logger"/>. Running in
/// parallel with the WebApplicationFactory-based collection tests that own that
/// static logger would interleave those emissions into their sinks, so this
/// class must be serialized alongside every other static-logger test.
/// </para>
/// </summary>
[Collection("GlobalSerilog")]
public sealed class AgentCircuitBreakerTests
{
    private static readonly DateTimeOffset T0 =
        new(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);

    private static CircuitBreakerSettings Settings(
        int threshold = 3,
        int windowMinutes = 5,
        int cooldownMinutes = 2,
        int halfOpenTrials = 1) =>
        CircuitBreakerSettings.Create(
            threshold,
            TimeSpan.FromMinutes(windowMinutes),
            TimeSpan.FromMinutes(cooldownMinutes),
            halfOpenTrials);

    // ── Pure state machine: Closed → Open ───────────────────────────────────

    [Fact]
    public void Closed_BelowThreshold_StaysClosedAndAllows()
    {
        var cfg = Settings(threshold: 3);
        var s = CircuitBreakerState.Initial;

        var o1 = CircuitBreakerLogic.Observe(s, success: false, T0, cfg);
        var o2 = CircuitBreakerLogic.Observe(o1.State, success: false, T0.AddSeconds(1), cfg);

        Assert.Equal(CircuitPhase.Closed, o2.State.Phase);
        Assert.Equal(CircuitTransition.None, o2.Transition);
        Assert.True(CircuitBreakerLogic.CanDispatch(o2.State, T0.AddSeconds(2), cfg));
        Assert.Equal(2, o2.FailureCount);
    }

    [Fact]
    public void Closed_AtThreshold_Opens()
    {
        var cfg = Settings(threshold: 3);
        var s = CircuitBreakerState.Initial;

        s = CircuitBreakerLogic.Observe(s, false, T0, cfg).State;
        s = CircuitBreakerLogic.Observe(s, false, T0.AddSeconds(1), cfg).State;
        var opened = CircuitBreakerLogic.Observe(s, false, T0.AddSeconds(2), cfg);

        Assert.Equal(CircuitPhase.Open, opened.State.Phase);
        Assert.Equal(CircuitTransition.Opened, opened.Transition);
        Assert.Equal(3, opened.FailureCount);
        Assert.False(CircuitBreakerLogic.CanDispatch(opened.State, T0.AddSeconds(3), cfg));
    }

    [Fact]
    public void Closed_FailuresOutsideWindow_ArePrunedAndDoNotOpen()
    {
        var cfg = Settings(threshold: 3, windowMinutes: 5);
        var s = CircuitBreakerState.Initial;

        // Two failures far apart, the first ages out of the 5-min window before
        // the third arrives — so only 2 remain in-window and it stays closed.
        s = CircuitBreakerLogic.Observe(s, false, T0, cfg).State;
        s = CircuitBreakerLogic.Observe(s, false, T0.AddMinutes(6), cfg).State;
        var third = CircuitBreakerLogic.Observe(s, false, T0.AddMinutes(7), cfg);

        Assert.Equal(CircuitPhase.Closed, third.State.Phase);
        Assert.Equal(2, third.FailureCount);
    }

    [Fact]
    public void Closed_SuccessResetsFailureWindow()
    {
        var cfg = Settings(threshold: 3);
        var s = CircuitBreakerState.Initial;
        s = CircuitBreakerLogic.Observe(s, false, T0, cfg).State;
        s = CircuitBreakerLogic.Observe(s, false, T0.AddSeconds(1), cfg).State;

        var reset = CircuitBreakerLogic.Observe(s, success: true, T0.AddSeconds(2), cfg);
        Assert.Empty(reset.State.RecentFailures);

        // A single fresh failure after the reset must not open (counter cleared).
        var afterReset = CircuitBreakerLogic.Observe(reset.State, false, T0.AddSeconds(3), cfg);
        Assert.Equal(CircuitPhase.Closed, afterReset.State.Phase);
        Assert.Equal(1, afterReset.FailureCount);
    }

    // ── Pure state machine: Open → HalfOpen → Closed/Open ───────────────────

    [Fact]
    public void Open_WithinCooldown_BlocksDispatch()
    {
        var cfg = Settings(threshold: 1, cooldownMinutes: 2);
        var opened = CircuitBreakerLogic.Observe(CircuitBreakerState.Initial, false, T0, cfg).State;

        Assert.False(CircuitBreakerLogic.CanDispatch(opened, T0.AddMinutes(1), cfg));
        var begin = CircuitBreakerLogic.BeginDispatch(opened, T0.AddMinutes(1), cfg);
        Assert.False(begin.Allowed);
        Assert.Equal(CircuitPhase.Open, begin.State.Phase);
    }

    [Fact]
    public void Open_AfterCooldown_BeginDispatchEntersHalfOpenAndAdmitsTrial()
    {
        var cfg = Settings(threshold: 1, cooldownMinutes: 2);
        var opened = CircuitBreakerLogic.Observe(CircuitBreakerState.Initial, false, T0, cfg).State;

        var begin = CircuitBreakerLogic.BeginDispatch(opened, T0.AddMinutes(2), cfg);

        Assert.True(begin.Allowed);
        Assert.Equal(CircuitPhase.HalfOpen, begin.State.Phase);
        Assert.Equal(CircuitTransition.HalfOpened, begin.Transition);
        Assert.Equal(1, begin.State.HalfOpenInFlight);
    }

    [Fact]
    public void HalfOpen_ExhaustsTrialsThenBlocks()
    {
        var cfg = Settings(threshold: 1, cooldownMinutes: 2, halfOpenTrials: 2);
        var opened = CircuitBreakerLogic.Observe(CircuitBreakerState.Initial, false, T0, cfg).State;

        var now = T0.AddMinutes(2);
        var b1 = CircuitBreakerLogic.BeginDispatch(opened, now, cfg);
        Assert.True(b1.Allowed);
        var b2 = CircuitBreakerLogic.BeginDispatch(b1.State, now, cfg);
        Assert.True(b2.Allowed);
        // Third trial within the same probe window is refused.
        var b3 = CircuitBreakerLogic.BeginDispatch(b2.State, now, cfg);
        Assert.False(b3.Allowed);
    }

    [Fact]
    public void HalfOpen_SuccessClosesBreaker()
    {
        var cfg = Settings(threshold: 1, cooldownMinutes: 2);
        var opened = CircuitBreakerLogic.Observe(CircuitBreakerState.Initial, false, T0, cfg).State;
        var half = CircuitBreakerLogic.BeginDispatch(opened, T0.AddMinutes(2), cfg).State;

        var closed = CircuitBreakerLogic.Observe(half, success: true, T0.AddMinutes(2), cfg);

        Assert.Equal(CircuitPhase.Closed, closed.State.Phase);
        Assert.Equal(CircuitTransition.Closed, closed.Transition);
        Assert.Empty(closed.State.RecentFailures);
    }

    [Fact]
    public void HalfOpen_FailureReOpensWithFreshCooldown()
    {
        var cfg = Settings(threshold: 1, cooldownMinutes: 2);
        var opened = CircuitBreakerLogic.Observe(CircuitBreakerState.Initial, false, T0, cfg).State;
        var trialAt = T0.AddMinutes(2);
        var half = CircuitBreakerLogic.BeginDispatch(opened, trialAt, cfg).State;

        var reopened = CircuitBreakerLogic.Observe(half, success: false, trialAt, cfg);

        Assert.Equal(CircuitPhase.Open, reopened.State.Phase);
        Assert.Equal(CircuitTransition.ReOpened, reopened.Transition);
        Assert.Equal(trialAt, reopened.State.OpenedAt);
        // Fresh cooldown anchored at the re-open, not the original open.
        Assert.False(CircuitBreakerLogic.CanDispatch(reopened.State, trialAt.AddMinutes(1), cfg));
        Assert.True(CircuitBreakerLogic.CanDispatch(reopened.State, trialAt.AddMinutes(2), cfg));
    }

    [Fact]
    public void Open_LingeringFailureExtendsCooldown()
    {
        var cfg = Settings(threshold: 1, cooldownMinutes: 2);
        var opened = CircuitBreakerLogic.Observe(CircuitBreakerState.Initial, false, T0, cfg).State;

        // A late failure lands while still open, one minute in.
        var extended = CircuitBreakerLogic.Observe(opened, false, T0.AddMinutes(1), cfg);

        Assert.Equal(CircuitPhase.Open, extended.State.Phase);
        Assert.Equal(T0.AddMinutes(1), extended.State.OpenedAt);
        // Original cooldown would have elapsed at T0+2; now pushed to T0+3.
        Assert.False(CircuitBreakerLogic.CanDispatch(extended.State, T0.AddMinutes(2), cfg));
        Assert.True(CircuitBreakerLogic.CanDispatch(extended.State, T0.AddMinutes(3), cfg));
    }

    [Fact]
    public void HalfOpen_StaleUnresolvedTrialExpiresAfterCooldown()
    {
        var cfg = Settings(threshold: 1, cooldownMinutes: 2);
        var opened = CircuitBreakerLogic.Observe(CircuitBreakerState.Initial, false, T0, cfg).State;
        var trialAt = T0.AddMinutes(2);
        var half = CircuitBreakerLogic.BeginDispatch(opened, trialAt, cfg).State;

        // The trial was admitted but never resolved. Within a cooldown it still
        // pins HalfOpen; once a full cooldown passes it frees a fresh trial.
        Assert.False(CircuitBreakerLogic.CanDispatch(half, trialAt.AddMinutes(1), cfg));
        Assert.True(CircuitBreakerLogic.CanDispatch(half, trialAt.AddMinutes(2), cfg));
    }

    [Fact]
    public void NextRetryAt_ReflectsCooldownEndAndClearsWhenAllowed()
    {
        var cfg = Settings(threshold: 1, cooldownMinutes: 2);
        var opened = CircuitBreakerLogic.Observe(CircuitBreakerState.Initial, false, T0, cfg).State;

        Assert.Equal(T0.AddMinutes(2), CircuitBreakerLogic.NextRetryAt(opened, T0.AddMinutes(1), cfg));
        Assert.Null(CircuitBreakerLogic.NextRetryAt(opened, T0.AddMinutes(2), cfg));
        Assert.Null(CircuitBreakerLogic.NextRetryAt(CircuitBreakerState.Initial, T0, cfg));
    }

    // ── Settings validation: clamp rather than throw ────────────────────────

    [Fact]
    public void Settings_Create_ClampsInvalidValues()
    {
        var s = CircuitBreakerSettings.Create(
            failureThreshold: 0,
            window: TimeSpan.Zero,
            cooldown: TimeSpan.FromSeconds(-5),
            halfOpenTrials: 0);

        Assert.Equal(1, s.FailureThreshold);
        Assert.True(s.Window > TimeSpan.Zero);
        Assert.Equal(TimeSpan.Zero, s.Cooldown);
        Assert.Equal(1, s.HalfOpenTrials);
    }

    // ── Options resolution: per-agent override inherits unset globals ───────

    [Fact]
    public void Options_ResolveSettings_AppliesOverrideAndInheritsGlobals()
    {
        var opts = new AgentCircuitBreakerOptions
        {
            FailureThreshold = 3,
            Window = TimeSpan.FromMinutes(5),
            Cooldown = TimeSpan.FromMinutes(2),
            HalfOpenTrials = 1,
            PerAgent =
            {
                ["claude"] = new AgentCircuitBreakerOverride { FailureThreshold = 7 },
            },
        };

        var claude = opts.ResolveSettings(AgentKind.Claude);
        Assert.Equal(7, claude.FailureThreshold);          // overridden
        Assert.Equal(TimeSpan.FromMinutes(2), claude.Cooldown); // inherited

        var codex = opts.ResolveSettings(AgentKind.Codex);
        Assert.Equal(3, codex.FailureThreshold);           // global default
    }

    // ── Stateful holder: disabled is a total no-op ──────────────────────────

    [Fact]
    public void Breaker_Disabled_AlwaysAllows()
    {
        var breaker = NewBreaker(new AgentCircuitBreakerOptions { Enabled = false, FailureThreshold = 1 });
        var member = Sub(AgentKind.Claude);

        breaker.RecordOutcome(member, success: false, T0);
        breaker.RecordOutcome(member, success: false, T0.AddSeconds(1));

        Assert.True(breaker.IsDispatchAllowed(member, T0.AddSeconds(2)));
        Assert.Null(breaker.NextRetryAt(member, T0.AddSeconds(2)));
    }

    // ── Stateful holder: per-route-key isolation ────────────────────────────

    [Fact]
    public void Breaker_BenchesOneInstance_LeavesSiblingHealthy()
    {
        var breaker = NewBreaker(new AgentCircuitBreakerOptions
        {
            Enabled = true,
            FailureThreshold = 2,
            Window = TimeSpan.FromMinutes(5),
            Cooldown = TimeSpan.FromMinutes(2),
        });

        var a = SubInstance(AgentKind.Claude, "alpha");
        var b = SubInstance(AgentKind.Claude, "beta");

        breaker.RecordOutcome(a, false, T0);
        breaker.RecordOutcome(a, false, T0.AddSeconds(1));

        Assert.False(breaker.IsDispatchAllowed(a, T0.AddSeconds(2)));
        Assert.Equal(CircuitPhase.Open, breaker.PhaseOf(a));
        // Sibling instance never failed → still dispatchable.
        Assert.True(breaker.IsDispatchAllowed(b, T0.AddSeconds(2)));
        Assert.Equal(CircuitPhase.Closed, breaker.PhaseOf(b));
    }

    [Fact]
    public void Breaker_FullCycle_OpensThenRecoversOnTrialSuccess()
    {
        var breaker = NewBreaker(new AgentCircuitBreakerOptions
        {
            Enabled = true,
            FailureThreshold = 2,
            Window = TimeSpan.FromMinutes(5),
            Cooldown = TimeSpan.FromMinutes(2),
            HalfOpenTrials = 1,
        });
        var member = Sub(AgentKind.Claude);

        breaker.RecordOutcome(member, false, T0);
        breaker.RecordOutcome(member, false, T0.AddSeconds(1)); // opens here; cooldown anchored at T0+1s
        Assert.False(breaker.IsDispatchAllowed(member, T0.AddMinutes(1)));

        // Cooldown elapsed: the commit-point admits exactly one half-open trial.
        var trialAt = T0.AddMinutes(3);
        Assert.True(breaker.TryBeginDispatch(member, trialAt));
        Assert.False(breaker.TryBeginDispatch(member, trialAt)); // second refused
        Assert.Equal(CircuitPhase.HalfOpen, breaker.PhaseOf(member));

        // Trial succeeds → breaker closes and dispatch flows freely again.
        breaker.RecordOutcome(member, success: true, trialAt.AddSeconds(30));
        Assert.Equal(CircuitPhase.Closed, breaker.PhaseOf(member));
        Assert.True(breaker.IsDispatchAllowed(member, trialAt.AddMinutes(1)));
    }

    // ── Router integration: benched member is excluded and routing spills ───

    [Fact]
    public async Task Router_BenchedMember_SpillsToHealthyMember()
    {
        var breaker = NewBreaker(new AgentCircuitBreakerOptions
        {
            Enabled = true,
            FailureThreshold = 2,
            Window = TimeSpan.FromMinutes(5),
            Cooldown = TimeSpan.FromMinutes(2),
        });
        var top = Sub(AgentKind.Claude, score: 95);   // higher-ranked but benched
        var next = Sub(AgentKind.Codex, score: 90);   // healthy fallback

        // Bench the top-ranked member.
        breaker.RecordOutcome(top, false, T0);
        breaker.RecordOutcome(top, false, T0.AddSeconds(1));
        Assert.Equal(CircuitPhase.Open, breaker.PhaseOf(top));

        var router = BuildRouterWithBreaker(
            FrontierClass(top, next),
            [new FakeProbe(AgentKind.Claude, 90.0), new FakeProbe(AgentKind.Codex, 90.0)],
            breaker);

        var decision = await router.ResolveAsync(MakeItem(), project: null, CancellationToken.None);

        Assert.NotNull(decision.Chosen);
        Assert.Equal(AgentKind.Codex, decision.Chosen!.Agent);
    }

    [Fact]
    public async Task Router_AllMembersBenched_DefersWithBreakerReason()
    {
        var breaker = NewBreaker(new AgentCircuitBreakerOptions
        {
            Enabled = true,
            FailureThreshold = 1,
            Window = TimeSpan.FromMinutes(5),
            Cooldown = TimeSpan.FromMinutes(2),
        });
        var only = Sub(AgentKind.Claude, score: 95);
        breaker.RecordOutcome(only, false, T0);
        Assert.Equal(CircuitPhase.Open, breaker.PhaseOf(only));

        var router = BuildRouterWithBreaker(
            FrontierClass(only),
            [new FakeProbe(AgentKind.Claude, 90.0)],
            breaker);

        var decision = await router.ResolveAsync(MakeItem(), project: null, CancellationToken.None);

        Assert.Null(decision.Chosen);
        Assert.True(decision.ShouldWait);
        Assert.Contains("circuit breaker", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Router_ClosedBreaker_LowQuota_DefersOnQuota()
    {
        // Tests the reverse composition branch: the circuit breaker is closed
        // and healthy, but the member's quota is below MinQuotaPct (5% < 10%).
        // The router must defer on quota floor despite the healthy breaker.
        var breaker = NewBreaker(new AgentCircuitBreakerOptions
        {
            Enabled = true,
            FailureThreshold = 2,
            Window = TimeSpan.FromMinutes(5),
            Cooldown = TimeSpan.FromMinutes(2),
        });
        var member = Sub(AgentKind.Claude, score: 95);
        Assert.Equal(CircuitPhase.Closed, breaker.PhaseOf(member));

        var router = BuildRouterWithBreaker(
            FrontierClass(member),
            [new FakeProbe(AgentKind.Claude, 5.0)],
            breaker);

        var decision = await router.ResolveAsync(MakeItem(), project: null, CancellationToken.None);

        Assert.Null(decision.Chosen);
        Assert.True(decision.ShouldWait);
        Assert.Contains("below the effective quota floor", decision.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CircuitPhase.Closed, breaker.PhaseOf(member));
    }

    [Fact]
    public async Task Router_RecordDispatchOutcome_ForwardsToBreakerAndExcludesMember()
    {
        // Validates AgentClassRouter.RecordDispatchOutcome forwards failures to
        // the router's internal circuit breaker and benches the member on subsequent routing.
        var breaker = NewBreaker(new AgentCircuitBreakerOptions
        {
            Enabled = true,
            FailureThreshold = 2,
            Window = TimeSpan.FromMinutes(5),
            Cooldown = TimeSpan.FromMinutes(2),
        });
        var top = Sub(AgentKind.Claude, score: 95);
        var next = Sub(AgentKind.Codex, score: 90);

        var router = BuildRouterWithBreaker(
            FrontierClass(top, next),
            [new FakeProbe(AgentKind.Claude, 90.0), new FakeProbe(AgentKind.Codex, 90.0)],
            breaker);

        router.RecordDispatchOutcome(top.Agent, top.RouteKey, success: false);
        router.RecordDispatchOutcome(top.Agent, top.RouteKey, success: false);

        Assert.Equal(CircuitPhase.Open, breaker.PhaseOf(top));

        var decision = await router.ResolveAsync(MakeItem(), project: null, CancellationToken.None);

        Assert.NotNull(decision.Chosen);
        Assert.Equal(AgentKind.Codex, decision.Chosen!.Agent);
    }

    [Fact]
    public async Task Router_AllPayPerApiMembersBenched_DefersWithBreakerReasonAndTightenedRecheck()
    {
        // When all PayPerApi members are benched by the circuit breaker, the router
        // must attribute the deferral to the circuit breaker (not budget exhaustion)
        // and tighten the retry interval to earliestBreakerRetry.
        var breaker = NewBreaker(new AgentCircuitBreakerOptions
        {
            Enabled = true,
            FailureThreshold = 1,
            Window = TimeSpan.FromMinutes(5),
            Cooldown = TimeSpan.FromMinutes(2),
        });
        var payPerApi = PayPerApi(AgentKind.Claude, score: 95);
        breaker.RecordOutcome(payPerApi, false, T0);
        Assert.Equal(CircuitPhase.Open, breaker.PhaseOf(payPerApi));

        var router = BuildRouterWithBreaker(
            FrontierClass(payPerApi),
            [new FakeProbe(AgentKind.Claude, 5.0)],
            breaker);

        var decision = await router.ResolveAsync(MakeItem(), project: null, CancellationToken.None);

        Assert.Null(decision.Chosen);
        Assert.True(decision.ShouldWait);
        Assert.Contains("circuit breaker", decision.Reason, StringComparison.OrdinalIgnoreCase);
        // Cooldown was 2m, failure at T0, router clock at T0+2s → 118s remaining until cooldown
        Assert.Equal(TimeSpan.FromSeconds(118), decision.SuggestedRecheckIn);
    }

    [Fact]
    public async Task Router_QuotaRetryAdmission_DoesNotBypassCircuitBreaker()
    {
        // QuotaRetryAdmission must NOT bypass an Open circuit breaker. The breaker
        // composes with quota-gate as an independent gate (blocked if EITHER blocks).
        var breaker = NewBreaker(new AgentCircuitBreakerOptions
        {
            Enabled = true,
            FailureThreshold = 1,
            Window = TimeSpan.FromMinutes(5),
            Cooldown = TimeSpan.FromMinutes(2),
        });
        var member = Sub(AgentKind.Claude, score: 95);

        var router = BuildRouterWithBreaker(
            FrontierClass(member),
            [new FakeProbe(AgentKind.Claude, 90.0)],
            breaker);

        var item = MakeItem();

        // While healthy, resolve a quota retry to create an active QuotaRetryAdmission for this item+member
        var initialRetry = await router.ResolveQuotaRetryAsync(item, project: null, CancellationToken.None);
        Assert.False(initialRetry.ShouldWait);

        // Now trip the circuit breaker
        breaker.RecordOutcome(member, false, T0);
        Assert.Equal(CircuitPhase.Open, breaker.PhaseOf(member));

        // Readiness check must report Unavailable due to open circuit breaker
        var readiness = await router.CheckReadinessAsync(
            item,
            project: null,
            new AlwaysHasCapacity(),
            CancellationToken.None);
        Assert.Equal(AgentRoutingReadinessState.Unavailable, readiness.State);
        Assert.Contains("circuit breaker", readiness.Reason, StringComparison.OrdinalIgnoreCase);

        // Dispatch resolve must also defer due to open circuit breaker
        var dispatchDecision = await router.ResolveAsync(item, project: null, CancellationToken.None);
        Assert.Null(dispatchDecision.Chosen);
        Assert.True(dispatchDecision.ShouldWait);
        Assert.Contains("circuit breaker", dispatchDecision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static AgentCircuitBreaker NewBreaker(AgentCircuitBreakerOptions opts) =>
        new(new AgentCircuitBreakerSnapshot(opts), NullLogger<AgentCircuitBreaker>.Instance);

    private static AgentClassRouter BuildRouterWithBreaker(
        AgentClass cls, IEnumerable<IAgentQuotaProbe> probes, AgentCircuitBreaker breaker) =>
        new(
            [cls],
            probes,
            new QuotaRouterOptions
            {
                MinQuotaPct = 10.0,
                QuotaRecheckInterval = TimeSpan.FromMinutes(5),
            },
            NullLogger<AgentClassRouter>.Instance,
            // Inject a fixed clock a couple of seconds past the seeded failures so
            // the router evaluates the breaker at the same instant the breaker was
            // opened at — otherwise the router would fall back to wall-clock, the
            // cooldown would have elapsed, and an Open breaker would already admit
            // a half-open trial (rule 8: injected clock, no wall-clock).
            timeProvider: new FakeTimeProvider(T0.AddSeconds(2)),
            circuitBreaker: breaker);

    private static AgentMembership Sub(AgentKind kind, int score = 100) =>
        new() { Agent = kind, Billing = AgentBilling.Subscription, QualityScore = score };

    private static AgentMembership SubInstance(AgentKind kind, string instanceId, int score = 100) =>
        new() { Agent = kind, InstanceId = instanceId, Billing = AgentBilling.Subscription, QualityScore = score };

    private static AgentMembership PayPerApi(AgentKind kind, int score = 100) =>
        new() { Agent = kind, Billing = AgentBilling.PayPerApi, QualityScore = score };

    private sealed class AlwaysHasCapacity : IAgentCapacitySnapshot
    {
        public bool HasCapacity(AgentKind agent) => true;
    }

    private static AgentClass FrontierClass(params AgentMembership[] members) => new()
    {
        Id = "frontier",
        DisplayName = "Frontier",
        Members = members,
    };

    private static WorkItem MakeItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("proj"),
        Title = "t",
        Prompt = "p",
        AgentClassId = "frontier",
    };
}

/// <summary>
/// Pure-function tests for <see cref="PipelineRunner.ClassifyDispatchOutcome"/>,
/// the single classifier every dispatch-attempt path feeds into
/// <see cref="AgentClassRouter.RecordDispatchOutcome"/>. It emits nothing, so
/// unlike <see cref="AgentCircuitBreakerTests"/> it does not join the serialized
/// static-logger collection and runs in parallel.
/// </summary>
public sealed class ClassifyDispatchOutcomeTests
{
    [Fact]
    public void Success_ResetsBreaker()
    {
        // No terminal exception → the attempt succeeded (true resets the window).
        Assert.Equal(true, PipelineRunner.ClassifyDispatchOutcome(error: null, genuineAttemptTimeout: false));
    }

    [Fact]
    public void GenuineAttemptTimeout_IsFailure()
    {
        // A real per-attempt timeout surfaces as an OCE but is the agent's fault.
        Assert.Equal(
            false,
            PipelineRunner.ClassifyDispatchOutcome(
                new OperationCanceledException(), genuineAttemptTimeout: true));
    }

    [Fact]
    public void HostOrPhaseCancellation_IsSkipped()
    {
        // An OCE that is NOT a genuine per-attempt timeout is a host/operator/phase
        // cancellation — null so it neither opens nor resets the breaker.
        Assert.Null(
            PipelineRunner.ClassifyDispatchOutcome(
                new OperationCanceledException(), genuineAttemptTimeout: false));
    }

    [Fact]
    public void NonCancellationException_IsFailure()
    {
        // Every other terminal exception (agent/quota/infrastructure) is a real
        // dispatch failure that feeds the windowed counter.
        Assert.Equal(
            false,
            PipelineRunner.ClassifyDispatchOutcome(
                new InvalidOperationException("agent error"), genuineAttemptTimeout: false));
    }

    [Fact]
    public void DerivedCancellationException_IsSkipped()
    {
        // Subtypes of OperationCanceledException (e.g. TaskCanceledException) that
        // are not genuine attempt timeouts are still host-side cancellations.
        Assert.Null(
            PipelineRunner.ClassifyDispatchOutcome(
                new TaskCanceledException(), genuineAttemptTimeout: false));
    }
}

/// <summary>
/// Verifies audit log emission for circuit breaker transitions.
/// Runs within the <see cref="GlobalSerilogCollection"/> since it mutates the static logger.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class AgentCircuitBreakerAuditLogTests : IDisposable
{
    private static readonly DateTimeOffset T0 =
        new(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);

    private readonly TestSink _sink = new();

    public AgentCircuitBreakerAuditLogTests()
    {
        Log.Logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Sink(_sink)
            .CreateLogger();
    }

    public void Dispose() => Log.CloseAndFlush();

    [Fact]
    public void BreakerTransitions_EmitAuditLogEvents()
    {
        var opts = new AgentCircuitBreakerOptions
        {
            Enabled = true,
            FailureThreshold = 2,
            Window = TimeSpan.FromMinutes(5),
            Cooldown = TimeSpan.FromMinutes(2),
            HalfOpenTrials = 1,
        };
        var breaker = new AgentCircuitBreaker(
            new AgentCircuitBreakerSnapshot(opts),
            NullLogger<AgentCircuitBreaker>.Instance);

        var member = new AgentMembership
        {
            Agent = AgentKind.Claude,
            Billing = AgentBilling.Subscription,
            QualityScore = 100,
        };

        // 1. First failure — below threshold, no transition event yet.
        breaker.RecordOutcome(member, false, T0);
        Assert.Empty(GetTransitionEvents());

        // 2. Second failure — reaches threshold 2: Closed → Open.
        breaker.RecordOutcome(member, false, T0.AddSeconds(1));
        var openEvents = GetTransitionEvents();
        var openEvt = Assert.Single(openEvents);
        Assert.Equal("Closed", GetScalar<string>(openEvt, "From"));
        Assert.Equal("Open", GetScalar<string>(openEvt, "To"));
        Assert.Equal("claude", GetScalar<string>(openEvt, "Agent"));
        Assert.Equal(2, GetScalar<int>(openEvt, "FailureCount"));

        // 3. Cooldown elapses: TryBeginDispatch triggers Open → HalfOpen.
        var trialAt = T0.AddMinutes(3);
        Assert.True(breaker.TryBeginDispatch(member, trialAt));
        var halfOpenEvents = GetTransitionEvents();
        Assert.Equal(2, halfOpenEvents.Count);
        var halfOpenEvt = halfOpenEvents[1];
        Assert.Equal("Open", GetScalar<string>(halfOpenEvt, "From"));
        Assert.Equal("HalfOpen", GetScalar<string>(halfOpenEvt, "To"));
        Assert.Equal("claude", GetScalar<string>(halfOpenEvt, "Agent"));

        // 4. Trial succeeds: HalfOpen → Closed.
        breaker.RecordOutcome(member, success: true, trialAt.AddSeconds(10));
        var closedEvents = GetTransitionEvents();
        Assert.Equal(3, closedEvents.Count);
        var closedEvt = closedEvents[2];
        Assert.Equal("HalfOpen", GetScalar<string>(closedEvt, "From"));
        Assert.Equal("Closed", GetScalar<string>(closedEvt, "To"));
        Assert.Equal("claude", GetScalar<string>(closedEvt, "Agent"));
        Assert.Equal(0, GetScalar<int>(closedEvt, "FailureCount"));
    }

    private List<LogEvent> GetTransitionEvents() =>
        _sink.Events
            .Where(e => e.Properties.TryGetValue("EventName", out var ev)
                && ev is ScalarValue sv
                && (string?)sv.Value == "agent_circuit_breaker.transition")
            .ToList();

    private static T? GetScalar<T>(LogEvent evt, string key)
    {
        if (!evt.Properties.TryGetValue(key, out var prop) || prop is not ScalarValue sv)
            return default;
        if (sv.Value is T t)
            return t;
        if (typeof(T) == typeof(int) && sv.Value is long l)
            return (T)(object)(int)l;
        return default;
    }
}
