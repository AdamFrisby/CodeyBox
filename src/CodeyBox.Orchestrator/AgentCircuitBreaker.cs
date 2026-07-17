using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Lifecycle phase of a per-agent dispatch circuit breaker.
/// </summary>
public enum CircuitPhase
{
    /// <summary>Dispatch flows normally; failures accumulate in a rolling window.</summary>
    Closed,

    /// <summary>Dispatch is blocked until the cooldown elapses.</summary>
    Open,

    /// <summary>Cooldown elapsed; a bounded number of trial dispatches are admitted to probe recovery.</summary>
    HalfOpen,
}

/// <summary>
/// A single state transition a breaker made, used purely to drive audit-log
/// emission from the stateful holder — the pure core never logs.
/// </summary>
public enum CircuitTransition
{
    None,
    Opened,
    HalfOpened,
    Closed,
    ReOpened,
}

/// <summary>
/// Validated, immutable per-agent breaker tuning. Constructed via
/// <see cref="Create"/> so an invalid combination cannot be represented.
/// </summary>
public sealed record CircuitBreakerSettings
{
    private CircuitBreakerSettings(int failureThreshold, TimeSpan window, TimeSpan cooldown, int halfOpenTrials)
    {
        FailureThreshold = failureThreshold;
        Window = window;
        Cooldown = cooldown;
        HalfOpenTrials = halfOpenTrials;
    }

    /// <summary>Consecutive windowed failures that OPEN the breaker. ≥ 1.</summary>
    public int FailureThreshold { get; }

    /// <summary>Rolling window failures are counted within. &gt; 0.</summary>
    public TimeSpan Window { get; }

    /// <summary>How long the breaker stays Open before admitting a half-open trial. ≥ 0.</summary>
    public TimeSpan Cooldown { get; }

    /// <summary>Trial dispatches admitted while HalfOpen before further dispatch is blocked. ≥ 1.</summary>
    public int HalfOpenTrials { get; }

    /// <summary>
    /// Builds settings, clamping each field into its valid range rather than
    /// throwing: config is operator-supplied and hot-reloaded, so a bad value
    /// must degrade to a safe default instead of crashing the dispatch path.
    /// </summary>
    public static CircuitBreakerSettings Create(
        int failureThreshold, TimeSpan window, TimeSpan cooldown, int halfOpenTrials) =>
        new(
            failureThreshold < 1 ? 1 : failureThreshold,
            window <= TimeSpan.Zero ? TimeSpan.FromMinutes(5) : window,
            cooldown < TimeSpan.Zero ? TimeSpan.Zero : cooldown,
            halfOpenTrials < 1 ? 1 : halfOpenTrials);
}

/// <summary>
/// Immutable snapshot of one breaker's state. A value-like record so the pure
/// <see cref="CircuitBreakerLogic"/> can transform it without mutating shared
/// data; the stateful <see cref="AgentCircuitBreaker"/> swaps the reference
/// under a per-key lock.
/// </summary>
public sealed record CircuitBreakerState
{
    /// <summary>Current phase.</summary>
    public CircuitPhase Phase { get; init; } = CircuitPhase.Closed;

    /// <summary>
    /// Failure timestamps still inside the rolling window while
    /// <see cref="CircuitPhase.Closed"/>. Cleared by any success.
    /// </summary>
    public ImmutableArray<DateTimeOffset> RecentFailures { get; init; } = ImmutableArray<DateTimeOffset>.Empty;

    /// <summary>When the breaker last OPENED — anchors the cooldown.</summary>
    public DateTimeOffset? OpenedAt { get; init; }

    /// <summary>Trial dispatches admitted-but-unresolved while <see cref="CircuitPhase.HalfOpen"/>.</summary>
    public int HalfOpenInFlight { get; init; }

    /// <summary>When the current half-open probe window began — anchors stale-trial expiry.</summary>
    public DateTimeOffset? HalfOpenSince { get; init; }

    /// <summary>The initial closed state a never-seen agent starts from.</summary>
    public static CircuitBreakerState Initial { get; } = new();
}

/// <summary>
/// Result of a pure transition: the next state, whether a dispatch is admitted
/// (for <see cref="CircuitBreakerLogic.BeginDispatch"/>), and the transition
/// that fired so the caller can audit it.
/// </summary>
public readonly record struct CircuitOutcome(
    CircuitBreakerState State,
    CircuitTransition Transition,
    bool Allowed,
    int FailureCount);

