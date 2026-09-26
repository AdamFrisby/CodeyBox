using System.Text.Json;
using System.Text.Json.Nodes;
using CodeyBox.PluginSdk.Tools;

namespace CodeyBox.CodeqlAuditorPlugin;

/// <summary>
/// Parser for the SARIF dialect <c>codeql database analyze</c> emits.
/// Individual results carry no <c>level</c> — CodeQL records each query's
/// severity once per run in <c>tool.driver.rules[]</c> (as
/// <c>defaultConfiguration.level</c>, with the query's own
/// <c>properties["problem.severity"]</c> as fallback) — so this parser
/// recovers the per-result severity token from that rule metadata (falling
/// back to the SARIF default of <c>"warning"</c>) and then delegates the
/// shape parsing (rule id, message, location) to the shared
/// <see cref="SarifToolOutputParser"/>. The recovered token is still mapped
/// through the auditor's declared <see cref="ExternalToolSeverityMapping"/>;
/// raw tool levels never reach findings.
/// </summary>
public sealed class CodeqlSarifOutputParser : IExternalToolOutputParser
{
    private readonly SarifToolOutputParser _inner;

    public CodeqlSarifOutputParser(int maxResults = SarifToolOutputParser.DefaultMaxResults)
        => _inner = new SarifToolOutputParser(maxResults);

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
                $"Tool '{input.ToolName}' produced output that is not valid SARIF JSON: {SingleLine(ex.Message)}.",
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
            input.ToolName, root.ToJsonString(), input.Stderr, input.ExitCode));
    }

    private static Dictionary<string, string> CollectRuleSeverities(JsonObject run)
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
            var severity = CoerceString(rule["defaultConfiguration"]?["level"])
                ?? CoerceString(rule["properties"]?["problem.severity"]);
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

    private static string SingleLine(string message)
        => message.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
