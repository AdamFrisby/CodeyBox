using System.Text.Json;
using CodeyBox.PluginSdk.Tools;

namespace CodeyBox.GosecAuditorPlugin;

/// <summary>
/// SARIF parser for gosec's <c>-fmt sarif -stdout</c> report. Delegates the
/// run/result → finding shape to the shared <see cref="SarifToolOutputParser"/>
/// and adds the two things the shared parser cannot express for this tool:
///
/// <para><b>Native severity recovery.</b> gosec flattens its LOW/MEDIUM/HIGH
/// severity into the SARIF <c>level</c> (LOW → <c>warning</c>, MEDIUM and
/// HIGH → <c>error</c>), so <c>level</c> alone would make MEDIUM findings
/// block like HIGH ones. The native severity survives in each rule
/// descriptor's <c>properties.tags</c> (<c>["security", "HIGH"]</c>); this
/// parser resolves <c>ruleId</c> → descriptor and substitutes the native
/// token so the auditor's declared severity map sees the tool's real
/// vocabulary. When the descriptor carries no recognizable severity the
/// finding keeps its SARIF <c>level</c>, which the map also covers.</para>
///
/// <para><b>Exit-1 discrimination.</b> gosec exits 1 both for "found
/// unsuppressed issues" and for "recorded per-file/per-package analysis
/// errors" — and its SARIF report carries no error detail, so a run that
/// could not analyze anything emits a perfectly valid document with an
/// empty <c>results</c> array. Reading that as a clean pass would let a
/// broken or unloaded package evade the audit, so exit 1 with zero parsed
/// findings throws <see cref="ExternalToolParseException"/> — reported by
/// the base as infrastructure, never a pass.</para>
/// </summary>
internal sealed class GosecSarifOutputParser : IExternalToolOutputParser
{
    // A scan's rule set is the ~150 built-in rules; a bound guards against a
    // malformed document carrying a huge descriptor list.
    private const int MaxRuleDescriptors = 4096;

    private static readonly HashSet<string> SeverityTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "LOW", "MEDIUM", "HIGH",
    };

    private readonly SarifToolOutputParser _inner = new();

    /// <inheritdoc />
    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Throws on empty or malformed stdout — an exit-1 run that wrote no
        // report is an operational failure, surfaced as infrastructure.
        var findings = _inner.Parse(input);

        if (input.ExitCode == GosecAuditor.IssuesOrErrorsExitCode && findings.Count == 0)
        {
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' exited {GosecAuditor.IssuesOrErrorsExitCode} with a SARIF report "
                + "carrying no findings. gosec raises that exit for analysis errors (packages it could "
                + "not load or parse) as well as for issues, and its SARIF report has no error channel — "
                + "an empty report here means the scan errored, not that the tree is clean.");
        }

        var severities = ExtractRuleSeverities(input.Stdout);
        if (severities.Count == 0)
            return findings;

        var remapped = new List<ExternalToolFinding>(findings.Count);
        foreach (var finding in findings)
        {
            remapped.Add(
                finding.RuleId is not null
                && severities.TryGetValue(finding.RuleId, out var severity)
                    ? finding with { SeverityLevel = severity }
                    : finding);
        }

        return remapped;
    }

    /// <summary>
    /// Maps each declared rule id to its gosec severity token
    /// (<c>HIGH</c>/<c>MEDIUM</c>/<c>LOW</c>), recovered from
    /// <c>runs[].tool.driver.rules[].properties.tags</c>. Returns an empty
    /// map when no descriptor severities are readable — findings then keep
    /// their SARIF <c>level</c>. Never throws on descriptor oddities: a
    /// missing severity annotation is a downgrade, not a parse failure.
    /// </summary>
    private static IReadOnlyDictionary<string, string> ExtractRuleSeverities(string sarif)
    {
        var severities = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(sarif);
            if (!document.RootElement.TryGetProperty("runs"u8, out var runs)
                || runs.ValueKind != JsonValueKind.Array)
                return severities;

            foreach (var run in runs.EnumerateArray())
            {
                if (run.ValueKind != JsonValueKind.Object
                    || !run.TryGetProperty("tool"u8, out var tool)
                    || tool.ValueKind != JsonValueKind.Object
                    || !tool.TryGetProperty("driver"u8, out var driver)
                    || driver.ValueKind != JsonValueKind.Object
                    || !driver.TryGetProperty("rules"u8, out var rules)
                    || rules.ValueKind != JsonValueKind.Array)
                    continue;

                var count = 0;
                foreach (var rule in rules.EnumerateArray())
                {
                    if (++count > MaxRuleDescriptors)
                        break;
                    if (rule.ValueKind != JsonValueKind.Object
                        || !rule.TryGetProperty("id"u8, out var id)
                        || id.ValueKind != JsonValueKind.String)
                        continue;

                    var severity = SeverityOf(rule);
                    if (severity is not null)
                        severities.TryAdd(id.GetString()!, severity);
                }
            }
        }
        catch (JsonException)
        {
            // The shared parser already reported malformed JSON; severity
            // recovery is best-effort on top of it.
        }

        return severities;
    }

    /// <summary>
    /// Reads the gosec severity from a rule descriptor: the element of
    /// <c>properties.tags</c> that is a gosec severity token
    /// (<c>HIGH</c>/<c>MEDIUM</c>/<c>LOW</c>). Null when absent or
    /// unrecognised.
    /// </summary>
    private static string? SeverityOf(JsonElement rule)
    {
        if (!rule.TryGetProperty("properties"u8, out var properties)
            || properties.ValueKind != JsonValueKind.Object
            || !properties.TryGetProperty("tags"u8, out var tags)
            || tags.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var tag in tags.EnumerateArray())
        {
            if (tag.ValueKind == JsonValueKind.String
                && tag.GetString() is { } value
                && SeverityTags.Contains(value))
                return value.ToUpperInvariant();
        }

        return null;
    }
}
