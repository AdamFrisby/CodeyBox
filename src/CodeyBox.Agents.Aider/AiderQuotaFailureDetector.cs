using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Aider;

/// <summary>
/// Recognises quota / rate-limit / auth failures emitted by the aider CLI.
///
/// <para>Aider relays the backing provider's error verbatim as a litellm
/// exception line on stdout (verified against aider 0.86.2: a bad OpenRouter key
/// yields <c>litellm.AuthenticationError: AuthenticationError:
/// OpenrouterException - {"error":{"message":"Missing Authentication
/// header","code":401}}</c> followed by <c>The API provider is not able to
/// authenticate you. Check your API key.</c>), and exits 0. Because the
/// one-shot output is plaintext on stdout (not stderr), the detector scans
/// BOTH streams.</para>
///
/// <para>The pattern list is config-driven: built-in defaults ship in
/// <see cref="DefaultPatterns"/>, and operators can append additional patterns
/// via <c>CodeyBox:QuotaFailurePatterns:aider</c> without recompilation
/// (mirroring the pi detector). Patterns stay anchored to provider-shaped
/// phrases (litellm exception names, HTTP status text, full sentences) rather
/// than bare numbers or single words: aider prompts contain repository content
/// under review, and model output citing "429" or discussing quota code must
/// not gate dispatch.</para>
/// </summary>
public sealed class AiderQuotaFailureDetector : IAgentQuotaFailureDetector
{
    public AgentKind Kind => AgentKind.Aider;

    /// <summary>
    /// Built-in pattern allowlist. Order matters: rate-limit rows come first
    /// so a refusal carrying both shapes parks on the rate-limit backoff
    /// rather than terminating the item as a hard quota failure. Billing-
    /// exhaustion rows come before the auth rows: litellm wraps some provider
    /// 402/insufficient-credit refusals in an AuthenticationError relay, and
    /// the exhaustion substance ("insufficient credits", 402) is more specific
    /// than the wrapper exception class.
    /// </summary>
    public static readonly IReadOnlyList<QuotaFailurePattern> DefaultPatterns =
    [
        // Shared provider rate-limit rows (transient throughput refusals).
        .. SharedRateLimitPatterns.ProviderRateLimitPatterns,
        // litellm rate-limit relay (verbatim provider error on stdout). The
        // "litellm." prefix anchors to the relay rather than to model output
        // discussing errors in code under review.
        new("litellm.RateLimitError", QuotaFailureKind.RateLimitExceeded),
        // Billing / hard-cap exhaustion relayed verbatim. Checked before the
        // auth rows (see the doc above): substance beats wrapper class.
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
        // litellm exception relays (verbatim provider errors on stdout).
        new("litellm.AuthenticationError", QuotaFailureKind.Unauthorized),
        new("litellm.NotFoundError", QuotaFailureKind.Unauthorized),
        // Human sentence aider prints after an auth relay.
        new("not able to authenticate you", QuotaFailureKind.Unauthorized),
        new("Check your API key", QuotaFailureKind.Unauthorized),
        // Provider-shaped auth failures relayed verbatim in the exception body.
        new("Missing Authentication header", QuotaFailureKind.Unauthorized),
        new("invalid_api_key", QuotaFailureKind.Unauthorized),
        new("incorrect api key", QuotaFailureKind.Unauthorized),
        new("authentication_error", QuotaFailureKind.Unauthorized),
        new("API key is invalid", QuotaFailureKind.Unauthorized),
        new("401 Unauthorized", QuotaFailureKind.Unauthorized),
        new("API Error: 401", QuotaFailureKind.Unauthorized),
    ];

    private readonly IReadOnlyList<QuotaFailurePattern> _patterns;

    /// <summary>
    /// Constructs a detector with the built-in <see cref="DefaultPatterns"/>.
    /// </summary>
    public AiderQuotaFailureDetector() : this(additionalPatterns: null) { }

    /// <summary>
    /// Constructs a detector whose pattern list is <see cref="DefaultPatterns"/>
    /// followed by <paramref name="additionalPatterns"/>. Operator-configured
    /// patterns are checked after defaults; null/empty input behaves
    /// identically to the parameterless constructor.
    /// </summary>
    public AiderQuotaFailureDetector(IEnumerable<QuotaFailurePattern>? additionalPatterns)
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
