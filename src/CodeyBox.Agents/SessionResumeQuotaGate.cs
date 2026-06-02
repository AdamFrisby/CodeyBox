using CodeyBox.Core;

namespace CodeyBox.Agents;

/// <summary>
/// Policy for deciding whether a captured CLI session may be resumed after a
/// non-zero agent-process exit. The default is to resume generic crashes; only
/// provider-detected quota/rate failures and deterministic terminal API crashes
/// block the in-place resume path.
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
            QuotaFailureClassificationKind.Quota => false,
            _ => false,
        };
    }
}