/// <summary>
/// Pure decision core for the per-agent dispatch circuit breaker. Every method
/// is a total function of (state, now, settings) → new state, so the behaviour
/// is exhaustively unit-testable with an injected clock and holds no ambient
/// state. The stateful <see cref="AgentCircuitBreaker"/> is the only mutator.
/// </summary>
public static class CircuitBreakerLogic
{
    /// <summary>
    /// Non-mutating check: would a dispatch be admitted at <paramref name="now"/>?
    /// Used by the router's gate to decide exclusion/spill and by readiness peeks
    /// (which must never consume a half-open trial).
    /// </summary>
    public static bool CanDispatch(CircuitBreakerState s, DateTimeOffset now, CircuitBreakerSettings cfg) =>
        s.Phase switch
        {
            CircuitPhase.Closed => true,
            CircuitPhase.Open => CooldownElapsed(s.OpenedAt, now, cfg),
            CircuitPhase.HalfOpen => EffectiveInFlight(s, now, cfg) < cfg.HalfOpenTrials,
            _ => true,
        };

    /// <summary>
    /// Mutating admission at the dispatch-commit point. Consumes a half-open
    /// trial and performs the Open→HalfOpen transition when the cooldown has
    /// elapsed. Returns <see cref="CircuitOutcome.Allowed"/> = false when the
    /// breaker is Open within cooldown or all half-open trials are outstanding.
    /// </summary>
    public static CircuitOutcome BeginDispatch(CircuitBreakerState s, DateTimeOffset now, CircuitBreakerSettings cfg)
    {
        switch (s.Phase)
        {
            case CircuitPhase.Closed:
                return new CircuitOutcome(s, CircuitTransition.None, Allowed: true, s.RecentFailures.Length);

            case CircuitPhase.Open:
                if (!CooldownElapsed(s.OpenedAt, now, cfg))
                    return new CircuitOutcome(s, CircuitTransition.None, Allowed: false, s.RecentFailures.Length);
                // Cooldown elapsed: enter HalfOpen and admit the first trial.
                return new CircuitOutcome(
                    s with
                    {
                        Phase = CircuitPhase.HalfOpen,
                        HalfOpenInFlight = 1,
                        HalfOpenSince = now,
                        RecentFailures = ImmutableArray<DateTimeOffset>.Empty,
                    },
                    CircuitTransition.HalfOpened,
                    Allowed: true,
                    FailureCount: 0);

            case CircuitPhase.HalfOpen:
            default:
                var eff = EffectiveInFlight(s, now, cfg);
                if (eff >= cfg.HalfOpenTrials)
                    return new CircuitOutcome(s, CircuitTransition.None, Allowed: false, 0);
                // A stale probe window (eff reset to 0) starts a fresh window at now.
                var since = eff == 0 ? now : s.HalfOpenSince;
                return new CircuitOutcome(
                    s with { HalfOpenInFlight = eff + 1, HalfOpenSince = since },
                    CircuitTransition.None,
                    Allowed: true,
                    FailureCount: 0);
        }
    }

    /// <summary>
    /// Records a dispatch outcome. A success resets the breaker to Closed from
    /// any phase; a failure of ANY kind increments the windowed counter (Closed),
    /// re-opens (HalfOpen), or refreshes the cooldown (Open).
    /// </summary>
    public static CircuitOutcome Observe(
        CircuitBreakerState s, bool success, DateTimeOffset now, CircuitBreakerSettings cfg)
    {
        if (success)
        {
            if (s.Phase == CircuitPhase.Closed)
                // Reset the consecutive-failure window; stay closed, no transition.
                return new CircuitOutcome(
                    s with { RecentFailures = ImmutableArray<DateTimeOffset>.Empty },
                    CircuitTransition.None, Allowed: true, FailureCount: 0);

            // A trial (or lingering) success closes the breaker.
            return new CircuitOutcome(CircuitBreakerState.Initial, CircuitTransition.Closed, Allowed: true, 0);
        }

        switch (s.Phase)
        {
            case CircuitPhase.Closed:
                var failures = PruneWindow(s.RecentFailures, now, cfg.Window).Add(now);
                if (failures.Length >= cfg.FailureThreshold)
                    return new CircuitOutcome(
                        new CircuitBreakerState { Phase = CircuitPhase.Open, OpenedAt = now },
                        CircuitTransition.Opened, Allowed: false, failures.Length);
                return new CircuitOutcome(
                    s with { RecentFailures = failures }, CircuitTransition.None, Allowed: true, failures.Length);

            case CircuitPhase.HalfOpen:
                // A trial failed — re-open with a fresh cooldown.
                return new CircuitOutcome(
                    new CircuitBreakerState { Phase = CircuitPhase.Open, OpenedAt = now },
                    CircuitTransition.ReOpened, Allowed: false, 1);

            case CircuitPhase.Open:
            default:
                // Lingering failure while already open extends the cooldown.
                return new CircuitOutcome(
                    s with { OpenedAt = now }, CircuitTransition.None, Allowed: false, 0);
        }
    }

