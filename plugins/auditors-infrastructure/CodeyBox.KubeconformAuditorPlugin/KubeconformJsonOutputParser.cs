using System.Text.Json;
using CodeyBox.PluginSdk.Tools;

namespace CodeyBox.KubeconformAuditorPlugin;

/// <summary>
/// Parses kubeconform's <c>-output json -summary</c> report —
/// <c>{ "resources": [ { "filename", "kind", "name", "version", "status",
/// "msg", "validationErrors"[] } ], "summary": {…} }</c> — into
/// <see cref="ExternalToolFinding"/> records. The summary object is required
/// only as a shape marker: the auditor always passes <c>-summary</c>, and
/// demanding the object-with-<c>resources</c> shape is what distinguishes a
/// completed scan (exit 0/1 with a report) from a run failure (exit 1 with a
/// plain-text error on stderr and no report) — see
/// <see cref="KubeconformAuditor"/> for the exit-code analysis.
///
/// <para>Only resources kubeconform could not prove valid become findings:
/// <c>statusInvalid</c> (schema violation) and <c>statusError</c> (resource
/// could not be validated — unreadable file, YAML parse failure, missing or
/// unreachable schema) are reported; <c>statusValid</c>, <c>statusSkipped</c>,
/// <c>statusEmpty</c> and absent statuses are not. The raw status token is
/// carried as the severity level and mapped by the auditor's declared
/// <see cref="ExternalToolSeverityMapping"/> — raw tool vocabulary never
/// reaches findings.</para>
///
/// <para>kubeconform supplies no rule ids and no line numbers. The parser
/// synthesizes a stable rule id — <c>kubeconform/&lt;Kind&gt;</c> for schema
/// violations (so <c>ExcludedRules</c> can silence a specific kind) and
/// <c>kubeconform/error</c> for resources that could not be validated — and
/// locations carry the file path only.</para>
///
/// <para>Malformed output — empty stdout, non-JSON, or JSON without a
/// <c>resources</c> array — throws <see cref="ExternalToolParseException"/>,
/// which the base reports as infrastructure, never as a pass.</para>
/// </summary>
internal sealed class KubeconformJsonOutputParser : IExternalToolOutputParser
{
    /// <summary>Rule id for resources kubeconform could not validate (<c>statusError</c>).</summary>
    internal const string ErrorRuleId = "kubeconform/error";

    /// <summary>Rule id fallback when a reported resource has no <c>kind</c> signature.</summary>
    internal const string ResourceRuleId = "kubeconform/resource";

    /// <summary>Rule id prefix for schema violations: <c>kubeconform/&lt;Kind&gt;</c>.</summary>
    internal const string InvalidRulePrefix = "kubeconform/";

    // Same per-document result bound the shared SARIF parser applies.
    private const int MaxResults = SarifToolOutputParser.DefaultMaxResults;

