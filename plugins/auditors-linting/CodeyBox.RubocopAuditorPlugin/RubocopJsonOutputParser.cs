using System.Text.Json;
using CodeyBox.PluginSdk.Tools;
using static CodeyBox.PluginSdk.Tools.ExternalToolJsonHelpers;

namespace CodeyBox.RubocopAuditorPlugin;

/// <summary>
/// Parses RuboCop's built-in <c>--format json</c> report —
/// <c>{ "metadata": { "rubocop_version", … }, "files": [ { "path",
/// "offenses": [ { "severity", "message", "cop_name", "location":
/// { "start_line", … } } ] } ], "summary": { "offense_count", … } }</c> —
/// into <see cref="ExternalToolFinding"/> records. The JSON formatter writes
/// only the report to stdout (human-readable diffs, corrected sources, and
/// extension suggestions are suppressed for machine-readable formats), so
/// stdout is the whole report.
///
/// <para>Severity is kept in RuboCop's own vocabulary — one of
/// <c>info</c>, <c>refactor</c>, <c>convention</c>, <c>warning</c>,
/// <c>error</c>, <c>fatal</c> — and <see cref="ExternalToolAuditorBase"/>
/// maps it through the auditor's declared
/// <see cref="ExternalToolSeverityMapping"/>; raw levels never reach
/// findings. The rule id is the cop name (e.g.
/// <c>Style/FrozenStringLiteralComment</c>, <c>Lint/UselessAssignment</c>);
/// the location is the offense's <c>start_line</c> (falling back to the
/// legacy <c>line</c> alias).</para>
///
/// <para>Malformed output — empty stdout, non-JSON, or a JSON document
/// without a RuboCop <c>files</c> array — throws
/// <see cref="ExternalToolParseException"/>, which the base reports as
/// infrastructure, never as a pass. A non-zero findings exit whose stdout
/// yields no offenses — a crash, a truncated report, or an operator
/// <c>--format</c>/<c>--out</c> override that moved the report off stdout —
/// likewise throws: "found problems" and "could not run" must stay
/// distinguishable.</para>
/// </summary>
internal sealed class RubocopJsonOutputParser : IExternalToolOutputParser
{
    // Same per-report result bound the shared SARIF parser applies.
    private const int MaxResults = SarifToolOutputParser.DefaultMaxResults;

    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Stdout))
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced no RuboCop JSON output on stdout.");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(input.Stdout);
        }
        catch (JsonException ex)
        {
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced output that is not valid RuboCop JSON: {SingleLine(ex.Message)}.",
                ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("files"u8, out var files)
                || files.ValueKind != JsonValueKind.Array)
                throw new ExternalToolParseException(
                    $"Tool '{input.ToolName}' produced JSON without a RuboCop 'files' array.");

            var findings = new List<ExternalToolFinding>();
            foreach (var file in files.EnumerateArray())
            {
                if (findings.Count >= MaxResults)
                    break;
                if (file.ValueKind != JsonValueKind.Object
                    || !file.TryGetProperty("offenses"u8, out var offenses)
                    || offenses.ValueKind != JsonValueKind.Array)
                    continue;

                var path = NormalizeFilePath(GetString(file, "path"u8));
                foreach (var offense in offenses.EnumerateArray())
                {
                    if (findings.Count >= MaxResults)
                        break;
                    if (offense.ValueKind == JsonValueKind.Object)
                        findings.Add(ParseOffense(offense, input.ToolName, path));
                }
            }

            // RuboCop exits non-zero from a completed scan only when it found
            // offenses (or hit an error — which emits no JSON); a findings
            // exit with nothing parseable on stdout means the run did not
            // produce its declared report — crash, truncation, or foreign
            // output — not a clean pass.
            if (input.ExitCode != 0 && findings.Count == 0)
                throw new ExternalToolParseException(
                    $"Tool '{input.ToolName}' exited {input.ExitCode} but produced no RuboCop offenses on stdout.");
            return findings;
        }
    }

    private static ExternalToolFinding ParseOffense(JsonElement offense, string toolName, string? path)
    {
        if (!offense.TryGetProperty("message"u8, out var messageElement)
            || messageElement.ValueKind != JsonValueKind.String)
            throw new ExternalToolParseException(
                $"Tool '{toolName}' produced an offense entry without a message string.");

        var message = NullIfWhiteSpace(messageElement.GetString()) ?? "(no message)";

        int? line = null;
        if (offense.TryGetProperty("location"u8, out var location)
            && location.ValueKind == JsonValueKind.Object)
            line = ReadPositiveInt(location, "start_line"u8) ?? ReadPositiveInt(location, "line"u8);

        return new ExternalToolFinding(
            SeverityLevel: NullIfWhiteSpace(GetString(offense, "severity"u8)),
            RuleId: NullIfWhiteSpace(GetString(offense, "cop_name"u8)),
            Message: message,
            Path: path,
            Line: line);
    }

    private static int? ReadPositiveInt(JsonElement element, ReadOnlySpan<byte> name)
        => element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var parsed)
            && parsed > 0
            ? parsed
            : null;

    // RuboCop reports the paths it was given, relative to the scan cwd when
    // the target is "."; strip a "./" or absolute-root prefix so findings
    // land on repository-relative paths the ExcludePaths filter can see.
    private static string? NormalizeFilePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var path = raw.Trim().Replace('\\', '/');
        if (path.StartsWith("./", StringComparison.Ordinal))
            path = path[2..];
        if (path.StartsWith('/'))
            path = path[1..];
        return path;
    }
}
