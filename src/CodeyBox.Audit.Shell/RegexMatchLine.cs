using System.Text.RegularExpressions;

namespace CodeyBox.Audit.Shell;

/// <summary>
/// Shared extraction of the full output line containing a regex match, for
/// result classifiers that quote the matching line into an exception message.
/// The line is trimmed and bounded to
/// <see cref="ExecTransportFailure.DefaultMaxDiagnosticLineChars"/> chars —
/// the same bound <see cref="ExecTransportFailure.FirstDiagnosticLine"/>
/// applies — so a pathological single line cannot flood the audit record.
/// </summary>
internal static class RegexMatchLine
{
    /// <summary>
    /// Returns the line containing the first <paramref name="regex"/> match in
    /// <paramref name="output"/>, or <paramref name="fallback"/> when no match
    /// yields a non-empty line. Callers invoke this only after the regex
    /// already matched, so the fallback is unreachable in practice; it exists
    /// so the helper is total for any input.
    /// </summary>
    internal static string FirstMatchingLine(string output, Regex regex, string fallback)
    {
        foreach (var match in regex.EnumerateMatches(output.AsSpan()))
        {
            var start = match.Index;
            while (start > 0 && output[start - 1] is not ('\r' or '\n'))
                start--;
            var end = match.Index + match.Length;
            while (end < output.Length && output[end] is not ('\r' or '\n'))
                end++;
            var line = output[start..end].Trim();
            if (line.Length == 0)
                continue;
            return line.Length <= ExecTransportFailure.DefaultMaxDiagnosticLineChars
                ? line
                : line[..ExecTransportFailure.DefaultMaxDiagnosticLineChars] + "...";
        }

        return fallback;
    }
}
