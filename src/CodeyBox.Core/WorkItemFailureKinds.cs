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
