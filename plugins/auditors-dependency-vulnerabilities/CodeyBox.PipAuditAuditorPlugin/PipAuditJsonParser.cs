using System.Text;
using System.Text.Json;
using CodeyBox.PluginSdk.Tools;

namespace CodeyBox.PipAuditAuditorPlugin;

/// <summary>
/// Parses pip-audit's JSON report (<c>--format json</c>) into tool findings.
/// pip-audit emits a manifest object — <c>{"dependencies": [...], "fixes":
/// [...]}</c> — where each resolved dependency carries a <c>vulns</c> array
/// of <c>{id, fix_versions, aliases?, description?}</c> entries and each
/// skipped dependency carries a <c>skip_reason</c> instead of <c>vulns</c>.
///
/// <para>pip-audit reports no per-vulnerability severity: its
/// <c>VulnerabilityResult</c> carries an id, a description, fix versions,
/// and aliases, but no severity field. Vulnerability findings therefore
/// surface with a null tool severity and take the auditor's declared mapping
/// default; a <c>severity</c> property — absent from pip-audit 2.10.1 output
/// — is read only when present so a future report shape still flows through
/// the declared mapping instead of failing the parse.</para>
///
/// <para>Dependencies pip-audit declined to audit are not dropped silently:
/// each one surfaces as a single advisory finding with the synthesized
/// severity token <see cref="SkippedDependencyToken"/> (mapped to
/// advisory by the auditor), naming the package and the tool's skip reason,
/// so a coverage gap is visible rather than a partial pass.</para>
///
/// <para>pip-audit reports package identities, not file positions, so
/// findings carry no path or line. Malformed input — empty stdout, invalid
/// JSON, a missing <c>dependencies</c> array, or a vulnerability entry
/// without an id — throws <see cref="ExternalToolParseException"/>: the
/// base reports that as infrastructure (the check could not run), never as
/// a pass. That discriminator is load-bearing because pip-audit's exit
/// code alone cannot separate "found vulnerabilities" from "could not run"
/// (see the auditor's exit-code documentation).</para>
/// </summary>
public sealed class PipAuditJsonParser(
    int maxResults = ExternalToolReportLimits.DefaultMaxResults) : IExternalToolOutputParser
{
    private readonly int _maxResults = maxResults <= 0
        ? throw new ArgumentOutOfRangeException(nameof(maxResults), "Maximum pip-audit results must be positive.")
        : maxResults;

    /// <summary>
    /// Synthesized severity token for dependencies pip-audit skipped: not a
    /// tool-reported level, it marks a coverage gap the auditor maps to
    /// advisory so skips stay visible without failing the audit.
    /// </summary>
    public const string SkippedDependencyToken = "skipped";

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
                $"Tool '{input.ToolName}' produced output that is not valid pip-audit JSON: {ToolOutputText.SingleLine(ex.Message)}.",
                ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("dependencies"u8, out var dependencies)
                || dependencies.ValueKind != JsonValueKind.Array)
                throw new ExternalToolParseException(
                    $"Tool '{input.ToolName}' produced JSON without a pip-audit 'dependencies' array.");

            var findings = new List<ExternalToolFinding>();
            foreach (var dependency in dependencies.EnumerateArray())
            {
                if (findings.Count >= _maxResults)
                    break;
                if (dependency.ValueKind != JsonValueKind.Object)
                    continue;
                ParseDependency(input.ToolName, dependency, findings);
            }

            return findings;
        }
    }

    private void ParseDependency(
        string toolName,
        JsonElement dependency,
        List<ExternalToolFinding> findings)
    {
        var name = ExternalToolJsonHelpers.GetString(dependency, "name"u8);
        if (string.IsNullOrWhiteSpace(name))
            throw new ExternalToolParseException(
                $"Tool '{toolName}' reported a dependency without a name.");

        if (!dependency.TryGetProperty("vulns"u8, out var vulns)
            || vulns.ValueKind != JsonValueKind.Array)
        {
            if (findings.Count >= _maxResults)
                return;
            var skipReason = ExternalToolJsonHelpers.GetString(dependency, "skip_reason"u8);
            findings.Add(new ExternalToolFinding(
                SeverityLevel: SkippedDependencyToken,
                RuleId: null,
                Message: $"Package '{name.Trim()}' was skipped by pip-audit and was not checked "
                    + $"for vulnerabilities: {SkipReasonText(skipReason)}",
                Path: null,
                Line: null));
            return;
        }

        var version = ExternalToolJsonHelpers.GetString(dependency, "version"u8);
        var versionText = string.IsNullOrWhiteSpace(version) ? "(unknown)" : version.Trim();
        foreach (var vuln in vulns.EnumerateArray())
        {
            if (findings.Count >= _maxResults)
                break;
            if (vuln.ValueKind != JsonValueKind.Object)
                continue;
            findings.Add(ParseVulnerability(toolName, name.Trim(), versionText, vuln));
        }
    }

    private static ExternalToolFinding ParseVulnerability(
        string toolName,
        string name,
        string version,
        JsonElement vuln)
    {
        var id = ExternalToolJsonHelpers.GetString(vuln, "id"u8);
        if (string.IsNullOrWhiteSpace(id))
            throw new ExternalToolParseException(
                $"Tool '{toolName}' reported a vulnerability without an id for package '{name}'.");
        id = id.Trim();

        var fixVersions = ReadStringList(vuln, "fix_versions"u8);
        var aliases = ReadStringList(vuln, "aliases"u8);
        var description = ExternalToolJsonHelpers.GetString(vuln, "description"u8);
        var severity = ExternalToolJsonHelpers.GetString(vuln, "severity"u8);

        var message = new StringBuilder();
        message.Append($"Package '{name}@{version}' is vulnerable to '{id}'");
        if (aliases.Count > 0)
            message.Append($" (aliases: {string.Join(", ", aliases)})");
        var fixText = fixVersions.Count > 0
            ? $"fix in: {string.Join(", ", fixVersions)}."
            : "no fixed release reported.";
        message.Append($"; {fixText}");
        if (!string.IsNullOrWhiteSpace(description))
            message.Append($" {description.Trim()}");

        return new ExternalToolFinding(
            SeverityLevel: string.IsNullOrWhiteSpace(severity) ? null : severity.Trim(),
            RuleId: id,
            Message: message.ToString(),
            Path: null,
            Line: null);
    }

    private static string SkipReasonText(string? skipReason)
        => string.IsNullOrWhiteSpace(skipReason) ? "(no reason given)" : skipReason.Trim();

    private static List<string> ReadStringList(JsonElement element, ReadOnlySpan<byte> propertyName)
    {
        var values = new List<string>();
        if (!element.TryGetProperty(propertyName, out var array)
            || array.ValueKind != JsonValueKind.Array)
            return values;
        foreach (var item in array.EnumerateArray())
        {
            var text = ExternalToolJsonHelpers.CoerceString(item);
            if (!string.IsNullOrWhiteSpace(text))
                values.Add(text.Trim());
        }
        return values;
    }
}
