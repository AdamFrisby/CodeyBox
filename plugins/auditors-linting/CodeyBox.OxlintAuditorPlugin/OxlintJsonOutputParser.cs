using System.Text;
using System.Text.Json;
using CodeyBox.PluginSdk.Tools;

namespace CodeyBox.OxlintAuditorPlugin;

/// <summary>
/// Parses oxlint's built-in <c>--format json</c> report —
/// <c>{ "diagnostics": [ { "message", "code", "severity", "filename", "labels" } ], "number_of_files": … }</c> —
/// into <see cref="ExternalToolFinding"/> records. Verified against oxlint v1.85.0:
/// <c>oxlint --format json</c> emits one JSON object on stdout whose
/// <c>diagnostics</c> array carries one entry per diagnostic; nothing is
/// written to stderr on a completed scan.
///
/// <para>Severity is kept in oxlint's own vocabulary (<c>"error"</c>,
/// <c>"warning"</c> — and <c>"advice"</c> for diagnostics the tool may emit
/// in other configurations) and <see cref="ExternalToolAuditorBase"/>
/// maps it through the auditor's declared
/// <see cref="ExternalToolSeverityMapping"/>; raw levels never reach findings.
/// The rule id is the diagnostic <c>code</c> (e.g.
/// <c>eslint(no-debugger)</c>, <c>typescript(no-explicit-any)</c>); syntax
/// errors carry no <c>code</c> and are reported with a null rule id rather
/// than an invented one. The location is the diagnostic's repo-relative
/// <c>filename</c> plus the first label's <c>span.line</c> when positive —
/// oxlint emits <c>filename</c> relative to the process working directory,
/// which the auditor sets to the audited worktree root, so no
/// relativization is needed.</para>
///
/// <para>Malformed output — empty stdout, non-JSON, or a JSON document without
/// a <c>diagnostics</c> array (usage text such as
/// <c>Error: `--bogus-flag` is not expected in this context</c>, a help dump,
/// a crash trace) — throws <see cref="ExternalToolParseException"/>, which
/// the base reports as infrastructure, never as a pass. An empty
/// <c>diagnostics</c> array is a clean verdict, not malformed output.</para>
/// </summary>
internal sealed class OxlintJsonOutputParser : IExternalToolOutputParser
{
    // Same per-document result bound the shared SARIF parser applies.
    private const int MaxResults = SarifToolOutputParser.DefaultMaxResults;
    private const int MessageMaxChars = 512;

    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Stdout))
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced no oxlint JSON output on stdout.");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(input.Stdout);
        }
        catch (JsonException ex)
        {
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced output that is not valid oxlint JSON: {SingleLine(ex.Message)}.",
                ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("diagnostics"u8, out var diagnostics)
                || diagnostics.ValueKind != JsonValueKind.Array)
            {
                throw new ExternalToolParseException(
                    $"Tool '{input.ToolName}' produced JSON that is not an oxlint report "
                    + "(missing the 'diagnostics' array).");
            }

            var findings = new List<ExternalToolFinding>();
            foreach (var diagnostic in diagnostics.EnumerateArray())
            {
                if (findings.Count >= MaxResults)
                    break;
                if (diagnostic.ValueKind == JsonValueKind.Object)
                    findings.Add(ParseDiagnostic(diagnostic));
            }

            return findings;
        }
    }

    private static ExternalToolFinding ParseDiagnostic(JsonElement diagnostic)
    {
        var severity = NullIfWhiteSpace(GetString(diagnostic, "severity"u8));
        var ruleId = NullIfWhiteSpace(GetString(diagnostic, "code"u8));
        var message = NullIfWhiteSpace(GetString(diagnostic, "message"u8)) ?? "(no message)";
        var path = NormalizeFilePath(GetString(diagnostic, "filename"u8));

        int? line = null;
        if (diagnostic.TryGetProperty("labels"u8, out var labels)
            && labels.ValueKind == JsonValueKind.Array)
        {
            foreach (var label in labels.EnumerateArray())
            {
                if (label.ValueKind != JsonValueKind.Object)
                    continue;
                if (label.TryGetProperty("span"u8, out var span)
                    && span.ValueKind == JsonValueKind.Object
                    && span.TryGetProperty("line"u8, out var lineElement)
                    && lineElement.ValueKind == JsonValueKind.Number
                    && lineElement.TryGetInt32(out var lineValue)
                    && lineValue > 0)
                {
                    line = lineValue;
                    break;
                }
            }
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

    private static string? GetString(JsonElement element, ReadOnlySpan<byte> name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

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
