using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Unreal;

/// <summary>
/// Recognises quota, rate-limit, and auth failures emitted by the Unreal CLI runner.
/// Scans both stdout (which carries structured error frames) and stderr.
/// </summary>
public sealed class UnrealQuotaFailureDetector : IAgentQuotaFailureDetector
{
    public AgentKind Kind => AgentKind.Unreal;

    public static readonly IReadOnlyList<QuotaFailurePattern> DefaultPatterns =
    [
        // Shared provider rate-limit patterns (transient throughput refusals)
        .. SharedRateLimitPatterns.ProviderRateLimitPatterns,

        // Spend-limit / quota exhaustion
        new("Key limit exceeded", QuotaFailureKind.LimitReached),
        new("insufficient_quota", QuotaFailureKind.LimitReached),
        new("insufficient credits", QuotaFailureKind.LimitReached),
        new("credit balance too low", QuotaFailureKind.LimitReached),
        new("quota exceeded", QuotaFailureKind.LimitReached),
        new("quota exhausted", QuotaFailureKind.LimitReached),
        new("usage limit reached", QuotaFailureKind.LimitReached),
        new("RESOURCE_EXHAUSTED", QuotaFailureKind.LimitReached),
        new("402 Payment Required", QuotaFailureKind.LimitReached),
        new("HTTP 402", QuotaFailureKind.LimitReached),

        // Auth / credential failures
        new("must be set", QuotaFailureKind.Unauthorized),
        new("UNREAL_HARNESS_LLM_API_KEY", QuotaFailureKind.Unauthorized),
        new("unsupported provider", QuotaFailureKind.Unauthorized),
        new("API key is invalid", QuotaFailureKind.Unauthorized),
        new("invalid api key", QuotaFailureKind.Unauthorized),
        new("invalid_api_key", QuotaFailureKind.Unauthorized),
        new("incorrect api key", QuotaFailureKind.Unauthorized),
        new("authentication_error", QuotaFailureKind.Unauthorized),
        new("Authentication failed", QuotaFailureKind.Unauthorized),
        new("401 Unauthorized", QuotaFailureKind.Unauthorized),
        new("API Error: 401", QuotaFailureKind.Unauthorized),
    ];

    private readonly IReadOnlyList<QuotaFailurePattern> _patterns;

    public UnrealQuotaFailureDetector() : this(additionalPatterns: null) { }

    public UnrealQuotaFailureDetector(IEnumerable<QuotaFailurePattern>? additionalPatterns)
    {
        if (additionalPatterns is null)
        {
            _patterns = DefaultPatterns;
            return;
        }

        var extras = additionalPatterns.Where(p => !string.IsNullOrEmpty(p.Pattern)).ToArray();
        if (extras.Length == 0)
        {
            _patterns = DefaultPatterns;
            return;
        }

        var combined = new List<QuotaFailurePattern>(DefaultPatterns.Count + extras.Length);
        combined.AddRange(DefaultPatterns);
        combined.AddRange(extras);
        _patterns = combined;
    }

    public QuotaDetection? Detect(string? stderr, string? stdout)
    {
        if (string.IsNullOrEmpty(stderr) && string.IsNullOrEmpty(stdout))
            return null;

        foreach (var entry in _patterns)
        {
            var inStderr = !string.IsNullOrEmpty(stderr) && stderr.Contains(entry.Pattern, StringComparison.OrdinalIgnoreCase);
            var inStdout = !string.IsNullOrEmpty(stdout) && stdout.Contains(entry.Pattern, StringComparison.OrdinalIgnoreCase);
            if (!inStderr && !inStdout)
                continue;

            var resetSources = new List<string?>(2);
            if (!string.IsNullOrEmpty(stderr)) resetSources.Add(stderr);
            if (!string.IsNullOrEmpty(stdout)) resetSources.Add(stdout);
            return new QuotaDetection(
                entry.Kind,
                QuotaResetParser.TryParseResetAt(resetSources)
                    ?? QuotaResetParser.TryParseRetryAfterHeader(resetSources));
        }

        return null;
    }
}
