using System.Text.Json;
using CodeyBox.PluginSdk.Tools;

namespace CodeyBox.DetectSecretsAuditorPlugin;

/// <summary>
/// Parses the detect-secrets baseline document — the JSON report
/// <c>detect-secrets scan</c> writes to stdout (or to the
/// <c>--baseline</c> file the auditor reads back) — into findings. The
/// report shape is a JSON object whose <c>results</c> object maps each
/// repo-relative filename to an array of secret records carrying
/// <c>type</c> (the detecting plugin's human-readable rule name, e.g.
/// "Secret Keyword" or "AWS Access Key"), <c>filename</c>,
/// <c>line_number</c>, <c>hashed_secret</c> (SHA-1 of the matched value —
/// the report never carries the secret itself), <c>is_verified</c>, and —
/// only when a baseline was merged — an audit label <c>is_secret</c>.
///
/// <para>Entries with <c>is_secret: false</c> are dropped: that label only
/// exists when the scan ran against an operator-supplied baseline, and it
/// is exactly the "audited false positive" suppression the auditable
/// baseline file exists for. The label never appears in a plain scan.</para>
///
/// <para>Severity: detect-secrets has no severity vocabulary, so the parser
/// reports the verification state as the level (<c>verified</c> /
/// <c>unverified</c>) and the auditor's declared mapping sends every result
/// to Error — a detected credential blocks the audit.</para>
///
/// <para>Malformed output — missing/invalid JSON, or a missing/non-object
/// <c>results</c> — throws <see cref="ExternalToolParseException"/>, which
/// the base reports as infrastructure (the check could not run), never as
/// a pass.</para>
/// </summary>
internal sealed class DetectSecretsBaselineParser : IExternalToolOutputParser
{
    /// <summary>
    /// Upper bound on results consumed from one document, mirroring the
    /// shared <see cref="SarifToolOutputParser"/> bound so one pathological
    /// report cannot grow findings without limit. The base still applies its
    /// own <c>MaxFindings</c> cap on top.
    /// </summary>
    internal const int MaxResults = 10_000;

    private const int MessageValueMaxChars = 64;

    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var stdout = input.Stdout ?? string.Empty;
        if (string.IsNullOrWhiteSpace(stdout))
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced no baseline JSON on stdout.");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(stdout);
        }
        catch (JsonException ex)
        {
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced output that is not valid JSON: {ToolOutputText.SingleLine(ex.Message)}.",
                ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("results"u8, out var results)
                || results.ValueKind != JsonValueKind.Object)
                throw new ExternalToolParseException(
                    $"Tool '{input.ToolName}' produced JSON without a baseline 'results' object.");

            var findings = new List<ExternalToolFinding>();
            foreach (var fileResults in results.EnumerateObject())
            {
                if (findings.Count >= MaxResults)
                    break;
                if (fileResults.Value.ValueKind != JsonValueKind.Array)
                    throw new ExternalToolParseException(
                        $"Tool '{input.ToolName}' produced a 'results' entry for "
                        + $"'{BoundedName(fileResults.Name)}' that is not an array.");

                foreach (var item in fileResults.Value.EnumerateArray())
                {
                    if (findings.Count >= MaxResults)
                        break;
                    if (item.ValueKind != JsonValueKind.Object)
                        continue;
                    var finding = ParseSecret(item, fileResults.Name, input);
                    if (finding is not null)
                        findings.Add(finding);
                }
            }

            return findings;
        }
    }

    private static ExternalToolFinding? ParseSecret(
        JsonElement secret, string resultsKey, ExternalToolParseInput input)
    {
        // `is_secret` is only present on entries merged in from an
        // operator-supplied --baseline: false marks an audited false
        // positive (suppressed by definition of the audit), true an
        // audited real secret (still reported).
        if (secret.TryGetProperty("is_secret"u8, out var isSecret)
            && isSecret.ValueKind == JsonValueKind.False)
            return null;

        var type = ToolOutputText.NullIfWhiteSpace(
            ExternalToolJsonHelpers.GetString(secret, "type"u8));
        var filename = ExternalToolJsonHelpers.GetString(secret, "filename"u8);
        var path = ExternalToolJsonHelpers.NormalizeReportedPath(
            filename ?? resultsKey, input.ScanRoot, input.WorkingDirectory);

        int? line = null;
        if (secret.TryGetProperty("line_number"u8, out var lineElement)
            && lineElement.ValueKind == JsonValueKind.Number
            && lineElement.TryGetInt32(out var lineValue)
            && lineValue > 0)
            line = lineValue;

        var verified = secret.TryGetProperty("is_verified"u8, out var verifiedElement)
            && verifiedElement.ValueKind == JsonValueKind.True;
        var hash = ExternalToolJsonHelpers.GetString(secret, "hashed_secret"u8);

        var message = $"detect-secrets flagged a potential secret ({type ?? "unknown type"})"
            + (verified ? " — the credential was verified live by its provider" : string.Empty)
            + (!string.IsNullOrWhiteSpace(hash)
                ? $"; hashed_secret {hash} identifies the match without disclosing it"
                : string.Empty)
            + ".";

        return new ExternalToolFinding(
            SeverityLevel: verified ? "verified" : "unverified",
            RuleId: type,
            Message: message,
            Path: path,
            Line: line);
    }

    private static string BoundedName(string name)
        => ExternalToolJsonHelpers.Truncate(
            ExternalToolJsonHelpers.SingleLine(name), MessageValueMaxChars);
}
