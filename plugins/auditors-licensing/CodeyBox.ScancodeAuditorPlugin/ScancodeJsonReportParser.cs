using System.Text;
using System.Text.Json;
using CodeyBox.PluginSdk.Tools;
using static CodeyBox.PluginSdk.Tools.ExternalToolJsonHelpers;

namespace CodeyBox.ScancodeAuditorPlugin;

/// <summary>
/// Parser for the ScanCode Toolkit native JSON report (<c>--json</c>),
/// delivered as the parser input's stdout slot by the auditor's bounded
/// report-file read (see <see cref="ScancodeAuditor"/>). One finding is
/// emitted per license detection, per copyright statement, and per
/// per-file scan error, carrying the tool's native vocabulary for the
/// declared <see cref="ExternalToolSeverityMapping"/>: the license
/// <c>category</c> (e.g. <c>Copyleft</c>, <c>Permissive</c>), the synthetic
/// <c>Copyright</c> token for copyright statements, and no token (mapped to
/// the declared default) for scan errors. The license <c>key</c> (e.g.
/// <c>gpl-3.0</c>) is the rule id; copyright and scan-error findings use the
/// stable synthetic ids <see cref="CopyrightRuleId"/> and
/// <see cref="ScanErrorRuleId"/> so <c>IncludedRules</c>/<c>ExcludedRules</c>
/// select them. File paths are relativized through the shared
/// reported-path policy; line numbers pass through only when positive.
/// A missing or unparseable report throws
/// <see cref="ExternalToolParseException"/> so "the scan did not produce its
/// report" fails closed as infrastructure, never as a clean pass.
/// </summary>
internal sealed class ScancodeJsonReportParser : IExternalToolOutputParser
{
    /// <summary>Upper bound on findings consumed from one report document.</summary>
    public const int DefaultMaxResults = ExternalToolReportLimits.DefaultMaxResults;

    /// <summary>Rule id for copyright-statement findings (the tool supplies no rule id for these).</summary>
    public const string CopyrightRuleId = "copyright-notice";

    /// <summary>Rule id for per-file scan-error findings.</summary>
    public const string ScanErrorRuleId = "scan-error";

    /// <summary>
    /// Synthetic severity token for copyright statements. ScanCode reports no
    /// severity for copyrights, so the auditor's declared mapping translates
    /// this token — raw tool text never reaches findings unmapped.
    /// </summary>
    public const string CopyrightSeverityToken = "Copyright";

    private const int MessageDetailMaxChars = 300;
    private const int EvidenceMaxChars = 2000;

    private readonly int _maxResults;

