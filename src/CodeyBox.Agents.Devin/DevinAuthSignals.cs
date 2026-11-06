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
    /// Devin-owned auth shapes — the CLI's own status wording verified in
    /// the devin 3000.11.1 binary. This list is the single source of truth:
    /// <see cref="DevinQuotaFailureDetector.DefaultPatterns"/> composes it
    /// verbatim, and <see cref="HasAuthSignal"/> scans it (plus any
    /// operator-configured <see cref="QuotaFailureKind.Unauthorized"/>
    /// patterns the detector was built with) so the suppression consults
    /// exactly the vocabulary the quota classifier honours — never a
    /// narrower default-only subset.
    /// </summary>
    internal static readonly IReadOnlyList<QuotaFailurePattern> UnauthorizedPatterns =
    [
        new("Not logged in", QuotaFailureKind.Unauthorized),
        new("Devin needs authentication", QuotaFailureKind.Unauthorized),
        new("Authentication required", QuotaFailureKind.Unauthorized),
        new("run `devin auth login`", QuotaFailureKind.Unauthorized),
        new("401 Unauthorized", QuotaFailureKind.Unauthorized),
    ];

    /// <summary>
    /// True when any stream carries an authentication signal: shared CLI
    /// auth patterns, login-prompt shapes, or Devin's own unauthorized
    /// wording. Inputs are the same captured stderr/stdout/summary the
    /// detectors scan. <paramref name="unauthorizedPatterns"/> is the
    /// detector's effective Unauthorized-kind pattern set (defaults plus
    /// operator extras); null falls back to <see cref="UnauthorizedPatterns"/>.
    /// </summary>
    internal static bool HasAuthSignal(
        string? stderr,
        string? stdout,
        string? summary,
        IReadOnlyList<QuotaFailurePattern>? unauthorizedPatterns = null)
    {
        try
        {
            if (!string.IsNullOrEmpty(stderr))
            {
                if (AgentFailureClassifier.ContainsAuthErrorPattern(stderr))
                    return true;
                if (AgentFailureClassifier.ContainsAuthRequiredPatternInStderr(stderr))
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

            // Devin-owned auth shapes take precedence over the transient
            // signatures on BOTH provider streams: on ACP runs the CLI's
            // stderr reaches the host folded inside codeybox.stderr
            // envelopes on stdout, so an auth line can legitimately appear
            // only there. Scanning stdout can only suppress a transient
            // match (fail-closed: fewer retries), never invent one.
            return IsDevinUnauthorized(
                stderr, stdout, unauthorizedPatterns ?? UnauthorizedPatterns);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Scans stderr and stdout for the detector's effective
    /// <see cref="QuotaFailureKind.Unauthorized"/> pattern set. Never
    /// throws.
    /// </summary>
    private static bool IsDevinUnauthorized(
        string? stderr,
        string? stdout,
        IReadOnlyList<QuotaFailurePattern> unauthorizedPatterns)
    {
        try
        {
            foreach (var pattern in unauthorizedPatterns)
            {
                if (pattern.Kind != QuotaFailureKind.Unauthorized)
                    continue;
                if (!string.IsNullOrEmpty(stderr)
                    && stderr.Contains(pattern.Pattern, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                if (!string.IsNullOrEmpty(stdout)
                    && stdout.Contains(pattern.Pattern, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (Exception)
        {
        }

        return false;
    }
}
