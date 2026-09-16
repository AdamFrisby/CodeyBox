using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Kilo;

/// <summary>
/// Recognises quota / rate-limit / auth failures emitted by the kilo CLI
/// (verified against @kilocode/cli 7.7.2 live frames).
///
/// <para>Kilo wraps the backing provider's error in a
/// <c>{"type":"error",…,"error":{"name":…,"data":{"message":…}}}</c> stream
/// frame (also echoed to stderr as <c>Error: …</c>), so the detector scans
/// BOTH streams. Verified shapes:</para>
/// <list type="bullet">
/// <item><description>Missing API key (exit 1):
/// <c>No cookie auth credentials found</c> with <c>statusCode: 401</c> —
/// the OpenRouter origin when the request carries no usable key.</description></item>
/// <item><description>$0-spend-limit key against a paid model (exit 1,
/// observed on a sibling CLI against the same OpenRouter key):
/// <c>forbidden: Key limit exceeded (total limit)</c>.</description></item>
/// <item><description>Unseeded <c>models</c> map (exit 1):
/// <c>Model not found: openai-compatible/…</c> — a configuration error, NOT
/// a quota signal, deliberately unmatched.</description></item>
/// <item><description>Companion generic frame (exit 1):
/// <c>Unexpected server error. Check server logs for details.</c> — a
/// content-free wrapper, NOT a quota signal, deliberately unmatched.</description></item>
/// </list>
///
/// <para>The pattern list is config-driven: built-in defaults ship in
/// <see cref="DefaultPatterns"/>, and operators can append additional
/// patterns via <c>CodeyBox:QuotaFailurePatterns:kilo</c> without
/// recompilation (mirroring the autohand detector). Patterns stay anchored
/// to provider-shaped phrases rather than bare numbers or single words:
/// kilo prompts can contain repository content under review, and model
/// output citing "401"/"402" or discussing quota code must not gate
/// dispatch. <c>Model not found</c> and <c>Unexpected server error</c> are
/// intentionally absent — they are configuration/generic give-up shapes, not
/// evidence of quota/auth state.</para>
/// </summary>
public sealed class KiloQuotaFailureDetector : IAgentQuotaFailureDetector
{
    public AgentKind Kind => AgentKind.Kilo;

    /// <summary>
    /// Built-in pattern allowlist. Order matters: the spend-limit rows come
    /// before the access-denied row so a combined paid-model refusal (which
    /// can carry BOTH shapes) parks on the hard-cap backoff
    /// (<see cref="QuotaFailureKind.LimitReached"/>) rather than the auth
    /// recovery path.
    /// </summary>
    public static readonly IReadOnlyList<QuotaFailurePattern> DefaultPatterns =
    [
        // Shared provider rate-limit rows (transient throughput refusals).
        .. SharedRateLimitPatterns.ProviderRateLimitPatterns,
        // Spend-limit / hard-cap exhaustion (verified shape family on the
        // same OpenRouter key via a sibling CLI; kilo relays the provider
        // body verbatim in error.data.message).
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
        // Provider-shaped auth failures (verified: missing key against
        // OpenRouter). "User not found" alone is deliberately absent — it
        // matches reviewed prose (user-management code, docs) far more often
        // than provider auth state; the anchored rows below carry the signal.
        new("No cookie auth credentials found", QuotaFailureKind.Unauthorized),
        new("Authentication failed", QuotaFailureKind.Unauthorized),
        new("Missing Authentication header", QuotaFailureKind.Unauthorized),
        new("authentication_error", QuotaFailureKind.Unauthorized),
        new("API key is invalid", QuotaFailureKind.Unauthorized),
        new("invalid_api_key", QuotaFailureKind.Unauthorized),
        new("incorrect api key", QuotaFailureKind.Unauthorized),
        new("Access denied", QuotaFailureKind.Unauthorized),
        new("401 Unauthorized", QuotaFailureKind.Unauthorized),
        new("API Error: 401", QuotaFailureKind.Unauthorized),
    ];

    private readonly IReadOnlyList<QuotaFailurePattern> _patterns;

    /// <summary>
    /// Constructs a detector with the built-in <see cref="DefaultPatterns"/>.
    /// </summary>
    public KiloQuotaFailureDetector() : this(additionalPatterns: null) { }

    /// <summary>
    /// Constructs a detector whose pattern list is <see cref="DefaultPatterns"/>
    /// followed by <paramref name="additionalPatterns"/>. Operator-configured
    /// patterns are checked after defaults; null/empty input behaves
    /// identically to the parameterless constructor.
    /// </summary>
    public KiloQuotaFailureDetector(IEnumerable<QuotaFailurePattern>? additionalPatterns)
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
