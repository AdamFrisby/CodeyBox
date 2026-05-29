namespace CodeyBox.Core;

/// <summary>
/// Narrow availability port for cross-cutting routing/dispatch consumers
/// (the agent-class router, the pipeline runner, the admin availability
/// endpoints). Exposes only the read / run-outcome / snapshot surface
/// those callers need, so they depend on this rather than the concrete
/// availability registry.
///
/// <para>Reset is deliberately absent: operator reset must clear both the
/// registry and the in-VM smoke cache atomically, so it lives only on
/// <see cref="IAgentAvailabilityReset"/>. If reset were exposed here, a routing
/// or dispatch consumer could clear the registry while leaving a cached in-VM
/// pass intact, which the next gated dispatch would reconcile straight back
/// onto the registry — re-asserting the pre-fix verdict before the operator's
/// fix is re-verified.</para>
///
/// <para>Lives in <c>CodeyBox.Core</c> alongside <see cref="AgentAvailability"/>
/// and <see cref="IInVmSmokeGate"/> so the host (<c>CodeyBox.Api</c> admin
/// endpoints + DI) and other layers bind to a core-owned contract rather than an
/// orchestrator-internal type.</para>
///
/// <para>The smoke-subsystem-internal mutators that carry the exclusion
/// taxonomy (MarkSmokeResult with source + clearsFastFail, and
/// ExcludeForMissingProbe) are deliberately kept off this port — on the
/// orchestrator's <c>ISmokeAvailabilityRegistry</c> — so the exclusion model
/// stays encapsulated to the host/in-VM smoke services and coverage policy that
/// own it.</para>
/// </summary>
public interface IAgentAvailabilityRegistry
{
    /// <summary>Whether the agent is currently routable, with an exclusion reason when not.</summary>
    AgentAvailability GetAvailability(AgentKind kind);

    /// <summary>Feeds a real agent-run outcome into the fast-fail circuit breaker.</summary>
    AvailabilityTransition RecordRunOutcome(AgentKind kind, bool success, TimeSpan duration);

    /// <summary>Snapshot of every tracked agent's current state.</summary>
    IReadOnlyList<AgentAvailabilitySnapshot> Snapshot();
}

/// <summary>
/// Single operator-reset port for an agent's availability. Resetting an agent
/// after correcting an exclusion (installing the missing binary, rotating
/// credentials) must clear BOTH the availability registry <em>and</em> the in-VM
/// smoke cache as one operation. If a caller cleared only the registry, the next
/// gated dispatch would replay a stale cached pass via the in-VM prober's
/// cache-hit reconciliation and re-assert the pre-fix verdict, so the operator's
/// fix would not actually be re-verified until the cache TTL elapsed. Exposing
/// one contract keeps that pairing from leaking into — and being forgotten by —
/// callers such as the admin HTTP endpoint.
/// </summary>
public interface IAgentAvailabilityReset
{
    /// <summary>
    /// Clears <paramref name="kind"/>'s exclusion state and fast-fail counters
    /// and invalidates every cached in-VM smoke verdict for it, so the next
    /// sweep / dispatch re-execs the CLI from scratch.
    /// </summary>
    void Reset(AgentKind kind);
}

/// <summary>
/// State transition returned by registry mutators. Callers use
/// <c>!PreviouslyExcluded &amp;&amp; NowExcluded</c> to fire "agent newly
/// excluded" webhook events and <c>PreviouslyExcluded &amp;&amp; !NowExcluded</c>
/// to fire "agent recovered" events without duplicates on steady state.
/// </summary>
public sealed record AvailabilityTransition(bool PreviouslyExcluded, bool NowExcluded, string? Reason);

/// <summary>Per-agent state surfaced via the admin / concurrency endpoints.</summary>
public sealed record AgentAvailabilitySnapshot(
    AgentKind Agent,
    bool Excluded,
    string? Reason,
    int ConsecutiveFastFails,
    DateTimeOffset? LastSmokePassedAt,
    DateTimeOffset? LastSmokeFailedAt,
    DateTimeOffset? LastFastFailAt);
