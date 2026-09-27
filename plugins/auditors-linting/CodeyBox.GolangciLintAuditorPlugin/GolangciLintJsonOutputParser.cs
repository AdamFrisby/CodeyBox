using System.Text;
using System.Text.Json;
using CodeyBox.PluginSdk.Tools;

namespace CodeyBox.GolangciLintAuditorPlugin;

/// <summary>
/// Parses golangci-lint's native JSON report — emitted by
/// <c>golangci-lint run --output.json.path stdout</c> as
/// <c>{ "Issues": [ { "FromLinter", "Text", "Severity",
/// "Pos": { "Filename", "Line", "Column" } } ], "Report": { … } }</c> —
/// into <see cref="ExternalToolFinding"/> records. Verified against
/// golangci-lint v2.14.0: the report is a single JSON object on stdout with
/// a capitalized <c>Issues</c> array (lowercase <c>issues</c> is accepted
/// too); an empty array is a clean verdict, not malformed output.
///
/// <para>Severity is kept in the tool's own vocabulary — the issue's
/// <c>Severity</c> string, which is empty unless the audited repository's
/// (or the operator's) <c>severity</c> configuration assigns one — and
/// <see cref="ExternalToolAuditorBase"/> maps it through the auditor's
/// declared <see cref="ExternalToolSeverityMapping"/>; raw levels never
/// reach findings. The rule id is the issue's <c>FromLinter</c> (e.g.
/// <c>errcheck</c>, <c>govet</c>, <c>staticcheck</c>); the location is the
/// issue's repo-relative <c>Pos.Filename</c> plus <c>Pos.Line</c> when
/// positive — golangci-lint emits paths relative to the process working
/// directory, which the auditor sets to the audited worktree root, so no
/// relativization is needed.</para>
///
/// <para>Malformed output — empty stdout, non-JSON (usage text such as
/// <c>Error: unknown flag</c>, a help dump, a crash trace), a JSON document
/// without an <c>Issues</c> array, or JSON with a trailing statistics line
/// (what <c>--show-stats</c> appends to stdout when left enabled) — throws
/// <see cref="ExternalToolParseException"/>, which the base reports as
/// infrastructure, never as a pass.</para>
/// </summary>
internal sealed class GolangciLintJsonOutputParser : IExternalToolOutputParser
{
    // Same per-document result bound the shared SARIF parser applies.
    private const int MaxResults = SarifToolOutputParser.DefaultMaxResults;
    private const int MessageMaxChars = 512;

    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Stdout))
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced no golangci-lint JSON output on stdout.");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(input.Stdout);
        }
        catch (JsonException ex)
        {
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced output that is not valid golangci-lint JSON: {SingleLine(ex.Message)}.",
                ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !TryGetProperty(document.RootElement, "Issues", "issues", out var issues)
                || issues.ValueKind != JsonValueKind.Array)
            {
                throw new ExternalToolParseException(
                    $"Tool '{input.ToolName}' produced JSON that is not a golangci-lint report "
                    + "(missing the 'Issues' array).");
            }

            var findings = new List<ExternalToolFinding>();
            foreach (var issue in issues.EnumerateArray())
            {
                if (findings.Count >= MaxResults)
                    break;
                if (issue.ValueKind == JsonValueKind.Object)
                    findings.Add(ParseIssue(issue));
            }

            return findings;
        }
    }

    private static ExternalToolFinding ParseIssue(JsonElement issue)
    {
        var severity = NullIfWhiteSpace(GetString(issue, "Severity", "severity"));
        var ruleId = NullIfWhiteSpace(GetString(issue, "FromLinter", "fromLinter", "fromlinter"));
        var message = NullIfWhiteSpace(GetString(issue, "Text", "text")) ?? "(no message)";

        string? path = null;
        int? line = null;
        if (TryGetProperty(issue, "Pos", "pos", out var pos)
            && pos.ValueKind == JsonValueKind.Object)
        {
            path = NormalizeFilePath(GetString(pos, "Filename", "filename"));
            line = GetPositiveInt(pos, "Line", "line");
        }

        return new ExternalToolFinding(
            SeverityLevel: severity,
            RuleId: ruleId,
            Message: Truncate(message, MessageMaxChars),
            Path: path,
            Line: line);
    }

    private static string? NormalizeFilePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var path = raw.Trim().Replace('\\', '/');
        return path.StartsWith("./", StringComparison.Ordinal) ? path[2..] : path;
    }

    private static string? GetString(JsonElement element, string first, string? second = null, string? third = null)
    {
        if (element.TryGetProperty(first, out var value) && value.ValueKind == JsonValueKind.String)
            return value.GetString();
        if (second is not null
            && element.TryGetProperty(second, out value)
            && value.ValueKind == JsonValueKind.String)
            return value.GetString();
        if (third is not null
            && element.TryGetProperty(third, out value)
            && value.ValueKind == JsonValueKind.String)
            return value.GetString();

        return null;
    }

    private static bool TryGetProperty(
        JsonElement element,
        string first,
        string second,
        out JsonElement value)
    {
        if (element.TryGetProperty(first, out value))
            return true;
        return element.TryGetProperty(second, out value);
    }

    private static int? GetPositiveInt(JsonElement element, string first, string? second = null)
    {
        if (element.TryGetProperty(first, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var parsed)
            && parsed > 0)
            return parsed;
        if (second is not null
            && element.TryGetProperty(second, out value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out parsed)
            && parsed > 0)
            return parsed;

        return null;
    }

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string Truncate(string value, int maxChars)
        => value.Length <= maxChars ? value : value[..maxChars] + "...";

    private static string SingleLine(string message)
    {
        var builder = new StringBuilder(message.Length);
        foreach (var c in message)
            builder.Append(char.IsControl(c) ? ' ' : c);
        return builder.ToString().Trim();
    }
}
