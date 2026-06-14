namespace CodeyBox.Core;

/// <summary>
/// Source-neutral availability storage port for cross-cutting consumers such
/// as admin availability endpoints and run-outcome recorders. Dispatch call
/// sites that need effective gate semantics should layer that policy above
/// this port rather than adding source-specific read modes here.
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

    /// <summary>
    /// Feeds a "produced no changes on clean exit" outcome — the silent-failure
    /// signature a silently-broken agent exhibits when its exit code looks fine
    /// but the working tree is unchanged. After
    /// <see cref="AvailabilityOptions.MaxConsecutiveNoChanges"/> distinct work
    /// items in a row produce no changes, the agent is excluded. The same
    /// <paramref name="itemId"/> repeated (a retry of the same hard item) does
    /// not advance the counter, so a single legitimately-empty task can't trip
    /// the breaker on its own.
    /// </summary>
    AvailabilityTransition RecordNoChangesOutcome(AgentKind kind, WorkItemId itemId);

    /// <summary>
    /// Signals that <paramref name="kind"/> just produced real changes on a
    /// work item — clears the no-changes streak counter so an isolated
    /// no-change before this success is forgotten. Does NOT lift an existing
    /// no-changes exclusion: by design recovery from the breaker is operator-
    /// only via <c>POST /admin/agent/{name}/reset</c>, since an excluded
    /// silently-broken agent never gets dispatched and so never reaches this
    /// signal anyway.
    /// </summary>
    void RecordChangesProduced(AgentKind kind);

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
    DateTimeOffset? LastFastFailAt,
    int ConsecutiveNoChanges = 0,
    DateTimeOffset? LastNoChangesAt = null);
