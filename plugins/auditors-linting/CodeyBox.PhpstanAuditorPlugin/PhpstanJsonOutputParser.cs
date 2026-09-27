using System.Text.Json;
using CodeyBox.PluginSdk.Tools;

namespace CodeyBox.PhpstanAuditorPlugin;

/// <summary>
/// Parses PHPStan's built-in <c>--error-format=json</c> report —
/// <c>{ "totals": {"errors": n, "file_errors": n}, "files": {"&lt;abs path&gt;":
/// {"errors": n, "messages": [{"message", "line", "ignorable", "tip"?,
/// "identifier"?}]}}, "errors": ["…non-file-specific errors…"] }</c> —
/// into <see cref="ExternalToolFinding"/> records.
///
/// <para>PHPStan's report keys <c>files</c> by <em>absolute</em> path (the
/// analyser absolutizes the paths it was given); they are relativized against
/// <see cref="ExternalToolParseInput.WorkingDirectory"/> so locations and the
/// shared <c>ExcludePaths</c> filter see repository-relative paths. Paths that
/// do not sit under the working directory are left absolute.</para>
///
/// <para>PHPStan has no severity vocabulary — every issue it reports is an
/// error that fails the analysis — so each finding carries the token
/// <c>"error"</c>, which <see cref="ExternalToolAuditorBase"/> maps through the
/// auditor's declared <see cref="ExternalToolSeverityMapping"/>; raw levels
/// never reach findings.</para>
///
/// <para>Fail-closed on malformed output — and this is what keeps PHPStan's
/// ambiguous exit code honest: exit <c>1</c> means <em>either</em> "analysis
/// completed and reported errors" <em>or</em> "could not run" (inception
/// failures such as a missing config, zero files to analyse, or internal
/// errors — all of which print plain text, not the JSON report). A stdout that
/// is not a PHPStan JSON report therefore throws
/// <see cref="ExternalToolParseException"/> — which the base reports as
/// infrastructure, never as a pass — as does a non-zero findings exit whose
/// report contains zero findings (a crash or truncated report, not a clean
/// run).</para>
/// </summary>
internal sealed class PhpstanJsonOutputParser : IExternalToolOutputParser
{
    // Same per-report result bound the shared SARIF parser applies.
    private const int MaxResults = SarifToolOutputParser.DefaultMaxResults;

    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Stdout))
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced no PHPStan JSON output on stdout.");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(input.Stdout);
        }
        catch (JsonException ex)
        {
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced output that is not valid PHPStan JSON: {SingleLine(ex.Message)}.",
                ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("totals"u8, out var totals)
                || totals.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("files"u8, out var files)
                || files.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("errors"u8, out var nonFileErrors)
                || nonFileErrors.ValueKind != JsonValueKind.Array)
                throw new ExternalToolParseException(
                    $"Tool '{input.ToolName}' produced JSON that is not a PHPStan report "
                    + "(expected 'totals', 'files' and 'errors' members).");

            var findings = new List<ExternalToolFinding>();
            foreach (var file in files.EnumerateObject())
            {
                if (findings.Count >= MaxResults)
                    break;
                if (file.Value.ValueKind != JsonValueKind.Object
                    || !file.Value.TryGetProperty("messages"u8, out var messages)
                    || messages.ValueKind != JsonValueKind.Array)
                    continue;

                var path = NormalizeFilePath(file.Name, input.WorkingDirectory);
                foreach (var message in messages.EnumerateArray())
                {
                    if (findings.Count >= MaxResults)
                        break;
                    if (message.ValueKind == JsonValueKind.Object)
                        findings.Add(ParseMessage(message, path));
                }
            }

            // Non-file-specific errors ("Ignored error pattern … was not
            // matched", config-level problems PHPStan still counts into
            // totals.errors) carry no location or identifier.
            foreach (var error in nonFileErrors.EnumerateArray())
            {
                if (findings.Count >= MaxResults)
                    break;
                var text = error.ValueKind == JsonValueKind.String
                    ? error.GetString()
                    : error.GetRawText();
                findings.Add(new ExternalToolFinding(
                    SeverityLevel: "error",
                    RuleId: null,
                    Message: NullIfWhiteSpace(text) ?? "(no message)"));
            }

            // A non-zero exit means PHPStan either reported errors (findings
            // above) or could not complete the analysis — in which case no
            // report is emitted and this throw already fired on the JSON
            // shape check. A findings exit whose report yields zero findings
            // is a corrupt report, not a clean pass: fail closed.
            if (input.ExitCode != 0 && findings.Count == 0)
                throw new ExternalToolParseException(
                    $"Tool '{input.ToolName}' exited {input.ExitCode} but its JSON report contains no findings.");
            return findings;
        }
    }

    private static ExternalToolFinding ParseMessage(JsonElement element, string? path)
    {
        var message = NullIfWhiteSpace(GetString(element, "message"u8)) ?? "(no message)";
        // PHPStan attaches remediation text ("tip": "…") to some errors;
        // keep it inside the finding message rather than dropping it.
        var tip = NullIfWhiteSpace(GetString(element, "tip"u8));
        if (tip is not null)
            message += "\n" + tip;

        int? line = null;
        if (element.TryGetProperty("line"u8, out var lineElement)
            && lineElement.ValueKind == JsonValueKind.Number
            && lineElement.TryGetInt32(out var lineValue)
            && lineValue > 0)
            line = lineValue;

        return new ExternalToolFinding(
            SeverityLevel: "error",
            RuleId: NullIfWhiteSpace(GetString(element, "identifier"u8)),
            Message: message,
            Path: path,
            Line: line);
    }

    // PHPStan absolutizes the analyse targets (".", or whatever was passed)
    // against its process cwd, so files keys are absolute. Strip the exec
    // working-directory prefix so findings land on repository-relative paths
    // the ExcludePaths filter can see; leave foreign paths untouched.
    private static string? NormalizeFilePath(string? raw, string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var path = raw.Trim().Replace('\\', '/');
        var root = workingDirectory?.Trim().Replace('\\', '/').TrimEnd('/');
        if (!string.IsNullOrEmpty(root)
            && path.Length > root.Length + 1
            && path.StartsWith(root + "/", StringComparison.Ordinal))
            path = path[(root.Length + 1)..];
        return path;
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
