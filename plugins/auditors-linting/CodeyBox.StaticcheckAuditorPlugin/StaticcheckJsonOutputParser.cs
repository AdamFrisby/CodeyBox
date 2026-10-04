using System.Text.Json;
using CodeyBox.PluginSdk.Tools;

namespace CodeyBox.StaticcheckAuditorPlugin;

/// <summary>
/// Parses staticcheck's JSON report — emitted by
/// <c>staticcheck -f json ./...</c> as a stream of one JSON object per line
/// (not an array), each carrying <c>code</c> (e.g. <c>SA4006</c>,
/// <c>S1039</c>, <c>ST1000</c>), <c>severity</c> (<c>error</c>,
/// <c>warning</c>, or <c>ignored</c> — decided by the tool's <c>-fail</c>
/// flag), <c>location.file</c>/<c>location.line</c>, and <c>message</c> —
/// into <see cref="ExternalToolFinding"/> records. Verified against
/// staticcheck 2025.1.1: an empty stdout is a clean verdict, not malformed
/// output.
///
/// <para>Severity is kept in the tool's own vocabulary and
/// <see cref="ExternalToolAuditorBase"/> maps it through the auditor's
/// declared <see cref="ExternalToolSeverityMapping"/>; raw levels never
/// reach findings. The rule id is the diagnostic's <c>code</c>. The
/// location is relativized from the absolute path staticcheck reports
/// (the tool runs in the worktree root and prints slash-absolute file
/// names) onto a repository-relative path through the shared
/// <see cref="ExternalToolJsonHelpers.NormalizeReportedPath"/> policy, so
/// finding locations and <c>ExcludePaths</c> behave like every other
/// auditor's. Diagnostics the tool cannot locate — the <c>compile</c>
/// records for package-load failures, whose <c>location.file</c> is empty
/// — surface with a null location rather than a guessed one.</para>
///
/// <para>Malformed output — empty stdout when the exit code says findings
/// were reported (a crash before the report is written), a line that is
/// not a JSON object, or a JSON value that is not a diagnostic object —
/// throws <see cref="ExternalToolParseException"/>, which the base reports
/// as infrastructure, never as a pass.</para>
/// </summary>
internal sealed class StaticcheckJsonOutputParser : IExternalToolOutputParser
{
    // Same per-document result bound the shared SARIF parser applies.
    private const int MaxResults = SarifToolOutputParser.DefaultMaxResults;
    private const int MessageMaxChars = 512;

    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Stdout))
        {
            // A clean tree prints nothing. A non-zero findings-producing
            // exit with no report means the tool died before writing one —
            // fail closed as infrastructure, never as a clean pass.
            if (input.ExitCode == 0)
                return [];
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' exited {input.ExitCode} but produced no staticcheck JSON output on stdout.");
        }

        var findings = new List<ExternalToolFinding>();
        foreach (var line in input.Stdout.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            if (findings.Count >= MaxResults)
                break;
            findings.Add(ParseDiagnostic(input.ToolName, line, input.ScanRoot, input.WorkingDirectory));
        }

        return findings;
    }

    private static ExternalToolFinding ParseDiagnostic(
        string toolName, string line, string? scanRoot, string? workingDirectory)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException ex)
        {
            throw new ExternalToolParseException(
                $"Tool '{toolName}' produced output that is not valid staticcheck JSON: {ExternalToolJsonHelpers.SingleLine(ex.Message)}.",
                ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ExternalToolParseException(
                    $"Tool '{toolName}' produced a staticcheck JSON line that is not a diagnostic object.");
            }

            var root = document.RootElement;
            var severity = ExternalToolJsonHelpers.NullIfWhiteSpace(GetString(root, "severity"));
            var ruleId = ExternalToolJsonHelpers.NullIfWhiteSpace(GetString(root, "code"));
            var message = ExternalToolJsonHelpers.NullIfWhiteSpace(GetString(root, "message"))
                ?? "(no message)";

            string? path = null;
            int? parsedLine = null;
            if (root.TryGetProperty("location", out var location)
                && location.ValueKind == JsonValueKind.Object)
            {
                path = ExternalToolJsonHelpers.NormalizeReportedPath(
                    GetString(location, "file"), scanRoot, workingDirectory);
                if (location.TryGetProperty("line", out var lineElement)
                    && lineElement.ValueKind == JsonValueKind.Number
                    && lineElement.TryGetInt32(out var lineValue)
                    && lineValue > 0)
                {
                    parsedLine = lineValue;
                }
            }

            return new ExternalToolFinding(
                SeverityLevel: severity,
                RuleId: ruleId,
                Message: ExternalToolJsonHelpers.Truncate(message, MessageMaxChars),
                Path: path,
                Line: parsedLine);
        }
    }

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
