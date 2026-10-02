using System.Globalization;

namespace CodeyBox.Majordomo;

/// <summary>
/// Deterministic, dependency-free rollup for conversation rows that no
/// longer fit the durable or assembled budgets. Extractive, never
/// model-generated: counts by role, the tool names used (ordinal-sorted),
/// and the covered sequence window and time span — so the rollup is a pure
/// function of its inputs and identical inputs always compact identically.
/// Older history is summarised rather than dropped.
/// </summary>
public static class MajordomoHistorySummarizer
{
    /// <summary>Hard cap on one stored summary, keeping the newest tail.</summary>
    internal const int MaxStoredSummaryChars = 8192;

    private const string TrimMarker = "[earlier summary trimmed to fit the stored bound] ";

    /// <summary>
    /// Builds the one-line rollup covering <paramref name="covered"/>.
    /// Empty input yields an empty string; otherwise the line names the
    /// sequence window, the row count by role, the tools used, and the time
    /// span. Deterministic: rows are ordered by sequence, ties broken by
    /// role, text, and tool name, all with ordinal comparison.
    /// </summary>
    public static string Summarize(IReadOnlyList<MajordomoConversationEntry> covered)
    {
        ArgumentNullException.ThrowIfNull(covered);
        if (covered.Count == 0)
            return string.Empty;

        var ordered = covered
            .OrderBy(static e => e.Sequence)
            .ThenBy(static e => e.Role)
            .ThenBy(static e => e.Text, StringComparer.Ordinal)
            .ThenBy(static e => e.ToolName, StringComparer.Ordinal)
            .ToList();

        var operators = 0;
        var majordomos = 0;
        var calls = 0;
        var results = 0;
        var tools = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var entry in ordered)
        {
            switch (entry.Role)
            {
                case MajordomoConversationRole.Operator: operators++; break;
                case MajordomoConversationRole.Majordomo: majordomos++; break;
                case MajordomoConversationRole.ToolCall: calls++; break;
                case MajordomoConversationRole.ToolResult: results++; break;
            }
            if (entry.ToolName is not null)
                tools.Add(entry.ToolName);
        }

        var first = ordered[0];
        var last = ordered[^1];
        var toolList = tools.Count == 0 ? "none" : string.Join(",", tools);
        return string.Create(CultureInfo.InvariantCulture,
            $"entries {first.Sequence}..{last.Sequence} ({ordered.Count} rows: " +
            $"{operators} operator, {majordomos} majordomo, {calls} tool calls, " +
            $"{results} tool results; tools {toolList}; " +
            $"{first.RecordedAt:O}..{last.RecordedAt:O})");
    }

    /// <summary>
    /// Extends <paramref name="existing"/> with the rollup of
    /// <paramref name="covered"/>, advancing the watermark to the highest
    /// covered sequence. The stored text is capped at
    /// <see cref="MaxStoredSummaryChars"/> keeping the newest tail, so the
    /// summary itself cannot grow without bound as compactions accumulate.
    /// </summary>
    public static MajordomoConversationSummary Combine(
        MajordomoConversationSummary existing,
        IReadOnlyList<MajordomoConversationEntry> covered,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(covered);
        if (covered.Count == 0)
            return existing;

        var addition = Summarize(covered);
        var watermark = Math.Max(existing.UpToSequence, covered.Max(static e => e.Sequence));
        var text = existing.UpToSequence == 0
            ? $"Earlier history: {addition}."
            : $"{existing.Text} Earlier history: {addition}.";
        if (text.Length > MaxStoredSummaryChars)
            text = TrimMarker + text[^Math.Max(0, MaxStoredSummaryChars - TrimMarker.Length)..];
        return new MajordomoConversationSummary(watermark, text, now);
    }
}
