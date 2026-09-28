using System.Text.Json;
using CodeyBox.PluginSdk.Tools;
using static CodeyBox.PluginSdk.Tools.ExternalToolJsonHelpers;

namespace CodeyBox.PsalmAuditorPlugin;

/// <summary>
/// Parses psalm's <c>--output-format=json</c> report — a flat JSON array of
/// <c>IssueData</c> objects carrying <c>severity</c> (<c>"error"</c> or
/// <c>"info"</c>), <c>type</c> (the issue/rule identifier, e.g.
/// <c>InvalidReturnType</c>), <c>message</c>, <c>file_name</c> (path
/// relative to psalm's base directory), <c>file_path</c> (absolute), and
/// <c>line_from</c> (1-based) — into <see cref="ExternalToolFinding"/>
/// records.
///
/// <para>Locations prefer <c>file_path</c> relativized against
/// <see cref="ExternalToolParseInput.ScanRoot"/> — the directory the scan
/// actually ran in, resolved by the auditor per run and carried on the
/// parse input so this parser stays stateless and shared across concurrent
/// audits — because <c>file_name</c> is relative to psalm's base directory,
/// which a <c>resolveFromConfigFile</c> config outside the worktree root
/// can detach from the audited tree. Both fields go through the shared
/// <see cref="ExternalToolJsonHelpers.RelativizeOrMarkFileUri"/>: an issue
/// whose path sits outside the scan root (a composer stub, a
/// <c>projectFiles</c>/<c>--file</c> target outside the repo, an absolute
/// or <c>..</c>-escaping <c>file_name</c>) keeps it re-marked with an
/// explicit <c>file://</c> scheme, because the base's finding-path
/// normalization trims a bare leading <c>/</c> — which would otherwise
/// de-root <c>/opt/…</c> into a string that reads as a repository path
/// and could match repo-relative <c>ExcludePaths</c> entries. Issues with
/// neither field (config-level issues such as
/// <c>UnusedBaselineEntry</c>) carry no location.</para>
///
/// <para>Lines are already 1-based in the report (<c>line_from</c>);
/// <c>0</c> (config-level issues) maps to no line. Severity is kept in
/// psalm's own vocabulary (<c>"error"</c>/<c>"info"</c>) and
/// <see cref="ExternalToolAuditorBase"/> maps it through the auditor's
/// declared <see cref="ExternalToolSeverityMapping"/>; raw levels never
/// reach findings.</para>
///
/// <para>Malformed output — empty stdout, non-JSON, or a JSON document
/// that is not an array — throws <see cref="ExternalToolParseException"/>,
/// which the base reports as infrastructure, never as a pass.</para>
/// </summary>
internal sealed class PsalmJsonOutputParser : IExternalToolOutputParser
{
    // Same per-document result bound the shared SARIF parser applies.
    private const int MaxResults = SarifToolOutputParser.DefaultMaxResults;

    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Stdout))
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced no psalm JSON output on stdout.");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(input.Stdout);
        }
        catch (JsonException ex)
        {
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced output that is not valid psalm JSON: {SingleLine(ex.Message)}.",
                ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new ExternalToolParseException(
                    $"Tool '{input.ToolName}' produced a JSON document that is not a psalm issue array.");

            var findings = new List<ExternalToolFinding>();
            foreach (var issue in document.RootElement.EnumerateArray())
            {
                if (findings.Count >= MaxResults)
                    break;
                if (issue.ValueKind == JsonValueKind.Object)
                    findings.Add(ParseIssue(issue, input.ScanRoot));
            }

            return findings;
        }
    }

    private static ExternalToolFinding ParseIssue(JsonElement issue, string? scanRoot)
    {
        int? line = null;
        if (issue.TryGetProperty("line_from"u8, out var lineElement)
            && lineElement.ValueKind == JsonValueKind.Number
            && lineElement.TryGetInt32(out var lineFrom)
            && lineFrom > 0)
            line = lineFrom;

        return new ExternalToolFinding(
            SeverityLevel: NullIfWhiteSpace(GetString(issue, "severity"u8)),
            RuleId: NullIfWhiteSpace(GetString(issue, "type"u8)),
            Message: NullIfWhiteSpace(GetString(issue, "message"u8)) ?? "(no message)",
            Path: RelativizeOrMarkFileUri(
                    GetString(issue, "file_path"u8),
                    scanRoot)
                ?? RelativizeOrMarkFileUri(
                    GetString(issue, "file_name"u8),
                    scanRoot),
            Line: line);
    }
}
