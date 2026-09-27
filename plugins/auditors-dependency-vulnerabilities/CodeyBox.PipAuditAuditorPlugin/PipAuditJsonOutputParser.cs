using System.Text;
using System.Text.Json;
using CodeyBox.PluginSdk.Tools;

namespace CodeyBox.PipAuditAuditorPlugin;

/// <summary>
/// Parses pip-audit's <c>--format json</c> report on <b>stdout</b> into
/// <see cref="ExternalToolFinding"/> records — one per vulnerability.
/// Current releases emit an object (<c>{"dependencies":[…],"fixes":[…]}</c>);
/// older releases emitted a bare array of dependency objects. Both shapes are
/// accepted so a pinned older release keeps parsing. Each dependency carries
/// <c>name</c>, <c>version</c>, and <c>vulns[]</c> with <c>id</c>,
/// <c>fix_versions[]</c>, <c>aliases[]</c>, and <c>description</c>;
/// dependencies the tool skipped carry <c>skip_reason</c> instead of
/// <c>vulns</c> and yield no findings.
///
/// <para>The report is the run-completed discriminator, not the exit code:
/// pip-audit exits <c>1</c> both when vulnerabilities are found and when the
/// run fails outright (unresolvable input, unreachable vulnerability
/// service, strict-mode skip). Only a successful run prints the JSON manifest
/// to stdout; a missing or unparseable report therefore throws
/// <see cref="ExternalToolParseException"/>, which the base reports as
/// infrastructure, never as a pass.</para>
///
/// <para>pip-audit reports no per-vulnerability severity, file, or line —
/// the advisory database has no file position to give — so findings carry no
/// <c>Path</c>/<c>Line</c>; the package name, installed version, fix
/// versions, aliases, and description are folded into the finding message
/// instead, bounded. The raw <c>id</c> becomes the rule id verbatim
/// (e.g. <c>PYSEC-2019-179</c>, <c>CVE-2019-1010083</c>) so
/// <c>IncludedRules</c>/<c>ExcludedRules</c> share pip-audit's own
/// <c>--ignore-vuln</c> vocabulary; advisories with no id get
/// <c>pip-audit/advisory</c>. An optional <c>severity</c> member, when a
/// report carries one, is passed through for the auditor's declared
/// <see cref="ExternalToolSeverityMapping"/> — tool vocabulary never reaches
/// findings unmapped.</para>
/// </summary>
internal sealed class PipAuditJsonOutputParser : IExternalToolOutputParser
{
    /// <summary>Rule id for advisories that carry no vulnerability id.</summary>
    internal const string FallbackRuleId = "pip-audit/advisory";

    // Same per-document result bound the shared SARIF parser applies.
    private const int MaxResults = SarifToolOutputParser.DefaultMaxResults;

    // One advisory can list many aliases and a long description; bound what
    // is folded into the message so a single advisory cannot produce an
    // unbounded finding description.
    private const int MaxAliasesInMessage = 8;
    private const int MaxFixVersionsInMessage = 16;
    private const int MaxDescriptionChars = 2000;

    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Stdout))
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced no JSON report on stdout — "
                + "pip-audit prints its JSON manifest to stdout only when the audit completes; "
                + "an empty report means the run did not complete.");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(input.Stdout);
        }
        catch (JsonException ex)
        {
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced output that is not valid pip-audit JSON: {SingleLine(ex.Message)}.",
                ex);
        }

        using (document)
        {
            var root = document.RootElement;
            JsonElement dependencies;
            if (root.ValueKind == JsonValueKind.Array)
            {
                dependencies = root;
            }
            else if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("dependencies"u8, out var nested)
                && nested.ValueKind == JsonValueKind.Array)
            {
                dependencies = nested;
            }
            else
            {
                throw new ExternalToolParseException(
                    $"Tool '{input.ToolName}' produced JSON without a dependency array — "
                    + "expected either a bare array or an object with a 'dependencies' array.");
            }

            var findings = new List<ExternalToolFinding>();
            foreach (var dependency in dependencies.EnumerateArray())
            {
                if (findings.Count >= MaxResults)
                    break;
                if (dependency.ValueKind != JsonValueKind.Object)
                    continue;
                var name = NullIfWhiteSpace(GetString(dependency, "name"u8));
                if (name is null)
                    continue;
                if (!dependency.TryGetProperty("vulns"u8, out var vulns)
                    || vulns.ValueKind != JsonValueKind.Array)
                    continue;
                var version = NullIfWhiteSpace(GetString(dependency, "version"u8));
                foreach (var vuln in vulns.EnumerateArray())
                {
                    if (findings.Count >= MaxResults)
                        break;
                    if (vuln.ValueKind == JsonValueKind.Object)
                        findings.Add(ParseVulnerability(name, version, vuln));
                }
            }

            return findings;
        }
    }

    private static ExternalToolFinding ParseVulnerability(string name, string? version, JsonElement vuln)
    {
        var id = NullIfWhiteSpace(GetString(vuln, "id"u8));
        var severity = NullIfWhiteSpace(GetString(vuln, "severity"u8));
        var description = NullIfWhiteSpace(GetString(vuln, "description"u8));

        var builder = new StringBuilder(name);
        if (version is not null)
            builder.Append(' ').Append(version);
        builder.Append(" is affected by ").Append(id ?? "(unknown advisory)");

        if (vuln.TryGetProperty("fix_versions"u8, out var fixVersions)
            && fixVersions.ValueKind == JsonValueKind.Array)
        {
            AppendBoundedStrings(
                builder,
                fixVersions,
                MaxFixVersionsInMessage,
                "; fixed in ",
                ", ",
                static omitted => $" and {omitted} more");
        }

        if (vuln.TryGetProperty("aliases"u8, out var aliases)
            && aliases.ValueKind == JsonValueKind.Array)
        {
            AppendBoundedStrings(
                builder,
                aliases,
                MaxAliasesInMessage,
                "; aliases: ",
                ", ",
                static omitted => $" and {omitted} more");
        }

        if (description is not null)
        {
            builder.Append(". ").Append(description.Length > MaxDescriptionChars
                ? description[..MaxDescriptionChars] + "…"
                : description);
        }

        return new ExternalToolFinding(
            SeverityLevel: severity,
            RuleId: id ?? FallbackRuleId,
            Message: builder.ToString(),
            Path: null,
            Line: null);
    }

    private static void AppendBoundedStrings(
        StringBuilder builder,
        JsonElement array,
        int cap,
        string prefix,
        string separator,
        Func<int, string> remainder)
    {
        var details = new List<string>(cap + 1);
        var omitted = 0;
        foreach (var element in array.EnumerateArray())
        {
            var detail = element.ValueKind == JsonValueKind.String
                ? NullIfWhiteSpace(element.GetString())
                : null;
            if (detail is null)
                continue;
            if (details.Count >= cap)
            {
                omitted++;
                continue;
            }
            details.Add(detail);
        }
        if (details.Count == 0)
            return;
        builder.Append(prefix).Append(string.Join(separator, details));
        if (omitted > 0)
            builder.Append(remainder(omitted));
    }

    private static string? GetString(JsonElement element, ReadOnlySpan<byte> name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string SingleLine(string message)
        => message.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
