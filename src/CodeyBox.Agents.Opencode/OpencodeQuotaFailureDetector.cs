using System.Text.RegularExpressions;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Opencode;

/// <summary>
/// Recognises quota / rate-limit / auth failures emitted by the opencode CLI.
///
/// <para>The patterns below are intentionally anchored with surrounding
/// context (HTTP status text, full phrases) rather than bare numeric or
/// single-word substrings. opencode prompts can contain repository content
/// under review, and an agent reviewing rate-limit / quota code (or simply
/// citing HTTP 402 in passing) must not be misclassified as quota-exhausted —
/// that would gate dispatch unnecessarily under
/// <c>QuotaUnknownPolicy=UseObservedFailures</c>. Mirrors the CodexQuotaFailureDetector
/// pattern of using full HTTP-status text (e.g. <c>API Error: 401</c>,
/// <c>429 Too Many Requests</c>). Expanding this list reactively, once a real
/// failure has been observed in production, follows the
/// <c>feedback-vendor-api-drift</c> rule.</para>
///
/// <para>OpenCode subscription rolling windows emit a multi-line stderr shape
/// documented upstream (<c>sst/opencode</c>,
/// <c>packages/opencode/test/session/retry.test.ts</c>): e.g.
/// <c>5 hour usage limit reached. It will reset in 5 hours 23 minutes.</c>
/// Weekly and monthly windows use parallel phrasing. These are matched via
/// <see cref="UsageLimitReachedRegexes"/> before the generic substring table.</para>
/// </summary>
public sealed class OpencodeQuotaFailureDetector : IAgentQuotaFailureDetector
{
    public AgentKind Kind => AgentKind.Opencode;

    /// <summary>
    /// OpenCode subscription rate-limit stderr shapes (rolling / weekly /
    /// monthly windows). Checked before generic substring patterns so reset
    /// windows in the same message are parsed via <see cref="OpencodeQuotaResetParser"/>.
    /// </summary>
    private static readonly Regex[] UsageLimitReachedRegexes =
    [
        new(@"(?:\d+)\s+hour usage limit reached\.\s*It will reset in\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"weekly usage limit reached\.\s*It will reset in\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"monthly usage limit reached\.\s*It will reset in\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        // Defensive fallback: require the opencode.ai workspace URL that accompanies
        // real subscription-limit stderr so code-review prose mentioning
        // "usage limit reached" does not gate dispatch.
        new(@"\busage limit reached\b[\s\S]*?opencode\.ai/workspace/", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    ];

    private static readonly (string Pattern, QuotaFailureKind Kind)[] Patterns =
    [
        // HTTP 402 = "Payment Required". opencode surfaces this when the
        // subscription has been billed up to its hard cap. Anchor with the
        // HTTP prefix or full status text so bare "402" mentions in code
        // under review (line numbers, enum values, error catalogues) don't
        // trip a false positive.
        ("HTTP 402", QuotaFailureKind.LimitReached),
        ("402 Payment Required", QuotaFailureKind.LimitReached),
        ("insufficient credits", QuotaFailureKind.LimitReached),
        ("limit reached", QuotaFailureKind.LimitReached),
        // "quota" alone matches reviewing-quota-code text; require a verb
        // that conveys exhaustion. Add more shapes reactively as real
        // opencode failures are observed.
        ("quota exceeded", QuotaFailureKind.LimitReached),
        ("quota exhausted", QuotaFailureKind.LimitReached),
        ("quota reached", QuotaFailureKind.LimitReached),
        ("monthly quota", QuotaFailureKind.LimitReached),
        // Anchor with the HTTP status so the bare word "Unauthorized" in
        // model output (e.g. discussing access-control code) doesn't trigger.
        ("401 Unauthorized", QuotaFailureKind.Unauthorized),
        ("API Error: 401", QuotaFailureKind.Unauthorized),
    ];

    public QuotaDetection? Detect(string? stderr, string? stdout)
    {
        if (string.IsNullOrEmpty(stderr) && string.IsNullOrEmpty(stdout))
            return null;

        if (TryDetectUsageLimitReached(stderr, stdout, out var usageLimitDetection))
            return usageLimitDetection;

        foreach (var (pattern, kind) in Patterns)
        {
            var inStderr = !string.IsNullOrEmpty(stderr) && ContainsPattern(stderr, pattern);
            var inStdout = !string.IsNullOrEmpty(stdout) && ContainsPattern(stdout, pattern);
            if (inStderr || inStdout)
            {
                var sources = CollectSources(stderr, stdout);
                return new QuotaDetection(kind, QuotaResetParser.TryParseResetAt(sources));
            }
        }

        return null;
    }

    private static bool TryDetectUsageLimitReached(
        string? stderr,
        string? stdout,
        out QuotaDetection? detection)
    {
        detection = null;
        foreach (var text in new[] { stderr, stdout })
        {
            if (string.IsNullOrEmpty(text)) continue;
            foreach (var regex in UsageLimitReachedRegexes)
            {
                if (!regex.IsMatch(text)) continue;
                var sources = CollectSources(stderr, stdout);
                detection = new QuotaDetection(
                    QuotaFailureKind.LimitReached,
                    OpencodeQuotaResetParser.TryParseResetAt(sources));
                return true;
            }
        }

        return false;
    }

    private static bool ContainsPattern(string text, string pattern)
    {
        // "usage limit reached" contains the substring "limit reached" but is
        // handled by UsageLimitReachedRegexes; skip the generic row to avoid
        // misclassifying code-review prose about subscription-limit handlers.
        if (pattern.Equals("limit reached", StringComparison.Ordinal)
            && text.Contains("usage limit reached", StringComparison.OrdinalIgnoreCase))
            return false;

        return text.Contains(pattern, StringComparison.OrdinalIgnoreCase);
    }

    private static List<string?> CollectSources(string? stderr, string? stdout)
    {
        var sources = new List<string?>(2);
        if (!string.IsNullOrEmpty(stderr)) sources.Add(stderr);
        if (!string.IsNullOrEmpty(stdout)) sources.Add(stdout);
        return sources;
    }
}
