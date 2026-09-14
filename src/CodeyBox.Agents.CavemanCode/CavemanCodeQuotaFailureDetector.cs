using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.CavemanCode;

/// <summary>
/// Recognises auth / rate-limit failures emitted by the caveman-code CLI.
///
/// <para>Every pattern below is verified, not guessed:</para>
/// <list type="bullet">
///   <item>The two <c>Unauthorized</c> rows are the CLI's own missing-key
///   output, observed live against 0.65.2
///   (<c>No API key found for unknown.</c> followed by
///   <c>Use /login or set an API key environment variable.</c>).</item>
///   <item><c>overloaded_error</c> is the provider error code the CLI's own
///   retry classifier matches (shipped <c>dist/core/agent-session.js</c>,
///   0.65.2); anchored with the <c>_error</c> suffix so prose about an
///   "overloaded" server under review does not gate dispatch.</item>
///   <item>Numeric 429 rows come from
///   <see cref="SharedRateLimitPatterns.ProviderRateLimitPatterns"/> and
///   stay anchored with companion text for the same reason — a bare
///   <c>429</c> in code under review must not bench the agent.</item>
/// </list>
///
/// <para>Hard-quota shapes (provider spend caps) are deliberately absent:
/// BYOK keys have no single quota meter and no such stderr shape has been
/// observed. They get added reactively once a real failure is seen in
/// production, mirroring the opencode detector's stance. There is no
/// <c>IAgentQuotaProbe</c> for caveman-code for the same reason.</para>
/// </summary>
public sealed class CavemanCodeQuotaFailureDetector : IAgentQuotaFailureDetector
{
    public AgentKind Kind => AgentKind.CavemanCode;

    private static readonly QuotaFailurePattern[] Patterns =
    [
        // Transient provider refusals relayed verbatim by the CLI — owned by
        // SharedRateLimitPatterns so caveman-code stays in step with the
        // Copilot/opencode detectors. Checked first so a refusal carrying
        // both shapes parks on the rate-limit backoff.
        .. Agents.SharedRateLimitPatterns.ProviderRateLimitPatterns,
        // Provider-side retryable error code matched by the CLI's own retry
        // classifier (agent-session.js, 0.65.2). Suffix-anchored: bare
        // "overloaded" prose must not trip a false positive.
        new("overloaded_error", QuotaFailureKind.RateLimitExceeded),
        // Missing-key output observed live (0.65.2). Both lines are matched
        // so a truncated capture still classifies.
        new("No API key found", QuotaFailureKind.Unauthorized),
        new("set an API key environment variable", QuotaFailureKind.Unauthorized),
        // Anchor with the HTTP status so the bare word "Unauthorized" in
        // model output (e.g. discussing access-control code) doesn't trigger.
        new("401 Unauthorized", QuotaFailureKind.Unauthorized),
        new("API Error: 401", QuotaFailureKind.Unauthorized),
    ];

    public QuotaDetection? Detect(string? stderr, string? stdout)
    {
        if (string.IsNullOrEmpty(stderr) && string.IsNullOrEmpty(stdout))
            return null;

        foreach (var (pattern, kind) in Patterns)
        {
            var inStderr = !string.IsNullOrEmpty(stderr)
                && stderr.Contains(pattern, StringComparison.OrdinalIgnoreCase);
            var inStdout = !string.IsNullOrEmpty(stdout)
                && stdout.Contains(pattern, StringComparison.OrdinalIgnoreCase);
            if (inStderr || inStdout)
            {
                var sources = new List<string?>(2);
                if (!string.IsNullOrEmpty(stderr)) sources.Add(stderr);
                if (!string.IsNullOrEmpty(stdout)) sources.Add(stdout);
                return new QuotaDetection(
                    kind,
                    QuotaResetParser.TryParseResetAt(sources)
                        ?? QuotaResetParser.TryParseRetryAfterHeader(sources));
            }
        }

        return null;
    }
}
