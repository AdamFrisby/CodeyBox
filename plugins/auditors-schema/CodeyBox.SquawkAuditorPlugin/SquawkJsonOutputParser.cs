using System.Text.Json;
using CodeyBox.PluginSdk.Tools;
using static CodeyBox.PluginSdk.Tools.ExternalToolJsonHelpers;

namespace CodeyBox.SquawkAuditorPlugin;

/// <summary>
/// Parses squawk's <c>--reporter json</c> output — a flat JSON array of
/// <c>{ "file", "line", "column", "level", "message", "help", "rule_name",
/// "column_end", "line_end" }</c> violation objects — into
/// <see cref="ExternalToolFinding"/> records. The array shape is required as
/// the discriminator for squawk's ambiguous exit convention: a completed
/// scan (exit 0 or 1) always writes this array — empty when clean — while a
/// run failure exits 1 with a plain-text error on stderr and no report.
/// Empty stdout, non-JSON output, or JSON that is not an array therefore
/// throws <see cref="ExternalToolParseException"/>, which the base reports
/// as infrastructure, never as a pass.
///
/// <para><c>level</c> carries squawk's vocabulary — <c>"Warning"</c> for a
/// violated rule, <c>"Error"</c> for SQL the parser rejected — through
/// <see cref="ExternalToolFinding.SeverityLevel"/>; the auditor's declared
/// <see cref="ExternalToolSeverityMapping"/> converts it, so raw levels
/// never reach findings. <c>rule_name</c> (e.g.
/// <c>require-concurrent-index-creation</c>, <c>syntax-error</c>) becomes the
/// finding's rule id verbatim, so <c>IncludedRules</c>/<c>ExcludedRules</c>
/// select squawk rules directly.</para>
///
/// <para>squawk's <c>line</c> is zero-based (the parser reports a
/// <c>line_col</c> index); findings carry it converted to the 1-based line
/// the audit report expects. <c>column_end</c>/<c>line_end</c> add no
/// information the finding needs and are ignored. <c>file</c> is the matched
/// path as globbed — repo-relative for the default <c>**/*.sql</c> pattern;
/// when an operator supplies absolute patterns the value runs through the
/// shared <see cref="ExternalToolJsonHelpers.NormalizeReportedPath"/> policy:
/// relativized against the scan root or working directory the scan ran in,
/// or marked <c>file://</c> when it cannot be made repository-relative.</para>
/// </summary>
internal sealed class SquawkJsonOutputParser : IExternalToolOutputParser
{
    // Same per-document result bound the shared SARIF parser applies.
    private const int MaxResults = SarifToolOutputParser.DefaultMaxResults;

    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Stdout))
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced no squawk JSON report on stdout.");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(input.Stdout);
        }
        catch (JsonException ex)
        {
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced output that is not valid squawk JSON: {ToolOutputText.SingleLine(ex.Message)}.",
                ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new ExternalToolParseException(
                    $"Tool '{input.ToolName}' produced JSON that is not a squawk violations array.");

            var findings = new List<ExternalToolFinding>();
            foreach (var violation in document.RootElement.EnumerateArray())
            {
                if (findings.Count >= MaxResults)
                    break;
                if (violation.ValueKind != JsonValueKind.Object)
                    continue;
                findings.Add(ParseViolation(violation, input));
            }

            return findings;
        }
    }

    private static ExternalToolFinding ParseViolation(JsonElement violation, ExternalToolParseInput input)
    {
        var level = GetString(violation, "level"u8);
        var ruleName = ToolOutputText.NullIfWhiteSpace(GetString(violation, "rule_name"u8));
        var message = ToolOutputText.NullIfWhiteSpace(GetString(violation, "message"u8))
            ?? "(no message)";
        var help = ToolOutputText.NullIfWhiteSpace(GetString(violation, "help"u8));
        var path = NormalizeReportedPath(
            ToolOutputText.NullIfWhiteSpace(GetString(violation, "file"u8)),
            input.ScanRoot,
            input.WorkingDirectory);
        var line = ParseLine(violation);

        return new ExternalToolFinding(
            SeverityLevel: level,
            RuleId: ruleName,
            Message: help is null ? message : $"{message}\nhelp: {help}",
            Path: path,
            Line: line);
    }

    private static int? ParseLine(JsonElement violation)
    {
        // squawk reports a zero-based line index; findings carry the 1-based
        // line the audit report expects. A missing or non-numeric field keeps
        // the finding file-scoped rather than failing the report.
        if (!violation.TryGetProperty("line"u8, out var element)
            || element.ValueKind != JsonValueKind.Number
            || !element.TryGetInt32(out var line)
            || line < 0
            || line == int.MaxValue)
            return null;
        return line + 1;
    }
}
