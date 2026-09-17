using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Continue;

/// <summary>
/// Recognises quota / rate-limit / auth failures emitted by the Continue CLI
/// (verified against @continuedev/cli 1.5.47 live runs).
///
/// <para>Continue relays the backing provider's error inside the exit-0
/// <c>{"status":"error","message":"…"}</c> envelope on stdout (verified:
/// a $0-spend-limit key against a paid model yields <c>403 Key limit
/// exceeded (total limit)…</c>), so the detector scans BOTH streams. Invalid
/// or missing keys were not observed live (the runner fails fast on a blank
/// key before dispatch), so the auth rows below are the standard
/// provider-relay vocabulary, marked as such.</para>
///
/// <para>The pattern list is config-driven: built-in defaults ship in
/// <see cref="DefaultPatterns"/>, and operators can append additional
/// patterns via <c>CodeyBox:QuotaFailurePatterns:continue</c> without
/// recompilation (mirroring the kilo detector). Patterns stay anchored to
/// provider-shaped phrases rather than bare numbers or single words:
/// Continue prompts can contain repository content under review, and model
/// output citing "401"/"402" or discussing quota code must not gate
/// dispatch.</para>
/// </summary>
public sealed class ContinueQuotaFailureDetector : IAgentQuotaFailureDetector
{
    public AgentKind Kind => AgentKind.Continue;

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
        // Spend-limit / hard-cap exhaustion (verified live: $0-spend-limit
        // key against a paid model; the CLI relays the provider body
        // verbatim in the envelope message).
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
        // Provider-shaped auth failures (standard relay vocabulary —
        // invalid-key runs were not observed live because the runner fails
        // fast on a blank key before dispatch). "User not found" alone is
        // deliberately absent — it matches reviewed prose (user-management
        // code, docs) far more often than provider auth state; the anchored
        // rows below carry the signal.
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
    public ContinueQuotaFailureDetector() : this(additionalPatterns: null) { }

    /// <summary>
    /// Constructs a detector whose pattern list is <see cref="DefaultPatterns"/>
    /// followed by <paramref name="additionalPatterns"/>. Operator-configured
    /// patterns are checked after defaults; null/empty input behaves
    /// identically to the parameterless constructor.
    /// </summary>
    public ContinueQuotaFailureDetector(IEnumerable<QuotaFailurePattern>? additionalPatterns)
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
