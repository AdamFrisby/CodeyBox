using System.Text.RegularExpressions;
using CodeyBox.Agents;

namespace CodeyBox.Agents.Opencode;

/// <summary>
/// Parses reset windows from OpenCode subscription usage-limit stderr.
///
/// <para>Shares the <see cref="Agents.QuotaResetParser"/> threat model: agent
/// output is prompt-injectable prose, so word-form durations (e.g. "5 hours
/// 23 minutes") must not be able to widen the quota-reset pause window from
/// an arbitrary message. The word-capable <c>ResetInRegex</c> below is
/// therefore honoured only for sources that also carry the subscription-limit
/// anchor (<c>usage limit reached</c> — the same exhaustion phrase the
/// detector's usage-limit regexes require before this parser runs). Anything
/// else falls through to the shared compact-duration parser, so both parsers
/// agree on every unanchored input and differ only on anchored subscription
/// limits, where the anchor — not bare prose — vouches for the duration.
/// </para>
/// </summary>
internal static class OpencodeQuotaResetParser
{
    // OpenCode subscription limits surface "It will reset in 5 hours 23 minutes."
    // alongside compact tails used by other CLIs.
    private static readonly Regex ResetInRegex = new(
        @"reset\s+in\s+(?:(\d+)\s*(?:h(?:ou)?rs?|h))?\s*(?:(\d+)\s*(?:m(?:in(?:ute)?s?|ins?)|m))?\s*(?:(\d+)\s*(?:s(?:ec(?:ond)?s?|ecs?)|s))?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// True when <paramref name="source"/> carries the subscription-limit
    /// anchor that vouches for a word-form duration: the exhaustion phrase
    /// the detector's usage-limit shapes all contain. Unanchored prose —
    /// including prompt-injectable agent output — never satisfies this, so
    /// word-form durations in it are rejected exactly as the shared parser
    /// rejects them.
    /// </summary>
    internal static bool HasSubscriptionLimitAnchor(string? source) =>
        !string.IsNullOrEmpty(source)
        && source.Contains("usage limit reached", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the first parseable reset time from <paramref name="sources"/>.
    /// Anchored word-form durations are honoured first; anything else —
    /// compact tails in any source, anchored or not — is parsed by the shared
    /// <see cref="Agents.QuotaResetParser"/> so both parsers agree on every
    /// input except anchored subscription limits. Returns null when no source
    /// yields a non-zero duration; in particular, word-form durations in
    /// unanchored (agent-authored) prose are rejected here exactly as the
    /// shared parser rejects them.
    /// </summary>
    public static DateTimeOffset? TryParseResetAt(IEnumerable<string?> sources)
    {
        var materialized = sources as IReadOnlyList<string?> ?? sources.ToList();

        foreach (var source in materialized)
        {
            if (string.IsNullOrEmpty(source) || !HasSubscriptionLimitAnchor(source))
                continue;

            var match = ResetInRegex.Match(source);
            if (!match.Success)
                continue;

            var h = 0;
            var m = 0;
            var s = 0;

            if (match.Groups[1].Success && int.TryParse(match.Groups[1].Value, out var hv)) h = Math.Min(hv, 10_000);
            if (match.Groups[2].Success && int.TryParse(match.Groups[2].Value, out var mv)) m = Math.Min(mv, 10_000);
            if (match.Groups[3].Success && int.TryParse(match.Groups[3].Value, out var sv)) s = Math.Min(sv, 10_000);

            if (h > 0 || m > 0 || s > 0)
                return DateTimeOffset.UtcNow.Add(new TimeSpan(h, m, s));
        }

        return QuotaResetParser.TryParseResetAt(materialized);
    }
}
