using System.Text.Json;
using CodeyBox.PluginSdk.Tools;
using static CodeyBox.PluginSdk.Tools.ExternalToolJsonHelpers;

namespace CodeyBox.KyvernoAuditorPlugin;

/// <summary>
/// Parses the JSON policy report produced by <c>kyverno apply --policy-report
/// --output-format json</c> — a PolicyReport-shaped document (<c>{ "kind":
/// "PolicyReport", "results": [ { "policy", "rule", "result", "message",
/// "resources": [ { "apiVersion", "kind", "name", "namespace" } ] } ],
/// "summary": { "pass", "fail", "warn", "error", "skip" } }</c>) — into
/// <see cref="ExternalToolFinding"/> records.
///
/// <para>Result accounting is explicit and fail-closed: <c>fail</c>,
/// <c>warn</c>, <c>error</c>, and <c>skip</c> results are all reported (the
/// auditor's severity mapping decides which fail the audit); only
/// <c>pass</c> results are dropped. An <c>error</c> means kyverno could not
/// evaluate the rule (unsupported policy, missing context, engine failure)
/// and a <c>skip</c> means it chose not to — both are coverage the audit did
/// not get, so both surface as findings under a <c>kyverno/…</c> rule id
/// instead of vanishing. Anything unrecognised from a foreign build is
/// reported too, mapped through the auditor's severity default.</para>
///
/// <para>The parser also accepts a JSON array of PolicyReport documents and
/// newline-delimited JSON (one report per line): kyverno prints one report
/// per evaluated batch, so the shape on stdout depends on how many resource
/// files the scan covered. Empty stdout, non-JSON, JSON without a
/// <c>results</c> array, and a vacuous report (zero results with a zeroed or
/// absent summary — nothing was evaluated) all throw
/// <see cref="ExternalToolParseException"/>, which the base reports as
/// infrastructure, never as a pass. A clean evaluation carries its passing
/// count in <c>summary.pass</c> (passing rules may be omitted from
/// <c>results</c> without <c>--detailed-results</c>, which the auditor
/// deliberately does not pass), so an empty <c>results</c> array with a
/// non-zero summary is a genuine clean pass, not a vacuous one.</para>
///
/// <para>PolicyReport results carry resource identity
/// (<c>kind</c>/<c>name</c>/<c>namespace</c>), not file paths — kyverno does
/// not record which file a resource came from. Finding messages therefore
/// identify the resource by coordinates; locations carry no file path. The
/// <c>policy</c>/<c>rule</c> pair doubles as the rule id
/// (<c>kyverno/&lt;policy&gt;/&lt;rule&gt;</c>), so
/// <c>IncludedRules</c>/<c>ExcludedRules</c> select by policy rule.</para>
/// </summary>
internal sealed class KyvernoPolicyReportParser : IExternalToolOutputParser
{
    private const int MaxResults = SarifToolOutputParser.DefaultMaxResults;

