using System.Text;
using System.Text.Json;
using CodeyBox.PluginSdk.Tools;
using static CodeyBox.PluginSdk.Tools.ExternalToolJsonHelpers;

namespace CodeyBox.KicsAuditorPlugin;

/// <summary>
/// Parser for the KICS native JSON report (<c>--report-formats json</c>),
/// delivered as the parser input's stdout slot by the auditor's bounded
/// report-file read (see <see cref="KicsAuditor"/>). One
/// finding is emitted per (query, file) pair — KICS groups affected files
/// under each query — carrying the tool's native severity token
/// (<c>CRITICAL</c>/<c>HIGH</c>/<c>MEDIUM</c>/<c>LOW</c>/<c>INFO</c>/<c>TRACE</c>)
/// for the declared <see cref="ExternalToolSeverityMapping"/>, the query UUID
/// as the rule id, and the file/line KICS reports. A missing or unparseable
/// report throws <see cref="ExternalToolParseException"/> so "the scan did
/// not produce its report" fails closed as infrastructure, never as a clean
/// pass. Counters of files KICS could not parse and queries that failed to
/// execute surface as a synthetic <c>kics/incomplete-scan</c> warning finding:
/// partial coverage must not pass silently.
///
/// <para><b>Secret hygiene.</b> Per-file <c>search_key</c>,
/// <c>expected_value</c>, and <c>actual_value</c> are deliberately NOT copied
/// into findings: for KICS's "Passwords And Secrets" queries those fields
/// carry the matched source snippet — the literal committed secret — and
/// findings flow to the rework prompt, outbound webhooks, and the persisted
/// audit report. Only non-value metadata (platform, category, issue_type,
/// the similarity-id hash) is attached. The report bytes themselves never
/// reach <c>AuditResult.RawOutput</c> either — the auditor fetches them
/// through a separate bounded read that feeds only this parser, not the
/// captured scan streams the pipeline persists.</para>
/// </summary>
internal sealed class KicsJsonReportParser : IExternalToolOutputParser
{
    /// <summary>Upper bound on (query, file) findings consumed from one report.</summary>
    public const int DefaultMaxResults = 10_000;

    /// <summary>Synthesized rule id for the partial-coverage finding.</summary>
    internal const string IncompleteScanRuleId = "kics/incomplete-scan";

    private const int MessageDetailMaxChars = 300;
    private const int DescriptionMaxChars = 2000;

    private readonly int _maxResults;

