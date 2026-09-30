using System.Text.Json;
using CodeyBox.PluginSdk.Tools;
using static CodeyBox.PluginSdk.Tools.ExternalToolJsonHelpers;

namespace CodeyBox.ValeAuditorPlugin;

/// <summary>
/// Parses vale's <c>--output=JSON</c> report into
/// <see cref="ExternalToolFinding"/> records. The report is an object keyed
/// by file path, each holding that file's alerts (verified against vale
/// v3.23.0, <c>cmd/vale/json.go</c>):
///
/// <code>
/// {
///   "README.md": [
///     { "Check": "Vale.Spelling", "Severity": "error",
///       "Message": "Did you really mean 'detials'?",
///       "Line": 6, "Span": [23, 30], "Match": "detials", … }
///   ]
/// }
/// </code>
///
/// Files without alerts carry no key, so a clean tree reports <c>{}</c> —
/// still a JSON object, still a verdict. The rule id is the alert's
/// <c>Check</c> (e.g. <c>Vale.Spelling</c>, <c>write-good.Weasel</c>), so the
/// shared <c>IncludedRules</c>/<c>ExcludedRules</c> configuration selects
/// vale checks by their stable check name. Severity stays in vale's own
/// vocabulary (<c>suggestion</c>/<c>warning</c>/<c>error</c>,
/// <c>internal/core/alert.go</c>) and the auditor's declared
/// <see cref="ExternalToolSeverityMapping"/> decides its meaning; raw levels
/// never reach findings. <c>Line</c> is carried through as the finding line
/// when positive.
///
/// <para>Reported paths are normalized with the shared
/// <see cref="ExternalToolJsonHelpers.NormalizeReportedPath"/> policy:
/// <c>./</c> prefixes and dot segments collapse, absolute paths relativize
/// against the scan root and working directory, and anything still escaping
/// the tree is re-marked with the <c>file://</c> scheme so the shared
/// <c>ExcludePaths</c> prefix filter cannot mistake it for a
/// repository-relative location.</para>
///
/// <para>Malformed output — empty stdout, non-JSON, a non-object document, or
/// a file entry that is not an alert array (e.g. the <c>--counts</c> wrapper
/// shape after an <c>ExtraArguments</c> <c>--counts</c>/<c>--output</c>
/// override) — throws <see cref="ExternalToolParseException"/>, which the
/// base reports as infrastructure, never as a pass. An empty object
/// (<c>{}</c>) is a clean verdict, not malformed output.</para>
/// </summary>
internal sealed class ValeJsonOutputParser : IExternalToolOutputParser
{
    // Same per-document result bound the shared SARIF parser applies.
    private const int MaxResults = SarifToolOutputParser.DefaultMaxResults;
    private const int MessageMaxChars = 512;

    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Stdout))
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced no vale JSON output on stdout.");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(input.Stdout);
        }
        catch (JsonException ex)
        {
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced output that is not valid JSON: {ToolOutputText.SingleLine(ex.Message)}.",
                ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new ExternalToolParseException(
                    $"Tool '{input.ToolName}' produced JSON that is not a vale report "
                    + "(the report is an object keyed by file path).");

            var findings = new List<ExternalToolFinding>();
            foreach (var property in root.EnumerateObject())
            {
                if (findings.Count >= MaxResults)
                    break;
                if (property.Value.ValueKind != JsonValueKind.Array)
                    throw new ExternalToolParseException(
                        $"Tool '{input.ToolName}' produced JSON that is not a vale report "
                        + "(a file entry is not an alert array).");
                var path = NormalizeReportedPath(property.Name, input.ScanRoot, input.WorkingDirectory);
                foreach (var alert in property.Value.EnumerateArray())
                {
                    if (findings.Count >= MaxResults)
                        break;
                    if (alert.ValueKind == JsonValueKind.Object)
                        findings.Add(ParseAlert(alert, path));
                }
            }

            return findings;
        }
    }

    private static ExternalToolFinding ParseAlert(JsonElement alert, string? path)
    {
        var severity = NullIfWhiteSpace(GetString(alert, "Severity"u8));
        var ruleId = NullIfWhiteSpace(GetString(alert, "Check"u8));
        var message = NullIfWhiteSpace(GetString(alert, "Message"u8)) ?? "(no message)";

        int? line = null;
        if (alert.TryGetProperty("Line"u8, out var lineElement)
            && lineElement.ValueKind == JsonValueKind.Number
            && lineElement.TryGetInt32(out var lineValue)
            && lineValue > 0)
            line = lineValue;

        return new ExternalToolFinding(
            SeverityLevel: severity,
            RuleId: ruleId,
            Message: message.Length > MessageMaxChars ? message[..MessageMaxChars] + "…" : message,
            Path: path,
            Line: line);
    }
}
