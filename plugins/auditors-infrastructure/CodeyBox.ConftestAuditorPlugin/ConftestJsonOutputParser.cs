using System.Text.Json;
using CodeyBox.PluginSdk.Tools;
using static CodeyBox.PluginSdk.Tools.ExternalToolJsonHelpers;

namespace CodeyBox.ConftestAuditorPlugin;

/// <summary>
/// Parses conftest's <c>test --output json</c> report — a JSON array of
/// per-file <c>{ "filename", "namespace", "successes", "skipped",
/// "warnings", "failures", "exceptions" }</c> objects, where each entry in
/// <c>warnings</c>/<c>failures</c>/<c>exceptions</c> carries a <c>msg</c>, an
/// optional <c>loc</c> (<c>{ "file", "line" }</c>, from the Rego
/// <c>_loc</c> metadata), and a <c>metadata.query</c> (e.g.
/// <c>data.main.deny</c>) — into <see cref="ExternalToolFinding"/> records.
/// A clean evaluation reports entries with empty result lists (or an empty
/// array); any other shape (empty stdout, non-JSON, a non-array document)
/// means the tool did not produce its report and throws
/// <see cref="ExternalToolParseException"/>, which the base reports as
/// infrastructure, never as a pass.
/// <para>The result category (<c>failure</c>, <c>warning</c>,
/// <c>exception</c>) is carried as the severity level and mapped by the
/// auditor's declared <see cref="ExternalToolSeverityMapping"/> — raw tool
/// vocabulary never reaches findings. The <c>metadata.query</c> doubles as
/// the rule id, so <c>IncludedRules</c>/<c>ExcludedRules</c> select by query
/// (e.g. <c>data.main.deny</c>). <c>skipped</c> entries are dropped: a skip
/// means a policy chose not to evaluate, not a problem with the tree.</para>
/// </summary>
internal sealed class ConftestJsonOutputParser : IExternalToolOutputParser
{
    // Same per-document result bound the shared SARIF parser applies, so a
    // finding-dense tree cannot produce an unbounded in-memory list before
    // the base applies its own MaxFindings cap.
    private const int MaxResults = SarifToolOutputParser.DefaultMaxResults;

    // One policy message can run long; bound a single finding's message so
    // one result cannot dominate the audit report.
    private const int MaxMessageChars = 4000;

    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Stdout))
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced no conftest JSON report on stdout.");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(input.Stdout);
        }
        catch (JsonException ex)
        {
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced output that is not valid conftest JSON: {SingleLine(ex.Message)}.",
                ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new ExternalToolParseException(
                    $"Tool '{input.ToolName}' produced JSON without the conftest check-result array.");

            var findings = new List<ExternalToolFinding>();
            foreach (var check in document.RootElement.EnumerateArray())
            {
                if (findings.Count >= MaxResults)
                    break;
                if (check.ValueKind != JsonValueKind.Object)
                    continue;
                AppendResults(check, "failures", "failure", findings);
                if (findings.Count >= MaxResults)
                    break;
                AppendResults(check, "warnings", "warning", findings);
                if (findings.Count >= MaxResults)
                    break;
                AppendResults(check, "exceptions", "exception", findings);
            }

            return findings;
        }
    }

    private static void AppendResults(
        JsonElement check,
        string property,
        string category,
        List<ExternalToolFinding> findings)
    {
        var fileName = NullIfWhiteSpace(NormalizePath(GetString(check, "filename"u8)));
        if (!check.TryGetProperty(property, out var results)
            || results.ValueKind != JsonValueKind.Array)
            return;

        foreach (var result in results.EnumerateArray())
        {
            if (findings.Count >= MaxResults)
                break;
            if (result.ValueKind != JsonValueKind.Object)
                continue;
            findings.Add(ParseResult(result, fileName, category));
        }
    }

    private static ExternalToolFinding ParseResult(JsonElement result, string? fileName, string category)
    {
        var message = NullIfWhiteSpace(GetString(result, "msg"u8)) ?? "(no message)";

        string? query = null;
        if (result.TryGetProperty("metadata"u8, out var metadata)
            && metadata.ValueKind == JsonValueKind.Object)
            query = NullIfWhiteSpace(GetString(metadata, "query"u8));

        // Lenient per entry, strict per document: a result without a query
        // still reports under a synthesized rule id (mapped through the
        // severity default); only a document without the check-result array
        // fails closed above.
        var ruleId = query ?? $"conftest/{category}";

        string? path = fileName;
        int? line = null;
        if (result.TryGetProperty("loc"u8, out var loc)
            && loc.ValueKind == JsonValueKind.Object)
        {
            var locFile = NullIfWhiteSpace(NormalizePath(GetString(loc, "file"u8)));
            if (locFile is not null)
                path = locFile;
            line = ReadLine(loc);
        }

        return new ExternalToolFinding(
            SeverityLevel: category,
            RuleId: ruleId,
            Message: Truncate(message, MaxMessageChars),
            Path: NullIfWhiteSpace(path),
            Line: line);
    }

    private static int? ReadLine(JsonElement loc)
    {
        if (loc.TryGetProperty("line"u8, out var line))
        {
            if (line.ValueKind == JsonValueKind.Number && line.TryGetInt32(out var number) && number > 0)
                return number;
            if (line.ValueKind == JsonValueKind.String
                && int.TryParse(line.GetString(), out var text) && text > 0)
                return text;
        }

        return null;
    }
}
