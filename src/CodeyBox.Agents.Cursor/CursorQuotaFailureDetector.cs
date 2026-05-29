using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Cursor;

/// <summary>
/// Recognises quota / rate-limit / auth / agent-unavailable failures emitted by
/// the Cursor CLI.
///
/// <para>The pattern list is config-driven: built-in defaults ship in
/// <see cref="DefaultPatterns"/>, and operators can append additional patterns
/// via <c>CodeyBox:QuotaFailurePatterns:cursor</c> without recompilation. This
/// is the first detector to expose the pattern list as configuration; other
/// agent-kind detectors follow the same shape as their patterns stabilise.</para>
///
/// <para>The defaults are a conservative allowlist drawn from generic HTTP /
/// billing language and Cursor's documented error vocabulary; per
/// <c>feedback-vendor-api-drift</c> we extend reactively when real failures
/// appear in the audit log, not speculatively from vendor documentation. The
/// "out of usage" / "Switch to Auto" / "increase your limit" entries cover the
/// observed Cursor subscription-exhausted stderr ("You're out of usage. Switch
/// to Auto, or ask your admin to increase your limit to continue.") which
/// previously fell through as failureKind="other" and hard-failed the work
/// item rather than triggering a class-member failover.</para>
/// </summary>
public sealed class CursorQuotaFailureDetector : IAgentQuotaFailureDetector
{
    public AgentKind Kind => AgentKind.Cursor;

    /// <summary>
    /// Built-in pattern allowlist. Order matters: more-specific patterns win
    /// over more-generic ones (e.g. "rate limit reached" must classify as
    /// RateLimitExceeded, not LimitReached, so rate-limit patterns are checked
    /// first).
    /// </summary>
    public static readonly IReadOnlyList<QuotaFailurePattern> DefaultPatterns =
    [
        new("rate_limit_exceeded", QuotaFailureKind.RateLimitExceeded),
        new("rate limit", QuotaFailureKind.RateLimitExceeded),
        new("429 Too Many Requests", QuotaFailureKind.RateLimitExceeded),
        new("401 Unauthorized", QuotaFailureKind.Unauthorized),
        new("limit reached", QuotaFailureKind.LimitReached),
        new("usage limit", QuotaFailureKind.LimitReached),
        new("quota exceeded", QuotaFailureKind.LimitReached),
        new("402 Payment Required", QuotaFailureKind.LimitReached),
        // Cursor subscription-exhausted stderr; the CLI exits 1 with
        // "You're out of usage. Switch to Auto, or ask your admin to increase
        // your limit to continue." The three phrases below cover the three
        // observed shapes of that line; matching any one is sufficient.
        new("out of usage", QuotaFailureKind.LimitReached),
        new("Switch to Auto", QuotaFailureKind.LimitReached),
        new("increase your limit", QuotaFailureKind.LimitReached),
    ];

    private readonly IReadOnlyList<QuotaFailurePattern> _patterns;

    /// <summary>
    /// Constructs a detector with the built-in <see cref="DefaultPatterns"/>.
    /// </summary>
    public CursorQuotaFailureDetector() : this(additionalPatterns: null) { }

    /// <summary>
    /// Constructs a detector whose pattern list is <see cref="DefaultPatterns"/>
    /// followed by <paramref name="additionalPatterns"/>. Operator-configured
    /// patterns are checked after defaults so a future Cursor-side rename
    /// doesn't silently override the built-in coverage; null/empty input
    /// behaves identically to the parameterless constructor.
    /// </summary>
    public CursorQuotaFailureDetector(IEnumerable<QuotaFailurePattern>? additionalPatterns)
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
            return new QuotaDetection(entry.Kind, QuotaResetParser.TryParseResetAt(resetSources));
        }

        return null;
    }
}
