using System.Text.Json;
using CodeyBox.PluginSdk.Tools;
using static CodeyBox.PluginSdk.Tools.ExternalToolJsonHelpers;

namespace CodeyBox.PsScriptAnalyzerAuditorPlugin;

/// <summary>
/// Parses the JSON diagnostic report the provisioned
/// <c>Invoke-ScriptAnalyzer</c> shim emits on stdout: a top-level array of
/// <c>{ "RuleName", "Severity", "Message", "File", "Line" }</c> objects, one
/// per PSScriptAnalyzer diagnostic record (see <see cref="PsScriptAnalyzerAuditor"/>
/// for the shim contract). The tool's native severity token
/// (<c>Error</c>/<c>ParseError</c>/<c>Warning</c>/<c>Information</c>) is
/// carried through for the auditor's declared
/// <see cref="ExternalToolSeverityMapping"/> — raw levels never reach
/// findings — and the file/line the record supplies is preserved.
///
/// <para><c>File</c> is the path the analyzer resolved: relative to the scan
/// target when it can be, absolute otherwise. Absolute paths are relativized
/// against <see cref="ExternalToolParseInput.ScanRoot"/> (probed per run,
/// since sandbox providers may translate the working directory) or the exec
/// working directory; a path that stays absolute is re-marked with a
/// <c>file://</c> prefix so it cannot accidentally match repo-relative
/// <see cref="ExternalToolAuditorOptions.ExcludePaths"/> entries.</para>
///
/// <para>Malformed output — empty stdout, non-JSON, or a JSON document that
/// is not an array (a help dump, an error trace) — throws
/// <see cref="ExternalToolParseException"/>, which the base reports as
/// infrastructure, never as a pass. A findings-producing exit whose stdout
/// parses to an empty report — a shim/version contract drift — likewise
/// throws: "found problems" and "could not run" must stay
/// distinguishable.</para>
/// </summary>
internal sealed class PsScriptAnalyzerJsonOutputParser : IExternalToolOutputParser
{
    /// <summary>Upper bound on diagnostics consumed from one report — the same bound the shared SARIF parser applies.</summary>
    internal const int MaxResults = SarifToolOutputParser.DefaultMaxResults;

    private const int MessageMaxChars = 2000;
    private const string FileSchemePrefix = "file://";

    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Stdout))
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced no Invoke-ScriptAnalyzer JSON output on stdout.");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(input.Stdout);
        }
        catch (JsonException ex)
        {
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced output that is not valid Invoke-ScriptAnalyzer JSON: {SingleLine(ex.Message)}.",
                ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new ExternalToolParseException(
                    $"Tool '{input.ToolName}' produced JSON that is not an Invoke-ScriptAnalyzer report "
                    + "(expected a top-level array of diagnostic objects).");

            var findings = new List<ExternalToolFinding>();
            foreach (var record in document.RootElement.EnumerateArray())
            {
                if (findings.Count >= MaxResults)
                    break;
                if (record.ValueKind == JsonValueKind.Object)
                    findings.Add(ParseRecord(record, input));
            }

            // A non-zero findings-producing exit means the shim saw
            // diagnostics; a report parsing to nothing then means the JSON
            // contract drifted (foreign shim, version skew) rather than a
            // clean tree — fail closed as infrastructure.
            if (input.ExitCode != 0 && findings.Count == 0)
                throw new ExternalToolParseException(
                    $"Tool '{input.ToolName}' exited {input.ExitCode} (diagnostics reported) but produced "
                    + "no parseable diagnostic records on stdout.");
            return findings;
        }
    }

    private static ExternalToolFinding ParseRecord(JsonElement record, ExternalToolParseInput input)
    {
        var message = NullIfWhiteSpace(GetString(record, "Message"u8)) ?? "(no message)";

        return new ExternalToolFinding(
            SeverityLevel: NullIfWhiteSpace(GetString(record, "Severity"u8)),
            RuleId: NullIfWhiteSpace(GetString(record, "RuleName"u8)),
            Message: Truncate(SingleLine(message), MessageMaxChars),
            Path: NormalizeReportedPath(GetString(record, "File"u8), input),
            Line: ReadPositiveInt(record, "Line"u8));
    }

    private static int? ReadPositiveInt(JsonElement element, ReadOnlySpan<byte> name)
        => element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out var value)
            && value > 0
            ? value
            : null;

    private static string? NormalizeReportedPath(string? raw, ExternalToolParseInput input)
    {
        var path = NormalizePath(raw);
        if (path.Length == 0)
            return null;
        if (path.StartsWith(FileSchemePrefix, StringComparison.OrdinalIgnoreCase))
            path = path[FileSchemePrefix.Length..];

        // A tool-reported "./x" is the same location as "x", and a reported
        // "a/../b" is "b" — dot segments are collapsed lexically BEFORE
        // relativization, otherwise "/work/../outside.ps1" would strip the
        // scan-root prefix and reach findings as the pseudo repo-relative
        // "../outside.ps1".
        path = CollapseDotSegments(path);
        if (path.Length == 0)
            return null;

        if (!path.StartsWith("/", StringComparison.Ordinal))
        {
            // A relative path still carrying '..' escapes its base — mark
            // it out-of-tree like an unresolved absolute path rather than
            // letting it reach finding locations as a repo-relative value.
            return HasDotDotSegment(path) ? FileSchemePrefix + path : path;
        }

        var relative = RelativizeToRoot(path, input.ScanRoot);
        if (!relative.StartsWith("/", StringComparison.Ordinal))
            return relative;
        relative = RelativizeToRoot(path, input.WorkingDirectory);
        if (!relative.StartsWith("/", StringComparison.Ordinal))
            return relative;

        // Out-of-root absolute path: re-mark with the file:// scheme so it
        // stays distinguishable from a repo-relative path (the base trims a
        // bare leading '/' from finding paths).
        return FileSchemePrefix + path;
    }

    // Lexically resolves '.' and '..' segments on a '/'-separated path —
    // '..' past the root clamps for absolute paths, and stays a leading
    // segment for relative ones (a genuine escape the caller marks).
    private static string CollapseDotSegments(string path)
    {
        var absolute = path.Length > 0 && path[0] == '/';
        var segments = new List<string>();
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
                continue;
            if (segment == "..")
            {
                if (segments.Count > 0 && segments[^1] != "..")
                    segments.RemoveAt(segments.Count - 1);
                else if (!absolute)
                    segments.Add(segment);
                continue;
            }
            segments.Add(segment);
        }
        var joined = string.Join('/', segments);
        return absolute ? "/" + joined : joined;
    }

    private static bool HasDotDotSegment(string path)
        => path.Split('/').Contains("..", StringComparer.Ordinal);
}
