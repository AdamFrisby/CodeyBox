using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Devin;

/// <summary>
/// Recognises quota / rate-limit / auth failures emitted by the devin CLI.
///
/// <para>Devin reports run failures on stderr as <c>Error: …</c> lines and
/// exits nonzero; its account-state strings are taken from the devin
/// 3000.11.1 binary (<c>Quota exhausted</c>, <c>Usage limit reached</c>,
/// <c>Usage paused</c>, <c>Authentication required</c>, <c>Devin needs
/// authentication</c>, <c>Not logged in</c>, <c>Purchase on-demand usage or
/// turn on auto-reload, or wait for your quota to reset.</c>). Because the
/// failure text lives on stderr — print-mode stdout is plain assistant text —
/// the detector scans BOTH streams.</para>
///
/// <para>The pattern list is config-driven: built-in defaults ship in
/// <see cref="DefaultPatterns"/>, and operators can append additional patterns
/// via <c>CodeyBox:QuotaFailurePatterns:devin</c> without recompilation
/// (mirroring the goose/vibe detectors). Patterns stay anchored to the CLI's
/// own status wording rather than bare numbers or single words: devin prompts
/// can contain repository content under review, and model output citing
/// "quota" or discussing rate limits must not gate dispatch.</para>
/// </summary>
public sealed class DevinQuotaFailureDetector : IAgentQuotaFailureDetector
{
    public AgentKind Kind => AgentKind.Devin;

    /// <summary>
    /// Built-in pattern allowlist. Order matters: rate-limit rows come first
    /// so a refusal carrying both shapes parks on the rate-limit backoff
    /// rather than terminating the item as a hard quota failure.
    /// </summary>
    public static readonly IReadOnlyList<QuotaFailurePattern> DefaultPatterns =
    [
        // Shared provider rate-limit rows (transient throughput refusals).
        .. SharedRateLimitPatterns.ProviderRateLimitPatterns,
        // Devin's own quota-exhaustion status strings (verified in the
        // 3000.11.1 binary).
        new("Quota exhausted", QuotaFailureKind.LimitReached),
        new("Usage limit reached", QuotaFailureKind.LimitReached),
        new("Usage paused", QuotaFailureKind.LimitReached),
        new("Purchase on-demand usage or turn on auto-reload", QuotaFailureKind.LimitReached),
        new("out of ACUs", QuotaFailureKind.LimitReached),
        new("quota exceeded", QuotaFailureKind.LimitReached),
        new("insufficient credits", QuotaFailureKind.LimitReached),
        new("402 Payment Required", QuotaFailureKind.LimitReached),
        // Auth failures (verified: `Error: Not logged in.` on stderr, exit 1).
        new("Not logged in", QuotaFailureKind.Unauthorized),
        new("Devin needs authentication", QuotaFailureKind.Unauthorized),
        new("Authentication required", QuotaFailureKind.Unauthorized),
        new("run `devin auth login`", QuotaFailureKind.Unauthorized),
        new("401 Unauthorized", QuotaFailureKind.Unauthorized),
    ];

    private readonly IReadOnlyList<QuotaFailurePattern> _patterns;

    /// <summary>
    /// Constructs a detector with the built-in <see cref="DefaultPatterns"/>.
    /// </summary>
    public DevinQuotaFailureDetector() : this(additionalPatterns: null) { }

    /// <summary>
    /// Constructs a detector whose pattern list is <see cref="DefaultPatterns"/>
    /// followed by <paramref name="additionalPatterns"/>. Operator-configured
    /// patterns are checked after defaults; null/empty input behaves
    /// identically to the parameterless constructor.
    /// </summary>
    public DevinQuotaFailureDetector(IEnumerable<QuotaFailurePattern>? additionalPatterns)
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
