using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Crush;

/// <summary>
/// Recognises quota / rate-limit / auth failures emitted by the Crush CLI
/// (verified against @charmland/crush 0.95.0 live runs).
///
/// <para>Crush renders terminal failures as styled <c>ERROR</c> blocks on
/// stderr with empty stdout and exit 1 (verified: no key yields
/// <c>No providers configured - please run 'crush' to set up a provider
/// interactively.</c>; an unknown <c>-m</c> id yields
/// <c>Failed to override models: large model "…" not found.</c>; a paid
/// model on a $0-spend-limit OpenRouter key yields
/// <c>Agent processing failed: failed to start agent processing stream:
/// forbidden: Key limit exceeded (total limit)…</c>). Because the text is
/// human rendering rather than a machine envelope, the detector scans BOTH
/// streams — same posture as the continue detector.</para>
///
/// <para>Deliberately unmatched: <c>Failed to override models: … not
/// found</c> (an unknown model id is a configuration error, not quota —
/// mirroring kilo's <c>Model not found</c> exclusion); <c>Unknown flag</c>
/// (a dispatch-construction failure — the runner never emits flags outside
/// its pinned set, so it cannot fire from dispatch); and the
/// small-model title-generation warning (a non-fatal advisory — the run
/// still exits 0 with the reply intact).</para>
///
/// <para>The pattern list is config-driven: built-in defaults ship in
/// <see cref="DefaultPatterns"/>, and operators can append additional
/// patterns via <c>CodeyBox:QuotaFailurePatterns:crush</c> without
/// recompilation (mirroring the continue detector). Patterns stay anchored
/// to provider-shaped phrases (spend-limit sentences, the provider-missing
/// sentence, full error codes) rather than bare numbers or single words:
/// Crush prompts can contain repository content under review, and model
/// output citing "401" / "402" / "403" or discussing quota code must not
/// gate dispatch.</para>
/// </summary>
public sealed class CrushQuotaFailureDetector : IAgentQuotaFailureDetector
{
    public AgentKind Kind => AgentKind.Crush;

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
        // $0-spend-limit key exits 1 relaying the provider body verbatim in
        // the ERROR block).
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
        // Provider-missing / auth failures (verified live: the bare-machine
        // shape; the remaining rows are the standard provider-relay
        // vocabulary for invalid-key runs, which fail at request time).
        new("No providers configured", QuotaFailureKind.Unauthorized),
        new("API key is invalid", QuotaFailureKind.Unauthorized),
        new("authentication_error", QuotaFailureKind.Unauthorized),
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
    public CrushQuotaFailureDetector() : this(additionalPatterns: null) { }

    /// <summary>
    /// Constructs a detector whose pattern list is <see cref="DefaultPatterns"/>
    /// followed by <paramref name="additionalPatterns"/>. Operator-configured
    /// patterns are checked after defaults; null/empty input behaves
    /// identically to the parameterless constructor.
    /// </summary>
    public CrushQuotaFailureDetector(IEnumerable<QuotaFailurePattern>? additionalPatterns)
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
