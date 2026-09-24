using System.Globalization;
using System.Text.Json;
using CodeyBox.PluginSdk.Tools;

namespace CodeyBox.EslintAuditorPlugin;

/// <summary>
/// Parses ESLint's built-in <c>--format json-with-metadata</c> report —
/// <c>{ "results": [ { "filePath", "messages"[] } ], "metadata": { "cwd",
/// "rulesMeta" } }</c> — into <see cref="ExternalToolFinding"/> records. The
/// metadata variant is used rather than plain <c>json</c> because it embeds
/// the process <c>cwd</c> in the report itself: ESLint emits absolute
/// <c>filePath</c> values, and the report's own <c>metadata.cwd</c> is what
/// they are relativized against — no out-of-band state. A bare results array
/// (plain <c>--format json</c>, e.g. if an operator override changes the
/// format) is still parsed, with absolute paths left as-is.
///
/// <para>Severity is kept in ESLint's own vocabulary — the numeric level
/// (<c>"1"</c> warn, <c>"2"</c> error) or <c>"fatal"</c> for parse errors —
/// and <see cref="ExternalToolAuditorBase"/> maps it through the auditor's
/// declared <see cref="ExternalToolSeverityMapping"/>; raw levels never reach
/// findings.</para>
///
/// <para>Malformed output — empty stdout, non-JSON, or a JSON document that is
/// neither a results array nor a results/metadata object — throws
/// <see cref="ExternalToolParseException"/>, which the base reports as
/// infrastructure, never as a pass.</para>
/// </summary>
internal sealed class EslintJsonOutputParser : IExternalToolOutputParser
{
    // Same per-document result bound the shared SARIF parser applies.
    private const int MaxResults = SarifToolOutputParser.DefaultMaxResults;

    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Stdout))
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced no ESLint JSON output on stdout.");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(input.Stdout);
        }
        catch (JsonException ex)
        {
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced output that is not valid ESLint JSON: {SingleLine(ex.Message)}.",
                ex);
        }

        using (document)
        {
            string? scanRoot = null;
            JsonElement results;
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (!document.RootElement.TryGetProperty("results"u8, out results)
                    || results.ValueKind != JsonValueKind.Array)
                    throw new ExternalToolParseException(
                        $"Tool '{input.ToolName}' produced a JSON object without an ESLint 'results' array.");
                if (document.RootElement.TryGetProperty("metadata"u8, out var metadata)
                    && metadata.ValueKind == JsonValueKind.Object)
                    scanRoot = GetString(metadata, "cwd"u8);
            }
            else if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                results = document.RootElement;
            }
            else
            {
                throw new ExternalToolParseException(
                    $"Tool '{input.ToolName}' produced JSON that is not an ESLint report.");
            }

            var findings = new List<ExternalToolFinding>();
            foreach (var file in results.EnumerateArray())
            {
                if (findings.Count >= MaxResults)
                    break;
                if (file.ValueKind != JsonValueKind.Object
                    || !file.TryGetProperty("messages"u8, out var messages)
                    || messages.ValueKind != JsonValueKind.Array)
                    continue;

                var path = NormalizeFilePath(GetString(file, "filePath"u8), scanRoot);
                foreach (var message in messages.EnumerateArray())
                {
                    if (findings.Count >= MaxResults)
                        break;
                    if (message.ValueKind == JsonValueKind.Object)
                        findings.Add(ParseMessage(message, path));
                }
            }

            return findings;
        }
    }

    private static ExternalToolFinding ParseMessage(JsonElement message, string? path)
    {
        // fatal:true marks a parse failure (no rule fired); ESLint still
        // reports severity 2, but the distinct level keeps the tool's own
        // vocabulary so a file that could not be parsed at all stays visible.
        var fatal = message.TryGetProperty("fatal"u8, out var fatalElement)
            && fatalElement.ValueKind == JsonValueKind.True;
        string? severity = null;
        if (fatal)
            severity = "fatal";
        else if (message.TryGetProperty("severity"u8, out var severityElement)
            && severityElement.ValueKind == JsonValueKind.Number
            && severityElement.TryGetInt32(out var severityValue))
            severity = severityValue.ToString(CultureInfo.InvariantCulture);

        int? line = null;
        if (message.TryGetProperty("line"u8, out var lineElement)
            && lineElement.ValueKind == JsonValueKind.Number
            && lineElement.TryGetInt32(out var lineValue)
            && lineValue > 0)
            line = lineValue;

        return new ExternalToolFinding(
            SeverityLevel: severity,
            RuleId: NullIfWhiteSpace(GetString(message, "ruleId"u8)),
            Message: NullIfWhiteSpace(GetString(message, "message"u8)) ?? "(no message)",
            Path: path,
            Line: line);
    }

    private static string? NormalizeFilePath(string? raw, string? scanRoot)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var path = raw.Trim().Replace('\\', '/');
        var root = scanRoot?.Trim().Replace('\\', '/').TrimEnd('/');
        if (!string.IsNullOrEmpty(root)
            && path.Length > root.Length + 1
            && path.StartsWith(root + "/", StringComparison.Ordinal))
            path = path[(root.Length + 1)..];
        return path;
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
