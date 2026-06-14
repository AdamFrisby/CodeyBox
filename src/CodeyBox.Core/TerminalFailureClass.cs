namespace CodeyBox.Core;

/// <summary>
/// Coarse routing class for a terminal work-item failure (Failed,
/// AuditFailed, MergeConflictResolutionFailed). Picked by
/// <see cref="ITerminalFailureClassifier"/> and consumed by the
/// recovery service that decides whether to auto-retry, park, or
/// dead-letter the item.
/// <para>
/// This taxonomy replaces the blunt "requeue every terminal failure"
/// reflex that an external operator chaperone implemented: TRANSIENT
/// failures retry with bounded backoff; DETERMINISTIC failures park
/// for operator triage (re-running unchanged input cannot help);
/// quota stays delegated to the existing per-window scheduler.
/// </para>
/// </summary>
public enum TerminalFailureClass
{
    /// <summary>
    /// Classifier could not place the failure into any other class.
    /// Fail-closed: do NOT auto-retry — surface it for operator triage.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Infrastructure / network / agent-process / sandbox-provisioning
    /// failure that is reasonable to retry: the same input has a chance
    /// of succeeding next time. Recovery service schedules a bounded
    /// number of auto-retries with exponential backoff + jitter, then
    /// dead-letters to <see cref="WorkItemState.NeedsOperatorInput"/>.
    /// </summary>
    Transient = 1,

    /// <summary>
    /// Failure where retrying the same input cannot succeed — the agent
    /// produced output that does not compile, the audit cannot converge,
    /// configuration is rejected, the operator cancelled. The recovery
    /// service does NOT auto-retry; the item remains in its terminal
    /// state and the audit log records the classification so operators
    /// can take action.
    /// </summary>
    Deterministic = 2,

    /// <summary>
    /// Per-window quota exhaustion. Owned by
    /// <c>QuotaRetryScheduler</c>; the recovery service surfaces a single
    /// audit log line and otherwise no-ops to avoid stepping on the
    /// per-window retry scheduler's targeted timers.
    /// </summary>
    PolicyQuota = 3,
}

/// <summary>
/// Classifier output. <see cref="Reason"/> captures the signal the
/// classifier used (failure_kind matched, last-error pattern, repeated
/// identical failure, etc.) so the audit log row is self-describing and
/// post-hoc triage does not need to re-run the classifier.
/// </summary>
public sealed record TerminalFailureClassification(
    TerminalFailureClass Class,
    string Reason);
