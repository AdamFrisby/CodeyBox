using System.Text.RegularExpressions;

namespace CodeyBox.Audit;

/// <summary>
/// A single added (or modified) line in a <c>git diff --unified=0 --no-color</c>
/// patch, projected onto the NEW-file coordinate system.
/// </summary>
/// <param name="File">
/// Repository-relative path from the <c>+++ b/&lt;path&gt;</c> header, or
/// <c>null</c> for added lines that appear before any file header (git never
/// emits this, but the parser preserves the distinction so callers can decide
/// how to attribute an unlocated line).
/// </param>
/// <param name="NewLine">1-based line number in the new file.</param>
/// <param name="Content">The added line's content, with the leading <c>+</c> stripped.</param>
public readonly record struct AddedDiffLine(string? File, int NewLine, string Content);

/// <summary>
/// Pure parser for unified diffs produced with <c>--unified=0 --no-color</c>.
/// It yields the ADDED lines (new-file coordinates), which is the set both the
/// diff-pattern auditor (matching regexes against added content) and the
/// coverage gate (intersecting changed executable lines with the coverage
/// report) reason over. Deletions consume no new-file line number and are not
/// reported.
///
/// <para>Single source of truth for "which new-file lines did this diff add or
/// modify" — used by <see cref="DiffPatternAuditor"/> and
/// <see cref="CoverageAuditor"/> so the two never drift on hunk-header/line
/// accounting.</para>
/// </summary>
public static partial class UnifiedDiffParser
{
    /// <summary>
    /// Parses a <c>--unified=0</c> diff into its added lines, in document order.
    /// Zero context means every emitted line is genuinely added or modified;
    /// with non-zero context this would also surface unchanged context lines, so
    /// callers MUST diff with <c>--unified=0</c>.
    /// </summary>
    public static IReadOnlyList<AddedDiffLine> ParseAddedLines(string diff)
    {
        ArgumentNullException.ThrowIfNull(diff);

        var added = new List<AddedDiffLine>();
        string? currentFile = null;
        var lineNumber = 0;

        foreach (var line in diff.Split('\n'))
        {
            var rawLine = line.TrimEnd('\r');

            // "+++ b/path" names the new-side file for the following hunks.
            if (rawLine.StartsWith("+++ b/", StringComparison.Ordinal))
            {
                currentFile = rawLine[6..];
                continue;
            }

            // "@@ -A,B +C,D @@" resets the new-file line cursor to C.
            if (rawLine.StartsWith("@@", StringComparison.Ordinal))
            {
                var m = HunkHeader().Match(rawLine);
                if (m.Success && int.TryParse(m.Groups[1].Value, out var ln))
                    lineNumber = ln;
                continue;
            }

            // Diff metadata lines that are not added content.
            if (rawLine.StartsWith("+++", StringComparison.Ordinal)) continue;
            if (rawLine.StartsWith("---", StringComparison.Ordinal)) continue;

            // Deletions ("-…") and unexpected context lines consume no new-file
            // line number; only "+…" advances the cursor.
            if (!rawLine.StartsWith('+')) continue;

            added.Add(new AddedDiffLine(currentFile, lineNumber, rawLine[1..]));
            lineNumber++;
        }

        return added;
    }

    /// <summary>
    /// Groups <see cref="ParseAddedLines"/> into the set of changed new-file
    /// line numbers per file. Lines with no file header are dropped (they cannot
    /// be attributed to a source file). Paths are compared with
    /// <see cref="StringComparer.Ordinal"/> — the sandbox is Linux and git paths
    /// are case-sensitive.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlySet<int>> ChangedLinesByFile(string diff)
    {
        var byFile = new Dictionary<string, SortedSet<int>>(StringComparer.Ordinal);
        foreach (var line in ParseAddedLines(diff))
        {
            if (line.File is null)
                continue;
            if (!byFile.TryGetValue(line.File, out var set))
                byFile[line.File] = set = new SortedSet<int>();
            set.Add(line.NewLine);
        }

        return byFile.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlySet<int>)kvp.Value,
            StringComparer.Ordinal);
    }

    [GeneratedRegex(@"\+(\d+)(?:,(\d+))? @@", RegexOptions.CultureInvariant)]
    private static partial Regex HunkHeader();
}
