using System.Text.Json;
using CodeyBox.PluginSdk.Tools;
using static CodeyBox.PluginSdk.Tools.ExternalToolJsonHelpers;

namespace CodeyBox.SqlfluffAuditorPlugin;

/// <summary>
/// Parses sqlfluff's <c>lint --format json</c> output — a JSON array with one
/// entry per linted file, <c>{ "filepath", "violations": [
/// { "start_line_no", "start_line_pos", "code", "description", "name",
/// "warning", ... } ] }</c> — into <see cref="ExternalToolFinding"/>
/// records. Files whose <c>violations</c> array is empty contribute no
/// findings, so a clean scan (<c>[]</c>, or entries with empty arrays) parses
/// to zero findings and a pass.
///
/// <para>The array shape is required as the discriminator for the run's
/// health: a completed scan (exit 0 or 1) always writes this array — with
/// entries, possibly violation-free, for every linted file — while every
/// "could not run" failure (unknown dialect, unreadable config, nonexistent
/// path, usage error — all exit 2) writes a plain-text error to stderr and
/// no report. Empty stdout, non-JSON output, or JSON that is not an array
/// therefore throws <see cref="ExternalToolParseException"/>, which the base
/// reports as infrastructure, never as a pass.</para>
///
/// <para>sqlfluff's own severity vocabulary is a single boolean:
/// <c>warning: false</c> is a rule violation that fails the run (exit 1),
/// <c>warning: true</c> is a rule the repository's configuration downgraded
/// via its <c>warnings</c> list (the run still exits 0). The parser carries
/// those through <see cref="ExternalToolFinding.SeverityLevel"/> as
/// <c>"error"</c> and <c>"warning"</c> respectively — an absent or
/// non-boolean <c>warning</c> fails closed to <c>"error"</c> — and the
/// auditor's declared <see cref="ExternalToolSeverityMapping"/> converts
/// them, so no tool spelling reaches findings. <c>code</c> (e.g.
/// <c>LT01</c>, <c>PRS</c>) becomes the finding's rule id verbatim, so
/// <c>IncludedRules</c>/<c>ExcludedRules</c> select sqlfluff rules directly;
/// the dotted rule <c>name</c> (e.g. <c>layout.spacing</c>) is appended to
/// the message for rule-documentation lookup.</para>
///
/// <para><c>start_line_no</c> is already 1-based and is carried verbatim; a
/// missing or non-positive value keeps the finding file-scoped rather than
/// failing the report. <c>filepath</c> is the path as linted — repo-relative
/// for the default <c>.</c> scan; when an operator supplies absolute paths
/// the value runs through the shared
/// <see cref="ExternalToolJsonHelpers.NormalizeReportedPath"/> policy:
/// relativized against the scan root or working directory the scan ran in,
/// or marked <c>file://</c> when it cannot be made repository-relative.</para>
/// </summary>
internal sealed class SqlfluffJsonOutputParser : IExternalToolOutputParser
{
    // Same per-document result bound the shared SARIF parser applies.
    private const int MaxResults = SarifToolOutputParser.DefaultMaxResults;

    // Levels the parser itself emits — the auditor's declared severity
    // mapping translates them, so these spellings never reach findings.
    internal const string ErrorLevel = "error";
    internal const string WarningLevel = "warning";

    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Stdout))
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced no sqlfluff JSON report on stdout.");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(input.Stdout);
        }
        catch (JsonException ex)
        {
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced output that is not valid sqlfluff JSON: {ToolOutputText.SingleLine(ex.Message)}.",
                ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new ExternalToolParseException(
                    $"Tool '{input.ToolName}' produced JSON that is not a sqlfluff file-results array.");

            var findings = new List<ExternalToolFinding>();
            foreach (var fileResult in document.RootElement.EnumerateArray())
            {
                if (findings.Count >= MaxResults)
                    break;
                if (fileResult.ValueKind != JsonValueKind.Object)
                    continue;
                ParseFileResult(fileResult, input, findings);
                if (findings.Count >= MaxResults)
                    break;
            }

            return findings;
        }
    }

    private static void ParseFileResult(
        JsonElement fileResult, ExternalToolParseInput input, List<ExternalToolFinding> findings)
    {
        if (!fileResult.TryGetProperty("violations"u8, out var violations)
            || violations.ValueKind != JsonValueKind.Array)
            return;

        var path = NormalizeReportedPath(
            NullIfWhiteSpace(GetString(fileResult, "filepath"u8)),
            input.ScanRoot,
            input.WorkingDirectory);

        foreach (var violation in violations.EnumerateArray())
        {
            if (findings.Count >= MaxResults)
                break;
            if (violation.ValueKind != JsonValueKind.Object)
                continue;
            findings.Add(ParseViolation(violation, path));
        }
    }

    private static ExternalToolFinding ParseViolation(JsonElement violation, string? path)
    {
        var code = NullIfWhiteSpace(GetString(violation, "code"u8));
        var description = NullIfWhiteSpace(GetString(violation, "description"u8))
            ?? "(no message)";
        var name = NullIfWhiteSpace(GetString(violation, "name"u8));
        var level = IsWarning(violation) ? WarningLevel : ErrorLevel;

        return new ExternalToolFinding(
            SeverityLevel: level,
            RuleId: code,
            Message: name is null ? description : $"{description}\nrule: {name}",
            Path: path,
            Line: ParseLine(violation));
    }

    private static bool IsWarning(JsonElement violation)
    {
        // The tool omits nothing here in practice; an absent or non-boolean
        // value fails closed to error (blocking) rather than advisory.
        return violation.TryGetProperty("warning"u8, out var element)
            && element.ValueKind == JsonValueKind.True;
    }

    private static int? ParseLine(JsonElement violation)
    {
        // sqlfluff reports 1-based lines. A missing, non-numeric, or
        // non-positive value keeps the finding file-scoped rather than
        // failing the report.
        if (!violation.TryGetProperty("start_line_no"u8, out var element)
            || element.ValueKind != JsonValueKind.Number
            || !element.TryGetInt32(out var line)
            || line <= 0)
            return null;
        return line;
    }
}
