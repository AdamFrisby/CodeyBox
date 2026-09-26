using System.Text.Json;
using CodeyBox.PluginSdk.Tools;

namespace CodeyBox.AstGrepAuditorPlugin;

/// <summary>
/// Parses <c>ast-grep scan --json=compact</c> output — a bare JSON array of
/// diagnostics, each
/// <c>{ "text", "range": { "byteOffset", "start": {"line","column"}, "end" },
/// "file", "lines", "charCount", "language", "metaVariables"?, "ruleId",
/// "severity", "note", "message", "labels"? }</c> — into
/// <see cref="ExternalToolFinding"/> records. Verified against ast-grep
/// 0.45.3 (<c>RuleMatchJSON</c> in <c>crates/cli/src/print/json_print.rs</c>).
///
/// <para>The array shape is the discriminator between a completed scan and a
/// run failure: every failure mode that still exits 1 — an unclassified
/// anyhow error — writes a plain-text <c>Error: …</c> diagnostic to stderr
/// and no JSON to stdout, while a scan that found error-severity matches
/// also exits 1 but emits the array first. Empty stdout, non-JSON, or JSON
/// that is not an array throws <see cref="ExternalToolParseException"/>,
/// which the base reports as infrastructure, never as a pass.</para>
///
/// <para><c>range.start.line</c>/<c>column</c> are zero-based (ast-grep's
/// documented JSON convention); findings carry the 1-based line.
/// <c>severity</c> is the tool's own token (<c>error</c>, <c>warning</c>,
/// <c>info</c>, <c>hint</c>, <c>off</c>) and is mapped by the auditor's
/// declared <see cref="ExternalToolSeverityMapping"/> — raw levels never
/// reach findings. A rule's <c>note</c> (its longer explanation) is appended
/// to the message so it survives into the finding description.</para>
/// </summary>
internal sealed class AstGrepJsonOutputParser : IExternalToolOutputParser
{
    // Same per-document result bound the shared SARIF parser applies.
    private const int MaxResults = SarifToolOutputParser.DefaultMaxResults;

    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Stdout))
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced no ast-grep JSON report on stdout.");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(input.Stdout);
        }
        catch (JsonException ex)
        {
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced output that is not valid ast-grep JSON: {SingleLine(ex.Message)}.",
                ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new ExternalToolParseException(
                    $"Tool '{input.ToolName}' produced JSON that is not the ast-grep match array.");

            var findings = new List<ExternalToolFinding>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (findings.Count >= MaxResults)
                    break;
                if (element.ValueKind != JsonValueKind.Object)
                    continue;
                findings.Add(ParseMatch(element));
            }

            return findings;
        }
    }

    private static ExternalToolFinding ParseMatch(JsonElement match)
    {
        var ruleId = NullIfWhiteSpace(GetString(match, "ruleId"u8));
        var severity = NullIfWhiteSpace(GetString(match, "severity"u8));
        var message = NullIfWhiteSpace(GetString(match, "message"u8)) ?? "(no message)";
        var note = NullIfWhiteSpace(GetString(match, "note"u8));
        if (note is not null)
            message = message + " " + note;
        var path = NullIfWhiteSpace(GetString(match, "file"u8));

        int? line = null;
        if (match.TryGetProperty("range"u8, out var range)
            && range.ValueKind == JsonValueKind.Object
            && range.TryGetProperty("start"u8, out var start)
            && start.ValueKind == JsonValueKind.Object
            && start.TryGetProperty("line"u8, out var lineElement)
            && lineElement.ValueKind == JsonValueKind.Number
            && lineElement.TryGetInt32(out var zeroBasedLine)
            && zeroBasedLine >= 0
            && zeroBasedLine < int.MaxValue)
            line = zeroBasedLine + 1;

        return new ExternalToolFinding(
            SeverityLevel: severity,
            RuleId: ruleId,
            Message: message,
            Path: path,
            Line: line);
    }

    private static string? GetString(JsonElement element, ReadOnlySpan<byte> name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string SingleLine(string message)
        => message.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
