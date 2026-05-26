using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Opencode;

/// <summary>
/// Recognises quota / rate-limit / auth failures emitted by the opencode CLI.
///
/// <para>The patterns below are the conservative set the operator named in
/// the integration brief. They are intentionally substring-only — opencode
/// fronts multiple upstream providers (DeepSeek, Anthropic, OpenAI, …) and
/// each surfaces its own canonical error text; expanding this list reactively
/// (once a real failure has been observed in production) follows the
/// <c>feedback-vendor-api-drift</c> rule.</para>
/// </summary>
public sealed class OpencodeQuotaFailureDetector : IAgentQuotaFailureDetector
{
    public AgentKind Kind => AgentKind.Opencode;

    private static readonly (string Pattern, QuotaFailureKind Kind)[] Patterns =
    [
        // HTTP 402 = "Payment Required". opencode surfaces this when the
        // subscription has been billed up to its hard cap.
        ("402", QuotaFailureKind.LimitReached),
        ("insufficient credits", QuotaFailureKind.LimitReached),
        ("limit reached", QuotaFailureKind.LimitReached),
        ("quota", QuotaFailureKind.LimitReached),
        ("Unauthorized", QuotaFailureKind.Unauthorized),
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
