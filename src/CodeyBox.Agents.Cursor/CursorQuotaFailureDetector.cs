using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Cursor;

/// <summary>
/// Recognises quota / rate-limit / auth failures emitted by the Cursor CLI.
///
/// <para>The patterns below are a conservative starting allowlist drawn from
/// generic HTTP / billing language and Cursor's documented error vocabulary;
/// new shapes will be added as they are observed in production. Per the
/// operator's stated preference (<c>feedback-vendor-api-drift</c>), this
/// detector is intentionally reactive — we extend it when real failures
/// appear in the audit log, not pre-emptively from speculative vendor
/// documentation.</para>
/// </summary>
public sealed class CursorQuotaFailureDetector : IAgentQuotaFailureDetector
{
    public AgentKind Kind => AgentKind.Cursor;

    // Order matters: more-specific patterns win over more-generic ones. "rate
    // limit reached" must classify as RateLimitExceeded, not LimitReached, so
    // the rate-limit patterns are checked first.
    private static readonly (string Pattern, QuotaFailureKind Kind)[] Patterns =
    [
        ("rate_limit_exceeded", QuotaFailureKind.RateLimitExceeded),
        ("rate limit", QuotaFailureKind.RateLimitExceeded),
        ("429 Too Many Requests", QuotaFailureKind.RateLimitExceeded),
        ("401 Unauthorized", QuotaFailureKind.Unauthorized),
        ("limit reached", QuotaFailureKind.LimitReached),
        ("usage limit", QuotaFailureKind.LimitReached),
        ("quota exceeded", QuotaFailureKind.LimitReached),
        ("402 Payment Required", QuotaFailureKind.LimitReached),
    ];

    public QuotaDetection? Detect(string? stderr, string? stdout)
    {
        if (string.IsNullOrEmpty(stderr) && string.IsNullOrEmpty(stdout))
            return null;

        foreach (var (pattern, kind) in Patterns)
        {
            var inStderr = !string.IsNullOrEmpty(stderr) && stderr.Contains(pattern, StringComparison.OrdinalIgnoreCase);
            var inStdout = !string.IsNullOrEmpty(stdout) && stdout.Contains(pattern, StringComparison.OrdinalIgnoreCase);
            if (!inStderr && !inStdout) continue;

            var resetSources = new List<string?>(2);
            if (!string.IsNullOrEmpty(stderr)) resetSources.Add(stderr);
            if (!string.IsNullOrEmpty(stdout)) resetSources.Add(stdout);
            return new QuotaDetection(kind, QuotaResetParser.TryParseResetAt(resetSources));
        }

        return null;
    }
}