    public ScancodeJsonReportParser(int maxResults = DefaultMaxResults)
    {
        if (maxResults <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxResults), "Maximum report results must be positive.");
        _maxResults = maxResults;
    }

    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Stdout))
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced no report output.");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(input.Stdout);
        }
        catch (JsonException ex)
        {
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced output that is not a valid JSON report: {ToolOutputText.SingleLine(ex.Message)}.",
                ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("files"u8, out var files)
                || files.ValueKind != JsonValueKind.Array)
                throw new ExternalToolParseException(
                    $"Tool '{input.ToolName}' produced JSON without a ScanCode 'files' array.");

            var findings = new List<ExternalToolFinding>();
            foreach (var file in files.EnumerateArray())
            {
                if (findings.Count >= _maxResults)
                    break;
                if (file.ValueKind != JsonValueKind.Object)
                    continue;
                ParseFile(file, input, findings);
            }

            return findings;
        }
    }

    private void ParseFile(JsonElement file, ExternalToolParseInput input, List<ExternalToolFinding> findings)
    {
        var rawPath = GetString(file, "path"u8);
        var path = NormalizeReportedPath(rawPath, input.ScanRoot, input.WorkingDirectory);

        if (file.TryGetProperty("licenses"u8, out var licenses)
            && licenses.ValueKind == JsonValueKind.Array)
        {
            foreach (var license in licenses.EnumerateArray())
            {
                if (findings.Count >= _maxResults)
                    return;
                if (license.ValueKind != JsonValueKind.Object)
                    continue;
                var finding = ParseLicense(license, path);
                if (finding is not null)
                    findings.Add(finding);
            }
        }

        if (file.TryGetProperty("copyrights"u8, out var copyrights)
            && copyrights.ValueKind == JsonValueKind.Array)
        {
            foreach (var copyright in copyrights.EnumerateArray())
            {
                if (findings.Count >= _maxResults)
                    return;
                if (copyright.ValueKind != JsonValueKind.Object)
                    continue;
                var finding = ParseCopyright(copyright, path);
                if (finding is not null)
                    findings.Add(finding);
            }
        }

        if (file.TryGetProperty("scan_errors"u8, out var scanErrors)
            && scanErrors.ValueKind == JsonValueKind.Array)
        {
            foreach (var scanError in scanErrors.EnumerateArray())
            {
                if (findings.Count >= _maxResults)
                    return;
                var finding = ParseScanError(scanError, path);
                if (finding is not null)
                    findings.Add(finding);
            }
        }
    }

    private static ExternalToolFinding? ParseLicense(JsonElement license, string? path)
    {
        var key = NullIfWhiteSpace(GetString(license, "key"u8));
        if (key is null)
            return null;

        var category = NullIfWhiteSpace(GetString(license, "category"u8));
        var line = ReadPositiveLine(license, "start_line"u8);
        var endLine = ReadPositiveLine(license, "end_line"u8);
        var score = ReadScore(license);
        var name = NullIfWhiteSpace(GetString(license, "name"u8))
            ?? NullIfWhiteSpace(GetString(license, "short_name"u8));

        string? matchedRule = null;
        if (license.TryGetProperty("matched_rule"u8, out var rule)
            && rule.ValueKind == JsonValueKind.Object)
            matchedRule = NullIfWhiteSpace(GetString(rule, "identifier"u8));

        var builder = new StringBuilder();
        builder.Append("License '").Append(key).Append('\'');
        if (category is not null)
            builder.Append(" (").Append(category).Append(')');
        if (score is not null)
            builder.Append(" with match score ").Append(score.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
        builder.Append('.');
        if (name is not null)
            builder.Append(' ').Append(Truncate(SingleLine(name), MessageDetailMaxChars));
        if (matchedRule is not null)
            builder.Append(" Matched rule: ").Append(Truncate(SingleLine(matchedRule), MessageDetailMaxChars)).Append('.');
        if (line.HasValue && endLine.HasValue && endLine != line)
            builder.Append($" Evidence at lines {line}-{endLine}.");
        else if (line.HasValue)
            builder.Append($" Evidence at line {line}.");

        return new ExternalToolFinding(
            SeverityLevel: category,
            RuleId: key,
            Message: Truncate(builder.ToString(), EvidenceMaxChars),
            Path: path,
            Line: line);
    }

    private static ExternalToolFinding? ParseCopyright(JsonElement copyright, string? path)
    {
        var text = NullIfWhiteSpace(GetString(copyright, "copyright"u8))
            ?? NullIfWhiteSpace(GetString(copyright, "value"u8))
            ?? NullIfWhiteSpace(GetString(copyright, "statement"u8));
        if (text is null)
            return null;

        var line = ReadPositiveLine(copyright, "start_line"u8);
        var message = "Copyright notice: " + Truncate(SingleLine(text), MessageDetailMaxChars) + ".";

        return new ExternalToolFinding(
            SeverityLevel: CopyrightSeverityToken,
            RuleId: CopyrightRuleId,
            Message: Truncate(message, EvidenceMaxChars),
            Path: path,
            Line: line);
    }

    private static ExternalToolFinding? ParseScanError(JsonElement scanError, string? path)
    {
        var text = scanError.ValueKind == JsonValueKind.String
            ? NullIfWhiteSpace(scanError.GetString())
            : NullIfWhiteSpace(GetString(scanError, "error"u8))
                ?? NullIfWhiteSpace(GetString(scanError, "message"u8));
        if (text is null)
            return null;

        return new ExternalToolFinding(
            SeverityLevel: null,
            RuleId: ScanErrorRuleId,
            Message: Truncate("ScanCode could not fully analyze this file: " + SingleLine(text) + ".", EvidenceMaxChars),
            Path: path,
            Line: null);
    }

    private static int? ReadPositiveLine(JsonElement element, ReadOnlySpan<byte> name)
    {
        if (!element.TryGetProperty(name, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number > 0)
            return number;
        if (value.ValueKind == JsonValueKind.String)
            return ToolOutputText.ParseLine(value.GetString());
        return null;
    }

    private static double? ReadScore(JsonElement license)
    {
        if (!license.TryGetProperty("score"u8, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
            return number;
        return null;
    }
}
