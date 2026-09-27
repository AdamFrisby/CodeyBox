using System.Text.Json;
using CodeyBox.PluginSdk.Tools;

namespace CodeyBox.MypyAuditorPlugin;

/// <summary>
/// Parses mypy's built-in <c>--output json</c> report — one JSON object per
/// line on stdout, e.g.
/// <c>{"file": "pkg/mod.py", "line": 3, "column": 7, "message": "...",
/// "hint": null, "code": "assignment", "severity": "error"}</c> — into
/// <see cref="ExternalToolFinding"/> records. A clean run emits only blank
/// lines (mypy suppresses the "Success: …" text under <c>--output</c>);
/// <c>--no-error-summary</c> keeps the human "Found N errors …" line out of
/// the stream, so every non-blank line is a diagnostic.
///
/// <para>Severity is kept in mypy's own vocabulary — <c>"error"</c> or
/// <c>"note"</c> — and <see cref="ExternalToolAuditorBase"/> maps it through
/// the auditor's declared <see cref="ExternalToolSeverityMapping"/>; raw
/// levels never reach findings.</para>
///
/// <para>Fail-closed on malformed output: a non-blank line that is not a JSON
/// diagnostic object throws <see cref="ExternalToolParseException"/>, which
/// the base reports as infrastructure, never as a pass. A non-zero findings
/// exit (mypy reported diagnostics) whose stdout yields none — a crash,
/// truncated report, or foreign output — likewise throws: "found problems"
/// and "could not run" must stay distinguishable.</para>
/// </summary>
internal sealed class MypyJsonOutputParser : IExternalToolOutputParser
{
    // Same per-report result bound the shared SARIF parser applies.
    private const int MaxResults = SarifToolOutputParser.DefaultMaxResults;

    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var findings = new List<ExternalToolFinding>();
        foreach (var rawLine in input.Stdout.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;
            if (findings.Count >= MaxResults)
                break;
            findings.Add(ParseDiagnostic(line, input.ToolName));
        }

        // mypy exits non-zero from a completed analysis only when it reported
        // at least one error-level diagnostic; a findings exit with nothing
        // parseable on stdout means the run did not produce its declared
        // report — crash, truncation, or foreign output — not a clean pass.
        if (input.ExitCode != 0 && findings.Count == 0)
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' exited {input.ExitCode} but produced no mypy JSON diagnostics on stdout.");
        return findings;
    }

    private static ExternalToolFinding ParseDiagnostic(string line, string toolName)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException ex)
        {
            throw new ExternalToolParseException(
                $"Tool '{toolName}' produced a line that is not valid mypy JSON: {SingleLine(line)}.",
                ex);
        }

        using (document)
        {
            var element = document.RootElement;
            if (element.ValueKind != JsonValueKind.Object
                || !element.TryGetProperty("message"u8, out var messageElement)
                || messageElement.ValueKind != JsonValueKind.String)
                throw new ExternalToolParseException(
                    $"Tool '{toolName}' produced a JSON line that is not a mypy diagnostic object: {SingleLine(line)}.");

            var message = NullIfWhiteSpace(messageElement.GetString()) ?? "(no message)";
            // mypy attaches remediation text ("hint": "…") to some codes;
            // keep it inside the finding message rather than dropping it.
            var hint = NullIfWhiteSpace(GetString(element, "hint"u8));
            if (hint is not null)
                message += "\n" + hint;

            int? lineNumber = null;
            if (element.TryGetProperty("line"u8, out var lineElement)
                && lineElement.ValueKind == JsonValueKind.Number
                && lineElement.TryGetInt32(out var lineValue)
                && lineValue > 0)
                lineNumber = lineValue;

            return new ExternalToolFinding(
                SeverityLevel: NullIfWhiteSpace(GetString(element, "severity"u8)),
                RuleId: NullIfWhiteSpace(GetString(element, "code"u8)),
                Message: message,
                Path: NormalizeFilePath(GetString(element, "file"u8)),
                Line: lineNumber);
        }
    }

    // mypy reports the paths it was given, relative to the scan cwd when the
    // target is "."; strip a "./" or absolute-root prefix so findings land on
    // repository-relative paths the ExcludePaths filter can see.
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

    private static string? GetString(JsonElement element, ReadOnlySpan<byte> name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string SingleLine(string message)
        => message.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
