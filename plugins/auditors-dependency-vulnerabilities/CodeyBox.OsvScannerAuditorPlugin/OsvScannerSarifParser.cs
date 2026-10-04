using System.Globalization;
using System.Text.Json;
using CodeyBox.PluginSdk.Tools;

namespace CodeyBox.OsvScannerAuditorPlugin;

/// <summary>
/// Parses osv-scanner's SARIF report (<c>scan source --format sarif</c>) into
/// tool findings. osv-scanner emits every result at SARIF level
/// <c>warning</c> — the level carries no severity signal — and instead
/// records the computed CVSS score per rule in the rule metadata property
/// bag (<c>runs[].tool.driver.rules[].properties["security-severity"]</c>,
/// e.g. <c>"8.1"</c>). This parser indexes those scores and converts each
/// one to a qualitative severity token
/// (<c>critical</c>/<c>high</c>/<c>medium</c>/<c>low</c>/<c>unknown</c>) using
/// the standard CVSS qualitative bands, so the auditor's declared
/// <see cref="ExternalToolSeverityMapping"/> maps real severities rather
/// than a constant. Results without a scored rule (or with an unparseable
/// score) surface as <c>unknown</c> and take the mapping default.
/// Locations are the lockfile or manifest paths the tool reports
/// (<c>file://</c> absolute URIs or repo-relative paths), normalized to
/// repository-relative form; osv-scanner reports no line regions, so
/// findings are file-level.
/// </summary>
public sealed class OsvScannerSarifParser(
    int maxResults = ExternalToolReportLimits.DefaultMaxResults) : IExternalToolOutputParser
{
    private readonly int _maxResults = maxResults <= 0
        ? throw new ArgumentOutOfRangeException(nameof(maxResults), "Maximum SARIF results must be positive.")
        : maxResults;

    /// <summary>Qualitative token for a CVSS score at or above 9.0.</summary>
    public const string CriticalToken = "critical";

    /// <summary>Qualitative token for a CVSS score from 7.0 up to (excluding) 9.0.</summary>
    public const string HighToken = "high";

    /// <summary>Qualitative token for a CVSS score from 4.0 up to (excluding) 7.0.</summary>
    public const string MediumToken = "medium";

    /// <summary>Qualitative token for a CVSS score above 0 and below 4.0 (including an exact 0).</summary>
    public const string LowToken = "low";

    /// <summary>Qualitative token when the tool supplied no usable score.</summary>
    public const string UnknownToken = "unknown";

    /// <inheritdoc />
    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Stdout))
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced no SARIF output on stdout.");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(input.Stdout);
        }
        catch (JsonException ex)
        {
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced output that is not valid SARIF JSON: {ToolOutputText.SingleLine(ex.Message)}.",
                ex);
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("runs"u8, out var runs)
                || runs.ValueKind != JsonValueKind.Array)
                throw new ExternalToolParseException(
                    $"Tool '{input.ToolName}' produced JSON without a SARIF 'runs' array.");

            var findings = new List<ExternalToolFinding>();
            foreach (var run in runs.EnumerateArray())
            {
                if (findings.Count >= _maxResults)
                    break;
                if (run.ValueKind != JsonValueKind.Object)
                    continue;
                var severities = IndexRuleSeverities(run);
                if (!run.TryGetProperty("results"u8, out var results)
                    || results.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var result in results.EnumerateArray())
                {
                    if (findings.Count >= _maxResults)
                        break;
                    if (result.ValueKind == JsonValueKind.Object)
                        findings.Add(ParseResult(input, result, severities));
                }
            }

            return findings;
        }
    }

    private static Dictionary<string, string> IndexRuleSeverities(JsonElement run)
    {
        var severities = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!run.TryGetProperty("tool"u8, out var tool)
            || tool.ValueKind != JsonValueKind.Object
            || !tool.TryGetProperty("driver"u8, out var driver)
            || driver.ValueKind != JsonValueKind.Object
            || !driver.TryGetProperty("rules"u8, out var rules)
            || rules.ValueKind != JsonValueKind.Array)
            return severities;

        foreach (var rule in rules.EnumerateArray())
        {
            if (rule.ValueKind != JsonValueKind.Object)
                continue;
            var id = ExternalToolJsonHelpers.GetString(rule, "id"u8);
            if (string.IsNullOrWhiteSpace(id) || severities.ContainsKey(id))
                continue;
            severities[id] = TokenForRule(rule);
        }

        return severities;
    }

    private static string TokenForRule(JsonElement rule)
    {
        if (rule.TryGetProperty("properties"u8, out var properties)
            && properties.ValueKind == JsonValueKind.Object
            && properties.TryGetProperty("security-severity"u8, out var scoreElement)
            && TryReadScore(scoreElement, out var score))
            return MapScoreToToken(score);
        return UnknownToken;
    }

    private static bool TryReadScore(JsonElement element, out double score)
    {
        if (element.ValueKind == JsonValueKind.Number
            && element.TryGetDouble(out score))
            return true;
        var text = ExternalToolJsonHelpers.CoerceString(element);
        return double.TryParse(
            text?.Trim(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out score);
    }

    /// <summary>
    /// Maps a numeric CVSS score to the qualitative token the auditor's
    /// severity mapping consumes, using the standard CVSS qualitative
    /// severity bands (None 0.0, Low 0.1–3.9, Medium 4.0–6.9, High 7.0–8.9,
    /// Critical 9.0–10.0). A zero score is <c>low</c> — CodeyBox's lowest
    /// severity is informational, matching a no-impact score.
    /// </summary>
    public static string MapScoreToToken(double score)
    {
        if (double.IsNaN(score) || score < 0)
            return UnknownToken;
        if (score >= 9.0)
            return CriticalToken;
        if (score >= 7.0)
            return HighToken;
        if (score >= 4.0)
            return MediumToken;
        return LowToken;
    }

    private static ExternalToolFinding ParseResult(
        ExternalToolParseInput input,
        JsonElement result,
        IReadOnlyDictionary<string, string> severities)
    {
        string? ruleId = ExternalToolJsonHelpers.GetString(result, "ruleId"u8);
        if (string.IsNullOrWhiteSpace(ruleId)
            && result.TryGetProperty("rule"u8, out var rule)
            && rule.ValueKind == JsonValueKind.Object)
            ruleId = ExternalToolJsonHelpers.GetString(rule, "id"u8);

        var message = "(no message)";
        if (result.TryGetProperty("message"u8, out var messageElement)
            && messageElement.ValueKind == JsonValueKind.Object)
        {
            message = ExternalToolJsonHelpers.GetString(messageElement, "text"u8)
                ?? ExternalToolJsonHelpers.GetString(messageElement, "markdown"u8)
                ?? message;
        }

        string? uri = null;
        if (result.TryGetProperty("locations"u8, out var locations)
            && locations.ValueKind == JsonValueKind.Array)
        {
            foreach (var location in locations.EnumerateArray())
            {
                if (location.ValueKind != JsonValueKind.Object
                    || !location.TryGetProperty("physicalLocation"u8, out var physical)
                    || physical.ValueKind != JsonValueKind.Object
                    || !physical.TryGetProperty("artifactLocation"u8, out var artifact)
                    || artifact.ValueKind != JsonValueKind.Object)
                    continue;
                uri = ExternalToolJsonHelpers.GetString(artifact, "uri"u8);
                if (!string.IsNullOrWhiteSpace(uri))
                    break;
            }
        }

        // osv-scanner attaches no region to dependency results — the match
        // is against the lockfile as a whole, not a line — so findings stay
        // file-level. The shared path policy strips file:// URIs,
        // relativizes absolute paths against the run directories, and marks
        // anything escaping the tree instead of guessing.
        var path = ExternalToolJsonHelpers.NormalizeReportedPath(uri, input.ScanRoot, input.WorkingDirectory);
        var severity = !string.IsNullOrWhiteSpace(ruleId) && severities.TryGetValue(ruleId, out var token)
            ? token
            : UnknownToken;

        return new ExternalToolFinding(
            SeverityLevel: severity,
            RuleId: string.IsNullOrWhiteSpace(ruleId) ? null : ruleId,
            Message: string.IsNullOrWhiteSpace(message) ? "(no message)" : message,
            Path: path,
            Line: null);
    }
}
