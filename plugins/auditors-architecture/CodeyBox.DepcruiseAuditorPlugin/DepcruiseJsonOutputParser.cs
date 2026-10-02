using System.Text.Json;
using CodeyBox.PluginSdk.Tools;
using static CodeyBox.PluginSdk.Tools.ExternalToolJsonHelpers;

namespace CodeyBox.DepcruiseAuditorPlugin;

/// <summary>
/// Parses <c>depcruise --output-type json</c> output —
/// <c>{ "modules": […], "summary": { "violations": [ { "type", "from", "to",
/// "rule": { "severity", "name" }, "cycle"? } ] } }</c> — into
/// <see cref="ExternalToolFinding"/> records. Verified against
/// dependency-cruiser 18.4.0: every completed run (clean or with violations
/// of any severity) exits 0 with this document on stdout, so the violations
/// array — not the exit code — is the verdict.
///
/// <para>Each violation's tool level is its rule's <c>severity</c>
/// (<c>error</c>, <c>warn</c>, <c>info</c>, <c>ignore</c>) and its rule id is
/// the rule's <c>name</c>; <see cref="ExternalToolAuditorBase"/> maps the
/// level through the auditor's declared
/// <see cref="ExternalToolSeverityMapping"/> so raw levels never reach
/// findings. The location is the violation's <c>from</c> module — the file
/// holding the forbidden dependency. dependency-cruiser reports no line
/// numbers for violations, so findings carry a path without a line.</para>
///
/// <para>Malformed output — empty stdout, non-JSON, or a JSON document
/// without a <c>summary.violations</c> array (the missing-config error text,
/// usage errors, and help dumps depcruise prints when it could not run) —
/// throws <see cref="ExternalToolParseException"/>, which the base reports
/// as infrastructure, never as a pass.</para>
/// </summary>
internal sealed class DepcruiseJsonOutputParser : IExternalToolOutputParser
{
    // Same per-document result bound the shared SARIF parser applies.
    private const int MaxResults = SarifToolOutputParser.DefaultMaxResults;
    private const int MessageMaxChars = 512;

    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Stdout))
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced no dependency-cruiser JSON output on stdout.");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(input.Stdout);
        }
        catch (JsonException ex)
        {
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced output that is not valid dependency-cruiser JSON: {SingleLine(ex.Message)}.",
                ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("summary"u8, out var summary)
                || summary.ValueKind != JsonValueKind.Object
                || !summary.TryGetProperty("violations"u8, out var violations)
                || violations.ValueKind != JsonValueKind.Array)
            {
                throw new ExternalToolParseException(
                    $"Tool '{input.ToolName}' produced JSON that is not a dependency-cruiser report "
                    + "(missing the 'summary.violations' array).");
            }

            var findings = new List<ExternalToolFinding>();
            foreach (var violation in violations.EnumerateArray())
            {
                if (findings.Count >= MaxResults)
                    break;
                if (violation.ValueKind == JsonValueKind.Object)
                    findings.Add(ParseViolation(violation));
            }

            return findings;
        }
    }

    private static ExternalToolFinding ParseViolation(JsonElement violation)
    {
        string? severity = null;
        string? ruleName = null;
        if (violation.TryGetProperty("rule"u8, out var rule)
            && rule.ValueKind == JsonValueKind.Object)
        {
            severity = NullIfWhiteSpace(GetString(rule, "severity"u8));
            ruleName = NullIfWhiteSpace(GetString(rule, "name"u8));
        }

        var from = NullIfWhiteSpace(GetString(violation, "from"u8));
        var to = NullIfWhiteSpace(GetString(violation, "to"u8));
        var type = NullIfWhiteSpace(GetString(violation, "type"u8));

        return new ExternalToolFinding(
            SeverityLevel: severity,
            RuleId: ruleName,
            Message: BuildMessage(type, from, to, CycleChain(violation)),
            Path: NormalizeViolationPath(from),
            Line: null);
    }

    private static string BuildMessage(string? type, string? from, string? to, string? cycle)
    {
        var label = type switch
        {
            "cycle" => "Circular dependency",
            "module" => "Module violation",
            "dependency" => "Forbidden dependency",
            null or "" => "Dependency violation",
            _ => "Dependency violation",
        };

        string endpoints;
        if (!string.IsNullOrEmpty(from) && !string.IsNullOrEmpty(to)
            && !string.Equals(from, to, StringComparison.Ordinal))
            endpoints = $"'{from}' → '{to}'";
        else if (!string.IsNullOrEmpty(from))
            endpoints = $"'{from}'";
        else if (!string.IsNullOrEmpty(to))
            endpoints = $"'{to}'";
        else
            endpoints = "(location not reported)";

        var message = $"{label}: {endpoints}";
        if (!string.IsNullOrEmpty(cycle))
            message += $" (cycle: {cycle})";
        return Truncate(message, MessageMaxChars);
    }

    private static string? CycleChain(JsonElement violation)
    {
        if (!violation.TryGetProperty("cycle"u8, out var cycle)
            || cycle.ValueKind != JsonValueKind.Array)
            return null;

        var names = new List<string>();
        foreach (var link in cycle.EnumerateArray())
        {
            if (link.ValueKind != JsonValueKind.Object)
                continue;
            var name = NullIfWhiteSpace(GetString(link, "name"u8));
            if (name is not null)
                names.Add(name);
        }

        return names.Count == 0 ? null : string.Join(" → ", names);
    }

    private static string? NormalizeViolationPath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var path = NormalizePath(raw);
        return path.StartsWith("./", StringComparison.Ordinal) ? path[2..] : path;
    }
}