    /// <summary>
    /// When a currently-blocking breaker will next admit a dispatch, or null
    /// when dispatch is allowed right now. Drives the router's defer interval so
    /// a benched agent is retried as soon as its cooldown elapses rather than on
    /// the longer quota-recheck cadence.
    /// </summary>
    public static DateTimeOffset? NextRetryAt(CircuitBreakerState s, DateTimeOffset now, CircuitBreakerSettings cfg)
    {
        switch (s.Phase)
        {
            case CircuitPhase.Open:
                return CooldownElapsed(s.OpenedAt, now, cfg) ? null : s.OpenedAt!.Value + cfg.Cooldown;
            case CircuitPhase.HalfOpen:
                if (EffectiveInFlight(s, now, cfg) < cfg.HalfOpenTrials) return null;
                return s.HalfOpenSince is { } hs ? hs + cfg.Cooldown : null;
            default:
                return null;
        }
    }

    private static bool CooldownElapsed(DateTimeOffset? openedAt, DateTimeOffset now, CircuitBreakerSettings cfg) =>
        openedAt is not { } at || now >= at + cfg.Cooldown;

    // Half-open trials that were admitted but never resolved (e.g. the dispatch
    // was aborted by host shutdown before recording an outcome) would otherwise
    // pin the breaker HalfOpen forever. Treat the probe window as expired once a
    // full cooldown passes with no resolution, freeing a fresh trial.
    private static int EffectiveInFlight(CircuitBreakerState s, DateTimeOffset now, CircuitBreakerSettings cfg) =>
        s.HalfOpenSince is { } since && now >= since + cfg.Cooldown ? 0 : s.HalfOpenInFlight;

    private static ImmutableArray<DateTimeOffset> PruneWindow(
        ImmutableArray<DateTimeOffset> failures, DateTimeOffset now, TimeSpan window)
    {
        if (failures.IsDefaultOrEmpty) return ImmutableArray<DateTimeOffset>.Empty;
        var cutoff = now - window;
        var builder = ImmutableArray.CreateBuilder<DateTimeOffset>(failures.Length);
        foreach (var ts in failures)
            if (ts > cutoff) builder.Add(ts);
        return builder.ToImmutable();
    }
}

/// <summary>
/// Per-(agent[,instance]) dispatch circuit breaker: a safety net that benches
/// an agent after <see cref="CircuitBreakerSettings.FailureThreshold"/> failures
/// of ANY kind within a rolling window, independent of quota classification.
/// It composes with the quota gate — an agent is dispatchable only when BOTH
/// this breaker and <see cref="QuotaGatePolicy"/> allow.
///
/// <para>
/// State is keyed by the member's normalized route key so one instance of an
/// agent can be benched while a sibling instance stays healthy; tuning is
/// resolved per agent kind (with global fallback) from a hot-reloadable
/// <see cref="AgentCircuitBreakerSnapshot"/>. All decision logic lives in the
/// pure <see cref="CircuitBreakerLogic"/>; this class only owns the concurrent
/// state map, per-key locking, and audit emission.
/// </para>
///
/// <para>Thread-safe: every read-modify-write is serialized on a per-key lock.</para>
/// </summary>
public sealed class AgentCircuitBreaker
{
    private sealed class Box
    {
        public CircuitBreakerState State = CircuitBreakerState.Initial;
    }

    private readonly AgentCircuitBreakerSnapshot _options;
    private readonly ILogger<AgentCircuitBreaker> _log;
    private readonly ConcurrentDictionary<string, Box> _states = new(StringComparer.Ordinal);

    public AgentCircuitBreaker(AgentCircuitBreakerSnapshot options, ILogger<AgentCircuitBreaker> log)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Non-mutating gate check: is a dispatch to <paramref name="member"/>
    /// currently admitted by the breaker? Returns true when the breaker is
    /// disabled so callers compose it unconditionally.
    /// </summary>
    public bool IsDispatchAllowed(AgentMembership member, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(member);
        var cfg = _options.Current;
        if (!cfg.Enabled) return true;
        var settings = cfg.ResolveSettings(member.Agent);
        var box = GetBox(member.RouteKey);
        lock (box) return CircuitBreakerLogic.CanDispatch(box.State, now, settings);
    }

