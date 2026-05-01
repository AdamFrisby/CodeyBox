using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeyBox.Core;

/// <summary>
/// Computes a best-effort stable ID for a finding so that the same issue flagged
/// across consecutive audit iterations produces the same ID. The ID is used for
/// diff-based "which finding persisted / is new / was resolved" analysis.
///
/// Limitation: two findings that describe the same defect but use different LLM-generated
/// titles will get different IDs. This is known and documented — we are not attempting
/// NLP-level deduplication.
/// </summary>
public static partial class FindingIdComputer
{
    // Strip filenames, paths, and "line NNN" fragments from the title so minor
    // phrasing variations (e.g. adding/removing a path reference) don't change the ID.
    [GeneratedRegex(
        @"(?:\bin\s+[\w/.\\\-]+\b|\bat\s+[\w/.\\\-]+(?::\d+)?\b|\bline\s+\d+\b|\(line\s+\d+\)|[\w/.\\\-]+\.\w{1,6}:\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FileAndLineRef();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex MultipleWhitespace();

    /// <summary>
    /// Returns a short, stable finding ID of the form <c>f-XXXXXXXX</c> (8 lowercase hex chars).
    /// </summary>
    public static string Compute(string auditorName, string title, IEnumerable<string> files)
    {
        var normalizedTitle = NormalizeTitle(title);
        var sortedFiles = string.Join("\0", files.OrderBy(f => f, StringComparer.Ordinal));
        var input = $"{auditorName}\0{normalizedTitle}\0{sortedFiles}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return "f-" + Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }

    private static string NormalizeTitle(string title)
    {
        var lower = title.ToLowerInvariant();
        var stripped = FileAndLineRef().Replace(lower, " ");
        return MultipleWhitespace().Replace(stripped, " ").Trim();
    }
}
