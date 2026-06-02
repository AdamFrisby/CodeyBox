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
        IAgentQuotaFailureDetector? detector,
        string? stderr,
        string? stdout)
    {
        if (detector is null)
            return true;

        try
        {
            if (detector.IsTerminalNonQuotaCrash(stderr, stdout))
                return true;

            var scopedStdout = detector.ScopeStdoutForQuotaDetection(stdout);
            var detection = detector.Detect(stderr, scopedStdout);
            return detection is null || IsTransientRateLimitWithoutReset(detection);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsTransientRateLimitWithoutReset(QuotaDetection detection) =>
        detection.Kind == QuotaFailureKind.RateLimitExceeded
        && detection.ResetAt is null;
}
