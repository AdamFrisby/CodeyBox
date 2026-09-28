using System.Text.Json;
using CodeyBox.PluginSdk.Tools;
using static CodeyBox.PluginSdk.Tools.ExternalToolJsonHelpers;

namespace CodeyBox.BufAuditorPlugin;

/// <summary>
/// Parses <c>buf breaking --error-format=json</c> output — one JSON object
/// per line, each carrying <c>type</c> (the breaking rule id, e.g.
/// <c>FIELD_SAME_TYPE</c>), <c>message</c>, an optional <c>path</c>, and an
/// optional <c>start_line</c> — into <see cref="ExternalToolFinding"/>
/// records. <c>buf breaking</c> reports no severity per violation: every
/// emitted line is a breaking change, so findings carry a null severity
/// level and the auditor's declared mapping resolves them all to
/// <see cref="CodeyBox.Core.AuditSeverity.Error"/>.
///
/// <para>A finding without a <c>path</c> (e.g. <c>FILE_NO_DELETE</c>, which
/// names the deleted file only inside its message) is still reported, with
/// a null location — the message carries the file identity. Paths are never
/// reconstructed by parsing human-readable messages.</para>
///
/// <para>Malformed output — a non-blank line that is not a JSON object, or
/// an empty report on a breaking-changes exit — throws
/// <see cref="ExternalToolParseException"/>, which the base reports as
/// infrastructure, never as a pass. This is what distinguishes a completed
/// scan (exit 0/100 with a matching report) from a run failure (exit 1 with
/// a plain-text <c>Failure: …</c> diagnostic and no report).</para>
/// </summary>
internal sealed class BufBreakingJsonParser : IExternalToolOutputParser
{
    // Same per-document result bound the shared SARIF parser applies.
    private const int MaxResults = SarifToolOutputParser.DefaultMaxResults;

    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Stdout))
        {
            if (input.ExitCode == BufAuditor.BreakingFindingsExitCode)
                throw new ExternalToolParseException(
                    $"Tool '{input.ToolName}' exited {BufAuditor.BreakingFindingsExitCode} "
                    + "(breaking changes found) but produced no JSON report on stdout.");
            return [];
        }

        var findings = new List<ExternalToolFinding>();
        foreach (var line in input.Stdout.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            if (findings.Count >= MaxResults)
                break;
            findings.Add(ParseLine(input.ToolName, line));
        }

        return findings;
    }

    private static ExternalToolFinding ParseLine(string toolName, string line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException ex)
        {
            throw new ExternalToolParseException(
                $"Tool '{toolName}' produced output that is not valid buf JSON: {SingleLine(ex.Message)}.",
                ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new ExternalToolParseException(
                    $"Tool '{toolName}' produced a JSON value that is not a buf violation object.");

            var root = document.RootElement;
            var ruleId = NullIfWhiteSpace(GetString(root, "type"u8));
            var message = NullIfWhiteSpace(GetString(root, "message"u8)) ?? "(no message)";
            var path = NullIfWhiteSpace(GetString(root, "path"u8));
            int? startLine = null;
            if (root.TryGetProperty("start_line"u8, out var startLineElement)
                && startLineElement.ValueKind == JsonValueKind.Number
                && startLineElement.TryGetInt32(out var startLineValue)
                && startLineValue > 0)
                startLine = startLineValue;

            return new ExternalToolFinding(
                SeverityLevel: null,
                RuleId: ruleId,
                Message: message,
                Path: path,
                Line: startLine);
        }
    }
}
