using System.Text.Json;
using CodeyBox.PluginSdk.Tools;

namespace CodeyBox.SocketAuditorPlugin;

/// <summary>
/// Parses the JSON policy report <c>socket scan create --json --report</c>
/// prints on stdout into findings. The report nests alerts as
/// <c>ecosystem → package → version → file → "line:col" → alert</c> (folded
/// shallower when the operator passes <c>--fold</c>); each leaf alert carries
/// the Socket alert <c>type</c> (the rule id), the organisation-policy action
/// <c>policy</c> (the severity vocabulary), a package-overview <c>url</c>, and
/// the manifest files it was seen in. File and line come from the nesting
/// keys wherever the tool supplies them; folded reports fall back to the
/// leaf's first manifest file.
/// </summary>
/// <remarks>
/// Socket's exit code is ambiguous by design (verified against socket 1.4.1):
/// <c>1</c> means both "report is unhealthy" and "the scan could not run".
/// This parser is the disambiguator: a <c>{ok:true, data:{alerts…}}</c>
/// envelope is the verdict, while the <c>{ok:false,…}</c> error envelope —
/// missing token or org, API failure, bad input — throws
/// <see cref="ExternalToolParseException"/> so the base reports
/// infrastructure, never findings and never a pass. An unhealthy report with
/// no extractable alerts likewise fails closed instead of passing silently.
/// </remarks>
public sealed class SocketScanReportParser : IExternalToolOutputParser
{
    /// <summary>Upper bound on alerts consumed from one report document.</summary>
    public const int MaxAlerts = ExternalToolReportLimits.DefaultMaxResults;

    // Nesting below this depth cannot be a Socket report (the full shape is
    // ecosystem → package → version → file → line:col → alert): stop and fail
    // closed rather than recursing into attacker-shaped JSON without bound.
    private const int MaxNestingDepth = 16;

    private const int TokenMaxChars = 256;

    /// <inheritdoc />
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
                $"Tool '{input.ToolName}' produced output that is not valid JSON: {ToolOutputText.SingleLine(ex.Message)}.",
                ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new ExternalToolParseException(
                    $"Tool '{input.ToolName}' produced JSON without a Socket report object.");

            if (IsErrorEnvelope(root))
                throw new ExternalToolParseException(
                    $"Tool '{input.ToolName}' could not run: {DescribeErrorEnvelope(root)}");

            if (!root.TryGetProperty("data"u8, out var data) || data.ValueKind != JsonValueKind.Object)
                throw new ExternalToolParseException(
                    $"Tool '{input.ToolName}' produced JSON without a Socket scan 'data' object.");

            var healthy = data.TryGetProperty("healthy"u8, out var healthyProperty)
                && healthyProperty.ValueKind == JsonValueKind.True;
            var hasHealthyFlag = data.TryGetProperty("healthy"u8, out _);

            var findings = new List<ExternalToolFinding>();
            if (data.TryGetProperty("alerts"u8, out var alerts) && alerts.ValueKind == JsonValueKind.Object)
                WalkAlerts(input.ToolName, alerts, [], findings, depth: 0);

            if (findings.Count == 0 && (!hasHealthyFlag || !healthy))
                throw new ExternalToolParseException(
                    $"Tool '{input.ToolName}' reported an unhealthy (or unrecognised) scan but the report " +
                    "carries no alerts to report — refusing to pass a scan the tool itself flagged.");

