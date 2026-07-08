namespace CodeyBox.Core;

/// <summary>
/// Classifies a terminal work-item failure into a
/// <see cref="TerminalFailureClass"/>. The default implementation,
/// <see cref="DefaultTerminalFailureClassifier"/>, is pure and
/// deterministic (keyed off <see cref="WorkItem.FailureKind"/>,
/// <see cref="WorkItem.LastError"/>, and cheap counters); future
/// implementations may layer an LLM-precision pass on top.
/// </summary>
public interface ITerminalFailureClassifier
{
    /// <summary>
    /// Classify <paramref name="item"/>. Implementations MUST default to
    /// <see cref="TerminalFailureClass.Unknown"/> when the failure shape
    /// cannot be confidently placed — the recovery service treats Unknown
    /// as "park for operator, do NOT auto-retry" so unknown failures never
    /// loop.
    /// </summary>
    TerminalFailureClassification Classify(WorkItem item);
}

/// <summary>
/// Pure deterministic classifier keyed off the failure_kind taxonomy
/// already produced by the pipeline plus a small number of cheap signals
/// from <paramref name="WorkItem"/>. Stateless and side-effect-free; safe
/// to register as a singleton.
/// </summary>
public sealed class DefaultTerminalFailureClassifier : ITerminalFailureClassifier
{
    public TerminalFailureClassification Classify(WorkItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        // PolicyQuota: per-window exhaustion is owned by the dedicated
        // quota retry scheduler. Surface the class so the recovery service
        // can log + no-op; never auto-retry from here or the targeted
        // timer is competing with a periodic sweep we own.
        if (string.Equals(item.FailureKind, "quota", StringComparison.OrdinalIgnoreCase))
            return new TerminalFailureClassification(
                TerminalFailureClass.PolicyQuota,
                "failureKind=quota: owned by QuotaRetryScheduler");

        // Runtime login prompts require operator re-authentication. Retrying the
        // same item before credentials are repaired only sends work back to the
        // benched agent.
        if (string.Equals(item.FailureKind, WorkItemFailureKinds.AuthRequired, StringComparison.OrdinalIgnoreCase))
            return new TerminalFailureClassification(
                TerminalFailureClass.Deterministic,
                "failureKind=auth_required: operator re-authentication required");

        // Cancellations are operator/host actions, not flaky infrastructure.
        // Auto-retrying would override the operator's explicit decision.
        if (string.Equals(item.FailureKind, "cancelled", StringComparison.OrdinalIgnoreCase))
            return new TerminalFailureClassification(
                TerminalFailureClass.Deterministic,
                "failureKind=cancelled: operator/host cancellation must not auto-retry");

        // Transient (auto-retryable) infrastructure / network / agent-process
        // / VM-provisioning failure shapes. The orchestrator stamps these on
        // failures that originated outside the agent's reasoning loop.
        if (string.Equals(item.FailureKind, WorkItemFailureKinds.Infrastructure, StringComparison.OrdinalIgnoreCase))
            return new TerminalFailureClassification(
                TerminalFailureClass.Transient,
                "failureKind=infrastructure: provisioning/network/sandbox transient");

        // Smoke-gate / pickup-time credential failures. The credential may
        // have rotated since the cache was filled; a later retry should
        // re-probe and re-evaluate.
        if (string.Equals(item.FailureKind, WorkItemFailureKinds.AgentUnavailable, StringComparison.OrdinalIgnoreCase))
            return new TerminalFailureClassification(
                TerminalFailureClass.Transient,
                "failureKind=agent_unavailable: credential/smoke transient");

        // Wall-clock timeouts and unattributed cancellations carry a
        // counter so we can degrade from Transient → Deterministic when
        // the same time-budget keeps blowing up. Persistent
        // TransientCancelRetries above 0 means the pipeline already
        // attempted at least one transient retry on the previous run; if
        // we hit the terminal state anyway, retrying again is unlikely to
        // help.
        if (string.Equals(item.FailureKind, "timeout", StringComparison.OrdinalIgnoreCase))
        {
            if (item.TransientCancelRetries > 0)
                return new TerminalFailureClassification(
                    TerminalFailureClass.Deterministic,
                    "failureKind=timeout: prior transient-cancel retries already exhausted");

            return new TerminalFailureClassification(
                TerminalFailureClass.Transient,
                "failureKind=timeout: first-attempt timeout (eligible for one bounded retry)");
        }

        // Build / agent-internal / configuration / other agent-reasoning
        // failures. Retrying with the same prompt and the same branch
        // cannot fix them — the agent itself already gave up. The
        // operator either rework the prompt, fix project config, or take
        // the item off-pipeline. The pipeline has its own focused loop
        // for compile failures (it converts them to a finding); reaching
        // a terminal state with build/agent kind means even that loop
        // surrendered.
        if (string.Equals(item.FailureKind, "build", StringComparison.OrdinalIgnoreCase))
            return new TerminalFailureClassification(
                TerminalFailureClass.Deterministic,
                "failureKind=build: agent code does not compile; rework required");

        if (string.Equals(item.FailureKind, "agent", StringComparison.OrdinalIgnoreCase))
            return new TerminalFailureClassification(
                TerminalFailureClass.Deterministic,
                "failureKind=agent: agent-internal failure (e.g. stuck probe); rework required");

        if (string.Equals(item.FailureKind, "configuration", StringComparison.OrdinalIgnoreCase))
            return new TerminalFailureClassification(
                TerminalFailureClass.Deterministic,
                "failureKind=configuration: pipeline rejected the work-item's config");

        // Audit non-convergence (state AuditFailed) is deterministic: the
        // audit loop produced its last verdict and an unchanged retry would
        // re-run the same audit on the same diff. Operator must intervene.
        if (item.State == WorkItemState.AuditFailed)
            return new TerminalFailureClassification(
                TerminalFailureClass.Deterministic,
                "state=AuditFailed: audit non-convergence; unchanged retry cannot help");

        // Merge conflict resolution exhausted its bounded loop (preventive
        // auto-rebase + LLM rerun + one focused conflict-rework). Same input
        // would re-walk the same exhausted ladder.
        if (item.State == WorkItemState.MergeConflictResolutionFailed)
            return new TerminalFailureClassification(
                TerminalFailureClass.Deterministic,
                "state=MergeConflictResolutionFailed: conflict ladder exhausted");

        // Fail-closed default. Anything we cannot place lands here and the
        // recovery service treats Unknown as "leave parked for operator".
        return new TerminalFailureClassification(
            TerminalFailureClass.Unknown,
            $"unclassified: failureKind={item.FailureKind ?? "(null)"} state={item.State}");
    }
}
