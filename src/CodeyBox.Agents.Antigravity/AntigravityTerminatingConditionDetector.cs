using System.Text.RegularExpressions;
using CodeyBox.Core;

namespace CodeyBox.Agents.Antigravity;

/// <summary>
/// Detects and extracts recognized terminating conditions (CLI-reported timeout,
/// shutdown sequence, explicit errors) from Antigravity agent captures and formats
/// budget exhaustion failure summaries naming configured options.
/// </summary>
public static class AntigravityTerminatingConditionDetector
{
    private static readonly Regex PrintModeTimeoutRegex = new(
        @"printmode\.go:\d+\]\s*Print mode:\s*timed out after\s+(\d+)\s+polls\s+\(printed=(\d+)\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex GlogErrorRegex = new(
        @"^E\d{4}\s+[\d:.]+\s+[^:]+:\d+\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] TimeoutMarkers =
    [
        "Print mode: timed out after",
    ];

    private static readonly string[] ShutdownMarkers =
    [
        "CLI store manager shutting down",
        "Language server shutting down",
    ];

    private static readonly string[] ExplicitErrorMarkers =
    [
        "panic:",
        "fatal:",
        "FATAL:",
        "Error:",
    ];

    /// <summary>
    /// Checks whether <paramref name="text"/> contains a recognized print-mode or response timeout.
    /// </summary>
    public static bool IsPrintModeTimeout(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (PrintModeTimeoutRegex.IsMatch(text)) return true;

        foreach (var marker in TimeoutMarkers)
        {
            if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Formats the budget exhaustion summary string naming the configuration key and its value.
    /// (e.g. <c>budget exhaustion: CodeyBox:Antigravity:PrintTimeoutMinutes=45m</c>).
    /// </summary>
    public static string FormatBudgetExhaustionSummary(TimeSpan printTimeout)
    {
        var budgetStr = printTimeout > TimeSpan.Zero
            ? (printTimeout.TotalSeconds % 60 == 0
                ? $"{(int)printTimeout.TotalMinutes}m"
                : $"{(int)printTimeout.TotalSeconds}s")
            : "5m";

        return $"budget exhaustion: {AntigravityAgentRunner.PrintTimeoutConfigKey}={budgetStr}";
    }

    /// <summary>
    /// Extracts the terminating condition slice (the terminating timeout, error, and shutdown sequence)
    /// from <paramref name="capture"/>, returning null if no recognized terminating condition is present.
    /// Repeated lines within the condition are collapsed.
    /// </summary>
    public static string? ExtractTerminatingCondition(string? capture, int maxScanLines = 100)
    {
        if (string.IsNullOrWhiteSpace(capture)) return null;

        var rawLines = capture.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        var end = rawLines.Length;
        while (end > 0 && rawLines[end - 1].Length == 0) end--;
        if (end == 0) return null;

        var startScan = Math.Max(0, end - maxScanLines);

        // Find the earliest marker within the tail that initiates the terminating sequence.
        // If a shutdown sequence exists at the end, any immediately preceding timeout or error
        // marker should be included as the root cause.
        var earliestMarkerIndex = -1;

        for (var i = end - 1; i >= startScan; i--)
        {
            var line = rawLines[i];
            if (IsTerminatingLine(line))
            {
                earliestMarkerIndex = i;
            }
            else if (earliestMarkerIndex != -1)
            {
                // We reached a non-terminating line before our earliest found marker.
                // Stop scanning backwards so we don't grab unrelated earlier errors.
                break;
            }
        }

        if (earliestMarkerIndex < 0)
            return null;

        var slice = string.Join("\n", rawLines[earliestMarkerIndex..end]);
        return RawOutputRedactor.CollapseRepeatedLines(slice);
    }

    private static bool IsTerminatingLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;

        if (IsPrintModeTimeout(line))
            return true;

        foreach (var marker in ShutdownMarkers)
        {
            if (line.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        if (GlogErrorRegex.IsMatch(line))
            return true;

        foreach (var marker in ExplicitErrorMarkers)
        {
            if (line.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