            return findings;
        }
    }

    private static bool IsErrorEnvelope(JsonElement root)
        => root.TryGetProperty("ok"u8, out var ok) && ok.ValueKind == JsonValueKind.False;

    private static string DescribeErrorEnvelope(JsonElement root)
    {
        var message = root.TryGetProperty("message"u8, out var messageProperty)
            && messageProperty.ValueKind == JsonValueKind.String
            ? messageProperty.GetString()
            : null;
        string? detail = null;
        foreach (var key in (ReadOnlySpan<string>)["data", "cause"])
        {
            if (root.TryGetProperty(key, out var detailProperty)
                && detailProperty.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(detailProperty.GetString()))
            {
                detail = detailProperty.GetString();
                break;
            }
        }

        var summary = string.IsNullOrWhiteSpace(message) ? "unknown failure" : message;
        var combined = string.IsNullOrWhiteSpace(detail) ? summary : $"{summary} — {detail}";
        return ToolOutputText.SingleLine(Truncate(combined, TokenMaxChars));
    }

    private static void WalkAlerts(
        string tool,
        JsonElement node,
        List<string> segments,
        List<ExternalToolFinding> findings,
        int depth)
    {
        if (depth > MaxNestingDepth)
            throw new ExternalToolParseException(
                $"Tool '{tool}' produced alerts nested deeper than the Socket report shape allows.");
        if (findings.Count >= MaxAlerts)
            return;

        foreach (var property in node.EnumerateObject())
        {
            if (findings.Count >= MaxAlerts)
                return;
            if (property.Value.ValueKind != JsonValueKind.Object)
                continue;

            if (IsAlertLeaf(property.Value))
            {
                findings.Add(BuildFinding(property.Value, segments, property.Name));
                continue;
            }

            segments.Add(property.Name);
            try
            {
                WalkAlerts(tool, property.Value, segments, findings, depth + 1);
            }
            finally
            {
                segments.RemoveAt(segments.Count - 1);
            }
        }
    }

    private static bool IsAlertLeaf(JsonElement node)
        => node.TryGetProperty("type"u8, out var type)
            && type.ValueKind == JsonValueKind.String
            && node.TryGetProperty("policy"u8, out var policy)
            && policy.ValueKind == JsonValueKind.String;

    private static ExternalToolFinding BuildFinding(JsonElement leaf, List<string> segments, string leafKey)
    {
        var type = Truncate(leaf.GetProperty("type"u8).GetString() ?? string.Empty, TokenMaxChars);
        var policy = Truncate(leaf.GetProperty("policy"u8).GetString() ?? string.Empty, TokenMaxChars);
        var url = leaf.TryGetProperty("url"u8, out var urlProperty)
            && urlProperty.ValueKind == JsonValueKind.String
            ? Truncate(urlProperty.GetString() ?? string.Empty, TokenMaxChars)
            : string.Empty;

        // Only leaves at depth 4+ (file, or file + line:col under it) carry
        // their location in the nesting keys: at shallower folds the leaf
        // key is a grouping key (package or version), never a path, so the
        // location must come from the leaf's manifest list instead.
        string? path = null;
        int? line = null;
        if (segments.Count >= 3)
        {
            var trail = segments.GetRange(3, segments.Count - 3);
            trail.Add(leafKey);
            (path, line) = SplitFileAndLine(trail);
        }

        if (string.IsNullOrWhiteSpace(path))
            path = FirstManifestFile(leaf);

        var ecosystem = Segment(segments, 0);
        var package = Segment(segments, 1);
        var version = Segment(segments, 2);
        var where = string.IsNullOrWhiteSpace(path) ? "an unknown manifest" : $"in {path}";
        var message = string.IsNullOrWhiteSpace(url)
            ? $"Socket supply-chain alert '{type}' (policy {policy}) on {ecosystem}/{package}@{version} {where}."
            : $"Socket supply-chain alert '{type}' (policy {policy}) on {ecosystem}/{package}@{version} {where} ({url}).";

        return new ExternalToolFinding(
            SeverityLevel: policy,
            RuleId: type,
            Message: message,
            Path: string.IsNullOrWhiteSpace(path) ? null : path,
            Line: line);
    }

    private static (string? Path, int? Line) SplitFileAndLine(List<string> trail)
    {
        if (trail.Count == 1)
            return (Truncate(trail[0], TokenMaxChars), null);

        var last = trail[^1];
        if (TryParseLineColumn(last, out var line))
            return (Truncate(string.Join("/", trail.GetRange(0, trail.Count - 1)), TokenMaxChars), line);
        return (Truncate(string.Join("/", trail), TokenMaxChars), null);
    }

    private static bool TryParseLineColumn(string value, out int? line)
    {
        line = null;
        var colon = value.IndexOf(':');
        if (colon <= 0)
            return false;
        if (!int.TryParse(value.AsSpan(0, colon), out var parsed) || parsed <= 0)
            return false;
        line = parsed;
        return true;
    }

    private static string? FirstManifestFile(JsonElement leaf)
    {
        if (leaf.TryGetProperty("manifest"u8, out var manifest)
            && manifest.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in manifest.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(entry.GetString()))
                    return Truncate(entry.GetString()!, TokenMaxChars);
            }
        }

        return null;
    }

    private static string Segment(List<string> segments, int index)
        => segments.Count > index
            ? Truncate(segments[index], TokenMaxChars)
            : "<unknown>";

    private static string Truncate(string value, int maxChars)
        => value.Length <= maxChars ? value : value[..maxChars];
}
