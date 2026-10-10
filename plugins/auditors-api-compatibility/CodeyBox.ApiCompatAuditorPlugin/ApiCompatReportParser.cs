using System.Text.RegularExpressions;
using CodeyBox.PluginSdk.Tools;

namespace CodeyBox.ApiCompatAuditorPlugin;

/// <summary>
/// Parses the <c>apicompat</c> human-readable report (Microsoft.DotNet.ApiCompat.Tool)
/// into <see cref="ExternalToolFinding"/> records. Verified against 10.0.401: the tool
/// exposes no JSON or SARIF output mode — its verdict is a plain-text report written
/// to <b>stderr</b> when breaking changes exist (stdout carries only the clean-run
/// marker), so both streams are scanned for diagnostic lines.
///
/// <para>A diagnostic line is <c>CPnnnn: &lt;message&gt;</c> (observed: <c>CP0001</c> type
/// removed, <c>CP0002</c> member removed, <c>CP0017</c> parameter name changed behind its
/// opt-in rule). Each becomes one finding carrying the diagnostic id as the rule id and
/// the remainder as the message. The tool prints no per-diagnostic severity — everything
/// appears under the <c>API compatibility errors between …</c> header — so findings carry
/// the canonical <see cref="ErrorLevel"/> token, which the auditor's declared mapping
/// resolves to <see cref="CodeyBox.Core.AuditSeverity.Error"/>; raw tool levels never
/// reach findings.</para>
///
/// <para>The tool reports member identities (e.g. <c>int TestLib.Widget.Id.get</c>), never
/// a file/line — findings therefore carry no location. Paths are never reconstructed by
/// parsing human-readable messages.</para>
///
/// <para>The exit code and the report cross-check each other, because exit <c>1</c> is
/// ambiguous: breaking changes exit <c>1</c> with diagnostic lines, but a run failure
/// (missing assembly → <c>Unhandled exception: …FileNotFoundException…</c> plus a stack
/// trace; bad invocation → <c>Option '…' is required</c> / <c>Unrecognized …</c> usage
/// text) also exits <c>1</c>. Exit <c>1</c> with diagnostic lines is the findings verdict;
/// exit <c>1</c> with none contradicts the output contract and throws
/// <see cref="ExternalToolParseException"/> — infrastructure, never a pass. An empty or
/// unrecognizable report on exit <c>0</c> without the clean-run marker is likewise
/// infrastructure: a silent tool is never a passing audit.</para>
/// </summary>
internal sealed class ApiCompatReportParser : IExternalToolOutputParser
{
    /// <summary>Canonical tool level for <c>CPnnnn</c> diagnostics (maps to Error).</summary>
    internal const string ErrorLevel = "error";

    /// <summary>Marker printed to stdout when the comparison completed with no breaking changes.</summary>
    internal const string CleanMarker = "APICompat ran successfully without finding any breaking changes.";

    /// <summary>Trailer printed after the diagnostics when breaking changes exist.</summary>
    internal const string BreakingTrailer = "API breaking changes found.";

    // Same per-report result bound the shared SARIF parser applies.
    private const int MaxResults = SarifToolOutputParser.DefaultMaxResults;
    private const int MessageMaxChars = 512;

    private static readonly Regex DiagnosticLine = new(
        @"^CP(?<id>\d+)\s*:\s*(?<message>.+?)\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var findings = new List<ExternalToolFinding>();
        var sawCleanMarker = false;

        foreach (var rawLine in SplitLines(input.Stdout, input.Stderr))
        {
            var line = ToolOutputText.SingleLine(rawLine).Trim();
            if (line.Length == 0)
                continue;

            if (line.Contains(CleanMarker, StringComparison.Ordinal))
            {
                sawCleanMarker = true;
                continue;
            }

            var match = DiagnosticLine.Match(line);
            if (!match.Success || findings.Count >= MaxResults)
                continue;

            var message = ExternalToolJsonHelpers.Truncate(match.Groups["message"].Value, MessageMaxChars);
            if (string.IsNullOrWhiteSpace(message))
                continue;

            findings.Add(new ExternalToolFinding(
                SeverityLevel: ErrorLevel,
                RuleId: "CP" + match.Groups["id"].Value,
                Message: message,
                Path: null,
                Line: null));
        }

        if (findings.Count == 0)
        {
            if (input.ExitCode == 0 && sawCleanMarker)
                return findings;

            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' exited {input.ExitCode} without a recognizable "
                + "apicompat report (no CPnnnn diagnostic lines and no clean-run marker) — "
                + "the check claims nothing the report shows.");
        }

        return findings;
    }

    private static IEnumerable<string> SplitLines(params string[] streams)
    {
        foreach (var stream in streams)
        {
            if (string.IsNullOrEmpty(stream))
                continue;
            using var reader = new StringReader(stream);
            while (reader.ReadLine() is { } line)
                yield return line;
        }
    }
}
