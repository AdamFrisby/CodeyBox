using CodeyBox.Core;

namespace CodeyBox.Agents.Devin;

/// <summary>
/// Shared auth-signal suppression for Devin's provider-transient
/// detectors. A genuine credential failure in the same capture must keep
/// its auth classification instead of being laundered into transient
/// retries by a co-occurring transient signature, so every structured
/// transient detector consults this one check. Never throws.
/// </summary>
internal static class DevinAuthSignals
{
    /// <summary>
    /// True when any stream carries an authentication signal: shared CLI
    /// auth patterns, login-prompt shapes, or Devin's own unauthorized
    /// wording. Inputs are the same captured stderr/stdout/summary the
    /// detectors scan.
    /// </summary>
    internal static bool HasAuthSignal(string? stderr, string? stdout, string? summary)
    {
        try
        {
            if (!string.IsNullOrEmpty(stderr))
            {
                if (AgentFailureClassifier.ContainsAuthErrorPattern(stderr))
                    return true;
                if (AgentFailureClassifier.ContainsAuthRequiredPatternInStderr(stderr))
                    return true;
                if (IsDevinUnauthorized(stderr, null))
                    return true;
            }

            if (!string.IsNullOrEmpty(stdout))
            {
                if (AgentFailureClassifier.ContainsAuthErrorPattern(stdout))
                    return true;
                if (AgentFailureClassifier.ContainsAuthRequiredPatternInStdout(stdout))
                    return true;
            }

            if (!string.IsNullOrEmpty(summary) && AgentFailureClassifier.ContainsAuthErrorPattern(summary))
                return true;
        }
        catch (Exception)
        {
        }

        return false;
    }

    /// <summary>
    /// Devin-owned auth shapes (<c>Not logged in</c>, <c>Authentication
    /// required</c>, <c>401 Unauthorized</c> — the CLI's own status wording
    /// from <see cref="DevinQuotaFailureDetector"/>) take precedence over the
    /// transient signatures. Reuses the detector's quota/auth
    /// vocabulary as the single source of truth instead of re-listing the
    /// strings. Never throws.
    /// </summary>
    private static bool IsDevinUnauthorized(string? stderr, string? stdout)
    {
        try
        {
            return new DevinQuotaFailureDetector().Detect(stderr, stdout)?.Kind == QuotaFailureKind.Unauthorized;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
