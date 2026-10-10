using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeyBox.PluginSdk.Tools;

/// <summary>
/// SARIF decorator for dialects whose results do not carry the severity the
/// auditor should map. Some tools (CodeQL, Semgrep) record a rule's severity
/// once per run in <c>tool.driver.rules[]</c> — as
/// <c>defaultConfiguration.level</c> or a scalar <c>properties</c> key —
/// instead of repeating it on every result; others (gosec) flatten several
/// native severities into the same result <c>level</c> and keep the real
/// vocabulary in non-scalar rule metadata such as <c>properties.tags</c>.
/// For each result this parser resolves the level from the referenced rule's
/// metadata before delegating shape parsing (rule id, message, location) to
/// the inner parser. Results that carry their own <c>level</c> are honored
/// as-is — matching SARIF's level-resolution order — unless the dialect's
/// per-result level is a lossy rendering and the decorator was configured to
/// overwrite it. The recovered token still flows through the auditor's
/// declared <see cref="ExternalToolSeverityMapping"/>; raw tool levels never
/// reach findings.
/// </summary>
public sealed class SarifRuleMetadataOutputParser : IExternalToolOutputParser
{
    private readonly IExternalToolOutputParser _inner;
    private readonly Func<JsonObject, string?> _ruleSeverityResolver;
    private readonly bool _overwriteResultLevel;

    /// <param name="inner">Parser that consumes the level-annotated document.</param>
    /// <param name="rulePropertyFallbackKeys">
    /// Additional <c>rules[].properties</c> keys consulted, in order, when a
    /// rule carries no <c>defaultConfiguration.level</c> (e.g. CodeQL's
    /// <c>problem.severity</c>).
    /// </param>
    public SarifRuleMetadataOutputParser(
        IExternalToolOutputParser inner,
        params string[] rulePropertyFallbackKeys)
        : this(inner, ScalarSeverityResolver(rulePropertyFallbackKeys ?? []), overwriteResultLevel: false)
    {
    }

    /// <param name="inner">Parser that consumes the level-annotated document.</param>
    /// <param name="ruleSeverityResolver">
    /// Extracts a rule descriptor's severity token — for dialects whose
    /// vocabulary lives somewhere the scalar
    /// <c>defaultConfiguration.level</c>/<c>properties</c>-key shape cannot
    /// express (e.g. membership in a <c>properties.tags</c> array). Return
    /// null when the rule carries none; a blank token is ignored too.
    /// </param>
    /// <param name="overwriteResultLevel">
    /// Overwrite a result's own <c>level</c> when its rule resolves a
    /// severity — for dialects whose per-result level is a lossy rendering
    /// of the rule's native severity (gosec flattens MEDIUM and HIGH both
    /// to <c>error</c>). Leave false when the result level is authoritative
    /// and rule metadata is only a fallback for results that omit it.
    /// </param>
    public SarifRuleMetadataOutputParser(
        IExternalToolOutputParser inner,
        Func<JsonObject, string?> ruleSeverityResolver,
        bool overwriteResultLevel = false)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(ruleSeverityResolver);
        _inner = inner;
        _ruleSeverityResolver = ruleSeverityResolver;
        _overwriteResultLevel = overwriteResultLevel;
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
                if (!_overwriteResultLevel && IsNonBlankString(result["level"]))
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

    // The scalar-metadata dialect: defaultConfiguration.level first, then
    // the configured properties keys in order. Every node is shape-checked
    // before indexing — a JsonValue/JsonArray where an object belongs is
    // malformed input the inner parser reports, not a reason to throw an
    // untyped InvalidOperationException.
    private static Func<JsonObject, string?> ScalarSeverityResolver(IReadOnlyList<string> fallbackKeys)
        => rule =>
        {
            if (rule["defaultConfiguration"] is JsonObject defaults
                && CoerceString(defaults["level"]) is { } level)
                return level;
            if (rule["properties"] is not JsonObject properties)
                return null;
            foreach (var key in fallbackKeys)
            {
                if (CoerceString(properties[key]) is { } severity)
                    return severity;
            }
            return null;
        };

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
            if (_ruleSeverityResolver(rule) is { } severity
                && !string.IsNullOrWhiteSpace(severity))
                severities[id] = severity;
        }

        return severities;
    }

    private static string? ResultRuleId(JsonObject result)
    {
        var ruleId = CoerceString(result["ruleId"]);
        if (!string.IsNullOrWhiteSpace(ruleId))
            return ruleId;
        return result["rule"] is JsonObject ruleRef ? CoerceString(ruleRef["id"]) : null;
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
