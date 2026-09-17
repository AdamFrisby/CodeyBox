using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Cmd;

/// <summary>
/// Recognises quota / rate-limit / auth failures emitted by the cmd CLI
/// (verified against command-code 1.54.2 live runs).
///
/// <para>Cmd relays the backing provider's error verbatim in the terminal
/// <c>type: "result"</c> line's <c>error</c> field (verified: an unknown
/// model id exits 1 with <c>Error: 400 …</c>; a paid model on a
/// $0-spend-limit OpenRouter key exits 4 with <c>Error: 403 Key limit
/// exceeded (total limit)…</c>), and the same body rides the in-stream
/// <c>run_error</c> event (<c>error: {name, message}</c>). Config-shape
/// failures that never reach the harness (missing auth placeholder → exit
/// 3 <c>Error: No auth credentials found. Run cmd login…</c>; missing
/// <c>OPENROUTER_API_KEY</c> env → exit 1 <c>Error: API key environment
/// variable OPENROUTER_API_KEY is not set</c>; spend-cap refusal → exit 4
/// <c>Error: API key spend cap reached</c>) land on stderr with empty
/// stdout. Because failure modes live on different streams, the detector
/// scans BOTH — same posture as the omp detector.</para>
///
/// <para>Deliberately unmatched: the <c>"&lt;model&gt;" isn't declared under
/// provider 'openrouter'</c> advisory (exit 0, sent anyway — not a
/// failure); <c>Model not found</c> without a provider shape (a
/// configuration error, not quota — mirroring kilo); the
/// <c>tool_hook_blocked</c> permission gate (a dispatch-configuration
/// failure surfaced through <see cref="CmdTerminalDiagnoser"/>, not quota);
/// and the <c>--effort</c> refusal (<c>has no adjustable reasoning
/// effort</c> — the runner never emits <c>--effort</c>, so it cannot fire
/// from dispatch).</para>
///
/// <para>The pattern list is config-driven: built-in defaults ship in
/// <see cref="DefaultPatterns"/>, and operators can append additional
/// patterns via <c>CodeyBox:QuotaFailurePatterns:cmd</c> without
/// recompilation (mirroring the omp detector). Patterns stay anchored to
/// provider-shaped phrases (HTTP status text, spend-limit sentences, full
/// error codes) rather than bare numbers or single words: cmd prompts can
/// contain repository content under review, and model output citing "401" /
/// "402" / "403" or discussing quota code must not gate dispatch.</para>
/// </summary>
public sealed class CmdQuotaFailureDetector : IAgentQuotaFailureDetector
{
    public AgentKind Kind => AgentKind.Cmd;

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
        // $0-spend-limit key exits 4 relaying the provider body verbatim in
        // result.error; the spend-cap refusal is the CLI's own cap check).
        new("Key limit exceeded", QuotaFailureKind.LimitReached),
        new("API key spend cap reached", QuotaFailureKind.LimitReached),
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
        // Provider-shaped auth failures relayed verbatim in result.error /
        // run_error.message / stderr Error: lines.
        new("No auth credentials found", QuotaFailureKind.Unauthorized),
        new("API key environment variable", QuotaFailureKind.Unauthorized),
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
    public CmdQuotaFailureDetector() : this(additionalPatterns: null) { }

    /// <summary>
    /// Constructs a detector whose pattern list is <see cref="DefaultPatterns"/>
    /// followed by <paramref name="additionalPatterns"/>. Operator-configured
    /// patterns are checked after defaults; null/empty input behaves
    /// identically to the parameterless constructor.
    /// </summary>
    public CmdQuotaFailureDetector(IEnumerable<QuotaFailurePattern>? additionalPatterns)
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