    /// <summary>
    /// Mutating admission at the dispatch-commit point: consumes a half-open
    /// trial and performs the Open→HalfOpen transition when eligible. Returns
    /// false when the breaker blocks dispatch (Open within cooldown, or all
    /// half-open trials outstanding) so the router spills to the next member.
    /// </summary>
    public bool TryBeginDispatch(AgentMembership member, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(member);
        var cfg = _options.Current;
        if (!cfg.Enabled) return true;
        var settings = cfg.ResolveSettings(member.Agent);
        var box = GetBox(member.RouteKey);
        CircuitOutcome outcome;
        lock (box)
        {
            outcome = CircuitBreakerLogic.BeginDispatch(box.State, now, settings);
            box.State = outcome.State;
        }
        EmitTransition(member, outcome, settings);
        return outcome.Allowed;
    }

    /// <summary>Records a dispatch outcome for a resolved membership.</summary>
    public void RecordOutcome(AgentMembership member, bool success, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(member);
        RecordOutcome(member.Agent, member.RouteKey, success, now);
    }

    /// <summary>
    /// Records a dispatch outcome keyed by a canonical route key (e.g. from the
    /// pipeline's invocation chokepoint, where the work item already carries the
    /// chosen member's route key). A success resets the breaker; a failure of
    /// ANY kind feeds the windowed counter. The route key must be canonical —
    /// the same value <see cref="AgentMembership.RouteKey"/> produces — so the
    /// outcome lands on the same state the dispatch gate reads.
    /// </summary>
    public void RecordOutcome(AgentKind agent, string routeKey, bool success, DateTimeOffset now)
    {
        var cfg = _options.Current;
        if (!cfg.Enabled) return;
        var settings = cfg.ResolveSettings(agent);
        var box = GetBox(routeKey);
        CircuitOutcome outcome;
        lock (box)
        {
            outcome = CircuitBreakerLogic.Observe(box.State, success, now, settings);
            box.State = outcome.State;
        }
        EmitTransition(agent, routeKey, outcome, settings);
    }

    /// <summary>
    /// When the breaker will next admit a dispatch to <paramref name="member"/>,
    /// or null when dispatch is allowed now (or the breaker is disabled).
    /// </summary>
    public DateTimeOffset? NextRetryAt(AgentMembership member, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(member);
        var cfg = _options.Current;
        if (!cfg.Enabled) return null;
        var settings = cfg.ResolveSettings(member.Agent);
        var box = GetBox(member.RouteKey);
        lock (box) return CircuitBreakerLogic.NextRetryAt(box.State, now, settings);
    }

    /// <summary>Current phase for a member — surface/diagnostics only.</summary>
    public CircuitPhase PhaseOf(AgentMembership member)
    {
        ArgumentNullException.ThrowIfNull(member);
        var box = GetBox(member.RouteKey);
        lock (box) return box.State.Phase;
    }

    private Box GetBox(string routeKey) =>
        _states.GetOrAdd(AgentQuotaMemberKey.NormalizeRouteKey(routeKey), static _ => new Box());

    private void EmitTransition(AgentMembership member, CircuitOutcome outcome, CircuitBreakerSettings settings) =>
        EmitTransition(member.Agent, member.RouteKey, outcome, settings);

    private void EmitTransition(AgentKind agent, string routeKey, CircuitOutcome outcome, CircuitBreakerSettings settings)
    {
        if (outcome.Transition == CircuitTransition.None) return;

        var (from, to, reason) = outcome.Transition switch
        {
            CircuitTransition.Opened => (CircuitPhase.Closed, CircuitPhase.Open,
                $"{outcome.FailureCount} failures within {settings.Window}"),
            CircuitTransition.ReOpened => (CircuitPhase.HalfOpen, CircuitPhase.Open, "half-open trial failed"),
            CircuitTransition.HalfOpened => (CircuitPhase.Open, CircuitPhase.HalfOpen,
                $"cooldown {settings.Cooldown} elapsed; admitting trial dispatch"),
            CircuitTransition.Closed => (CircuitPhase.HalfOpen, CircuitPhase.Closed, "dispatch succeeded"),
            _ => (CircuitPhase.Closed, CircuitPhase.Closed, ""),
        };

        AuditLog.AgentCircuitBreakerTransition(
            agent, routeKey, from.ToString(), to.ToString(), reason, outcome.FailureCount);
        _log.LogInformation(
            "Agent circuit breaker {RouteKey}: {From} → {To} ({Reason})",
            routeKey, from, to, reason);
    }
}
