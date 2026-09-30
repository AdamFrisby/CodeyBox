using System.Text;
using System.Text.Json;
using CodeyBox.PluginSdk.Tools;
using static CodeyBox.PluginSdk.Tools.ExternalToolJsonHelpers;

namespace CodeyBox.DependencyCheckAuditorPlugin;

/// <summary>
/// Parser for the OWASP dependency-check native JSON report
/// (<c>--format JSON</c>), delivered as the parser input's stdout slot by the
/// auditor's bounded report-file read (see <see cref="DependencyCheckAuditor"/>).
/// One finding is emitted per (dependency, vulnerability) pair, carrying the
/// tool's native severity token (<c>CRITICAL</c>/<c>HIGH</c>/<c>MEDIUM</c>/
/// <c>LOW</c>/<c>NONE</c> from the CVSS base-severity enums, or
/// <c>Unknown</c> for unscored vulnerabilities) for the declared
/// <see cref="ExternalToolSeverityMapping"/>, the vulnerability name (usually
/// a CVE id) as the rule id, and the dependency file path the tool reports.
/// dependency-check reports no line numbers, so findings are file-level. A
/// missing or unparseable report throws
/// <see cref="ExternalToolParseException"/> so "the scan did not produce its
/// report" fails closed as infrastructure, never as a clean pass.
///
/// <para>Only active vulnerabilities are reported: the JSON
/// <c>vulnerabilities</c> array carries the dependency's non-suppressed
/// matches (suppressed entries live in a separate set the template does not
/// render), so an operator-owned <c>--suppression</c> file genuinely narrows
/// the report — which is why suppression files must come from outside the
/// audited worktree.</para>
/// </summary>
internal sealed class DependencyCheckJsonReportParser : IExternalToolOutputParser
{
    /// <summary>Upper bound on (dependency, vulnerability) findings consumed from one report.</summary>
    public const int DefaultMaxResults = 10_000;

    private const int MessageDetailMaxChars = 300;
    private const int DescriptionMaxChars = 2000;

    private readonly int _maxResults;

    public DependencyCheckJsonReportParser(int maxResults = DefaultMaxResults)
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
                || !root.TryGetProperty("dependencies"u8, out var dependencies)
                || dependencies.ValueKind != JsonValueKind.Array)
                throw new ExternalToolParseException(
                    $"Tool '{input.ToolName}' produced JSON without a dependency-check 'dependencies' array.");

            var findings = new List<ExternalToolFinding>();
            foreach (var dependency in dependencies.EnumerateArray())
            {
                if (findings.Count >= _maxResults)
                    break;
                if (dependency.ValueKind != JsonValueKind.Object
                    || !dependency.TryGetProperty("vulnerabilities"u8, out var vulnerabilities)
                    || vulnerabilities.ValueKind != JsonValueKind.Array)
                    continue;

                var filePath = GetString(dependency, "filePath"u8)
                    ?? GetString(dependency, "fileName"u8);
                var path = NormalizeReportedPath(filePath, input.ScanRoot, input.WorkingDirectory);

                foreach (var vulnerability in vulnerabilities.EnumerateArray())
                {
                    if (findings.Count >= _maxResults)
                        break;
                    if (vulnerability.ValueKind != JsonValueKind.Object)
                        continue;
                    findings.Add(ParseVulnerability(vulnerability, path));
                }
            }

            return findings;
        }
    }

    private static ExternalToolFinding ParseVulnerability(JsonElement vulnerability, string? path)
    {
        var ruleId = GetString(vulnerability, "name"u8);
        var severity = GetString(vulnerability, "severity"u8);
        var description = GetString(vulnerability, "description"u8);

        return new ExternalToolFinding(
            SeverityLevel: NullIfWhiteSpace(severity),
            RuleId: NullIfWhiteSpace(ruleId),
            Message: ComposeMessage(ruleId, severity, description, vulnerability),
            Path: path,
            Line: null);
    }

    private static string ComposeMessage(
        string? ruleId, string? severity, string? description, JsonElement vulnerability)
    {
        var builder = new StringBuilder();
        builder.Append(string.IsNullOrWhiteSpace(ruleId)
            ? "Dependency vulnerability"
            : Truncate(SingleLine(ruleId.Trim()), MessageDetailMaxChars));

        var score = ReadCvssScore(vulnerability);
        if (!string.IsNullOrWhiteSpace(severity) || score is not null)
        {
            builder.Append(" (");
            builder.Append(string.IsNullOrWhiteSpace(severity)
                ? "unscored"
                : Truncate(SingleLine(severity.Trim()), MessageDetailMaxChars));
            if (score is not null)
                builder.Append($", CVSS {score}");
            builder.Append(')');
        }

        if (!string.IsNullOrWhiteSpace(description))
            builder.Append('\n').Append(Truncate(description.Trim(), DescriptionMaxChars));

        var cwes = ReadCwes(vulnerability);
        if (cwes.Count > 0)
            builder.Append("\nCWE: ").Append(string.Join(", ", cwes));

        // Evidence (file/vendor/product/version strings) and notes are
        // deliberately absent: they add bulk without verdict value, and the
        // description already carries what an operator triages on.
        return builder.ToString();
    }

    private static string? ReadCvssScore(JsonElement vulnerability)
    {
        foreach (var section in (ReadOnlySpan<string>)["cvssv4", "cvssv3", "cvssv2"])
        {
            if (vulnerability.TryGetProperty(section, out var cvss)
                && cvss.ValueKind == JsonValueKind.Object
                && cvss.TryGetProperty("baseScore"u8, out var score)
                && score.ValueKind is JsonValueKind.Number or JsonValueKind.String)
            {
                var text = score.GetRawText().Trim('"', ' ', '\t');
                if (!string.IsNullOrWhiteSpace(text))
                    return Truncate(SingleLine(text), MessageDetailMaxChars);
            }
        }

        return null;
    }

    private static List<string> ReadCwes(JsonElement vulnerability)
    {
        var cwes = new List<string>();
        if (vulnerability.TryGetProperty("cwes"u8, out var array)
            && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in array.EnumerateArray())
            {
                if (cwes.Count >= 3)
                    break;
                if (entry.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(entry.GetString()))
                    cwes.Add(Truncate(SingleLine(entry.GetString()!.Trim()), MessageDetailMaxChars));
            }
        }

        return cwes;
    }
}
