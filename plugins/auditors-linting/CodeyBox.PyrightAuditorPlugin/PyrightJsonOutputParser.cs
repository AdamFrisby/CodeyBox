using System.Text.Json;
using CodeyBox.PluginSdk.Tools;

namespace CodeyBox.PyrightAuditorPlugin;

/// <summary>
/// Parses pyright's <c>--outputjson</c> report —
/// <c>{ "version", "time", "generalDiagnostics": [ { "file", "severity",
/// "message", "range"?, "rule"? } ], "summary" }</c> — into
/// <see cref="ExternalToolFinding"/> records.
///
/// <para>Pyright emits <c>file</c> as an absolute path
/// (<c>fileUri.getFilePath()</c> upstream) and, unlike ESLint's
/// <c>json-with-metadata</c>, the report embeds no <c>cwd</c>, so paths are
/// relativized against <see cref="ScanRoot"/> — the audited worktree root the
/// auditor captures per run before the scan executes. Diagnostics outside the
/// scan root (e.g. a typeshed stub or an absolute path an operator added via
/// <c>ExtraArguments</c>) are kept verbatim so nothing is silently
/// rewritten.</para>
///
/// <para>Range lines are 0-based in the report; findings carry the 1-based
/// line. A diagnostic without <c>range</c> (config- or project-level) keeps a
/// file-only location. Severity is kept in pyright's own vocabulary
/// (<c>"error"</c>, <c>"warning"</c>, <c>"information"</c>) and
/// <see cref="ExternalToolAuditorBase"/> maps it through the auditor's
/// declared <see cref="ExternalToolSeverityMapping"/>; raw levels never reach
/// findings.</para>
///
/// <para>Malformed output — empty stdout, non-JSON, or a JSON document without
/// a <c>generalDiagnostics</c> array — throws
/// <see cref="ExternalToolParseException"/>, which the base reports as
/// infrastructure, never as a pass.</para>
/// </summary>
internal sealed class PyrightJsonOutputParser : IExternalToolOutputParser
{
    // Same per-document result bound the shared SARIF parser applies.
    private const int MaxResults = SarifToolOutputParser.DefaultMaxResults;

    private const string FileSchemePrefix = "file://";

    /// <summary>
    /// Normalized absolute path of the directory the scan ran in, captured by
    /// the auditor per invocation before the tool executes. Null leaves
    /// absolute <c>file</c> values unrelativized.
    /// </summary>
    internal string? ScanRoot { get; set; }

    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Stdout))
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced no pyright JSON output on stdout.");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(input.Stdout);
        }
        catch (JsonException ex)
        {
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced output that is not valid pyright JSON: {SingleLine(ex.Message)}.",
                ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("generalDiagnostics"u8, out var diagnostics)
                || diagnostics.ValueKind != JsonValueKind.Array)
                throw new ExternalToolParseException(
                    $"Tool '{input.ToolName}' produced a JSON document without a pyright 'generalDiagnostics' array.");

            var findings = new List<ExternalToolFinding>();
            foreach (var diagnostic in diagnostics.EnumerateArray())
            {
                if (findings.Count >= MaxResults)
                    break;
                if (diagnostic.ValueKind == JsonValueKind.Object)
                    findings.Add(ParseDiagnostic(diagnostic));
            }

            return findings;
        }
    }

    private ExternalToolFinding ParseDiagnostic(JsonElement diagnostic)
    {
        int? line = null;
        if (diagnostic.TryGetProperty("range"u8, out var range)
            && range.ValueKind == JsonValueKind.Object
            && range.TryGetProperty("start"u8, out var start)
            && start.ValueKind == JsonValueKind.Object
            && start.TryGetProperty("line"u8, out var lineElement)
            && lineElement.ValueKind == JsonValueKind.Number
            && lineElement.TryGetInt32(out var zeroBasedLine)
            && zeroBasedLine >= 0)
            line = zeroBasedLine + 1;

        return new ExternalToolFinding(
            SeverityLevel: NullIfWhiteSpace(GetString(diagnostic, "severity"u8)),
            RuleId: NullIfWhiteSpace(GetString(diagnostic, "rule"u8)),
            Message: NullIfWhiteSpace(GetString(diagnostic, "message"u8)) ?? "(no message)",
            Path: NormalizeFilePath(GetString(diagnostic, "file"u8)),
            Line: line);
    }

    private string? NormalizeFilePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var path = NormalizePath(raw);
        if (path.StartsWith(FileSchemePrefix, StringComparison.OrdinalIgnoreCase))
            path = path[FileSchemePrefix.Length..];

        var root = ScanRoot;
        if (!string.IsNullOrEmpty(root)
            && path.StartsWith(root + "/", StringComparison.Ordinal))
            path = path[(root.Length + 1)..];
        return path;
    }

    /// <summary>
    /// Normalizes a path for prefix comparison — forward slashes, trimmed, no
    /// trailing slash (except the filesystem root, which collapses to empty
    /// and therefore never relativizes).
    /// </summary>
    internal static string NormalizePath(string? path)
        => (path ?? string.Empty).Replace('\\', '/').Trim().TrimEnd('/');

    private static string? GetString(JsonElement element, ReadOnlySpan<byte> name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string SingleLine(string message)
        => message.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
