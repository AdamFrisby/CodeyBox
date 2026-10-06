using System.Text.Json;
using CodeyBox.PluginSdk.Tools;

namespace CodeyBox.AsyncApiAuditorPlugin;

/// <summary>
/// Parses the <c>asyncapi diff OLD NEW --format json</c> report on stdout into
/// <see cref="ExternalToolFinding"/> records. Verified against
/// <c>@asyncapi/cli</c> 5.0.7 (backed by <c>@asyncapi/diff</c> 0.5.0): with
/// <c>--type all</c> the tool prints a single JSON document
/// <c>{"changes": [...]}</c> where each entry carries <c>action</c>
/// (<c>add</c>/<c>remove</c>/<c>edit</c>), <c>path</c> (a JSON-pointer-style
/// location inside the compared documents), and <c>type</c>
/// (<c>breaking</c>/<c>non-breaking</c>/<c>unclassified</c>). With a
/// single-category <c>--type</c> selection the tool instead prints the bare
/// changes array; both shapes are accepted, and every entry keeps its own
/// <c>type</c> as the canonical severity level so a category is never
/// collapsed or reclassified by the auditor.
///
/// <para>Each change entry becomes one finding. The library supplies no
/// per-change rule catalogue — only the pointer path and the action — so the
/// rule id is <c>{CATEGORY}_{ACTION}</c> (for example
/// <c>BREAKING_REMOVE</c>, <c>NON_BREAKING_ADD</c>,
/// <c>UNCLASSIFIED_EDIT</c>), falling back to <c>{CATEGORY}_CHANGE</c> when
/// the action is missing. The severity token is the entry's own category,
/// mapped by the auditor's declared
/// <see cref="ExternalToolSeverityMapping"/> — never passed through, never
/// guessed.</para>
///
/// <para>The exit code and the report cross-check each other, because exit
/// <c>1</c> is ambiguous (verified against the 5.0.7
/// <c>src/apps/cli/commands/diff.ts</c> source): a thrown
/// <c>DiffBreakingChangeError</c> exits <c>1</c> after printing the JSON
/// report, but a load, parse, or validation failure exits <c>1</c> through
/// the base command's error handler with no JSON report at all — and an
/// invalid document short-circuits the command to a silent exit <c>0</c>
/// with no output. Exit <c>1</c> with at least one breaking entry is the
/// findings verdict; exit <c>1</c> with no breaking entry, exit <c>0</c>
/// with a breaking entry, and empty or unrecognizable stdout on any exit
/// all contradict the contract and throw
/// <see cref="ExternalToolParseException"/> — infrastructure, never a pass.
/// An entry whose <c>type</c> is outside the three known categories likewise
/// fails closed: the category is the only severity signal, so an unknown
/// one cannot be mapped without guessing.</para>
/// </summary>
internal sealed class AsyncApiDiffParser(Func<string?> newSpecPathProvider)
    : IExternalToolOutputParser
{
    /// <summary>Canonical tool level for <c>breaking</c> entries (maps to <see cref="CodeyBox.Core.AuditSeverity.Error"/>).</summary>
    internal const string BreakingLevel = "breaking";

    /// <summary>Canonical tool level for <c>unclassified</c> entries (maps to <see cref="CodeyBox.Core.AuditSeverity.Warning"/>).</summary>
    internal const string UnclassifiedLevel = "unclassified";

    /// <summary>Canonical tool level for <c>non-breaking</c> entries (maps to <see cref="CodeyBox.Core.AuditSeverity.Info"/>).</summary>
    internal const string NonBreakingLevel = "non-breaking";

    /// <summary>Fallback rule id when a breaking entry carries no usable action.</summary>
    internal const string BreakingFallbackRuleId = "BREAKING_CHANGE";

    /// <summary>Fallback rule id when an unclassified entry carries no usable action.</summary>
    internal const string UnclassifiedFallbackRuleId = "UNCLASSIFIED_CHANGE";

    /// <summary>Fallback rule id when a non-breaking entry carries no usable action.</summary>
    internal const string NonBreakingFallbackRuleId = "NON_BREAKING_CHANGE";

    private const int MaxResults = SarifToolOutputParser.DefaultMaxResults;
    private const int MessageMaxChars = 512;
    private const int RuleIdMaxChars = 80;

    private readonly Func<string?> _newSpecPathProvider =
        newSpecPathProvider ?? throw new ArgumentNullException(nameof(newSpecPathProvider));

    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var changes = ExtractChanges(input);
        var findings = new List<ExternalToolFinding>();
        var sawBreaking = false;

        foreach (var change in changes)
        {
            if (findings.Count >= MaxResults)
                break;
            var finding = ParseChange(input.ToolName, change);
            if (string.Equals(finding.SeverityLevel, BreakingLevel, StringComparison.Ordinal))
                sawBreaking = true;
            findings.Add(finding);
        }

        if (findings.Count == 0)
        {
            if (input.ExitCode == 0)
                return findings;

            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' exited {input.ExitCode} without reporting a breaking "
                + "AsyncAPI change — the exit claims findings the report does not show.");
        }

        if (input.ExitCode == 0 && sawBreaking)
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' exited 0 (no breaking changes) but its report carries "
                + "a breaking change entry — the exit claims a verdict the report contradicts.");

        if (input.ExitCode == 1 && !sawBreaking)
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' exited 1 (breaking changes) but its report carries "
                + "no breaking change entry — the exit claims findings the report does not show.");

        return findings;
    }

    private static IReadOnlyList<JsonElement> ExtractChanges(ExternalToolParseInput input)
    {
        var stdout = input.Stdout ?? string.Empty;
        if (string.IsNullOrWhiteSpace(stdout))
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced no diff report on stdout — a silent tool "
                + "is never a passing audit.");

        if (!TryParseReport(stdout, out var entries, out var shapeError)
            && !TryParseReport(ExtractJsonSlice(stdout), out entries, out shapeError))
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced output that is not a recognizable asyncapi "
                + $"diff JSON report: {shapeError}");

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

            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("changes"u8, out var changes)
                && changes.ValueKind == JsonValueKind.Array)
            {
                entries = changes.EnumerateArray().ToList();
                shapeError = string.Empty;
                return true;
            }

            shapeError = "JSON has neither a 'changes' array nor a bare changes array";
            return false;
        }
    }

    private static string ExtractJsonSlice(string stdout)
    {
        var firstObject = stdout.IndexOf('{');
        var firstArray = stdout.IndexOf('[');
        int start;
        char close;
        if (firstObject >= 0 && (firstArray < 0 || firstObject < firstArray))
        {
            start = firstObject;
            close = '}';
        }
        else if (firstArray >= 0)
        {
            start = firstArray;
            close = ']';
        }
        else
        {
            return string.Empty;
        }

        // The tool prints exactly one JSON document; tolerate non-JSON log
        // chatter around it by slicing from the first opening bracket to the
        // last matching close. A truncated report still fails below: the
        // slice either does not parse or lacks the 'changes' shape.
        var end = stdout.LastIndexOf(close);
        return end > start ? stdout[start..(end + 1)] : string.Empty;
    }

    private ExternalToolFinding ParseChange(string toolName, JsonElement change)
    {
        var category = CanonicalCategory(ExternalToolJsonHelpers.GetString(change, "type"u8));
        if (category is null)
            throw new ExternalToolParseException(
                $"Tool '{toolName}' reported a change with an unrecognized category — the "
                + "category is the only severity signal, so it cannot be mapped without guessing.");

        var action = ExternalToolJsonHelpers.NullIfWhiteSpace(
            ExternalToolJsonHelpers.GetString(change, "action"u8));
        var path = ExternalToolJsonHelpers.NullIfWhiteSpace(
            ExternalToolJsonHelpers.GetString(change, "path"u8));

        return new ExternalToolFinding(
            SeverityLevel: category,
            RuleId: ChangeRuleId(category, action),
            Message: ChangeMessage(category, action, path),
            Path: NormalizeNewSpecPath(_newSpecPathProvider()),
            Line: null);
    }

    /// <summary>
    /// Normalizes a reported <c>type</c> token to its canonical category, or
    /// null when the token names no known category. Comparison is
    /// case-insensitive; the canonical lowercase form is always returned so
    /// downstream severity mapping sees one spelling.
    /// </summary>
    internal static string? CanonicalCategory(string? type)
        => type?.Trim() is { } trimmed && trimmed.Length > 0
            ? trimmed.ToLowerInvariant() switch
            {
                BreakingLevel => BreakingLevel,
                UnclassifiedLevel => UnclassifiedLevel,
                NonBreakingLevel => NonBreakingLevel,
                _ => null,
            }
            : null;

    /// <summary>
    /// Builds the stable rule id for a change: <c>{CATEGORY}_{ACTION}</c>
    /// (for example <c>BREAKING_REMOVE</c>), or <c>{CATEGORY}_CHANGE</c>
    /// when the action is missing. Selectable through
    /// <c>IncludedRules</c>/<c>ExcludedRules</c>.
    /// </summary>
    internal static string ChangeRuleId(string category, string? action)
    {
        ArgumentNullException.ThrowIfNull(category);
        var prefix = category.ToUpperInvariant().Replace('-', '_');
        var sanitized = SanitizeToken(action);
        return sanitized.Length == 0 ? prefix + "_CHANGE" : prefix + "_" + sanitized;
    }

    private static string ChangeMessage(string category, string? action, string? path)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append(string.IsNullOrWhiteSpace(action) ? "changed" : action.Trim());
        if (!string.IsNullOrWhiteSpace(path))
            builder.Append(' ').Append(path.Trim());
        builder.Append(" (").Append(category).Append(" change)");
        return ExternalToolJsonHelpers.Truncate(
            ToolOutputText.SingleLine(builder.ToString()), MessageMaxChars);
    }

    private static string SanitizeToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var c in value.Trim().ToUpperInvariant())
            builder.Append(char.IsAsciiLetterOrDigit(c) ? c : '_');
        var sanitized = builder.ToString().Trim('_');
        return sanitized.Length > RuleIdMaxChars ? sanitized[..RuleIdMaxChars] : sanitized;
    }

    /// <summary>
    /// Echoes the configured candidate-spec pointer as the finding path when it
    /// names a repository-relative document; anything else carries no
    /// repository location, so the path stays unset rather than guessed. The
    /// tool reports JSON-pointer paths into the documents — never file/line —
    /// so findings never carry a line number.
    /// </summary>
    internal static string? NormalizeNewSpecPath(string? pointer)
    {
        if (string.IsNullOrWhiteSpace(pointer))
            return null;
        var candidate = pointer.Trim().Replace('\\', '/');
        if (candidate.Length == 0 || candidate.Length > MessageMaxChars)
            return null;
        if (candidate.StartsWith("./", StringComparison.Ordinal))
            candidate = candidate[2..];
        return string.IsNullOrWhiteSpace(candidate) ? null : candidate;
    }
}
