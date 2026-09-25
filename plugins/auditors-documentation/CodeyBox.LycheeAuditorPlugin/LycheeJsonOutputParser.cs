using System.Text.Json;
using CodeyBox.PluginSdk.Tools;

namespace CodeyBox.LycheeAuditorPlugin;

/// <summary>
/// Parses lychee's <c>--format json</c> status report into
/// <see cref="ExternalToolFinding"/> records. The report is a stats object
/// whose failure detail lives in two maps keyed by input source (the file the
/// link was extracted from, e.g. <c>"./docs/guide.md"</c>, or <c>"stdin"</c>/a
/// URL for non-file inputs):
///
/// <code>
/// "error_map":   { "&lt;source&gt;": [ { "url": …, "status": { "text": …, "code": 404, "details": … }, "span": { "line": …, "column": … } } ] },
/// "timeout_map": { "&lt;source&gt;": [ … same entry shape … ] }
/// </code>
///
/// Both maps are populated on every run (no verbosity flag needed) and both
/// drive lychee's exit code: <c>error_map</c> entries are confirmed link
/// failures, <c>timeout_map</c> entries are requests that timed out. Each
/// keeps its own vocabulary level (<c>"error"</c> / <c>"timeout"</c>) so the
/// auditor's declared <see cref="ExternalToolSeverityMapping"/> decides their
/// meaning; raw levels never reach findings. <c>success_map</c>,
/// <c>redirect_map</c>, <c>excluded_map</c>, and <c>suggestion_map</c> are not
/// failures — they never become findings.
///
/// <para>Rule ids are synthesized because lychee reports none:
/// <see cref="LycheeAuditor.BrokenLinkRuleId"/> for <c>error_map</c> entries
/// and <see cref="LycheeAuditor.TimeoutRuleId"/> for <c>timeout_map</c>
/// entries, so the shared <c>IncludedRules</c>/<c>ExcludedRules</c>
/// configuration works on a stable identity.</para>
///
/// <para><c>span.line</c> is carried through as the finding line when lychee
/// supplies it. Source keys are normalized by stripping the walker-emitted
/// <c>"./"</c> prefix so the shared <c>ExcludePaths</c> prefix matching lines
/// up with repository-relative paths.</para>
///
/// <para>Malformed output — empty stdout, non-JSON, or a JSON object without
/// lychee's <c>total</c>/<c>error_map</c> shape (e.g. a usage dump or a
/// report in a different format after an <c>ExtraArguments</c>
/// <c>--format</c> override) — throws <see cref="ExternalToolParseException"/>,
/// which the base reports as infrastructure, never as a pass.</para>
/// </summary>
internal sealed class LycheeJsonOutputParser : IExternalToolOutputParser
{
    // Same per-document result bound the shared SARIF parser applies.
    private const int MaxResults = SarifToolOutputParser.DefaultMaxResults;
    private const int MessageMaxChars = 512;

    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Stdout))
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced no lychee JSON output on stdout.");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(input.Stdout);
        }
        catch (JsonException ex)
        {
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced output that is not valid JSON: {SingleLine(ex.Message)}.",
                ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("total"u8, out var total)
                || total.ValueKind != JsonValueKind.Number
                || !root.TryGetProperty("error_map"u8, out var errorMap)
                || errorMap.ValueKind != JsonValueKind.Object)
                throw new ExternalToolParseException(
                    $"Tool '{input.ToolName}' produced JSON that is not a lychee report "
                    + "(missing the 'total'/'error_map' stats shape).");

            var findings = new List<ExternalToolFinding>();
            CollectMap(errorMap, LycheeAuditor.BrokenLinkRuleId, "error", findings);
            if (root.TryGetProperty("timeout_map"u8, out var timeoutMap)
                && timeoutMap.ValueKind == JsonValueKind.Object)
                CollectMap(timeoutMap, LycheeAuditor.TimeoutRuleId, "timeout", findings);
            return findings;
        }
    }

    private static void CollectMap(
        JsonElement map,
        string ruleId,
        string level,
        List<ExternalToolFinding> findings)
    {
        foreach (var property in map.EnumerateObject())
        {
            if (findings.Count >= MaxResults)
                return;
            if (property.Value.ValueKind != JsonValueKind.Array)
                continue;

            var path = NormalizeSourcePath(property.Name);
            foreach (var entry in property.Value.EnumerateArray())
            {
                if (findings.Count >= MaxResults)
                    return;
                if (entry.ValueKind == JsonValueKind.Object)
                    findings.Add(ParseEntry(entry, ruleId, level, path));
            }
        }
    }

    private static ExternalToolFinding ParseEntry(
        JsonElement entry,
        string ruleId,
        string level,
        string? path)
    {
        var url = GetString(entry, "url"u8);
        var statusText = GetStatusText(entry);

        int? line = null;
        if (entry.TryGetProperty("span"u8, out var span)
            && span.ValueKind == JsonValueKind.Object
            && span.TryGetProperty("line"u8, out var lineElement)
            && lineElement.ValueKind == JsonValueKind.Number
            && lineElement.TryGetInt32(out var lineValue)
            && lineValue > 0)
            line = lineValue;

        var message = (url ?? "(unknown url)") + " — " + (statusText ?? "link check failed");
        return new ExternalToolFinding(
            SeverityLevel: level,
            RuleId: ruleId,
            Message: message.Length > MessageMaxChars ? message[..MessageMaxChars] + "…" : message,
            Path: path,
            Line: line);
    }

    private static string? GetStatusText(JsonElement entry)
    {
        if (!entry.TryGetProperty("status"u8, out var status)
            || status.ValueKind != JsonValueKind.Object)
            return null;

        var text = GetString(status, "text"u8);
        var details = GetString(status, "details"u8);
        if (text is null)
            return details;
        return details is null ? text : text + "; " + details;
    }

    private static string? GetString(JsonElement element, ReadOnlySpan<byte> name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? NullIfWhiteSpace(value.GetString())
            : null;

    // Input sources arrive as the walker reported them ("./docs/guide.md",
    // "README.md", "stdin", a URL for remote inputs). Strip the "./" prefix so
    // ExcludePaths prefix matching sees repository-relative paths.
    private static string? NormalizeSourcePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var path = raw.Trim().Replace('\\', '/');
        while (path.StartsWith("./", StringComparison.Ordinal))
            path = path[2..];
        return path.Length == 0 ? null : path;
    }

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string SingleLine(string message)
        => message.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