    private const int MaxMessageChars = 4000;

    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Stdout))
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced no kyverno policy report on stdout.");

        var documents = ReadDocuments(input.ToolName, input.Stdout);

        var findings = new List<ExternalToolFinding>();
        var evaluatedTotal = 0;
        foreach (var document in documents)
        {
            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.TryGetProperty("results"u8, out var results)
                    || results.ValueKind != JsonValueKind.Array)
                    throw new ExternalToolParseException(
                        $"Tool '{input.ToolName}' produced JSON without a kyverno policy-report 'results' array.");
                evaluatedTotal += SumSummary(document.RootElement);
                foreach (var result in results.EnumerateArray())
                {
                    if (findings.Count >= MaxResults)
                        break;
                    if (result.ValueKind != JsonValueKind.Object)
                        continue;
                    var finding = ParseResult(input.ToolName, result);
                    if (finding is not null)
                        findings.Add(finding);
                    evaluatedTotal++;
                }
                if (findings.Count >= MaxResults)
                    break;
            }
        }

        if (findings.Count == 0 && evaluatedTotal == 0)
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced a kyverno policy report with zero evaluated results — "
                + "no policy evaluated any resource, so a clean verdict would be vacuous.");

        return findings;
    }

    private static List<JsonDocument> ReadDocuments(string toolName, string stdout)
    {
        try
        {
            var whole = JsonDocument.Parse(stdout);
            if (whole.RootElement.ValueKind == JsonValueKind.Array)
            {
                var items = new List<JsonDocument>();
                try
                {
                    foreach (var item in whole.RootElement.EnumerateArray())
                    {
                        items.Add(JsonDocument.Parse(item.GetRawText()));
                    }
                    return items;
                }
                catch
                {
                    foreach (var item in items)
                        item.Dispose();
                    throw;
                }
                finally
                {
                    whole.Dispose();
                }
            }
            return [whole];
        }
        catch (JsonException wholeEx)
        {
            var lines = stdout.Split('\n')
                .Select(static line => line.Trim())
                .Where(static line => line.Length > 0)
                .ToList();
            if (lines.Count <= 1)
                throw new ExternalToolParseException(
                    $"Tool '{toolName}' produced output that is not valid kyverno JSON: {SingleLine(wholeEx.Message)}.",
                    wholeEx);
            var items = new List<JsonDocument>();
            try
            {
                foreach (var line in lines)
                    items.Add(JsonDocument.Parse(line));
                return items;
            }
            catch (JsonException lineEx)
            {
                foreach (var item in items)
                    item.Dispose();
                throw new ExternalToolParseException(
                    $"Tool '{toolName}' produced output that is not valid kyverno JSON: {SingleLine(lineEx.Message)}.",
                    lineEx);
            }
        }
    }

    private static ExternalToolFinding? ParseResult(string toolName, JsonElement result)
    {
        var outcome = NullIfWhiteSpace(GetString(result, "result"u8));
        if (outcome is not null && outcome.Equals("pass", StringComparison.OrdinalIgnoreCase))
            return null;

        var policy = NullIfWhiteSpace(GetString(result, "policy"u8));
        var rule = NullIfWhiteSpace(GetString(result, "rule"u8));
        var ruleId = (policy, rule) switch
        {
            (not null, not null) => $"kyverno/{policy}/{rule}",
            (not null, null) => $"kyverno/{policy}",
            (null, not null) => $"kyverno/{rule}",
            _ => $"kyverno/{outcome ?? "unknown"}",
        };

        var message = NullIfWhiteSpace(GetString(result, "message"u8)) ?? "(no message)";
        var subject = DescribeResource(result);

        var detail = subject is null ? message : $"{subject}: {message}";
        if (policy is not null || rule is not null)
            detail += $" [policy/{policy ?? "?"} rule/{rule ?? "?"}]";

        return new ExternalToolFinding(
            SeverityLevel: outcome,
            RuleId: ruleId,
            Message: Truncate(detail, MaxMessageChars),
            Path: null,
            Line: null);
    }

    private static string? DescribeResource(JsonElement result)
    {
        if (!result.TryGetProperty("resources"u8, out var resources)
            || resources.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var resource in resources.EnumerateArray())
        {
            if (resource.ValueKind != JsonValueKind.Object)
                continue;
            var kind = NullIfWhiteSpace(GetString(resource, "kind"u8));
            var apiVersion = NullIfWhiteSpace(GetString(resource, "apiVersion"u8));
            var name = NullIfWhiteSpace(GetString(resource, "name"u8));
            var @namespace = NullIfWhiteSpace(GetString(resource, "namespace"u8));
            if (kind is null && name is null)
                continue;
            var described = (kind ?? "resource")
                + (name is null ? string.Empty : $" '{name}'")
                + (@namespace is null ? string.Empty : $" (namespace {@namespace})")
                + (apiVersion is null ? string.Empty : $" [{apiVersion}]");
            return described;
        }
        return null;
    }

    private static int SumSummary(JsonElement report)
    {
        if (!report.TryGetProperty("summary"u8, out var summary)
            || summary.ValueKind != JsonValueKind.Object)
            return 0;
        var total = 0;
        foreach (var name in (string[])[ "pass", "fail", "warn", "warning", "error", "skip" ])
        {
            if (summary.TryGetProperty(name, out var count)
                && count.ValueKind == JsonValueKind.Number
                && count.TryGetInt32(out var value)
                && value > 0)
                total += value;
        }
        return total;
    }
}
