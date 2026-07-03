using System.Text.RegularExpressions;

namespace CodeyBox.Agents;

/// <summary>
/// Shared helpers for parsing the "reset after Xh Ym Zs" tail that every
/// provider's quota error eventually surfaces in human-readable form.
/// Provider-neutral by design — the duration phrasings are common across
/// CLIs and their underlying HTTP responses.
/// </summary>
public static class QuotaResetParser
{
    // Matches the duration tail of common reset/retry phrasings:
    //   "reset after 21h41m24s", "will reset after 5m17s",
    //   "reset in 30m", "Resets in 8m14s" (agy's plural form),
    //   "retry after 1h", "try again after 2h30m".
    // The "in" branch accepts the optional plural "s" ("reset in" /
    // "resets in") because Antigravity's consumer-quota 429 reports its
    // rolling-window reset as "Individual quota reached (Resets in 8m14s)";
    // the singular-only form would have left that reset unparsed and parked
    // the item on the default backoff instead of the precise window.
    // The duration pieces are individually optional but at least one must
    // match; the surrounding code rejects the all-zero case.
    // Compact duration tokens only (5h23m, 21h41m24s). Word forms such as
    // "5 hours 23 minutes" are intentionally excluded so prompt-injectable
    // prose in agent output cannot widen the quota-reset pause window.
    private static readonly Regex ResetAfterRegex = new(
        @"(?:reset(?:s|ting)?(?:\s+will\s+reset)?\s+after|reset(?:s|ting)?\s+in|retry\s+after|try\s+again\s+after|available\s+(?:in|after))\s+(?:(\d+)\s*h(?![a-zA-Z]))?\s*(?:(\d+)\s*m(?![a-zA-Z]))?\s*(?:(\d+)\s*s(?![a-zA-Z]))?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Returns the first parseable reset time from <paramref name="sources"/>,
    /// computed as <paramref name="utcNow"/> (defaulting to the current UTC
    /// instant) + the parsed duration. Returns null when no source yields a
    /// non-zero duration.
    ///
    /// <para>Within each source ALL regex matches are scanned, not just the first:
    /// a non-duration prefix can produce a spurious all-zero first match (e.g.
    /// "quota resets in a moment; retry after 8m") that would otherwise be
    /// rejected and shadow the real duration later in the same string. We keep
    /// scanning until a non-zero duration is found. The optional
    /// <paramref name="utcNow"/> is the injected clock — callers pass a fixed
    /// instant so the parsed reset is deterministic in tests.</para>
    /// </summary>
    public static DateTimeOffset? TryParseResetAt(
        IEnumerable<string?> sources,
        DateTimeOffset? utcNow = null)
    {
        var now = utcNow ?? DateTimeOffset.UtcNow;
        foreach (var source in sources)
        {
            if (string.IsNullOrEmpty(source)) continue;

            foreach (Match match in ResetAfterRegex.Matches(source))
            {
                var h = 0;
                var m = 0;
                var s = 0;

                if (match.Groups[1].Success && int.TryParse(match.Groups[1].Value, out var hv)) h = Math.Min(hv, 10_000);
                if (match.Groups[2].Success && int.TryParse(match.Groups[2].Value, out var mv)) m = Math.Min(mv, 10_000);
                if (match.Groups[3].Success && int.TryParse(match.Groups[3].Value, out var sv)) s = Math.Min(sv, 10_000);

                if (h > 0 || m > 0 || s > 0)
                    return now.Add(new TimeSpan(h, m, s));
            }
        }

        return null;
    }
}