    public KicsJsonReportParser(int maxResults = DefaultMaxResults)
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
                || !root.TryGetProperty("queries"u8, out var queries)
                || queries.ValueKind != JsonValueKind.Array)
                throw new ExternalToolParseException(
                    $"Tool '{input.ToolName}' produced JSON without a KICS 'queries' array.");

            var findings = new List<ExternalToolFinding>();
            foreach (var query in queries.EnumerateArray())
            {
                if (findings.Count >= _maxResults)
                    break;
                if (query.ValueKind != JsonValueKind.Object
                    || !query.TryGetProperty("files"u8, out var files)
                    || files.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var file in files.EnumerateArray())
                {
                    if (findings.Count >= _maxResults)
                        break;
                    if (file.ValueKind != JsonValueKind.Object)
                        continue;
                    findings.Add(ParseFileResult(query, file, input));
                }
            }

            // Files KICS could not parse and queries that failed to execute
            // mean the scan covered less than the tree — surface the gap as a
            // finding instead of letting a partial scan read as complete.
            var filesFailed = ReadCounter(root, "files_failed_to_scan"u8);
            var queriesFailed = ReadCounter(root, "queries_failed_to_execute"u8);
            if ((filesFailed > 0 || queriesFailed > 0) && findings.Count < _maxResults)
            {
                findings.Add(new ExternalToolFinding(
                    SeverityLevel: "warning",
                    RuleId: IncompleteScanRuleId,
                    Message: $"KICS scan was incomplete: {filesFailed} file(s) failed to parse and "
                        + $"{queriesFailed} queries failed to execute — findings may undercount "
                        + "the true state of the tree."));
            }

            return findings;
        }
    }

    private static ExternalToolFinding ParseFileResult(
        JsonElement query, JsonElement file, ExternalToolParseInput input)
    {
        var ruleId = GetString(query, "query_id"u8);
        var severity = GetString(query, "severity"u8);
        var queryName = GetString(query, "query_name"u8);

        var path = NormalizeReportedPath(GetString(file, "file_name"u8), input);
        var line = ReadPositiveInt(file, "line"u8);

        return new ExternalToolFinding(
            SeverityLevel: NullIfWhiteSpace(severity),
            RuleId: NullIfWhiteSpace(ruleId),
            Message: ComposeMessage(query, file, queryName),
            Path: path,
            Line: line);
    }

    private static string ComposeMessage(JsonElement query, JsonElement file, string? queryName)
    {
        var builder = new StringBuilder();
        // query_name and issue_type are report-controlled text like every
        // other field — they get the same single-line truncation bound so a
        // malformed report cannot inflate a finding's message unboundedly.
        builder.Append(string.IsNullOrWhiteSpace(queryName)
            ? "KICS finding"
            : Truncate(SingleLine(queryName.Trim()), MessageDetailMaxChars));

        var issueType = GetString(file, "issue_type"u8);
        if (!string.IsNullOrWhiteSpace(issueType))
            builder.Append(" (").Append(Truncate(SingleLine(issueType.Trim()), MessageDetailMaxChars)).Append(')');

        var description = GetString(query, "description"u8);
        if (!string.IsNullOrWhiteSpace(description))
            builder.Append('\n').Append(Truncate(description.Trim(), DescriptionMaxChars));

        // search_key / expected_value / actual_value are deliberately absent:
        // KICS's secrets queries embed the matched literal secret in them.
        var details = new List<string>();
        AppendDetail(details, "platform", GetString(query, "platform"u8));
        AppendDetail(details, "category", GetString(query, "category"u8));
        AppendDetail(details, "similarity_id", GetString(file, "similarity_id"u8));
        if (details.Count > 0)
            builder.Append('\n').Append(string.Join("; ", details));

        return builder.ToString();
    }

    private static void AppendDetail(List<string> details, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        details.Add($"{label}={Truncate(SingleLine(value.Trim()), MessageDetailMaxChars)}");
    }

    /// <summary>
    /// Normalizes a KICS-reported <c>file_name</c> to a repository-relative
    /// path: dot segments are collapsed (<c>./</c> dropped, <c>a/../b</c> →
    /// <c>b</c>), and absolute paths are relativized against the scan root
    /// (resolved per run from the sandbox, since sandbox providers may
    /// translate the working directory) or the exec working directory. An
    /// absolute path outside the root is kept absolute rather than
    /// rewritten.
    /// </summary>
    private static string? NormalizeReportedPath(string? reported, ExternalToolParseInput input)
    {
        var normalized = CollapseDotSegments(NormalizePath(reported));
        if (normalized.Length == 0 || !normalized.StartsWith("/", StringComparison.Ordinal))
            return normalized.Length == 0 ? null : normalized;

        var relative = RelativizeToRoot(normalized, input.ScanRoot);
        if (!relative.StartsWith("/", StringComparison.Ordinal))
            return relative;
        relative = RelativizeToRoot(normalized, input.WorkingDirectory);
        return relative;
    }

    // Tool-reported paths are untrusted text: resolve "." and ".." lexically
    // so a report cannot smuggle traversal segments into finding locations.
    // A leading ".." on a relative path is kept — it cannot be resolved
    // without a base and dropping it would silently rewrite the location.
    private static string CollapseDotSegments(string path)
    {
        var rooted = path.StartsWith("/", StringComparison.Ordinal);
        var segments = new List<string>();
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
                continue;
            if (segment == "..")
            {
                if (segments.Count > 0 && segments[^1] != "..")
                    segments.RemoveAt(segments.Count - 1);
                else if (!rooted)
                    segments.Add(segment);
                continue;
            }
            segments.Add(segment);
        }
        var joined = string.Join('/', segments);
        return rooted ? "/" + joined : joined;
    }

    private static int ReadCounter(JsonElement root, ReadOnlySpan<byte> name)
        => TryReadPositiveInt(root, name, out var parsed) ? parsed : 0;

    private static int? ReadPositiveInt(JsonElement element, ReadOnlySpan<byte> name)
        => TryReadPositiveInt(element, name, out var parsed) ? parsed : null;

    private static bool TryReadPositiveInt(JsonElement element, ReadOnlySpan<byte> name, out int value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out value)
            && value > 0;
    }
}
