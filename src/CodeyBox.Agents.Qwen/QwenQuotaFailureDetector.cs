using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Qwen;

/// <summary>
/// Recognises quota / rate-limit / auth failures emitted by the Qwen Code
/// CLI (verified against qwen 0.24.0 live runs).
///
/// <para>Qwen relays the backing provider's error verbatim twice: inside
/// the stdout JSON <c>result</c> frame's <c>error.message</c> (verified: a
/// bogus key exits 1 with <c>[API Error: 401 Missing Authentication
/// header]</c>; a paid model on a $0-spend-limit OpenRouter key exits 1
/// with <c>[API Error: 403 Key limit exceeded (total limit)…]</c>) and as
/// the stderr <c>AlreadyReportedError</c> envelope's message. Because the
/// failure modes live on both streams, the detector scans BOTH — same
/// posture as the omp detector.</para>
///
/// <para>The pattern list is config-driven: built-in defaults ship in
/// <see cref="DefaultPatterns"/>, and operators can append additional
/// patterns via <c>CodeyBox:QuotaFailurePatterns:qwen</c> without
/// recompilation (mirroring the omp hook). Patterns stay anchored to
/// provider-shaped phrases (HTTP status text, spend-limit sentences, full
/// error codes) rather than bare numbers or single words: qwen prompts can
/// contain repository content under review, and model output citing "403"
/// or discussing quota code must not gate dispatch. Expanding this list
/// reactively, once a real failure has been observed in production, follows
/// the <c>feedback-vendor-api-drift</c> rule.</para>
/// </summary>
public sealed class QwenQuotaFailureDetector : IAgentQuotaFailureDetector
{
    public AgentKind Kind => AgentKind.Qwen;

    /// <summary>
    /// Built-in pattern allowlist. Order matters: rate-limit rows come first
    /// so a refusal carrying both shapes parks on the rate-limit backoff
    /// rather than terminating the item as a hard quota failure, and the
    /// spend-limit rows precede the access-denied rows so a combined
    /// paid-model refusal parks on the hard-cap backoff rather than the auth
    /// recovery path.
    /// </summary>
    public static readonly IReadOnlyList<QuotaFailurePattern> DefaultPatterns =
    [
        // Shared provider rate-limit rows (transient throughput refusals).
        .. SharedRateLimitPatterns.ProviderRateLimitPatterns,
        // Spend-limit / hard-cap exhaustion (verified live: paid model on a
        // $0-spend-limit OpenRouter key relays the provider body verbatim in
        // error.message on both streams).
        new("Key limit exceeded", QuotaFailureKind.LimitReached),
        new("insufficient_quota", QuotaFailureKind.LimitReached),
        new("insufficient credits", QuotaFailureKind.LimitReached),
        new("billing_hard_limit_reached", QuotaFailureKind.LimitReached),
        new("HTTP 402", QuotaFailureKind.LimitReached),
        new("402 Payment Required", QuotaFailureKind.LimitReached),
        // "quota" alone matches reviewing-quota-code text; require a verb
        // that conveys exhaustion.
        new("quota exceeded", QuotaFailureKind.LimitReached),
        new("quota exhausted", QuotaFailureKind.LimitReached),
        new("usage limit reached", QuotaFailureKind.LimitReached),
        // Provider-shaped auth failures relayed verbatim in error.message
        // (verified live: the bogus-key run reports "Missing
        // Authentication header" inside an API Error: 401 frame).
        new("Missing Authentication header", QuotaFailureKind.Unauthorized),
        new("authentication_error", QuotaFailureKind.Unauthorized),
        new("API key is invalid", QuotaFailureKind.Unauthorized),
        new("invalid_api_key", QuotaFailureKind.Unauthorized),
        new("incorrect api key", QuotaFailureKind.Unauthorized),
        new("Authentication failed", QuotaFailureKind.Unauthorized),
        new("Access denied", QuotaFailureKind.Unauthorized),
        new("401 Unauthorized", QuotaFailureKind.Unauthorized),
        new("API Error: 401", QuotaFailureKind.Unauthorized),
    ];

    private readonly IReadOnlyList<QuotaFailurePattern> _patterns;

    /// <summary>
    /// Constructs a detector with the built-in <see cref="DefaultPatterns"/>.
    /// </summary>
    public QwenQuotaFailureDetector() : this(additionalPatterns: null) { }

    /// <summary>
    /// Constructs a detector with operator-supplied patterns appended to the
    /// built-in <see cref="DefaultPatterns"/> (see
    /// <c>CodeyBox:QuotaFailurePatterns:qwen</c>).
    /// </summary>
    public QwenQuotaFailureDetector(IReadOnlyList<QuotaFailurePattern>? additionalPatterns)
    {
        _patterns = additionalPatterns is { Count: > 0 }
            ? [.. DefaultPatterns, .. additionalPatterns]
            : DefaultPatterns;
    }

    public QuotaDetection? Detect(string? stderr, string? stdout)
    {
        if (string.IsNullOrEmpty(stderr) && string.IsNullOrEmpty(stdout))
            return null;

        try
        {
            foreach (var (pattern, kind) in _patterns)
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
        catch (Exception)
        {
            // Contract: implementations must never throw.
            return null;
        }
    }
}
