using CodeyBox.Core;

namespace CodeyBox.Agents;

/// <summary>
/// Policy for deciding whether a captured CLI session may be resumed after a
/// non-zero agent-process exit. The default is to resume generic crashes; only
/// provider-detected quota/rate failures that would immediately re-fail block
/// the in-place resume path.
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
            QuotaFailureClassificationKind.Quota => IsTransientRateLimitWithoutReset(classification.Detection!),
            _ => false,
        };
    }

    private static bool IsTransientRateLimitWithoutReset(QuotaDetection detection) =>
        detection.Kind == QuotaFailureKind.RateLimitExceeded
        && detection.ResetAt is null;
}
