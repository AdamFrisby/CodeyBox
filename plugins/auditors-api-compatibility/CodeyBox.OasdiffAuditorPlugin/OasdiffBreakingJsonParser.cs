using System.Text.Json;
using CodeyBox.PluginSdk.Tools;

namespace CodeyBox.OasdiffAuditorPlugin;

/// <summary>
/// Parses the <c>oasdiff breaking BASE REVISION --format json</c> report on stdout into
/// <see cref="ExternalToolFinding"/> records. Verified against oasdiff 1.33.0: the tool prints
/// a single JSON array where each entry carries <c>id</c> (the stable kebab-case check id, e.g.
/// <c>response-success-status-removed</c>), <c>text</c> (human-readable description),
/// <c>level</c> (numeric <c>3</c>/<c>2</c>/<c>1</c> for ERR/WARN/INFO), the OpenAPI <c>path</c>
/// and <c>operation</c> the change applies to, and <c>revisionSource</c>/<c>baseSource</c>
/// objects carrying the <c>file</c> plus <c>line</c>/<c>column</c> origin of the change.
/// A clean comparison prints <c>[]</c>.
///
/// <para>Each entry becomes one finding. The rule id is the tool's own <c>id</c>, preserved
/// verbatim so findings stay selectable through the shared
/// <c>IncludedRules</c>/<c>ExcludedRules</c> knobs. The severity token is the entry's own
/// level, canonicalized to <c>error</c>/<c>warn</c>/<c>info</c> and mapped by the auditor's
/// declared <see cref="ExternalToolSeverityMapping"/> — never passed through, never guessed.
/// The finding location prefers the <c>revisionSource</c> (the candidate document under audit)
/// and falls back to the <c>baseSource</c>; the OpenAPI path and operation travel in the
/// message prose since they name a location inside the documents, not a file.</para>
///
/// <para>The exit code and the report cross-check each other, because only the combination is
/// unambiguous: with the auditor-owned <c>--fail-on</c> threshold (default <c>ERR</c>), exit
/// <c>1</c> means entries at or above the threshold were found, while exit <c>0</c> means none
/// were (WARN-only entries still print on exit <c>0</c>). Exit <c>1</c> with no
/// at-or-above-threshold entry, an at-or-above-threshold entry on exit <c>0</c>, and empty or
/// unrecognizable stdout on any exit all contradict the contract and throw
/// <see cref="ExternalToolParseException"/> — infrastructure, never a pass. An entry whose
/// <c>level</c> names no known severity likewise fails closed: the level is the only severity
/// signal, so an unknown one cannot be mapped without guessing.</para>
/// </summary>
internal sealed class OasdiffBreakingJsonParser(
    Func<string?> revisionSpecPathProvider,
    Func<string?> failOnProvider)
    : IExternalToolOutputParser
{
    /// <summary>Canonical tool level for definite breaking changes (maps to <see cref="CodeyBox.Core.AuditSeverity.Error"/>).</summary>
    internal const string ErrorLevel = "error";

    /// <summary>Canonical tool level for potential breaking changes (maps to <see cref="CodeyBox.Core.AuditSeverity.Warning"/>).</summary>
    internal const string WarnLevel = "warn";

    /// <summary>Canonical tool level for non-breaking changes (maps to <see cref="CodeyBox.Core.AuditSeverity.Info"/>).</summary>
    internal const string InfoLevel = "info";

    private const int MaxResults = SarifToolOutputParser.DefaultMaxResults;
    private const int MessageMaxChars = 512;

    private readonly Func<string?> _revisionSpecPathProvider =
        revisionSpecPathProvider ?? throw new ArgumentNullException(nameof(revisionSpecPathProvider));
    private readonly Func<string?> _failOnProvider =
        failOnProvider ?? throw new ArgumentNullException(nameof(failOnProvider));

    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var entries = ExtractEntries(input);
        var findings = new List<ExternalToolFinding>();
        var sawAtOrAboveThreshold = false;
        var threshold = ResolveThreshold();

        foreach (var entry in entries)
        {
            if (findings.Count >= MaxResults)
                break;
            var finding = ParseEntry(input, entry);
            if (AtOrAboveThreshold(finding.SeverityLevel, threshold))
                sawAtOrAboveThreshold = true;
            findings.Add(finding);
        }

        if (findings.Count == 0)
        {
            if (input.ExitCode == 0)
                return findings;

            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' exited {input.ExitCode} without reporting an OpenAPI "
                + "breaking change — the exit claims findings the report does not show.");
        }

        if (input.ExitCode == 0 && sawAtOrAboveThreshold)
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' exited 0 (no breaking changes at the --fail-on threshold) "
                + "but its report carries a breaking change entry at or above that threshold — "
                + "the exit claims a verdict the report contradicts.");

        if (input.ExitCode == 1 && !sawAtOrAboveThreshold)
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' exited 1 (breaking changes found) but its report carries "
                + "no breaking change entry at or above the --fail-on threshold — the exit claims "
                + "findings the report does not show.");

        return findings;
    }

    private static bool AtOrAboveThreshold(string? canonicalLevel, string threshold)
        => canonicalLevel switch
        {
            ErrorLevel => true,
            WarnLevel => string.Equals(threshold, OasdiffAuditor.WarnFailOn, StringComparison.Ordinal),
            _ => false,
        };

    private string ResolveThreshold()
    {
        var failOn = _failOnProvider()?.Trim().ToUpperInvariant();
        return string.Equals(failOn, OasdiffAuditor.WarnFailOn, StringComparison.Ordinal)
            ? OasdiffAuditor.WarnFailOn
            : OasdiffAuditor.ErrFailOn;
    }

    private static IReadOnlyList<JsonElement> ExtractEntries(ExternalToolParseInput input)
    {
        var stdout = input.Stdout ?? string.Empty;
        if (string.IsNullOrWhiteSpace(stdout))
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced no breaking-changes report on stdout — a silent "
                + "tool is never a passing audit.");

        if (!TryParseReport(stdout, out var entries, out var shapeError)
            && !TryParseReport(ExtractJsonSlice(stdout), out entries, out shapeError))
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced output that is not a recognizable oasdiff "
                + $"breaking-changes JSON report: {shapeError}");

        var changes = new List<JsonElement>();
        foreach (var entry in entries)
        {
            if (entry.ValueKind != JsonValueKind.Object)
                throw new ExternalToolParseException(
                    $"Tool '{input.ToolName}' reported a change entry that is not a JSON object — "
                    + "the report shape changed and cannot be mapped without guessing.");
            changes.Add(entry);
        }

        return changes;
    }

    private static bool TryParseReport(
        string? text, out List<JsonElement> entries, out string shapeError)
    {
        entries = [];
        shapeError = "no JSON document found";
        if (string.IsNullOrWhiteSpace(text))
            return false;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            shapeError = ToolOutputText.SingleLine(ex.Message);
            return false;
        }

        using (document)
        {
            // Clone before disposal: the returned entries outlive the document.
            var root = document.RootElement.Clone();
            if (root.ValueKind == JsonValueKind.Array)
            {
                entries = root.EnumerateArray().ToList();
                shapeError = string.Empty;
                return true;
            }

            shapeError = "JSON is not a breaking-changes array";
            return false;
        }
    }

    private static string ExtractJsonSlice(string stdout)
    {
        var firstArray = stdout.IndexOf('[');
        if (firstArray < 0)
            return string.Empty;

        // The tool prints exactly one JSON document; tolerate non-JSON log
        // chatter around it by slicing from the first opening bracket to the
        // last close. A truncated report still fails below: the slice either
        // does not parse or lacks the array shape.
        var end = stdout.LastIndexOf(']');
        return end > firstArray ? stdout[firstArray..(end + 1)] : string.Empty;
    }

    private ExternalToolFinding ParseEntry(ExternalToolParseInput input, JsonElement entry)
    {
        var level = CanonicalLevel(entry);
        if (level is null)
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' reported a change with an unrecognized level — the "
                + "level is the only severity signal, so it cannot be mapped without guessing.");

        var ruleId = ExternalToolJsonHelpers.NullIfWhiteSpace(
            ExternalToolJsonHelpers.GetString(entry, "id"u8));
        var text = ExternalToolJsonHelpers.NullIfWhiteSpace(
            ExternalToolJsonHelpers.GetString(entry, "text"u8));
        var operation = ExternalToolJsonHelpers.NullIfWhiteSpace(
            ExternalToolJsonHelpers.GetString(entry, "operation"u8));
        var openApiPath = ExternalToolJsonHelpers.NullIfWhiteSpace(
            ExternalToolJsonHelpers.GetString(entry, "path"u8));

        var (reportedFile, reportedLine) = ChangeLocation(entry);
        var file = ExternalToolJsonHelpers.NormalizeReportedPath(
                reportedFile, input.ScanRoot, input.WorkingDirectory)
            ?? NormalizeSpecFallback(_revisionSpecPathProvider());

        return new ExternalToolFinding(
            SeverityLevel: level,
            RuleId: ruleId,
            Message: ChangeMessage(text, operation, openApiPath),
            Path: file,
            Line: reportedLine);
    }

    /// <summary>
    /// Normalizes a numeric (<c>3</c>/<c>2</c>/<c>1</c>) or string
    /// (<c>error</c>/<c>warn</c>/<c>info</c>, case-insensitive) tool level to its canonical
    /// form, or null when the token names no known severity.
    /// </summary>
    internal static string? CanonicalLevel(JsonElement entry)
    {
        if (entry.TryGetProperty("level"u8, out var levelElement))
        {
            if (levelElement.ValueKind == JsonValueKind.Number
                && levelElement.TryGetInt32(out var numeric))
            {
                return numeric switch
                {
                    3 => ErrorLevel,
                    2 => WarnLevel,
                    1 => InfoLevel,
                    _ => null,
                };
            }

            if (levelElement.ValueKind == JsonValueKind.String)
                return CanonicalLevelToken(levelElement.GetString());
        }

        return null;
    }

    internal static string? CanonicalLevelToken(string? token)
        => token?.Trim().ToLowerInvariant() switch
        {
            "error" or "err" => ErrorLevel,
            "warn" or "warning" => WarnLevel,
            "info" or "informational" => InfoLevel,
            _ => null,
        };

    /// <summary>
    /// Resolves the finding location from the tool-supplied origins, preferring the
    /// <c>revisionSource</c> (the candidate document under audit) over the <c>baseSource</c>.
    /// </summary>
    internal static (string? File, int? Line) ChangeLocation(JsonElement entry)
    {
        foreach (var name in new[] { "revisionSource", "baseSource" })
        {
            if (!entry.TryGetProperty(name, out var source)
                || source.ValueKind != JsonValueKind.Object)
                continue;
            var file = ExternalToolJsonHelpers.NullIfWhiteSpace(
                ExternalToolJsonHelpers.GetString(source, "file"u8));
            int? line = null;
            if (source.TryGetProperty("line"u8, out var lineElement)
                && lineElement.ValueKind == JsonValueKind.Number
                && lineElement.TryGetInt32(out var lineValue)
                && lineValue > 0)
                line = lineValue;
            if (file is not null || line is not null)
                return (file, line);
        }

        return (null, null);
    }

    private static string ChangeMessage(string? text, string? operation, string? openApiPath)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append(string.IsNullOrWhiteSpace(text) ? "OpenAPI breaking change" : text.Trim());
        if (!string.IsNullOrWhiteSpace(operation) || !string.IsNullOrWhiteSpace(openApiPath))
        {
            builder.Append(" [");
            if (!string.IsNullOrWhiteSpace(operation))
                builder.Append(operation.Trim()).Append(' ');
            builder.Append(string.IsNullOrWhiteSpace(openApiPath) ? "(no path)" : openApiPath.Trim());
            builder.Append(']');
        }

        return ExternalToolJsonHelpers.Truncate(
            ToolOutputText.SingleLine(builder.ToString()), MessageMaxChars);
    }

    private static string? NormalizeSpecFallback(string? pointer)
    {
        if (string.IsNullOrWhiteSpace(pointer))
            return null;
        var candidate = pointer.Trim().Replace('\\', '/').TrimStart('/');
        if (candidate.StartsWith("./", StringComparison.Ordinal))
            candidate = candidate[2..];
        return string.IsNullOrWhiteSpace(candidate) ? null : candidate;
    }
}
