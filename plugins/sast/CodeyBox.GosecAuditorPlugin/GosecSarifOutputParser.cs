using System.Text.Json.Nodes;
using CodeyBox.PluginSdk.Tools;

namespace CodeyBox.GosecAuditorPlugin;

/// <summary>
/// SARIF parser for gosec's dialect: every result carries a
/// <c>level</c>, but that level is a lossy rendering of gosec's native
/// severity — <see href="https://github.com/securego/gosec">gosec</see>
/// <c>report/sarif/formatter.go#getSarifLevel</c> maps MEDIUM and HIGH both
/// to <c>error</c>, LOW to <c>warning</c>, and anything else to
/// <c>note</c>. gosec's own severity vocabulary (HIGH/MEDIUM/LOW) survives
/// intact in each rule descriptor's <c>properties.tags</c>
/// (<c>["security", "HIGH"]</c>), so the shared <see
/// cref="SarifRuleMetadataOutputParser"/> decorator is configured with the
/// tag resolver below and set to overwrite the flattened result level —
/// the auditor's declared severity map can then give gosec's "high" the
/// same meaning every other auditor's "high" has (a MEDIUM that would
/// read as SARIF <c>error</c> maps to Warning, not Error). Results whose
/// rule carries no severity tag keep their reported level.
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

    private readonly IExternalToolOutputParser _inner = new SarifRuleMetadataOutputParser(
        new SarifToolOutputParser(), SeverityFromRuleTags, overwriteResultLevel: true);

    /// <inheritdoc />
    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var findings = _inner.Parse(input);

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
    private static string? SeverityFromRuleTags(JsonObject rule)
    {
        if (rule["properties"] is not JsonObject properties
            || properties["tags"] is not JsonArray tags)
            return null;
        foreach (var tag in tags)
        {
            if (tag is JsonValue value
                && value.TryGetValue<string>(out var text)
                && SeverityTags.Contains(text))
                return text.ToUpperInvariant();
        }
        return null;
    }
}
