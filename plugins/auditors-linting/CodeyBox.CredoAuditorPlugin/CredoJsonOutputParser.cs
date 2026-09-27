using System.Text.Json;
using CodeyBox.PluginSdk.Tools;

namespace CodeyBox.CredoAuditorPlugin;

/// <summary>
/// Parses credo's built-in <c>--format json</c> report — a single JSON
/// document <c>{"issues": [...]}</c> on stdout, where each issue carries
/// e.g. <c>{"check": "Credo.Check.Readability.ModuleDoc", "category":
/// "readability", "filename": "lib/app.ex", "line_no": 1, "column": 1,
/// "message": "...", "priority": 1, "scope": "MyApp"}</c> — into
/// <see cref="ExternalToolFinding"/> records. A clean run emits
/// <c>{"issues": []}</c>.
///
/// <para>The issue <c>category</c> is kept in credo's own vocabulary and
/// <see cref="ExternalToolAuditorBase"/> maps it through the auditor's
/// declared <see cref="ExternalToolSeverityMapping"/>; raw levels never
/// reach findings. The numeric <c>priority</c> is preserved in the finding
/// message context rather than driving severity (see
/// <see cref="CredoAuditor"/> for the mapping rationale).</para>
///
/// <para>Fail-closed on malformed output: blank stdout, non-JSON output, a
/// document without an <c>issues</c> array, or a non-object issue entry
/// throws <see cref="ExternalToolParseException"/>, which the base reports
/// as infrastructure, never as a pass. A findings exit (credo reported
/// issues) whose stdout yields none — a crash, truncated report, or foreign
/// output such as an operator-overridden <c>--format</c> — likewise throws:
/// "found problems" and "could not run" must stay distinguishable.</para>
/// </summary>
internal sealed class CredoJsonOutputParser : IExternalToolOutputParser
{
    // Same per-report result bound the shared SARIF parser applies.
    private const int MaxResults = SarifToolOutputParser.DefaultMaxResults;

    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Stdout))
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced no JSON output on stdout.");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(input.Stdout);
        }
        catch (JsonException ex)
        {
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced output that is not valid credo JSON: {SingleLine(ex.Message)}.",
                ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("issues"u8, out var issues)
                || issues.ValueKind != JsonValueKind.Array)
                throw new ExternalToolParseException(
                    $"Tool '{input.ToolName}' produced JSON without a credo 'issues' array.");

            var findings = new List<ExternalToolFinding>();
            foreach (var issue in issues.EnumerateArray())
            {
                if (findings.Count >= MaxResults)
                    break;
                if (issue.ValueKind != JsonValueKind.Object)
                    throw new ExternalToolParseException(
                        $"Tool '{input.ToolName}' produced a credo 'issues' entry that is not a JSON object.");
                findings.Add(ParseIssue(issue, input));
            }

            // Credo exits non-zero from a completed analysis only when it
            // reported at least one issue; a findings exit with nothing
            // parseable on stdout means the run did not produce its declared
            // report — crash, truncation, or foreign output — not a clean
            // pass.
            if (input.ExitCode != 0 && findings.Count == 0)
                throw new ExternalToolParseException(
                    $"Tool '{input.ToolName}' exited {input.ExitCode} but produced no credo JSON issues on stdout.");
            return findings;
        }
    }

    private static ExternalToolFinding ParseIssue(JsonElement issue, ExternalToolParseInput input)
    {
        var message = NullIfWhiteSpace(GetString(issue, "message"u8)) ?? "(no message)";
        var priority = GetInt64(issue, "priority"u8);
        if (priority.HasValue)
            message += $"\n(priority: {priority.Value})";

        int? lineNumber = null;
        if (issue.TryGetProperty("line_no"u8, out var lineElement)
            && lineElement.ValueKind == JsonValueKind.Number
            && lineElement.TryGetInt32(out var lineValue)
            && lineValue > 0)
            lineNumber = lineValue;

        return new ExternalToolFinding(
            SeverityLevel: NullIfWhiteSpace(GetString(issue, "category"u8)),
            RuleId: NullIfWhiteSpace(GetString(issue, "check"u8)),
            Message: message,
            Path: NormalizeFilePath(GetString(issue, "filename"u8), input.WorkingDirectory),
            Line: lineNumber);
    }

    // Credo reports the paths it was given, relative to the scan cwd when
    // the target is "."; strip a "./" prefix so findings land on
    // repository-relative paths the ExcludePaths filter can see. An absolute
    // path under the scan working directory is relativized the same way;
    // anything else is kept verbatim rather than guessed.
    private static string? NormalizeFilePath(string? raw, string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var path = raw.Trim().Replace('\\', '/');
        if (path.StartsWith("./", StringComparison.Ordinal))
            path = path[2..];
        if (path.StartsWith("/", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(workingDirectory))
        {
            var root = workingDirectory.Trim().Replace('\\', '/').TrimEnd('/');
            if (path.StartsWith(root + "/", StringComparison.Ordinal))
                path = path[(root.Length + 1)..];
        }

        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    private static string? GetString(JsonElement element, ReadOnlySpan<byte> name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? GetInt64(JsonElement element, ReadOnlySpan<byte> name)
        => element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var number)
            ? number
            : null;

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string SingleLine(string message)
        => message.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
