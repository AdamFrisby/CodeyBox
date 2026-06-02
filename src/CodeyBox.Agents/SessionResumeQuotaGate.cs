using CodeyBox.Core;

namespace CodeyBox.Agents;

/// <summary>
/// Policy for deciding whether a captured CLI session may be resumed after a
/// non-zero agent-process exit.
///
/// <para>
/// Hard quota exhaustion (account caps, RESOURCE_EXHAUSTED, "usage limit
/// reached"), provider rate limits with a parsed reset/retry window, and
/// deterministic terminal API crashes (e.g. Claude 400 thinking-block
/// modification) block resume because a same-session relaunch would
/// immediately re-fail. Rate-limit/overload shapes with no reset window are
/// treated as transient blips and may resume within the bounded budget in
/// <see cref="SessionResumeOptions"/>.
/// </para>
/// </summary>
internal static class SessionResumeQuotaGate
{
    public static bool AllowsResume(
        IQuotaFailureClassifier? classifier,
        AgentKind agent,
        string? stderr,
        string? stdout)
    {
        if (classifier is null)
            return true;

        var classification = classifier.Classify(agent, stderr, stdout);
        return classification.Kind switch
        {
            QuotaFailureClassificationKind.None => true,
            QuotaFailureClassificationKind.TerminalNonQuota => false,
            QuotaFailureClassificationKind.Quota =>
                // Rate-limit / overload responses (HTTP 429, 529) are resumable
                // only when the provider did not give a retry/reset window. A
                // parsed window means the normal quota defer/fallback path should
                // handle the failure instead of relaunching the same session.
                classification.Detection is { Kind: QuotaFailureKind.RateLimitExceeded, ResetAt: null },
            _ => false,
        };
    }
}
