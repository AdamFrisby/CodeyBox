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
/// </summary>
public sealed class OpencodeQuotaFailureDetector : IAgentQuotaFailureDetector
{
    public AgentKind Kind => AgentKind.Opencode;

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

        foreach (var (pattern, kind) in Patterns)
        {
            var inStderr = !string.IsNullOrEmpty(stderr) && stderr.Contains(pattern, StringComparison.OrdinalIgnoreCase);
            var inStdout = !string.IsNullOrEmpty(stdout) && stdout.Contains(pattern, StringComparison.OrdinalIgnoreCase);
            if (inStderr || inStdout)
            {
                var sources = new List<string?>(2);
                if (!string.IsNullOrEmpty(stderr)) sources.Add(stderr);
                if (!string.IsNullOrEmpty(stdout)) sources.Add(stdout);
                return new QuotaDetection(kind, QuotaResetParser.TryParseResetAt(sources));
            }
        }

        return null;
    }
}
