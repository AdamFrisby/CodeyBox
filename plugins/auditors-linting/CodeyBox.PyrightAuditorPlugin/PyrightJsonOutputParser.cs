using System.Text.Json;
using CodeyBox.PluginSdk.Tools;
using static CodeyBox.PluginSdk.Tools.ExternalToolJsonHelpers;

namespace CodeyBox.PyrightAuditorPlugin;

/// <summary>
/// Parses pyright's <c>--outputjson</c> report —
/// <c>{ "version", "time", "generalDiagnostics": [ { "file", "severity",
/// "message", "range"?, "rule"? } ], "summary" }</c> — into
/// <see cref="ExternalToolFinding"/> records.
///
/// <para>Pyright emits <c>file</c> as an absolute path
/// (<c>fileUri.getFilePath()</c> upstream) and, unlike ESLint's
/// <c>json-with-metadata</c>, the report embeds no <c>cwd</c>, so paths are
/// relativized against <see cref="ExternalToolParseInput.ScanRoot"/> — the
/// directory the scan actually ran in, resolved by the auditor per run and
/// carried on the parse input so this parser stays stateless and shared
/// across concurrent audits. Diagnostics outside the scan root (e.g. a
/// typeshed stub or an absolute path an operator added via
/// <c>ExtraArguments</c>) are re-marked with an explicit <c>file://</c>
/// scheme on their absolute path: the base's finding-path normalization
/// trims a bare leading <c>/</c>, which would otherwise de-root
/// <c>/opt/typeshed/x.pyi</c> into <c>opt/typeshed/x.pyi</c> — a string that
/// reads as a repository path and could accidentally match repo-relative
/// <c>ExcludePaths</c> entries. The marker keeps out-of-tree evidence
/// distinguishable in the reported location.</para>
///
/// <para>Range lines are 0-based in the report; findings carry the 1-based
/// line. A diagnostic without <c>range</c> (config- or project-level) keeps a
/// file-only location. Severity is kept in pyright's own vocabulary
/// (<c>"error"</c>, <c>"warning"</c>, <c>"information"</c>) and
/// <see cref="ExternalToolAuditorBase"/> maps it through the auditor's
/// declared <see cref="ExternalToolSeverityMapping"/>; raw levels never reach
/// findings.</para>
///
/// <para>Malformed output — empty stdout, non-JSON, or a JSON document without
/// a <c>generalDiagnostics</c> array — throws
/// <see cref="ExternalToolParseException"/>, which the base reports as
/// infrastructure, never as a pass.</para>
/// </summary>
internal sealed class PyrightJsonOutputParser : IExternalToolOutputParser
{
    // Same per-document result bound the shared SARIF parser applies.
    private const int MaxResults = SarifToolOutputParser.DefaultMaxResults;

    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Stdout))
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced no pyright JSON output on stdout.");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(input.Stdout);
        }
        catch (JsonException ex)
        {
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced output that is not valid pyright JSON: {SingleLine(ex.Message)}.",
                ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("generalDiagnostics"u8, out var diagnostics)
                || diagnostics.ValueKind != JsonValueKind.Array)
                throw new ExternalToolParseException(
                    $"Tool '{input.ToolName}' produced a JSON document without a pyright 'generalDiagnostics' array.");

            var findings = new List<ExternalToolFinding>();
            foreach (var diagnostic in diagnostics.EnumerateArray())
            {
                if (findings.Count >= MaxResults)
                    break;
                if (diagnostic.ValueKind == JsonValueKind.Object)
                    findings.Add(ParseDiagnostic(diagnostic, input));
            }

            return findings;
        }
    }

    private static ExternalToolFinding ParseDiagnostic(JsonElement diagnostic, ExternalToolParseInput input)
    {
        int? line = null;
        if (diagnostic.TryGetProperty("range"u8, out var range)
            && range.ValueKind == JsonValueKind.Object
            && range.TryGetProperty("start"u8, out var start)
            && start.ValueKind == JsonValueKind.Object
            && start.TryGetProperty("line"u8, out var lineElement)
            && lineElement.ValueKind == JsonValueKind.Number
            && lineElement.TryGetInt32(out var zeroBasedLine)
            && zeroBasedLine >= 0)
            line = zeroBasedLine + 1;

        return new ExternalToolFinding(
            SeverityLevel: NullIfWhiteSpace(GetString(diagnostic, "severity"u8)),
            RuleId: NullIfWhiteSpace(GetString(diagnostic, "rule"u8)),
            Message: NullIfWhiteSpace(GetString(diagnostic, "message"u8)) ?? "(no message)",
            Path: NormalizeReportedPath(
                GetString(diagnostic, "file"u8), input.ScanRoot, input.WorkingDirectory),
            Line: line);
    }
}
