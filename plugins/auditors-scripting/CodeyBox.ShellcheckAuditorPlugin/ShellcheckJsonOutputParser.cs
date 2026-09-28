using System.Text.Json;
using CodeyBox.PluginSdk.Tools;

namespace CodeyBox.ShellcheckAuditorPlugin;

/// <summary>
/// Parses shellcheck's <c>-f json1</c> report (a JSON object with a
/// <c>comments</c> array; each entry carries <c>file</c>, <c>line</c>,
/// <c>level</c> (<c>error</c>/<c>warning</c>/<c>info</c>/<c>style</c>),
/// numeric <c>code</c> (reported as <c>SCxxxx</c>), and <c>message</c>) into
/// findings that preserve the rule identifier and file/line location
/// wherever the tool supplies them.
///
/// <para>One narrow exception: shellcheck prints <c>No files
/// specified.</c> to stderr and exits <c>3</c> with no report when it was
/// invoked with zero file arguments — i.e. the auditor's own discovery
/// found no shell scripts and the operator configured no
/// <c>Targets</c>. A tree with nothing checkable is a clean pass with zero
/// findings, not a failure. The sentinel requires the exact diagnostic on
/// an exit-<c>3</c> run whose output carries no json1 <c>"comments"</c>
/// document; every other report-less run (bad flags, unreadable files,
/// unknown formats) falls through to the JSON parse, which throws <see
/// cref="ExternalToolParseException"/> — reported by the base as
/// infrastructure, never as a pass.</para>
/// </summary>
internal sealed class ShellcheckJsonOutputParser : IExternalToolOutputParser
{
    /// <summary>
    /// Exact stderr diagnostic shellcheck prints (exit 3, no report) when
    /// invoked with zero file arguments.
    /// </summary>
    internal const string NoInputFilesSentinel = "No files specified.";

    /// <summary>
    /// Upper bound on comments consumed from one document, mirroring the
    /// shared <see cref="SarifToolOutputParser"/> bound so one pathological
    /// report cannot grow findings without limit. The base still applies its
    /// own <c>MaxFindings</c> cap on top.
    /// </summary>
    internal const int MaxResults = 10_000;

    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var stdout = input.Stdout ?? string.Empty;
        var stderr = input.Stderr ?? string.Empty;
        if (input.ExitCode == ShellcheckAuditor.NoFilesExitCode
            && stderr.Contains(NoInputFilesSentinel, StringComparison.Ordinal)
            && !stdout.Contains("\"comments\"", StringComparison.Ordinal))
            return [];

        if (string.IsNullOrWhiteSpace(stdout))
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced no JSON output on stdout.");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(stdout);
        }
        catch (JsonException ex)
        {
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced output that is not valid JSON: {ToolOutputText.SingleLine(ex.Message)}.",
                ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("comments"u8, out var comments)
                || comments.ValueKind != JsonValueKind.Array)
                throw new ExternalToolParseException(
                    $"Tool '{input.ToolName}' produced JSON without a json1 'comments' array.");

            var findings = new List<ExternalToolFinding>();
            foreach (var comment in comments.EnumerateArray())
            {
                if (findings.Count >= MaxResults)
                    break;
                if (comment.ValueKind == JsonValueKind.Object)
                    findings.Add(ParseComment(comment));
            }

            return findings;
        }
    }

    private static ExternalToolFinding ParseComment(JsonElement comment)
    {
        string? ruleId = null;
        if (comment.TryGetProperty("code"u8, out var code))
        {
            if (code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var numeric) && numeric > 0)
                ruleId = "SC" + numeric.ToString(System.Globalization.CultureInfo.InvariantCulture);
            else
            {
                var text = ExternalToolJsonHelpers.CoerceString(code);
                if (!string.IsNullOrWhiteSpace(text))
                    ruleId = text.Trim().StartsWith("SC", StringComparison.OrdinalIgnoreCase)
                        ? text.Trim()
                        : "SC" + text.Trim();
            }
        }

        var level = comment.TryGetProperty("level"u8, out var levelElement)
            ? ExternalToolJsonHelpers.CoerceString(levelElement)
            : null;

        var message = comment.TryGetProperty("message"u8, out var messageElement)
            ? ExternalToolJsonHelpers.CoerceString(messageElement)
            : null;

        string? path = null;
        if (comment.TryGetProperty("file"u8, out var fileElement))
            path = StripDotSlashPrefix(ExternalToolJsonHelpers.CoerceString(fileElement));

        int? line = null;
        if (comment.TryGetProperty("line"u8, out var lineElement)
            && lineElement.ValueKind == JsonValueKind.Number
            && lineElement.TryGetInt32(out var lineValue)
            && lineValue > 0)
            line = lineValue;

        return new ExternalToolFinding(
            SeverityLevel: string.IsNullOrWhiteSpace(level) ? null : level.Trim(),
            RuleId: string.IsNullOrWhiteSpace(ruleId) ? null : ruleId,
            Message: string.IsNullOrWhiteSpace(message) ? "(no message)" : message.Trim(),
            Path: string.IsNullOrWhiteSpace(path) ? null : path,
            Line: line);
    }

    // The auditor passes scan targets with a "./" prefix so dash-leading
    // names are never option-parsed; shellcheck echoes that prefix back in
    // the file field. Strip it so finding locations and ExcludePaths match
    // the repository-relative form every other auditor reports.
    private static string? StripDotSlashPrefix(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        var stripped = path.Trim();
        while (stripped.StartsWith("./", StringComparison.Ordinal))
            stripped = stripped[2..];
        return string.IsNullOrWhiteSpace(stripped) ? null : stripped;
    }
}
