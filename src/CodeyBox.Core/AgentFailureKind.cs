namespace CodeyBox.Core;

/// <summary>
/// Classification of a failed agent invocation. Used by the pipeline's
/// in-iteration quota fallback loop to decide whether to retry the iteration
/// against the next-best class member or surface the failure to the operator.
///
/// <para>
/// <see cref="QuotaExhausted"/> means the agent ran out of subscription quota
/// mid-flight (e.g. ChatGPT-Plus weekly cap, Anthropic 5h-rolling, Gemini
/// daily). The pipeline should mark the member exhausted and try the next one.
/// </para>
/// <para>
/// <see cref="Normal"/> covers genuine work failures — compile errors, agent
/// refusals, malformed output, etc. The iteration must fail; a retry against
/// a different member would just waste compute on the same task that the
/// agent itself signalled it couldn't complete.
/// </para>
/// </summary>
public enum AgentFailureKind
{
    /// <summary>Genuine work-related failure (the agent ran but reported failure).</summary>
    Normal = 0,

    /// <summary>
    /// Mid-flight quota exhaustion — the in-iteration fallback wrapper marks
    /// the member exhausted in the router and retries the same iteration
    /// against the next class member. Applies to work, rework (audit-loop),
    /// and merge phases.
    /// </summary>
    QuotaExhausted = 1,

    /// <summary>Transient network/connectivity failure that may benefit from a retry.</summary>
    TransientNetwork = 2,

    /// <summary>Authentication or authorisation failure (revoked token, expired creds).</summary>
    AuthError = 3,

    /// <summary>Failure shape the classifier didn't recognise.</summary>
    Unknown = 4,

    /// <summary>
    /// Sandbox/provisioning failure rather than an agent-health failure: the
    /// agent binary could not be launched, or runner prerequisite
    /// materialisation failed before the CLI meaningfully started.
    /// </summary>
    Infrastructure = 5,
}

/// <summary>
/// More specific quota shape for <see cref="AgentFailureKind.QuotaExhausted"/>.
/// Kept separate from <see cref="AgentFailureClassification.Reason"/> so
/// policy decisions do not depend on free-form reason text.
/// </summary>
public enum AgentQuotaFailureKind
{
    None,
    HardQuota,
    SoftRateLimit,
}

/// <summary>
/// Outcome of <see cref="IAgentRunner.ClassifyFailure"/> — the kind of failure
/// plus an optional caller-friendly hint (reset window for quota, message for
/// network/auth) the pipeline can surface in audit events without having to
/// re-parse stderr itself.
/// </summary>
public sealed record AgentFailureClassification(
    AgentFailureKind Kind,
    DateTimeOffset? QuotaResetAt = null,
    string? Reason = null,
    AgentQuotaFailureKind QuotaFailure = AgentQuotaFailureKind.None);
