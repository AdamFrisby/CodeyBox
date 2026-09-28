using System.Text.Json;
using CodeyBox.PluginSdk.Tools;
using static CodeyBox.PluginSdk.Tools.ExternalToolJsonHelpers;

namespace CodeyBox.ActionlintAuditorPlugin;

/// <summary>
/// Parses actionlint's <c>-format '{{json .}}'</c> report — a JSON array of
/// <c>{ "message", "filepath", "line", "column", "kind", "snippet",
/// "end_column" }</c> objects — into <see cref="ExternalToolFinding"/>
/// records. A clean scan reports an empty array (<c>[]</c>); any other shape
/// (empty stdout, non-JSON, a non-array document) means the tool did not
/// produce its report and throws <see cref="ExternalToolParseException"/>,
/// which the base reports as infrastructure, never as a pass.
/// <para>The raw <c>kind</c> token (the rule name that reported the error,
/// e.g. <c>action</c>, <c>syntax-check</c>) is carried as the severity level
/// and mapped by the auditor's declared
/// <see cref="ExternalToolSeverityMapping"/> — raw tool vocabulary never
/// reaches findings. It doubles as the rule id, so
/// <c>IncludedRules</c>/<c>ExcludedRules</c> select by rule name.</para>
/// </summary>
internal sealed class ActionlintJsonOutputParser : IExternalToolOutputParser
{
    // Same per-document result bound the shared SARIF parser applies, so a
    // finding-dense tree cannot produce an unbounded in-memory list before
    // the base applies its own MaxFindings cap.
    private const int MaxResults = SarifToolOutputParser.DefaultMaxResults;

    // One tool message (e.g. an unknown-input error listing every available
    // input) can run long; bound a single finding's message so one result
    // cannot dominate the audit report.
    private const int MaxMessageChars = 4000;

    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Stdout))
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced no actionlint JSON report on stdout.");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(input.Stdout);
        }
        catch (JsonException ex)
        {
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced output that is not valid actionlint JSON: {SingleLine(ex.Message)}.",
                ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new ExternalToolParseException(
                    $"Tool '{input.ToolName}' produced JSON without the actionlint error array.");

            var findings = new List<ExternalToolFinding>();
            foreach (var error in document.RootElement.EnumerateArray())
            {
                if (findings.Count >= MaxResults)
                    break;
                if (error.ValueKind != JsonValueKind.Object)
                    continue;
                findings.Add(ParseError(error));
            }

            return findings;
        }
    }

    private static ExternalToolFinding ParseError(JsonElement error)
    {
        var kind = NullIfWhiteSpace(GetString(error, "kind"u8));
        var message = NullIfWhiteSpace(GetString(error, "message"u8)) ?? "(no message)";
        var path = NullIfWhiteSpace(NormalizePath(GetString(error, "filepath"u8)));

        int? line = null;
        if (error.TryGetProperty("line"u8, out var lineElement)
            && lineElement.ValueKind == JsonValueKind.Number
            && lineElement.TryGetInt32(out var lineValue)
            && lineValue > 0)
            line = lineValue;

        // Lenient per entry, strict per document: a missing kind still
        // reports (mapped through the severity default, Title falling back
        // to the message); only a document without the error array fails
        // closed above.
        return new ExternalToolFinding(
            SeverityLevel: kind,
            RuleId: kind,
            Message: Truncate(message, MaxMessageChars),
            Path: path,
            Line: line);
    }
}
