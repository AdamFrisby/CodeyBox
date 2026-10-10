using System.Text.Json;
using System.Text.Json.Nodes;
using CodeyBox.PluginSdk.Tools;

namespace CodeyBox.GosecAuditorPlugin;

/// <summary>
/// SARIF decorator for gosec's dialect: every result carries a
/// <c>level</c>, but that level is a lossy rendering of gosec's native
/// severity — <see href="https://github.com/securego/gosec">gosec</see>
/// <c>report/sarif/formatter.go#getSarifLevel</c> maps MEDIUM and HIGH both
/// to <c>error</c>, LOW to <c>warning</c>, and anything else to
/// <c>note</c>. gosec's own severity vocabulary (HIGH/MEDIUM/LOW) survives
/// intact in each rule descriptor's <c>properties.tags</c>
/// (<c>["security", "HIGH"]</c>). This parser recovers the native token per
/// rule id and rewrites each result's <c>level</c> with it before
/// delegating shape parsing to the inner <see
/// cref="SarifToolOutputParser"/> — so the auditor's declared severity map
/// can give gosec's "high" the same meaning every other auditor's "high"
/// has (a MEDIUM that would read as SARIF <c>error</c> maps to Warning,
/// not Error). Results whose rule carries no severity tag keep their
/// reported level.
///
/// <para>It also encodes half of gosec's ambiguous exit convention:
/// exit <c>1</c> means "unsuppressed issues OR processing errors". The
/// auditor verifies the errors half against the JSON side-report before
/// this parser runs; reaching here with exit 1 and a findings-free report
/// means the exit's reason is unverifiable — fail closed rather than pass
/// on a scan whose outcome cannot be classified.</para>
/// </summary>
public sealed class GosecSarifOutputParser : IExternalToolOutputParser
{
    /// <summary>gosec's "issues were found or errors were recorded" exit.</summary>
    internal const int FindingsOrErrorsExitCode = 1;

    private static readonly IReadOnlySet<string> SeverityTags =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "LOW", "MEDIUM", "HIGH" };

    private readonly IExternalToolOutputParser _inner = new SarifToolOutputParser();

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
            if (severities.Count == 0 || run["results"] is not JsonArray results)
                continue;
            foreach (var result in results.OfType<JsonObject>())
            {
                var ruleId = ResultRuleId(result);
                if (ruleId is not null && severities.TryGetValue(ruleId, out var severity))
                    result["level"] = severity;
            }
        }

        var findings = _inner.Parse(new ExternalToolParseInput(
            input.ToolName, root.ToJsonString(), input.Stderr, input.ExitCode,
            input.ScanRoot, input.WorkingDirectory));

        if (input.ExitCode == FindingsOrErrorsExitCode && findings.Count == 0)
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' exited {FindingsOrErrorsExitCode} (its "
                + "issues-or-processing-errors verdict) but the SARIF report carries no results "
                + "— the exit's reason is unverifiable, so the run cannot be classified as a "
                + "verdict.");

        return findings;
    }

    // gosec records each rule's native severity in the descriptor's tags
    // array next to "security" (["security", "HIGH"]) — the only place the
    // un-flattened vocabulary survives the SARIF conversion.
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
            if (rule["properties"]?["tags"] is not JsonArray tags)
                continue;
            foreach (var tag in tags)
            {
                var value = CoerceString(tag);
                if (value is not null && SeverityTags.Contains(value))
                {
                    severities[id] = value.ToUpperInvariant();
                    break;
                }
            }
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

    private static string? CoerceString(JsonNode? node)
        => node is JsonValue value
            && value.TryGetValue<string>(out var text)
            && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;
}
