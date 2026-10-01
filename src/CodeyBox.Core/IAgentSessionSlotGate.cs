namespace CodeyBox.Core;

/// <summary>
/// Which kind of agent CLI session a slot reservation covers. Work sessions
/// are the sequential single turns of an item (work, rework, delegation,
/// conflict resolution) — at most one is active per item at a time, held
/// under the item's dispatch reservation. Audit sessions are the parallel
/// per-auditor LLM review runs, which fan out up to
/// <c>MaxLlmAuditorParallelism</c> concurrent sessions per item.
/// </summary>
public enum AgentSessionKind
{
    Work,
    Audit,
}

/// <summary>
/// Audit-aware extension of <see cref="IAgentSlotGate"/>. Work reservations
/// (the inherited <c>TryReserve</c>/<c>Release</c>) keep their dispatch-time
/// semantics; audit reservations cover each LLM auditor CLI session and are
/// gated against the same per-agent total (<c>MaxConcurrent</c>) plus an
/// optional per-agent audit sub-cap (<c>MaxConcurrentAuditSessions</c>), so
/// a provider's real concurrent-session limit is honoured whether the
/// session is a work turn or an auditor run.
///
/// <para>
/// <b>Deadlock contract (release-before-acquire):</b> an item holding a work
/// slot MUST release it before its own audit sessions wait for slots —
/// otherwise a cap of 1 self-deadlocks: the item waits on an audit slot that
/// only its own held work slot could free. The pipeline calls
/// <see cref="SuspendWorkSlotForAudit"/> before the LLM auditor fan-out and
/// <see cref="ResumeWorkSlotAfterAuditAsync"/> once the batch settles, so a
/// waiting item never holds a slot it is not actively using. While suspended
/// the freed capacity is genuinely available to other items' sessions; the
/// post-audit re-acquire is a normal capped wait, so progress never depends
/// on a holder that is itself blocked (work and audit sessions always run to
/// completion once admitted).
/// </para>
/// </summary>
public interface IAgentSessionSlotGate : IAgentSlotGate
{
    /// <summary>
    /// Non-blocking audit reservation. Returns true and increments the
    /// in-flight audit count for the member's route when the route's total
    /// (work + audit) is under its <c>MaxConcurrent</c> cap and its audit
    /// count is under the effective audit sub-cap; returns false otherwise.
    /// Every success MUST be paired with
    /// <see cref="ReleaseAudit(AgentMembership)"/> on every exit path
    /// (including auditor failure, timeout, and cancellation).
    /// </summary>
    bool TryReserveAudit(AgentMembership member);

    /// <summary>Kind-scoped variant of <see cref="TryReserveAudit(AgentMembership)"/>.</summary>
    bool TryReserveAudit(AgentKind agent);

    /// <summary>Releases an audit slot reserved for the member's route.</summary>
    void ReleaseAudit(AgentMembership member);

    /// <summary>Kind-scoped variant of <see cref="ReleaseAudit(AgentMembership)"/>.</summary>
    void ReleaseAudit(AgentKind agent);

    /// <summary>
    /// Asynchronously waits for an audit slot on the member's route,
    /// honouring <paramref name="ct"/> (cancellation never consumes a slot).
    /// Implementations wake waiters promptly on <c>Release</c>/
    /// <c>ReleaseAudit</c>/cap-relax so a cap-1 item finishing work then
    /// auditing completes without polling.
    /// </summary>
    Task WaitForAuditSlotAsync(AgentMembership member, CancellationToken ct);

    /// <summary>Kind-scoped variant of <see cref="WaitForAuditSlotAsync(AgentMembership, CancellationToken)"/>.</summary>
    Task WaitForAuditSlotAsync(AgentKind agent, CancellationToken ct);

    /// <summary>
    /// Releases the work slot the item reserved at dispatch so its audit
    /// sessions can acquire slots of the same agent without self-deadlock.
    /// Idempotent: returns false (no-op) when the item has no registered
    /// work slot (e.g. agent-control items or embedders that run the
    /// pipeline without a dispatch reservation) or it is already suspended.
    /// </summary>
    bool SuspendWorkSlotForAudit(WorkItemId item);

    /// <summary>
    /// Re-acquires the item's work slot after an audit batch — a normal
    /// capped wait, never granted while suspended capacity is accounted to
    /// the item. No-op when the item is not suspended (already held or never
    /// registered). Cancellation never consumes a slot: if
    /// <paramref name="ct"/> fires mid-wait the item stays suspended and the
    /// item-scoped release path still balances the books.
    /// </summary>
    Task ResumeWorkSlotAfterAuditAsync(WorkItemId item, CancellationToken ct);

    /// <summary>Live in-flight work sessions for <paramref name="agent"/>.</summary>
    int GetRunningWork(AgentKind agent);

    /// <summary>Live in-flight audit sessions for <paramref name="agent"/>.</summary>
    int GetRunningAudit(AgentKind agent);

    /// <summary>
    /// Effective audit sub-cap for the member's route:
    /// <c>MaxConcurrentAuditSessions</c> when configured, else the route's
    /// effective <c>MaxConcurrent</c>, else 0 (uncapped).
    /// </summary>
    int GetAuditCap(AgentMembership member);

    /// <summary>Kind-scoped variant of <see cref="GetAuditCap(AgentMembership)"/>.</summary>
    int GetAuditCap(AgentKind agent);
}
