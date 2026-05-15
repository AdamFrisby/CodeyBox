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
    Normal,

    /// <summary>Mid-flight quota exhaustion — fall back to the next class member.</summary>
    QuotaExhausted,

    /// <summary>Transient network/connectivity failure that may benefit from a retry.</summary>
    TransientNetwork,

    /// <summary>Authentication or authorisation failure (revoked token, expired creds).</summary>
    AuthError,

    /// <summary>Failure shape the classifier didn't recognise.</summary>
    Unknown,
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
    string? Reason = null);
