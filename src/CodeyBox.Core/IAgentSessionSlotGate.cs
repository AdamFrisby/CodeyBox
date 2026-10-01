namespace CodeyBox.Core;

/// <summary>
/// Which kind of agent CLI session a slot reservation covers. Work sessions
/// are the sequential single turns of an item (work, rework, delegation,
/// conflict resolution) — at most one is active per item at a time. Audit
/// sessions are the parallel per-auditor LLM review runs, which fan out up
/// to <c>MaxLlmAuditorParallelism</c> concurrent sessions per item.
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
/// optional per-agent audit sub-cap (<c>MaxConcurrentAuditSessions</c>).
///
/// <para>
/// <b>Deadlock contract (release-before-acquire):</b> an item holding a work
/// slot MUST release it before acquiring audit slots for its own review —
/// the work phase is finished by then, so the held slot would otherwise
/// starve the audit it is waiting for under a tight cap (cap 1 self-deadlock).
/// After the audit batch the item re-acquires a work slot before any rework
/// turn. Work and audit slots are therefore never held simultaneously by one
/// item while it waits for the other kind.
/// </para>
/// </summary>
public interface IAgentSessionSlotGate : IAgentSlotGate
{
    /// <summary>
    /// Non-blocking audit reservation. Returns true and increments the
    /// in-flight audit count when the agent's total (work + audit) is under
    /// its <c>MaxConcurrent</c> cap and its audit count is under its audit
    /// sub-cap; returns false otherwise. Every success MUST be paired with
    /// <see cref="ReleaseAudit(AgentKind)"/> on every exit path (including
    /// auditor failure, timeout, and cancellation).
    /// </summary>
    bool TryReserveAudit(AgentKind agent);

    /// <summary>Member-scoped variant of <see cref="TryReserveAudit(AgentKind)"/>.</summary>
    bool TryReserveAudit(AgentMembership member);

    /// <summary>Releases an audit slot reserved via <see cref="TryReserveAudit(AgentKind)"/> or <see cref="WaitForAuditSlotAsync(AgentKind, CancellationToken)"/>.</summary>
    void ReleaseAudit(AgentKind agent);

    /// <summary>Member-scoped variant of <see cref="ReleaseAudit(AgentKind)"/>.</summary>
    void ReleaseAudit(AgentMembership member);

    /// <summary>
    /// Asynchronously waits for an audit slot, honouring <paramref name="ct"/>
    /// (cancellation never consumes a slot). Implementations SHOULD wake
    /// waiters promptly on <c>Release</c>/<c>ReleaseAudit</c>/cap-relax so a
    /// cap-1 item finishing work then auditing completes without polling.
    /// </summary>
    Task WaitForAuditSlotAsync(AgentKind agent, CancellationToken ct);

    /// <summary>Member-scoped variant of <see cref="WaitForAuditSlotAsync(AgentKind, CancellationToken)"/>.</summary>
    Task WaitForAuditSlotAsync(AgentMembership member, CancellationToken ct);

    /// <summary>
    /// Asynchronously waits for a work slot (used to re-acquire the dispatch
    /// slot after an audit batch released it). Cancellation never consumes
    /// a slot.
    /// </summary>
    Task WaitForWorkSlotAsync(AgentKind agent, CancellationToken ct);

    /// <summary>Member-scoped variant of <see cref="WaitForWorkSlotAsync(AgentKind, CancellationToken)"/>.</summary>
    Task WaitForWorkSlotAsync(AgentMembership member, CancellationToken ct);

    /// <summary>Live in-flight work sessions for <paramref name="agent"/>.</summary>
    int GetRunningWork(AgentKind agent);

    /// <summary>Live in-flight audit sessions for <paramref name="agent"/>.</summary>
    int GetRunningAudit(AgentKind agent);

    /// <summary>
    /// Effective audit sub-cap for <paramref name="agent"/>:
    /// <c>MaxConcurrentAuditSessions</c> when configured, else
    /// <c>MaxConcurrent</c>, else 0 (uncapped).
    /// </summary>
    int GetAuditCap(AgentKind agent);
}
