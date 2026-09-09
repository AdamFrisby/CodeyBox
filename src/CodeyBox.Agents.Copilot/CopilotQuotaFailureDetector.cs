using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Copilot;

/// <summary>
/// Recognises quota / rate-limit failures emitted by the GitHub Copilot CLI,
/// in both subscription and BYOK modes.
///
/// <para>In BYOK mode inference goes to an operator-chosen OpenAI-compatible
/// endpoint (for example the opencode Go plan's Console endpoint), and the CLI
/// relays provider refusals verbatim. A short-term throughput limit surfaces
/// as e.g. <c>429 Error from provider (Console Go): Upstream request failed:
/// [rate_limit_exceeded] Rate limit exceeded. Please retry after a brief
/// wait.</c> — a transient rate condition that clears in minutes, not an
/// exhausted account. Without this detector that output falls through to a
/// generic agent failure and terminates the work item instead of parking it
/// for quota retry.</para>
///
/// <para>Numeric <c>429</c> patterns are anchored with companion text
/// (<c>HTTP 429</c>, <c>status 429</c>, <c>API Error: 429</c>,
/// <c>429 Too Many Requests</c>, <c>429 Error</c>) rather than matching a bare
/// <c>429</c>, so model output that merely cites the number (retry counts,
/// code under review) is not misclassified as a provider refusal. Quota nouns
/// likewise require a verb of exhaustion.</para>
/// </summary>
public sealed class CopilotQuotaFailureDetector : IAgentQuotaFailureDetector
{
    public AgentKind Kind => AgentKind.Copilot;

    private static readonly (string Pattern, QuotaFailureKind Kind)[] Patterns =
    [
        // Transient provider refusals: short-window throughput / concurrency
        // limits that clear on their own. Checked before the hard-quota rows
        // so a refusal carrying both shapes parks on the rate-limit backoff.
        ("rate_limit_exceeded", QuotaFailureKind.RateLimitExceeded),
        ("rate limit exceeded", QuotaFailureKind.RateLimitExceeded),
        ("429 Too Many Requests", QuotaFailureKind.RateLimitExceeded),
        ("429 Error", QuotaFailureKind.RateLimitExceeded),
        ("HTTP 429", QuotaFailureKind.RateLimitExceeded),
        ("status 429", QuotaFailureKind.RateLimitExceeded),
        ("API Error: 429", QuotaFailureKind.RateLimitExceeded),
        ("too many requests", QuotaFailureKind.RateLimitExceeded),
        // Hard account caps: subscription quota actually spent. The spaced
        // "usage limit" prose form is deliberately absent — "usage_limit"
        // (machine shape) plus "limit reached" (exhaustion verb) cover the
        // realistic CLI shapes without flagging model output that merely
        // discusses usage limits.
        ("quota exceeded", QuotaFailureKind.LimitReached),
        ("quota exhausted", QuotaFailureKind.LimitReached),
        ("usage_limit", QuotaFailureKind.LimitReached),
        ("limit reached", QuotaFailureKind.LimitReached),
        ("insufficient credits", QuotaFailureKind.LimitReached),
    ];

    public QuotaDetection? Detect(string? stderr, string? stdout)
    {
        if (string.IsNullOrEmpty(stderr) && string.IsNullOrEmpty(stdout))
            return null;

        foreach (var (pattern, kind) in Patterns)
        {
            if (Contains(stderr, pattern) || Contains(stdout, pattern))
            {
                var sources = CollectSources(stderr, stdout);
                return new QuotaDetection(
                    kind,
                    QuotaResetParser.TryParseResetAt(sources)
                        ?? QuotaResetParser.TryParseRetryAfterHeader(sources));
            }
        }

        return null;

        static bool Contains(string? text, string pattern) =>
            !string.IsNullOrEmpty(text)
            && text.Contains(pattern, StringComparison.OrdinalIgnoreCase);

        static List<string?> CollectSources(string? stderr, string? stdout)
        {
            var sources = new List<string?>(2);
            if (!string.IsNullOrEmpty(stderr)) sources.Add(stderr);
            if (!string.IsNullOrEmpty(stdout)) sources.Add(stdout);
            return sources;
        }
    }
}
