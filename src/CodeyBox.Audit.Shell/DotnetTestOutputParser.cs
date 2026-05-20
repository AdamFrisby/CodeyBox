using System.Globalization;
using System.Text.RegularExpressions;
using CodeyBox.Core;

namespace CodeyBox.Audit.Shell;

internal static class DotnetTestOutputParser
{
    private const double UnrunnableFailureThresholdMs = 50;
    private static readonly Regex FailedTestHeaderRegex = new(
        @"^\s*Failed\s+(?<name>.+?)\s+\[(?<duration>[^\]\r\n]+)\]\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline,
        TimeSpan.FromSeconds(1));
    private static readonly Regex PostFailureBodyRegex = new(
        @"^\s*(?:Failed!|Passed!|Skipped!|Test Run |Total tests:|Results File:)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline,
        TimeSpan.FromSeconds(1));
    private static readonly Regex CommandFailureSignalRegex = new(
        @"^\s*(?:.+(?:\)|:)\s*)?error\s+(?:CS|MSB|NETSDK|NU)\d+\b|^\s*Build FAILED\.|^\s*The argument .+ is invalid\.|^\s*The active test run was aborted\b|^\s*Testhost process\b.*\b(?:crashed|failed|error)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Multiline,
        TimeSpan.FromSeconds(1));

    public static DotnetTestOutputParseResult Parse(string auditorName, string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return new DotnetTestOutputParseResult([], 0, 0, false);

        var matches = FailedTestHeaderRegex.Matches(output);
        if (matches.Count == 0)
            return new DotnetTestOutputParseResult([], 0, 0, false);

        var findings = new List<AuditFinding>();
        var failureBodyRanges = new List<(int Start, int End)>();
        var excluded = 0;

        for (var i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            var testName = match.Groups["name"].Value.Trim();
            var durationText = match.Groups["duration"].Value.Trim();
            var bodyStart = match.Index + match.Length;
            var bodyEnd = i + 1 < matches.Count
                ? matches[i + 1].Index
                : output.Length;
            bodyEnd = FindFailureBodyEnd(output, bodyStart, bodyEnd);
            failureBodyRanges.Add((bodyStart, bodyEnd));
            var body = output[bodyStart..bodyEnd].Trim();
            var durationMs = TryParseDurationMilliseconds(durationText);
            var stackTrace = ExtractStackTrace(body);

            if (IsUnrunnableFailure(durationMs, stackTrace))
            {
                excluded++;
                continue;
            }

            findings.Add(new AuditFinding(
                AuditorName: auditorName,
                Severity: AuditSeverity.Error,
                Title: $"test failed: {testName}",
                Description: BuildDescription(durationText, body)));
        }

        return new DotnetTestOutputParseResult(
            findings,
            matches.Count,
            excluded,
            HasCommandFailureSignalOutsideRanges(output, failureBodyRanges));
    }

    private static int FindFailureBodyEnd(string output, int bodyStart, int defaultEnd)
    {
        var postBody = PostFailureBodyRegex.Match(output, bodyStart);
        return postBody.Success && postBody.Index < defaultEnd
            ? postBody.Index
            : defaultEnd;
    }

    private static bool HasCommandFailureSignalOutsideRanges(
        string output,
        IReadOnlyList<(int Start, int End)> excludedRanges)
    {
        foreach (Match signal in CommandFailureSignalRegex.Matches(output))
        {
            if (!IsInsideAnyRange(signal.Index, excludedRanges))
                return true;
        }

        return false;
    }

    private static bool IsInsideAnyRange(
        int index,
        IReadOnlyList<(int Start, int End)> ranges)
    {
        foreach (var (start, end) in ranges)
        {
            if (index >= start && index < end)
                return true;
        }

        return false;
    }

    private static bool IsUnrunnableFailure(double? durationMs, string stackTrace)
        => durationMs is not null
           && durationMs.Value < UnrunnableFailureThresholdMs
           && string.IsNullOrWhiteSpace(stackTrace);

    private static string BuildDescription(string durationText, string body)
    {
        var details = string.IsNullOrWhiteSpace(body)
            ? "(no failure details were emitted)"
            : Truncate(body, 4_000);

        return $"The test failed after {durationText}.\n\n{details}";
    }

    private static string ExtractStackTrace(string body)
    {
        var lines = body.Split('\n');
        var stackLines = new List<string>();
        var inStackTrace = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            var trimmed = line.Trim();

            if (!inStackTrace)
            {
                if (trimmed.Equals("Stack Trace:", StringComparison.OrdinalIgnoreCase))
                    inStackTrace = true;
                continue;
            }

            if (trimmed.Length == 0)
                continue;

            if (IsPostStackTraceSection(trimmed))
                break;

            stackLines.Add(line);
        }

        return string.Join('\n', stackLines).Trim();
    }

    private static bool IsPostStackTraceSection(string trimmed)
        => trimmed.Equals("Error Message:", StringComparison.OrdinalIgnoreCase)
           || trimmed.Equals("Standard Output Messages:", StringComparison.OrdinalIgnoreCase)
           || trimmed.Equals("Standard Error Messages:", StringComparison.OrdinalIgnoreCase)
           || trimmed.Equals("Attachments:", StringComparison.OrdinalIgnoreCase)
           || trimmed.StartsWith("Failed!", StringComparison.OrdinalIgnoreCase)
           || trimmed.StartsWith("Passed!", StringComparison.OrdinalIgnoreCase)
           || trimmed.StartsWith("Test Run ", StringComparison.OrdinalIgnoreCase)
           || trimmed.StartsWith("Total tests:", StringComparison.OrdinalIgnoreCase)
           || trimmed.StartsWith("Results File:", StringComparison.OrdinalIgnoreCase);

    private static double? TryParseDurationMilliseconds(string durationText)
    {
        var text = durationText.Trim();
        if (text.StartsWith('<'))
            text = text[1..].TrimStart();

        var match = Regex.Match(
            text,
            @"^(?<value>\d+(?:[.]\d+)?)\s*(?<unit>ms|millisecond|milliseconds|s|sec|secs|second|seconds|m|min|mins|minute|minutes)$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1));
        if (match.Success
            && double.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return match.Groups["unit"].Value.ToLowerInvariant() switch
            {
                "ms" or "millisecond" or "milliseconds" => value,
                "s" or "sec" or "secs" or "second" or "seconds" => value * 1_000,
                "m" or "min" or "mins" or "minute" or "minutes" => value * 60_000,
                _ => null,
            };
        }

        if (TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var timeSpan))
            return timeSpan.TotalMilliseconds;

        return null;
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "...";
}

internal sealed record DotnetTestOutputParseResult(
    IReadOnlyList<AuditFinding> Findings,
    int ParsedFailureCount,
    int ExcludedUnrunnableFailureCount,
    bool HasCommandFailureSignals);
