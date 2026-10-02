namespace CodeyBox.Core;

/// <summary>
/// Pure retry policy for provider-side transient failures. Every member is a
/// total function of its inputs so the retry contract is unit-testable
/// without a store or scheduler:
/// <list type="bullet">
/// <item>parked items reuse the existing <c>transient</c> failure kind, so
/// they count toward the established bounded transient-retry budget
/// (backoff with jitter) instead of incrementing
/// <see cref="WorkItem.TerminalFailureCount"/>;</item>
/// <item>the agent and model id are always preserved — capacity retries in
/// particular must never switch models, because only the configured model
/// may be used;</item>
/// <item>truncation retries resume the same session with a bounded
/// <c>continue</c> nudge, never a fresh turn.</item>
/// </list>
/// </summary>
public static class ProviderTransientRetryPolicy
{
    /// <summary>
    /// Failure kind parked items carry: the pre-existing transient budget.
    /// Deliberately the established <c>transient</c> value rather than a new
    /// per-family kind, because the retry scheduler, history preservation,
    /// and pending predicates all key on that exact string.
    /// </summary>
    public const string ParkedFailureKind = "transient";

    /// <summary>Hard upper bound for truncation continue-nudges per turn.</summary>
    public const int MaxTruncationContinueNudges = 5;

    /// <summary>
    /// Builds the parked <c>LastError</c> line for a provider-transient
    /// detection. Carries only the detector-owned family and signature label
    /// — never raw agent output, which may contain secrets.
    /// </summary>
    public static string BuildParkedError(ProviderTransientDetection detection)
    {
        ArgumentNullException.ThrowIfNull(detection);
        var family = detection.Kind switch
        {
            ProviderTransientKind.ModelCapacity => "model-capacity",
            ProviderTransientKind.OutputTruncation => "output-truncation",
            _ => "infra-transport",
        };
        return $"provider-transient({family}; signature {detection.MatchedSignature}): {detection.Detail}";
    }

    /// <summary>
    /// Builds the bounded <c>continue</c> prompt sent in the same session
    /// after an output-truncation failure. Names the truncation explicitly so
    /// the model resumes mid-output instead of restarting the task.
    /// </summary>
    public static string BuildTruncationContinuePrompt(string? phase)
    {
        var where = string.IsNullOrWhiteSpace(phase) ? "turn" : $"{phase.Trim()} turn";
        return $"Your previous {where} was cut off because the model hit its maximum output token limit, so the work is incomplete. " +
            "Continue exactly where you left off and finish the remaining work. " +
            "Do not restart from the beginning and do not repeat work that is already complete.";
    }

    /// <summary>
    /// Clamps a configured truncation-nudge bound into [0,
    /// <see cref="MaxTruncationContinueNudges"/>]. Config is
    /// operator-supplied and hot-reloaded, so out-of-range values degrade to
    /// the nearest bound instead of throwing on the dispatch path.
    /// </summary>
    public static int ClampTruncationNudges(int configured)
        => Math.Clamp(configured, 0, MaxTruncationContinueNudges);
}
