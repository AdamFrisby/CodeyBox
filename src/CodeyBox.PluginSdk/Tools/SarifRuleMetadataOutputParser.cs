using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeyBox.PluginSdk.Tools;

/// <summary>
/// SARIF decorator for dialects whose results omit <c>level</c>: some tools
/// (CodeQL, Semgrep) record a rule's severity once per run in
/// <c>tool.driver.rules[]</c> as <c>defaultConfiguration.level</c> instead of
/// repeating it on every result. For each result without its own
/// <c>level</c>, this parser injects the level resolved from the referenced
/// rule's metadata — <c>defaultConfiguration.level</c> first, then any
/// configured <c>properties</c> fallback keys — before delegating shape
/// parsing (rule id, message, location) to the inner parser. Results that
/// carry their own <c>level</c> are honored as-is, matching SARIF's
/// level-resolution order. The recovered token still flows through the
/// auditor's declared <see cref="ExternalToolSeverityMapping"/>; raw tool
/// levels never reach findings.
/// </summary>
public sealed class SarifRuleMetadataOutputParser : IExternalToolOutputParser
{
    private readonly IExternalToolOutputParser _inner;
    private readonly IReadOnlyList<string> _rulePropertyFallbackKeys;

    /// <param name="inner">Parser that consumes the level-annotated document.</param>
    /// <param name="rulePropertyFallbackKeys">
    /// Additional <c>rules[].properties</c> keys consulted, in order, when a
    /// rule carries no <c>defaultConfiguration.level</c> (e.g. CodeQL's
    /// <c>problem.severity</c>).
    /// </param>
    public SarifRuleMetadataOutputParser(
        IExternalToolOutputParser inner,
        params string[] rulePropertyFallbackKeys)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _rulePropertyFallbackKeys = rulePropertyFallbackKeys ?? [];
    }

    /// <inheritdoc />
    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Stdout))
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced no SARIF output on stdout.");

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(input.Stdout);
        }
        catch (JsonException ex)
        {
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced output that is not valid SARIF JSON: {ToolOutputText.SingleLine(ex.Message)}.",
                ex);
        }

        if (root is not JsonObject document
            || document["runs"] is not JsonArray runs)
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced JSON without a SARIF 'runs' array.");

        foreach (var run in runs.OfType<JsonObject>())
        {
            var severities = CollectRuleSeverities(run);
            if (run["results"] is not JsonArray results)
                continue;
            foreach (var result in results.OfType<JsonObject>())
            {
                if (IsNonBlankString(result["level"]))
                    continue;
                var ruleId = ResultRuleId(result);
                if (ruleId is not null && severities.TryGetValue(ruleId, out var severity))
                    result["level"] = severity;
            }
        }

        return _inner.Parse(new ExternalToolParseInput(
            input.ToolName, root.ToJsonString(), input.Stderr, input.ExitCode,
            input.ScanRoot, input.WorkingDirectory));
    }

    private Dictionary<string, string> CollectRuleSeverities(JsonObject run)
    {
        var severities = new Dictionary<string, string>(StringComparer.Ordinal);
        if (run["tool"] is not JsonObject tool
            || tool["driver"] is not JsonObject driver
            || driver["rules"] is not JsonArray rules)
            return severities;

        foreach (var rule in rules.OfType<JsonObject>())
        {
            var id = CoerceString(rule["id"]);
            if (string.IsNullOrWhiteSpace(id) || severities.ContainsKey(id))
                continue;
            var severity = CoerceString(rule["defaultConfiguration"]?["level"]);
            for (var i = 0; severity is null && i < _rulePropertyFallbackKeys.Count; i++)
                severity = CoerceString(rule["properties"]?[_rulePropertyFallbackKeys[i]]);
            if (!string.IsNullOrWhiteSpace(severity))
                severities[id] = severity;
        }

        return severities;
    }

    private static string? ResultRuleId(JsonObject result)
    {
        var ruleId = CoerceString(result["ruleId"]);
        if (!string.IsNullOrWhiteSpace(ruleId))
            return ruleId;
        return CoerceString(result["rule"]?["id"]);
    }

    private static bool IsNonBlankString(JsonNode? node)
        => node is JsonValue value
            && value.TryGetValue<string>(out var text)
            && !string.IsNullOrWhiteSpace(text);

    private static string? CoerceString(JsonNode? node)
        => node is JsonValue value
            && value.TryGetValue<string>(out var text)
            && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;
}
