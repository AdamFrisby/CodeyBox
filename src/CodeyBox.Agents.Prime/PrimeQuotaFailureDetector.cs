using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Prime;

/// <summary>
/// Recognises quota / rate-limit / auth failures emitted by the prime-agent
/// CLI.
///
/// <para>Prime relays the backing provider's error verbatim inside the JSON
/// event's <c>errorMessage</c> (verified against prime-agent 0.9.5: a bad
/// OpenRouter key yields
/// <c>401 User not found.\n\nRun /login to update credentials.</c>, and a
/// $0-limit key against a paid model yields
/// <c>403 Key limit exceeded (total limit)…\n\nRun /login to update
/// credentials.</c>), and emits a plaintext
/// <c>No API key found for the selected model.</c> on stderr when it cannot
/// start at all. Because prime exits 0 on all of these, the detector scans
/// BOTH streams — the terminal frames live on stdout, the pre-session
/// failure on stderr.</para>
///
/// <para>The pattern list is config-driven: built-in defaults ship in
/// <see cref="DefaultPatterns"/>, and operators can append additional
/// patterns via <c>CodeyBox:QuotaFailurePatterns:prime</c> without
/// recompilation (mirroring the cursor/pi detectors). Patterns stay anchored
/// to provider-shaped phrases (HTTP status text, full sentences, CLI
/// vocabulary) rather than bare numbers or single words: prime prompts can
/// contain repository content under review, and model output citing "403" or
/// discussing quota code must not gate dispatch.</para>
/// </summary>
public sealed class PrimeQuotaFailureDetector : IAgentQuotaFailureDetector
{
    public AgentKind Kind => AgentKind.Prime;

    /// <summary>
    /// Built-in pattern allowlist. Order matters: rate-limit rows come
    /// first, then billing/quota rows, then auth rows — so a refusal
    /// carrying both shapes (prime appends <c>Run /login to update
    /// credentials.</c> to EVERY provider error, including quota errors)
    /// parks on the more specific signal rather than terminating the item
    /// as an auth failure.
    /// </summary>
    public static readonly IReadOnlyList<QuotaFailurePattern> DefaultPatterns =
    [
        // Shared provider rate-limit rows (transient throughput refusals).
        .. SharedRateLimitPatterns.ProviderRateLimitPatterns,
        // Pre-session plaintext failure (exit 0, no JSON error event).
        new("No API key found", QuotaFailureKind.Unauthorized),
        // Billing / hard-cap exhaustion relayed verbatim. The OpenRouter
        // $0-limit refusal is the observed shape; the generic rows cover
        // other providers' equivalents.
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
        // Provider-shaped auth failures relayed verbatim in errorMessage.
        new("401 User not found", QuotaFailureKind.Unauthorized),
        new("authentication_error", QuotaFailureKind.Unauthorized),
        new("API key is invalid", QuotaFailureKind.Unauthorized),
        new("invalid_api_key", QuotaFailureKind.Unauthorized),
        new("incorrect api key", QuotaFailureKind.Unauthorized),
        new("Missing Authentication header", QuotaFailureKind.Unauthorized),
        new("401 Unauthorized", QuotaFailureKind.Unauthorized),
        new("API Error: 401", QuotaFailureKind.Unauthorized),
        // Prime CLI vocabulary appended to provider errors. Last of the
        // auth rows: it also trails quota errors, which the rows above
        // already claimed.
        new("Run /login to update credentials", QuotaFailureKind.Unauthorized),
    ];

    private readonly IReadOnlyList<QuotaFailurePattern> _patterns;

    /// <summary>
    /// Constructs a detector with the built-in <see cref="DefaultPatterns"/>.
    /// </summary>
    public PrimeQuotaFailureDetector() : this(additionalPatterns: null) { }

    /// <summary>
    /// Constructs a detector whose pattern list is <see cref="DefaultPatterns"/>
    /// followed by <paramref name="additionalPatterns"/>. Operator-configured
    /// patterns are checked after defaults; null/empty input behaves
    /// identically to the parameterless constructor.
    /// </summary>
    public PrimeQuotaFailureDetector(IEnumerable<QuotaFailurePattern>? additionalPatterns)
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
