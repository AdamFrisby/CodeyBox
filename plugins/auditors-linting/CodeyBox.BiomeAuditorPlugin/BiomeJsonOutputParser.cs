using System.Text.Json;
using CodeyBox.PluginSdk.Tools;
using static CodeyBox.PluginSdk.Tools.ExternalToolJsonHelpers;

namespace CodeyBox.BiomeAuditorPlugin;

/// <summary>
/// Parses Biome's built-in <c>--reporter=json</c> report —
/// <c>{ "diagnostics": [ { "severity", "message", "category", "location" } ], "summary": { … } }</c> —
/// into <see cref="ExternalToolFinding"/> records. Verified against Biome v2.5.14:
/// <c>biome lint --reporter=json</c> emits one JSON object on stdout whose
/// <c>diagnostics</c> array carries one entry per diagnostic; the human-readable
/// summary goes to stderr and never affects parsing.
///
/// <para>Severity is kept in Biome's own vocabulary (<c>"error"</c>,
/// <c>"warning"</c>, <c>"info"</c>) and <see cref="ExternalToolAuditorBase"/>
/// maps it through the auditor's declared
/// <see cref="ExternalToolSeverityMapping"/>; raw levels never reach findings.
/// The rule id is the diagnostic <c>category</c> (e.g.
/// <c>lint/suspicious/noDoubleEquals</c>, <c>parse</c> for syntax errors,
/// <c>deserialize</c> for configuration problems); the location is the
/// diagnostic's repo-relative <c>location.path</c> plus
/// <c>location.start.line</c> when positive.</para>
///
/// <para>Malformed output — empty stdout, non-JSON, or a JSON document without
/// a <c>diagnostics</c> array (usage text, a help dump, a crash trace) — throws
/// <see cref="ExternalToolParseException"/>, which the base reports as
/// infrastructure, never as a pass. An empty <c>diagnostics</c> array is a
/// clean verdict, not malformed output.</para>
/// </summary>
internal sealed class BiomeJsonOutputParser : IExternalToolOutputParser
{
    // Same per-document result bound the shared SARIF parser applies.
    private const int MaxResults = SarifToolOutputParser.DefaultMaxResults;
    private const int MessageMaxChars = 512;

    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Stdout))
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced no Biome JSON output on stdout.");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(input.Stdout);
        }
        catch (JsonException ex)
        {
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced output that is not valid Biome JSON: {SingleLine(ex.Message)}.",
                ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("diagnostics"u8, out var diagnostics)
                || diagnostics.ValueKind != JsonValueKind.Array)
            {
                throw new ExternalToolParseException(
                    $"Tool '{input.ToolName}' produced JSON that is not a Biome report "
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
        var ruleId = NullIfWhiteSpace(GetString(diagnostic, "category"u8));
        var message = NullIfWhiteSpace(GetString(diagnostic, "message"u8)) ?? "(no message)";

        string? path = null;
        int? line = null;
        if (diagnostic.TryGetProperty("location"u8, out var location)
            && location.ValueKind == JsonValueKind.Object)
        {
            path = NormalizeFilePath(GetString(location, "path"u8));
            if (location.TryGetProperty("start"u8, out var start)
                && start.ValueKind == JsonValueKind.Object
                && start.TryGetProperty("line"u8, out var lineElement)
                && lineElement.ValueKind == JsonValueKind.Number
                && lineElement.TryGetInt32(out var lineValue)
                && lineValue > 0)
            {
                line = lineValue;
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
        var path = NormalizePath(raw);
        return path.StartsWith("./", StringComparison.Ordinal) ? path[2..] : path;
    }
}
