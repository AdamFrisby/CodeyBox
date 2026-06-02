using CodeyBox.Core;

namespace CodeyBox.Agents;

/// <summary>
/// Policy for deciding whether a captured CLI session may be resumed after a
/// non-zero agent-process exit.
///
/// <para>
/// Hard quota exhaustion (account caps, RESOURCE_EXHAUSTED, "usage limit
/// reached") and deterministic terminal API crashes (e.g. Claude 400
/// thinking-block modification) block resume — a same-session relaunch would
/// immediately re-fail. Soft rate-limit shapes (429 rate_limit_exceeded, 529
/// overloaded) are <em>transient</em> blips per the original task spec; resume
/// is the intended recovery path for those, paired with the bounded resume
/// budget in <see cref="SessionResumeOptions"/>.
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
                // Soft rate-limit / overload responses (HTTP 429, 529) are the
                // transient blips the resume path was designed to recover. Hard
                // quota exhaustion (LimitReached, Unauthorized) would re-fail
                // immediately, so block those.
                classification.Detection?.Kind == QuotaFailureKind.RateLimitExceeded,
            _ => false,
        };
    }
}
