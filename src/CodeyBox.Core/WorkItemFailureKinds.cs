namespace CodeyBox.Core;

public static class WorkItemFailureKinds
{
    public const string AuthRequired = "auth_required";

    /// <summary>
    /// Sandbox/provisioning failures the pipeline classified as infrastructure
    /// (binary missing, materialisation failure, network blip in setup). The
    /// agent's reasoning loop never meaningfully started.
    /// </summary>
    public const string Infrastructure = "infrastructure";

    /// <summary>
    /// Pickup-time credential / smoke gate refused dispatch. The credential
    /// may have rotated since the cache was filled; re-probing on a later
    /// retry is the recovery path.
    /// </summary>
    public const string AgentUnavailable = "agent_unavailable";

    /// <summary>
    /// Aggregate routing/capacity failure where no single agent was invoked
    /// and therefore no restored-agent sweep can safely attribute blame.
    /// </summary>
    public const string AgentRoutingUnavailable = "agent_routing_unavailable";

    /// <summary>
    /// Deterministic pipeline/auditor configuration error (malformed command,
    /// rejected work-item config). An unchanged retry fails identically, so
    /// downstream classifiers treat this as non-retryable and surface it
    /// immediately instead of spending recovery budget on it.
    /// </summary>
    public const string Configuration = "configuration";

    /// <summary>
    /// An exec was killed (or its output cut) for exceeding a provider output
    /// bound. Infrastructure-shaped — the agent did nothing wrong — but kept
    /// distinct from <see cref="Infrastructure"/> so a volume kill is never
    /// misreported as generic termination (for example exit 137).
    /// </summary>
    public const string OutputBound = "output-bound";

    /// <summary>
    /// A runner exhausted its bounded same-session continuations after the
    /// model hit its per-turn output-token ceiling mid-turn (structured
    /// stop reason <c>max_tokens</c>; see
    /// <see cref="OutputTokenLimit"/>). The agent was cut off by a provider
    /// ceiling — not declining to work — so this is terminally distinct
    /// from an empty diff: downstream classifiers must never misreport it
    /// as a generic "produced no changes" failure. Not infra-shaped: the
    /// pipeline and the agent are healthy; retrying the same input cannot
    /// fit either, only a task that fits the ceiling can succeed.
    /// </summary>
    public const string OutputLimitExhausted = "output-limit-exhausted";

    /// <summary>
    /// A runner exhausted its bounded same-session continuations after a
    /// clean-exit turn ended with no final assistant message and no recorded
    /// edits (or no continuation was possible because no native session id
    /// was captured). The turn died mid-investigation — exploratory tool
    /// calls with no concluding message — not declining to work, so this is
    /// terminally distinct from an empty diff: downstream classifiers must
    /// never misreport it as a generic "produced no changes" failure. Not
    /// infra-shaped: the pipeline and the agent are healthy; a retry may
    /// succeed where the dead turn did not.
    /// </summary>
    public const string EndedWithoutFinalResponse = "ended-without-final-response";

    /// <summary>
    /// Upstream push blocked by the owned-branch lease guard: either a third
    /// party pushed to the CodeyBox-owned work branch (lease mismatch) or the
    /// remote diverged from an unrecorded prior push. Retrying the same input
    /// cannot succeed — the remote ref must be inspected (and the upstream
    /// step re-driven) by an operator — so downstream classifiers treat this
    /// as non-retryable and park the item instead of spending recovery budget.
    /// Never routed into conflict-rework: this is not a merge conflict.
    /// </summary>
    public const string UpstreamBlocked = "upstream_blocked";

    private static readonly string[] InfraShaped =
    [
        Infrastructure,
        AgentUnavailable,
        AuthRequired,
        OutputBound,
    ];

    public static bool IsInfraShaped(string? failureKind)
        => !string.IsNullOrEmpty(failureKind)
            && InfraShaped.Contains(failureKind, StringComparer.OrdinalIgnoreCase);
}
