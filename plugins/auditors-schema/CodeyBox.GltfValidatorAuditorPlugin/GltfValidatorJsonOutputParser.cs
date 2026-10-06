using System.Text.Json;
using CodeyBox.PluginSdk.Tools;
using static CodeyBox.PluginSdk.Tools.ExternalToolJsonHelpers;

namespace CodeyBox.GltfValidatorAuditorPlugin;

/// <summary>
/// Parses the Khronos glTF-Validator JSON report (written to stdout by
/// <c>gltf_validator -o &lt;asset&gt;</c>) into
/// <see cref="ExternalToolFinding"/> records. The report envelope is
/// <c>{ "uri", "mimeType", "validatorVersion", "issues": { "numErrors",
/// "numWarnings", "numInfos", "numHints", "messages": [ { "code",
/// "message", "severity", "pointer" | "offset" } ], "truncated" }, "info":
/// {…} }</c> (verified against <c>lib/src/validation_result.dart</c> and
/// <c>lib/src/errors.dart</c> upstream).
///
/// <para>Each entry of <c>issues.messages</c> becomes one finding. The stable
/// issue <c>code</c> (e.g. <c>ACCESSOR_INVALID_FLOAT</c>) is the rule id, so
/// <c>IncludedRules</c>/<c>ExcludedRules</c> select validator checks by
/// code. The JSON pointer (e.g. <c>/accessors/0</c>) — or the GLB byte
/// <c>offset</c> for binary-level issues — is preserved verbatim at the head
/// of the message, mirroring the validator's own <c>Issue.ToString</c>
/// rendering, because findings carry no separate pointer field. The numeric
/// <c>severity</c> (0 = Error, 1 = Warning, 2 = Information, 3 = Hint, per
/// the upstream <c>Severity</c> enum order) is carried as the severity level
/// and mapped by the auditor's declared
/// <see cref="ExternalToolSeverityMapping"/> — raw tool tokens never reach
/// findings. Word-form severities are accepted defensively and canonicalized
/// to the same levels.</para>
///
/// <para>The finding path is the report's <c>uri</c> (relative to the scan
/// working directory by default — the auditor rejects the tool's
/// absolute-path mode so locations stay repo-relative and
/// <c>ExcludePaths</c> keeps working). glTF issues have no line numbers, so
/// locations carry the asset path only.</para>
///
/// <para>Malformed output — empty stdout, non-JSON, or JSON without an
/// <c>issues.messages</c> array — throws
/// <see cref="ExternalToolParseException"/>, which the scan reports as
/// infrastructure, never as a pass. A usage failure (exit 1 with the
/// version/usage banner on stderr and no report) therefore fails closed.
/// Non-object message entries are skipped; a message without a code is
/// reported under <see cref="FallbackRuleId"/> rather than dropped.</para>
/// </summary>
internal sealed class GltfValidatorJsonOutputParser : IExternalToolOutputParser
{
    /// <summary>Shared stateless instance.</summary>
    internal static readonly GltfValidatorJsonOutputParser Instance = new();

    /// <summary>Rule id for validator messages that carry no issue code.</summary>
    internal const string FallbackRuleId = "gltf-validator/unknown";

    // Same per-document result bound the shared SARIF parser applies.
    private const int MaxResults = SarifToolOutputParser.DefaultMaxResults;

    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Stdout))
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced no glTF-Validator JSON report on stdout.");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(input.Stdout);
        }
        catch (JsonException ex)
        {
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced output that is not valid glTF-Validator JSON: {SingleLine(ex.Message)}.",
                ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("issues"u8, out var issues)
                || issues.ValueKind != JsonValueKind.Object
                || !issues.TryGetProperty("messages"u8, out var messages)
                || messages.ValueKind != JsonValueKind.Array)
                throw new ExternalToolParseException(
                    $"Tool '{input.ToolName}' produced JSON without a glTF-Validator 'issues.messages' array.");

            var uri = GetString(document.RootElement, "uri"u8);
            var path = NormalizeReportedPath(uri, input.ScanRoot, input.WorkingDirectory);

            var findings = new List<ExternalToolFinding>();
            foreach (var message in messages.EnumerateArray())
            {
                if (findings.Count >= MaxResults)
                    break;
                if (message.ValueKind != JsonValueKind.Object)
                    continue;
                findings.Add(ParseMessage(message, path));
            }

            return findings;
        }
    }

    private static ExternalToolFinding ParseMessage(JsonElement message, string? path)
    {
        var code = NullIfWhiteSpace(CoerceString(GetProperty(message, "code"u8)));
        var text = NullIfWhiteSpace(CoerceString(GetProperty(message, "message"u8)))
            ?? "(no message)";
        var pointer = NullIfWhiteSpace(CoerceString(GetProperty(message, "pointer"u8)));
        var offset = GetInt32(GetProperty(message, "offset"u8));

        var body = pointer is not null
            ? $"{pointer}: {text}"
            : offset.HasValue
                ? $"@{offset.Value}: {text}"
                : text;

        return new ExternalToolFinding(
            SeverityLevel: CanonicalSeverity(GetProperty(message, "severity"u8)),
            RuleId: code ?? FallbackRuleId,
            Message: body,
            Path: path,
            Line: null);
    }

    private static string? CanonicalSeverity(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetInt32(out var index) => index switch
            {
                0 => "error",
                1 => "warning",
                2 => "information",
                3 => "hint",
                _ => index.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
            JsonValueKind.String => CanonicalSeverityWord(element.GetString()),
            _ => null,
        };

    private static string? CanonicalSeverityWord(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return value.Trim().ToLowerInvariant() switch
        {
            "error" or "0" => "error",
            "warning" or "warn" or "1" => "warning",
            "information" or "informational" or "info" or "2" => "information",
            "hint" or "3" => "hint",
            var other => other,
        };
    }

    private static int? GetInt32(JsonElement element)
        => element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value)
            ? value
            : null;

    private static JsonElement GetProperty(JsonElement element, ReadOnlySpan<byte> name)
        => element.TryGetProperty(name, out var value) ? value : default;

    /// <summary>
    /// Best-effort one-line coverage summary for a report document
    /// (<c>errors=E warnings=W infos=I hints=H truncated=T</c>), or null
    /// when the document is not a recognizable report. Never throws: the
    /// scan path uses it for RawOutput headers only, and a header must
    /// never fail a run the parser itself would accept or reject.
    /// </summary>
    internal static string? TrySummarize(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return null;
        try
        {
            using var document = JsonDocument.Parse(stdout);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("issues"u8, out var issues)
                || issues.ValueKind != JsonValueKind.Object)
                return null;
            var errors = GetCount(issues, "numErrors"u8);
            var warnings = GetCount(issues, "numWarnings"u8);
            var infos = GetCount(issues, "numInfos"u8);
            var hints = GetCount(issues, "numHints"u8);
            if (errors is null && warnings is null && infos is null && hints is null)
                return null;
            var truncated = issues.TryGetProperty("truncated"u8, out var truncatedElement)
                && truncatedElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? truncatedElement.GetBoolean().ToString()
                : "unknown";
            return $"errors={errors ?? 0} warnings={warnings ?? 0} infos={infos ?? 0} hints={hints ?? 0} truncated={truncated}";
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static long? GetCount(JsonElement issues, ReadOnlySpan<byte> name)
        => issues.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var count)
            && count >= 0
            ? count
            : null;
}
