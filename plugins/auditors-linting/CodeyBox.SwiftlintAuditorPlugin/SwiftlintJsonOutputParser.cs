using System.Text.Json;
using CodeyBox.PluginSdk.Tools;
using static CodeyBox.PluginSdk.Tools.ExternalToolJsonHelpers;

namespace CodeyBox.SwiftlintAuditorPlugin;

/// <summary>
/// Parses SwiftLint's built-in <c>--reporter json</c> report — a top-level
/// JSON array with one object per violation —
/// <c>[ { "rule_id", "reason", "severity", "file", "line", "character",
/// "type" } ]</c> — into <see cref="ExternalToolFinding"/> records.
/// Verified against SwiftLint 0.65.1: the report goes to stdout as pure JSON
/// (the human <c>Linting …</c> / <c>Done linting!</c> progress log goes to
/// stderr), <c>file</c> is an absolute path, <c>line</c> is 1-based, and
/// <c>severity</c> is <c>Warning</c> or <c>Error</c> (<c>--strict</c>
/// upgrades warnings to errors in the report itself).
///
/// <para>Paths are relativized against
/// <see cref="ExternalToolParseInput.ScanRoot"/> — the directory the scan
/// actually ran in, resolved by the auditor per run — because the report
/// embeds no working directory. Diagnostics outside the scan root keep an
/// explicit <c>file://</c> scheme on their absolute path so they stay
/// distinguishable from repository-relative paths (and cannot accidentally
/// match repo-relative <c>ExcludePaths</c> entries). Severity is kept in
/// SwiftLint's own vocabulary and <see cref="ExternalToolAuditorBase"/> maps
/// it through the auditor's declared
/// <see cref="ExternalToolSeverityMapping"/>; raw levels never reach
/// findings.</para>
///
/// <para>Malformed output — empty stdout, non-JSON, or a JSON document that
/// is not an array (usage text such as <c>Error: Unknown option</c>, a help
/// dump, a crash trace) — throws <see cref="ExternalToolParseException"/>,
/// which the base reports as infrastructure, never as a pass. A non-zero
/// findings exit whose stdout yields no violations — a crash before the
/// report is written, or an operator <c>--reporter</c>/<c>--output</c>
/// override that moved the report off stdout — likewise throws:
/// "found problems" and "could not run" must stay distinguishable.</para>
/// </summary>
internal sealed class SwiftlintJsonOutputParser : IExternalToolOutputParser
{
    // Same per-document result bound the shared SARIF parser applies.
    private const int MaxResults = SarifToolOutputParser.DefaultMaxResults;
    private const int MessageMaxChars = 512;

    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Stdout))
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced no SwiftLint JSON output on stdout.");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(input.Stdout);
        }
        catch (JsonException ex)
        {
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced output that is not valid SwiftLint JSON: {SingleLine(ex.Message)}.",
                ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new ExternalToolParseException(
                    $"Tool '{input.ToolName}' produced JSON that is not a SwiftLint report (expected a top-level array).");

            var findings = new List<ExternalToolFinding>();
            foreach (var violation in document.RootElement.EnumerateArray())
            {
                if (findings.Count >= MaxResults)
                    break;
                if (violation.ValueKind == JsonValueKind.Object)
                    findings.Add(ParseViolation(violation, input));
            }

            // SwiftLint exits non-zero from a completed scan only when it
            // found error-level violations; a findings exit with nothing
            // parseable on stdout means the run did not produce its declared
            // report — crash, truncation, or foreign output — not a clean
            // pass.
            if (input.ExitCode != 0 && findings.Count == 0)
                throw new ExternalToolParseException(
                    $"Tool '{input.ToolName}' exited {input.ExitCode} but produced no SwiftLint violations on stdout.");
            return findings;
        }
    }

    private static ExternalToolFinding ParseViolation(JsonElement violation, ExternalToolParseInput input)
    {
        var message = NullIfWhiteSpace(GetString(violation, "reason"u8)) ?? "(no message)";

        int? line = null;
        if (violation.TryGetProperty("line"u8, out var lineElement)
            && lineElement.ValueKind == JsonValueKind.Number
            && lineElement.TryGetInt32(out var parsed)
            && parsed > 0)
            line = parsed;

        return new ExternalToolFinding(
            SeverityLevel: NullIfWhiteSpace(GetString(violation, "severity"u8)),
            RuleId: NullIfWhiteSpace(GetString(violation, "rule_id"u8)),
            Message: Truncate(SingleLine(message), MessageMaxChars),
            Path: NormalizeReportedPath(
                GetString(violation, "file"u8), input.ScanRoot, input.WorkingDirectory),
            Line: line);
    }
}
