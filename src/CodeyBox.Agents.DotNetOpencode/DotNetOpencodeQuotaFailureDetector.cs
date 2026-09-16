using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.DotNetOpencode;

/// <summary>
/// Recognises quota / rate-limit / auth failures emitted by the
/// dotnet-opencode CLI.
///
/// <para>Failures surface as <c>{"type":"error",…}</c> frames on stdout
/// (verified live: <c>provider.auth</c> /
/// <c>"Provider request failed with HTTP 401."</c> with numeric
/// <c>status</c>; <c>provider.invalid-request</c> /
/// <c>"No available model is present in the configured catalog."</c>), so the
/// detector scans BOTH streams — the terminal frames live on stdout, not
/// stderr. Because the CLI is a provider-agnostic BYOK front with no
/// subscription meter, only provider HTTP shapes are recognised: there are
/// deliberately NO rolling-window rows (the sst/opencode Go
/// "N hour usage limit reached" vocabulary was never observed here and
/// shipping it would fabricate a quota meter this CLI does not expose).</para>
///
/// <para>The pattern list is config-driven: built-in defaults ship in
/// <see cref="DefaultPatterns"/>, and operators can append additional patterns
/// via <c>CodeyBox:QuotaFailurePatterns:dotnet-opencode</c> without
/// recompilation (mirroring the pi/cursor detectors). Patterns stay anchored
/// to provider-shaped phrases (HTTP status text, <c>provider.*</c> error
/// types, <c>*_error</c> codes, full sentences) rather than bare numbers or
/// single words: prompts can contain repository content under review, and
/// model output citing "429" or discussing quota code must not gate
/// dispatch.</para>
/// </summary>
public sealed class DotNetOpencodeQuotaFailureDetector : IAgentQuotaFailureDetector
{
    public AgentKind Kind => AgentKind.DotNetOpencode;

    /// <summary>
    /// Built-in pattern allowlist. Order matters: rate-limit rows come first
    /// so a refusal carrying both shapes parks on the rate-limit backoff
    /// rather than terminating the item as a hard quota failure.
    /// </summary>
    public static readonly IReadOnlyList<QuotaFailurePattern> DefaultPatterns =
    [
        // Shared provider rate-limit rows (transient throughput refusals).
        .. SharedRateLimitPatterns.ProviderRateLimitPatterns,
        // CLI-native error types from the run-output envelope (verified live
        // against the bogus-key 401: error.type "provider.auth"). These are
        // CLI vocabulary, not words model output plausibly emits about code
        // under review.
        new("provider.auth", QuotaFailureKind.Unauthorized),
        new("provider.invalid-request", QuotaFailureKind.Unauthorized),
        // Provider-shaped auth failures relayed in error.message.
        new("has no usable credential", QuotaFailureKind.Unauthorized),
        new("No available model is present", QuotaFailureKind.Unauthorized),
        new("authentication_error", QuotaFailureKind.Unauthorized),
        new("API key is invalid", QuotaFailureKind.Unauthorized),
        new("invalid_api_key", QuotaFailureKind.Unauthorized),
        new("incorrect api key", QuotaFailureKind.Unauthorized),
        new("401 Unauthorized", QuotaFailureKind.Unauthorized),
        new("API Error: 401", QuotaFailureKind.Unauthorized),
        new("HTTP 401", QuotaFailureKind.Unauthorized),
        // Billing / hard-cap exhaustion relayed verbatim.
        new("insufficient_quota", QuotaFailureKind.LimitReached),
        new("insufficient credits", QuotaFailureKind.LimitReached),
        new("billing_hard_limit_reached", QuotaFailureKind.LimitReached),
        new("HTTP 402", QuotaFailureKind.LimitReached),
        new("402 Payment Required", QuotaFailureKind.LimitReached),
        // "quota" alone matches reviewing-quota-code text; require a verb
        // that conveys exhaustion.
        new("quota exceeded", QuotaFailureKind.LimitReached),
        new("quota exhausted", QuotaFailureKind.LimitReached),
    ];

    private readonly IReadOnlyList<QuotaFailurePattern> _patterns;

    /// <summary>
    /// Constructs a detector with the built-in <see cref="DefaultPatterns"/>.
    /// </summary>
    public DotNetOpencodeQuotaFailureDetector() : this(additionalPatterns: null) { }

    /// <summary>
    /// Constructs a detector whose pattern list is <see cref="DefaultPatterns"/>
    /// followed by <paramref name="additionalPatterns"/>. Operator-configured
    /// patterns are checked after defaults; null/empty input behaves
    /// identically to the parameterless constructor.
    /// </summary>
    public DotNetOpencodeQuotaFailureDetector(IEnumerable<QuotaFailurePattern>? additionalPatterns)
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