    // validationErrors entries carry an in-document JSON path, not a file
    // location — append a bounded number to the message so a single resource
    // cannot produce an unbounded finding description.
    private const int MaxValidationErrorsInMessage = 8;

    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Stdout))
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced no kubeconform JSON report on stdout.");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(input.Stdout);
        }
        catch (JsonException ex)
        {
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced output that is not valid kubeconform JSON: {SingleLine(ex.Message)}.",
                ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("resources"u8, out var resources)
                || resources.ValueKind != JsonValueKind.Array)
                throw new ExternalToolParseException(
                    $"Tool '{input.ToolName}' produced a JSON object without a kubeconform 'resources' array.");

            var findings = new List<ExternalToolFinding>();
            foreach (var resource in resources.EnumerateArray())
            {
                if (findings.Count >= MaxResults)
                    break;
                if (resource.ValueKind != JsonValueKind.Object)
                    continue;
                var finding = ParseResource(resource);
                if (finding is not null)
                    findings.Add(finding);
            }

            return findings;
        }
    }

    private static ExternalToolFinding? ParseResource(JsonElement resource)
    {
        var status = GetString(resource, "status"u8);
        // A missing or empty status is kubeconform's Empty resource state
        // (an empty YAML document) — not a problem — so it is skipped like
        // valid/skipped rather than reported.
        if (string.IsNullOrWhiteSpace(status) || IsNonProblemStatus(status))
            return null;

        var kind = NullIfWhiteSpace(GetString(resource, "kind"u8));
        var name = NullIfWhiteSpace(GetString(resource, "name"u8));
        var version = NullIfWhiteSpace(GetString(resource, "version"u8));
        var msg = NullIfWhiteSpace(GetString(resource, "msg"u8)) ?? "(no message)";
        var path = NullIfWhiteSpace(GetString(resource, "filename"u8));

        // statusError marks "could not validate" (unreadable file, YAML parse
        // failure, schema unavailable); statusInvalid marks a schema
        // violation. Anything else is a status this parser predates — still
        // reported, mapped through the declared severity default.
        var isError = IsStatusError(status);
        var ruleId = isError
            ? ErrorRuleId
            : kind is not null ? InvalidRulePrefix + kind : ResourceRuleId;

        return new ExternalToolFinding(
            SeverityLevel: status,
            RuleId: ruleId,
            Message: BuildMessage(kind, version, name, msg, resource, isError),
            Path: path,
            Line: null);
    }

    private static string BuildMessage(
        string? kind,
        string? version,
        string? name,
        string msg,
        JsonElement resource,
        bool isError)
    {
        var subject = new System.Text.StringBuilder();
        if (kind is not null)
        {
            subject.Append(kind);
            if (version is not null)
                subject.Append(' ').Append(version);
            if (name is not null)
                subject.Append(" '").Append(name).Append('\'');
            subject.Append(": ");
        }

        subject.Append(isError ? "resource could not be validated: " : string.Empty);
        subject.Append(msg);

        if (resource.TryGetProperty("validationErrors"u8, out var validationErrors)
            && validationErrors.ValueKind == JsonValueKind.Array)
        {
            var appended = 0;
            var omitted = 0;
            foreach (var error in validationErrors.EnumerateArray())
            {
                if (error.ValueKind != JsonValueKind.Object)
                    continue;
                var field = NullIfWhiteSpace(GetString(error, "path"u8));
                var detail = NullIfWhiteSpace(GetString(error, "msg"u8));
                if (field is null && detail is null)
                    continue;
                if (appended >= MaxValidationErrorsInMessage)
                {
                    omitted++;
                    continue;
                }
                subject.Append("; ");
                if (field is not null)
                    subject.Append(field).Append(": ");
                if (detail is not null)
                    subject.Append(detail);
                appended++;
            }

            if (omitted > 0)
                subject.Append("; +").Append(omitted).Append(" more validation error(s)");
        }

        return subject.ToString();
    }

    private static bool IsNonProblemStatus(string status)
    {
        // kubeconform's vocabulary (v0.8.x): statusValid, statusInvalid,
        // statusError, statusSkipped, statusEmpty. Older builds emitted bare
        // VALID/INVALID/… — accept both spellings defensively.
        return status.Equals("statusValid", StringComparison.OrdinalIgnoreCase)
            || status.Equals("valid", StringComparison.OrdinalIgnoreCase)
            || status.Equals("statusSkipped", StringComparison.OrdinalIgnoreCase)
            || status.Equals("skipped", StringComparison.OrdinalIgnoreCase)
            || status.Equals("statusEmpty", StringComparison.OrdinalIgnoreCase)
            || status.Equals("empty", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStatusError(string status)
        => status.Equals("statusError", StringComparison.OrdinalIgnoreCase)
            || status.Equals("error", StringComparison.OrdinalIgnoreCase);

    private static string? GetString(JsonElement element, ReadOnlySpan<byte> name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string SingleLine(string message)
        => message.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
