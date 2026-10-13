using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Single source of truth for test-kind base-broken evidence text. A
/// test-kind <see cref="BaseBrokenCondition.ErrorSummary"/> names the
/// failing tests/checks and the suspect commits in one bounded,
/// sanitized line; the tracker parses the suspects back out when filing
/// the fix item so both name the same commits. Failing names additionally
/// travel on <see cref="BaseBrokenCondition.FailingNames"/> (persisted
/// column); the summary line is the human-readable carrier the operator
/// sees on the condition record.
/// </summary>
internal static class BaseBrokenTestSupport
{
    /// <summary>Marker prefixing the suspect-commit list inside a summary.</summary>
    internal const string SuspectsMarker = "suspects:";

    /// <summary>Marker prefixing the failing-name list inside a summary.</summary>
    internal const string FailuresMarker = "failures:";

    /// <summary>
    /// Builds the bounded error summary for a test-kind condition from the
    /// failing test/check names and the suspect commits. Every carried
    /// string is untrusted (test-runner output, forge check names, SHAs),
    /// so names go through the same sanitize/scrub pipeline as the fix
    /// item text and SHAs are restricted to hex.
    /// </summary>
    internal static string BuildSummary(
        IReadOnlyList<string>? failingNames,
        IReadOnlyList<string>? suspectCommits)
    {
        var names = BaseBrokenFixItemPolicy.SanitizeNameList(
            failingNames, BaseBrokenFixItemPolicy.MaxRecordedFailingNames, 256);
        var suspects = SanitizeShas(suspectCommits);
        var count = failingNames?.Count ?? 0;
        var failures = names.Count == 0
            ? "(no failing test names captured)"
            : string.Join("; ", names) + (count > names.Count ? $"; … (+{count - names.Count} more)" : string.Empty);
        var summary = $"{FailuresMarker} {failures} {SuspectsMarker} " +
            (suspects.Count == 0 ? "(unknown)" : string.Join(", ", suspects));
        return summary.Length > BaseBrokenFixItemPolicy.MaxBuildOutputChars
            ? summary[..BaseBrokenFixItemPolicy.MaxBuildOutputChars]
            : summary;
    }

    /// <summary>
    /// Parses the suspect-commit list back out of a summary built by
    /// <see cref="BuildSummary"/>. Returns empty when the marker is absent
    /// (build-kind rows, or rows predating the test-kind extension).
    /// </summary>
    internal static IReadOnlyList<string> ParseSuspects(string? summary)
    {
        if (string.IsNullOrEmpty(summary))
            return [];
        var marker = summary.IndexOf(SuspectsMarker, StringComparison.Ordinal);
        if (marker < 0)
            return [];
        var tail = summary[(marker + SuspectsMarker.Length)..].Trim();
        if (tail.Length == 0 || tail.StartsWith("(unknown)", StringComparison.Ordinal))
            return [];
        var parts = tail.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            var sha = part.Trim();
            if (IsHexSha(sha) && result.Count < BaseBrokenFixItemPolicy.MaxSuspectCommits)
                result.Add(sha);
        }
        return result;
    }

    /// <summary>
    /// Parses the failing-name list back out of a summary built by
    /// <see cref="BuildSummary"/>. Used only as a fallback when the
    /// persisted <c>FailingNames</c> column is empty; the column is the
    /// primary carrier.
    /// </summary>
    internal static IReadOnlyList<string> ParseFailures(string? summary)
    {
        if (string.IsNullOrEmpty(summary))
            return [];
        var start = summary.IndexOf(FailuresMarker, StringComparison.Ordinal);
        if (start < 0)
            return [];
        var rest = summary[(start + FailuresMarker.Length)..];
        var end = rest.IndexOf(SuspectsMarker, StringComparison.Ordinal);
        var section = (end < 0 ? rest : rest[..end]).Trim();
        if (section.Length == 0 || section.StartsWith("(no failing", StringComparison.Ordinal))
            return [];
        return section.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    internal static IReadOnlyList<string> SanitizeShas(IReadOnlyList<string>? shas)
    {
        if (shas is null || shas.Count == 0)
            return [];
        var result = new List<string>(Math.Min(shas.Count, BaseBrokenFixItemPolicy.MaxSuspectCommits));
        foreach (var sha in shas)
        {
            if (result.Count >= BaseBrokenFixItemPolicy.MaxSuspectCommits)
                break;
            var clean = sha?.Trim() ?? string.Empty;
            if (clean.Length > 64)
                clean = clean[..64];
            if (IsHexSha(clean))
                result.Add(clean.ToLowerInvariant());
        }
        return result;
    }

    internal static bool IsHexSha(string value)
    {
        if (value.Length < 7 || value.Length > 64)
            return false;
        foreach (var c in value)
        {
            var isHex = (c >= '0' && c <= '9')
                || (c >= 'a' && c <= 'f')
                || (c >= 'A' && c <= 'F');
            if (!isHex)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Record-carrier sanitization for failing names: the same
    /// sanitize/scrub pipeline as the fix-item text, at record bounds.
    /// </summary>
    internal static IReadOnlyList<string> SanitizeNameListForRecord(IReadOnlyList<string>? names)
        => BaseBrokenFixItemPolicy.SanitizeNameList(
            names, BaseBrokenFixItemPolicy.MaxRecordedFailingNames, 256);

    /// <summary>Short-SHA rendering for park reasons and log lines.</summary>
    internal static string ShortSha(string? sha)
        => string.IsNullOrEmpty(sha) ? "unknown" : sha.Length <= 12 ? sha : sha[..12];
}
