using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Omp;

/// <summary>
/// Recognises quota / rate-limit / auth failures emitted by the omp CLI
/// (verified against omp 18.2.2 live runs).
///
/// <para>OMP relays the backing provider's error verbatim inside the JSON
/// event's <c>errorMessage</c> (verified: a paid model on a $0-spend-limit
/// OpenRouter key exits 1 with <c>403 Key limit exceeded (total limit)…</c>
/// on <c>message_end</c> / <c>turn_end</c> / the <c>agent_end</c>
/// <c>messages</c> array), and crashes with a plaintext
/// <c>No API key found for &lt;provider&gt;.</c> on stderr when it cannot
/// start at all (stdout then carries only the session header). Because the
/// two failure modes live on different streams, the detector scans BOTH —
/// same posture as the prime detector.</para>
///
/// <para>The pattern list is config-driven: built-in defaults ship in
/// <see cref="DefaultPatterns"/>, and operators can append additional patterns
/// via <c>CodeyBox:QuotaFailurePatterns:omp</c> without recompilation
/// (mirroring the prime detector). Patterns stay anchored to
/// provider-shaped phrases (HTTP status text, spend-limit sentences, full
/// error codes) rather than bare numbers or single words: omp prompts can
/// contain repository content under review, and model output citing "403"
/// or discussing quota code must not gate dispatch.</para>
/// </summary>
public sealed class OmpQuotaFailureDetector : IAgentQuotaFailureDetector
{
    public AgentKind Kind => AgentKind.Omp;

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
        // errorMessage).
        new("Key limit exceeded", QuotaFailureKind.LimitReached),
        new("permission for this model", QuotaFailureKind.LimitReached),
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
        // Pre-session plaintext failure (exit 1, no JSON error event —
        // verified live on stderr as `No API key found for anthropic.`).
        new("No API key found", QuotaFailureKind.Unauthorized),
        // Provider-shaped auth failures relayed verbatim in errorMessage.
        new("authentication_error", QuotaFailureKind.Unauthorized),
        new("API key is invalid", QuotaFailureKind.Unauthorized),
        new("invalid_api_key", QuotaFailureKind.Unauthorized),
        new("incorrect api key", QuotaFailureKind.Unauthorized),
        new("Authentication failed", QuotaFailureKind.Unauthorized),
        new("Missing Authentication header", QuotaFailureKind.Unauthorized),
        new("Access denied", QuotaFailureKind.Unauthorized),
        new("401 Unauthorized", QuotaFailureKind.Unauthorized),
        new("API Error: 401", QuotaFailureKind.Unauthorized),
    ];

    private readonly IReadOnlyList<QuotaFailurePattern> _patterns;

    /// <summary>
    /// Constructs a detector with the built-in <see cref="DefaultPatterns"/>.
    /// </summary>
    public OmpQuotaFailureDetector() : this(additionalPatterns: null) { }

    /// <summary>
    /// Constructs a detector whose pattern list is <see cref="DefaultPatterns"/>
    /// followed by <paramref name="additionalPatterns"/>. Operator-configured
    /// patterns are checked after defaults; null/empty input behaves
    /// identically to the parameterless constructor.
    /// </summary>
    public OmpQuotaFailureDetector(IEnumerable<QuotaFailurePattern>? additionalPatterns)
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
            if (!inStderr && !inStdout) continue;

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
