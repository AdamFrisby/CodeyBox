using System.Globalization;
using System.Text.RegularExpressions;
using CodeyBox.Core;

namespace CodeyBox.Audit.Shell;

internal static class DotnetTestOutputParser
{
    private const double UnrunnableFailureThresholdMs = 50;
    private const int MaxParsedFailureHeaders = 1_024;
    private const int MaxReportedFailureFindings = 50;
    private const int MaxFailureBodyChars = 4_000;
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
    private static readonly Regex UnrunnableFailureSignalRegex = new(
        @"\b(?:Microsoft\.Playwright\.PlaywrightException|Browser executable (?:was )?not found|Playwright\b.*\b(?:install|driver|browser)|unable to launch|failed to launch|connection refused|no connection could be made|ECONNREFUSED|missing (?:host|dependency|dependencies)|required (?:host|dependency|dependencies)\b.*\bnot (?:found|available))\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline,
        TimeSpan.FromSeconds(1));
    private static readonly Regex AssertionFailureSignalRegex = new(
        @"\b(?:Assert\.[A-Za-z0-9_]+\(\) Failure|Assert\.[A-Za-z0-9_]+ failed|Xunit\.Sdk\.[A-Za-z0-9_]+Exception|NUnit\.Framework\.AssertionException|Microsoft\.VisualStudio\.TestTools\.UnitTesting\.AssertFailedException|FluentAssertions\.Execution\.AssertionFailedException|Shouldly\.ShouldAssertException)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline,
        TimeSpan.FromSeconds(1));
    private static readonly Regex DurationRegex = new(
        @"^(?<value>\d+(?:[.]\d+)?)\s*(?<unit>ms|millisecond|milliseconds|s|sec|secs|second|seconds|m|min|mins|minute|minutes)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(5));

    public static DotnetTestOutputParseResult Parse(string auditorName, string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return new DotnetTestOutputParseResult([], 0, false);

        var match = FailedTestHeaderRegex.Match(output);
        if (!match.Success)
            return new DotnetTestOutputParseResult([], 0, false);

        var findings = new List<AuditFinding>();
        var failureBodyRanges = new List<(int Start, int End)>();
        var parsedFailureCount = 0;
        var omittedReportedFailureCount = 0;

        while (match.Success && parsedFailureCount < MaxParsedFailureHeaders)
        {
            var nextMatch = match.NextMatch();
            parsedFailureCount++;
            var testName = match.Groups["name"].Value.Trim();
            var durationText = match.Groups["duration"].Value.Trim();
            var bodyStart = match.Index + match.Length;
            var bodyEnd = nextMatch.Success
                ? nextMatch.Index
                : output.Length;
            bodyEnd = FindFailureBodyEnd(output, bodyStart, bodyEnd);
            failureBodyRanges.Add((bodyStart, bodyEnd));
            var fullBody = ExtractBody(output, bodyStart, bodyEnd);
            var durationMs = TryParseDurationMilliseconds(durationText);
            var stackTrace = ExtractStackTrace(fullBody);

            if (IsUnrunnableFailure(durationMs, stackTrace, fullBody))
            {
                match = nextMatch;
                continue;
            }

            if (findings.Count < MaxReportedFailureFindings)
            {
                findings.Add(new AuditFinding(
                    AuditorName: auditorName,
                    Severity: AuditSeverity.Error,
                    Title: $"test failed: {testName}",
                    Description: BuildDescription(durationText, fullBody)));
            }
            else
            {
                omittedReportedFailureCount++;
            }

            match = nextMatch;
        }

        if (omittedReportedFailureCount > 0)
            findings.Add(BuildOmittedFailureFinding(auditorName, omittedReportedFailureCount));

        if (match.Success)
            findings.Add(BuildUnparsedFailureOverflowFinding(auditorName));

        return new DotnetTestOutputParseResult(
            findings,
            parsedFailureCount,
            HasCommandFailureSignalOutsideRanges(output, failureBodyRanges));
    }

    private static int FindFailureBodyEnd(string output, int bodyStart, int defaultEnd)
    {
        var postBody = PostFailureBodyRegex.Match(output, bodyStart, Math.Max(0, defaultEnd - bodyStart));
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

    private static bool IsUnrunnableFailure(double? durationMs, string stackTrace, string body)
        => durationMs is not null
           && durationMs.Value < UnrunnableFailureThresholdMs
           && string.IsNullOrWhiteSpace(stackTrace)
           && !AssertionFailureSignalRegex.IsMatch(body)
           && UnrunnableFailureSignalRegex.IsMatch(body);

    private static string ExtractBody(string output, int bodyStart, int bodyEnd)
    {
        var bodyLength = Math.Max(0, bodyEnd - bodyStart);
        return output.Substring(bodyStart, bodyLength).Trim();
    }

    private static string BuildDescription(string durationText, string body)
    {
        var details = string.IsNullOrWhiteSpace(body)
            ? "(no failure details were emitted)"
            : Truncate(body, MaxFailureBodyChars);

        return $"The test failed after {durationText}.\n\n{details}";
    }

    private static AuditFinding BuildOmittedFailureFinding(string auditorName, int omittedCount)
        => new(
            AuditorName: auditorName,
            Severity: AuditSeverity.Error,
            Title: $"additional dotnet test failures omitted: {omittedCount}",
            Description: $"Only the first {MaxReportedFailureFindings} real test failures are reported individually to keep audit output bounded. Fix the reported failures, then rerun the auditor to surface any remaining failures.");

    private static AuditFinding BuildUnparsedFailureOverflowFinding(string auditorName)
        => new(
            AuditorName: auditorName,
            Severity: AuditSeverity.Error,
            Title: "dotnet test output had too many failed-test blocks to classify safely",
            Description: $"The parser inspected the first {MaxParsedFailureHeaders} failed-test blocks and stopped before enumerating the rest. Reduce the failing test volume or fix the reported failures, then rerun the auditor.");

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

        var match = DurationRegex.Match(text);
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
    bool HasCommandFailureSignals);
