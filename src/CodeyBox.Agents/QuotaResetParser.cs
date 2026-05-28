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
    //   "reset in 30m", "retry after 1h", "try again after 2h30m".
    // The duration pieces are individually optional but at least one must
    // match; the surrounding code rejects the all-zero case.
    // Duration pieces accept compact (5h23m) and word forms (5 hours 23 minutes)
    // surfaced by OpenCode subscription limits and other provider CLIs.
    private static readonly Regex ResetAfterRegex = new(
        @"(?:reset(?:s|ting)?(?:\s+will\s+reset)?\s+after|reset\s+in|retry\s+after|try\s+again\s+after|available\s+(?:in|after))\s+(?:(\d+)\s*(?:h(?:ou)?rs?|h))?\s*(?:(\d+)\s*(?:m(?:in(?:ute)?s?|ins?)|m))?\s*(?:(\d+)\s*(?:s(?:ec(?:ond)?s?|ecs?)|s))?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Returns the first parseable reset time from <paramref name="sources"/>,
    /// computed as <c>DateTimeOffset.UtcNow</c> + the parsed duration.
    /// Returns null when no source yields a non-zero duration.
    /// </summary>
    public static DateTimeOffset? TryParseResetAt(IEnumerable<string?> sources)
    {
        foreach (var source in sources)
        {
            if (string.IsNullOrEmpty(source)) continue;
            var match = ResetAfterRegex.Match(source);
            if (!match.Success) continue;

            var h = 0;
            var m = 0;
            var s = 0;

            if (match.Groups[1].Success && int.TryParse(match.Groups[1].Value, out var hv)) h = Math.Min(hv, 10_000);
            if (match.Groups[2].Success && int.TryParse(match.Groups[2].Value, out var mv)) m = Math.Min(mv, 10_000);
            if (match.Groups[3].Success && int.TryParse(match.Groups[3].Value, out var sv)) s = Math.Min(sv, 10_000);

            if (h > 0 || m > 0 || s > 0)
                return DateTimeOffset.UtcNow.Add(new TimeSpan(h, m, s));
        }

        return null;
    }
}
