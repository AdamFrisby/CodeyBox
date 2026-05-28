using System.Text.RegularExpressions;

namespace CodeyBox.Agents.Opencode;

/// <summary>
/// Parses reset windows from OpenCode subscription usage-limit stderr.
/// Word-form durations (e.g. "5 hours 23 minutes") are scoped here rather
/// than in the shared <see cref="Agents.QuotaResetParser"/> so other agents
/// are not influenced by prompt-injectable natural-language prose.
/// </summary>
internal static class OpencodeQuotaResetParser
{
    // OpenCode subscription limits surface "It will reset in 5 hours 23 minutes."
    // alongside compact tails used by other CLIs.
    private static readonly Regex ResetInRegex = new(
        @"reset\s+in\s+(?:(\d+)\s*(?:h(?:ou)?rs?|h))?\s*(?:(\d+)\s*(?:m(?:in(?:ute)?s?|ins?)|m))?\s*(?:(\d+)\s*(?:s(?:ec(?:ond)?s?|ecs?)|s))?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static DateTimeOffset? TryParseResetAt(IEnumerable<string?> sources)
    {
        foreach (var source in sources)
        {
            if (string.IsNullOrEmpty(source)) continue;
            var match = ResetInRegex.Match(source);
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
