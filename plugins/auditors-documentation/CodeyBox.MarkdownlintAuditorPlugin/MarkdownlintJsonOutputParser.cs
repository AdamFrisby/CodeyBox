using System.Text.Json;
using CodeyBox.PluginSdk.Tools;
using static CodeyBox.PluginSdk.Tools.ExternalToolJsonHelpers;

namespace CodeyBox.MarkdownlintAuditorPlugin;

/// <summary>
/// Parses markdownlint-cli's <c>--json</c> report into
/// <see cref="ExternalToolFinding"/> records. The report is a JSON array;
/// each entry carries <c>fileName</c>, <c>lineNumber</c>,
/// <c>ruleNames</c> (first entry is the <c>MDxxx</c> code),
/// <c>ruleDescription</c>, <c>errorDetail</c>/<c>errorContext</c>, and
/// <c>severity</c> (<c>"error"</c>/<c>"warning"</c>).
///
/// <para>Unlike most wrapped tools, markdownlint-cli writes its machine
/// report to <b>stderr</b> (usage text goes to stdout), so the report is
/// read from <see cref="ExternalToolParseInput.Stderr"/>.</para>
///
/// <para>Severity stays in the tool's own vocabulary and
/// <see cref="ExternalToolAuditorBase"/> maps it through the auditor's
/// declared <see cref="ExternalToolSeverityMapping"/> — raw levels never
/// reach findings.</para>
///
/// <para>Malformed output throws <see cref="ExternalToolParseException"/>,
/// which the base reports as infrastructure, never as a pass: empty output
/// on a findings-signalling exit, non-JSON, or a JSON document that is not
/// a markdownlint array. One narrow exception: a clean run
/// (exit <c>0</c>) prints no report at all, so empty stderr on exit
/// <c>0</c> is a clean pass with zero findings, not a failure.</para>
/// </summary>
internal sealed class MarkdownlintJsonOutputParser : IExternalToolOutputParser
{
    /// <summary>
    /// Upper bound on issues consumed from one document, mirroring the
    /// shared <see cref="SarifToolOutputParser"/> bound so one pathological
    /// report cannot grow findings without limit. The base still applies its
    /// own <c>MaxFindings</c> cap on top.
    /// </summary>
    internal const int MaxResults = 10_000;

    private const int MessageMaxChars = 512;

    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        // The JSON report lives on stderr; stdout carries only usage text.
        // A clean run prints nothing, so empty output on exit 0 is zero
        // findings — every other report-less run fails closed.
        if (string.IsNullOrWhiteSpace(input.Stderr))
        {
            if (input.ExitCode == MarkdownlintAuditor.CleanExitCode)
                return [];
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced no markdownlint JSON output on stderr.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(input.Stderr);
        }
        catch (JsonException ex)
        {
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced output that is not valid markdownlint JSON: {SingleLine(ex.Message)}.",
                ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new ExternalToolParseException(
                    $"Tool '{input.ToolName}' produced JSON that is not a markdownlint report (missing the issue array).");

            var findings = new List<ExternalToolFinding>();
            foreach (var issue in document.RootElement.EnumerateArray())
            {
                if (findings.Count >= MaxResults)
                    break;
                if (issue.ValueKind == JsonValueKind.Object)
                    findings.Add(ParseIssue(issue));
            }

            return findings;
        }
    }

    private static ExternalToolFinding ParseIssue(JsonElement issue)
    {
        string? ruleId = null;
        if (issue.TryGetProperty("ruleNames"u8, out var ruleNames)
            && ruleNames.ValueKind == JsonValueKind.Array)
        {
            foreach (var name in ruleNames.EnumerateArray())
            {
                ruleId = NullIfWhiteSpace(CoerceString(name));
                if (ruleId is not null)
                    break;
            }
        }

        var severity = NullIfWhiteSpace(GetString(issue, "severity"u8));

        var description = NullIfWhiteSpace(GetString(issue, "ruleDescription"u8)) ?? "(no message)";
        var detail = NullIfWhiteSpace(GetString(issue, "errorDetail"u8));
        if (detail is not null)
            description += " [" + detail + "]";
        var context = NullIfWhiteSpace(GetString(issue, "errorContext"u8));
        if (context is not null)
            description += " [Context: \"" + context + "\"]";
        if (description.Length > MessageMaxChars)
            description = description[..MessageMaxChars] + "…";

        string? path = NullIfWhiteSpace(GetString(issue, "fileName"u8));
        if (path is not null)
        {
            path = NormalizePath(path);
            while (path.StartsWith("./", StringComparison.Ordinal))
                path = path[2..];
            if (path.Length == 0)
                path = null;
        }

        int? line = null;
        if (issue.TryGetProperty("lineNumber"u8, out var lineElement)
            && lineElement.ValueKind == JsonValueKind.Number
            && lineElement.TryGetInt32(out var lineValue)
            && lineValue > 0)
            line = lineValue;

        return new ExternalToolFinding(
            SeverityLevel: severity,
            RuleId: ruleId,
            Message: description,
            Path: path,
            Line: line);
    }
}
