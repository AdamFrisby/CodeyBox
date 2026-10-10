using System.Text.Json;
using CodeyBox.PluginSdk.Tools;

namespace CodeyBox.PsalmAuditorPlugin;

/// <summary>
/// Parses Psalm's built-in <c>--output-format=json</c> report — a JSON array
/// of issue objects — into <see cref="ExternalToolFinding"/> records. Each
/// element carries <c>severity</c> (<c>"error"</c> or <c>"info"</c>),
/// <c>type</c> (the issue type, e.g. <c>UndefinedFunction</c>),
/// <c>message</c>, <c>file_name</c>, and <c>line_from</c>/<c>line_to</c>.
///
/// <para>Psalm reports <c>file_name</c> as written relative to the project
/// root in the common case, but absolute spellings occur (for example when
/// explicit file arguments are analysed); absolute paths under
/// <see cref="ExternalToolParseInput.WorkingDirectory"/> are relativized so
/// locations and the shared <c>ExcludePaths</c> filter see
/// repository-relative paths. Paths that do not sit under the working
/// directory are left untouched.</para>
///
/// <para>Fail-closed on malformed output — and this is what keeps Psalm's
/// exit codes honest: exit <c>1</c> means "could not run" (missing config,
/// bad flags, internal errors — printed as text, not the JSON report), so a
/// stdout that is not a Psalm JSON array throws
/// <see cref="ExternalToolParseException"/> — which the base reports as
/// infrastructure, never as a pass — as does a non-zero findings exit whose
/// report contains zero findings (a crash or truncated report, not a clean
/// run).</para>
/// </summary>
internal sealed class PsalmJsonOutputParser : IExternalToolOutputParser
{
    // Same per-report result bound the shared SARIF parser applies.
    private const int MaxResults = SarifToolOutputParser.DefaultMaxResults;

    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Stdout))
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced no Psalm JSON output on stdout.");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(input.Stdout);
        }
        catch (JsonException ex)
        {
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced output that is not valid Psalm JSON: {SingleLine(ex.Message)}.",
                ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array)
                throw new ExternalToolParseException(
                    $"Tool '{input.ToolName}' produced JSON that is not a Psalm report "
                    + "(expected a JSON array of issues).");

            var findings = new List<ExternalToolFinding>();
            foreach (var issue in root.EnumerateArray())
            {
                if (findings.Count >= MaxResults)
                    break;
                if (issue.ValueKind == JsonValueKind.Object)
                    findings.Add(ParseIssue(issue, input));
            }

            // A non-zero exit means Psalm either reported issues (findings
            // above) or could not complete the analysis — in which case no
            // JSON array is emitted and this throw already fired on the shape
            // check. A findings exit whose report yields zero findings is a
            // corrupt report, not a clean pass: fail closed.
            if (input.ExitCode != 0 && findings.Count == 0)
                throw new ExternalToolParseException(
                    $"Tool '{input.ToolName}' exited {input.ExitCode} but its JSON report contains no findings.");
            return findings;
        }
    }

    private static ExternalToolFinding ParseIssue(JsonElement element, ExternalToolParseInput input)
    {
        var message = NullIfWhiteSpace(GetString(element, "message"u8)) ?? "(no message)";

        int? line = null;
        if (element.TryGetProperty("line_from"u8, out var lineElement)
            && lineElement.ValueKind == JsonValueKind.Number
            && lineElement.TryGetInt32(out var lineValue)
            && lineValue > 0)
            line = lineValue;

        return new ExternalToolFinding(
            SeverityLevel: NullIfWhiteSpace(GetString(element, "severity"u8)),
            RuleId: NullIfWhiteSpace(GetString(element, "type"u8)),
            Message: message,
            Path: NormalizeFilePath(GetString(element, "file_name"u8), input.WorkingDirectory),
            Line: line);
    }

    // Psalm usually reports file_name relative to the project root already;
    // absolutize nothing, only strip the exec working-directory prefix from
    // absolute spellings so findings land on repository-relative paths the
    // ExcludePaths filter can see; leave foreign paths untouched.
    private static string? NormalizeFilePath(string? raw, string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var path = raw.Trim().Replace('\\', '/');
        while (path.StartsWith("./", StringComparison.Ordinal))
            path = path[2..];
        var root = workingDirectory?.Trim().Replace('\\', '/').TrimEnd('/');
        if (!string.IsNullOrEmpty(root)
            && path.Length > root.Length + 1
            && path.StartsWith(root + "/", StringComparison.Ordinal))
            path = path[(root.Length + 1)..];
        return string.IsNullOrWhiteSpace(path) ? null : path;
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
