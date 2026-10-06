using System.Text.RegularExpressions;
using CodeyBox.PluginSdk.Tools;

namespace CodeyBox.CueAuditorPlugin;

/// <summary>
/// Parses <c>cue vet -c</c> diagnostics into <see cref="ExternalToolFinding"/>
/// records. The tool is silent on success and reports failures on stderr as
/// blank-line-separated blocks: a one-line message followed by indented
/// <c>path:line:col</c> references, e.g.
/// <code>
/// replicas: invalid value 99 (out of bound &lt;=5):
///     ./schema/svc.cue:5:24
///     ./data/bad.yaml:2:11
/// </code>
/// The block message becomes the finding message; the location prefers the
/// first reference outside a <c>.cue</c> schema file (the validated data),
/// falling back to the first reference. Some diagnostics carry no location
/// at all (e.g. <c>replicas: incomplete value int</c>) and report
/// path-less. CUE reports no rule ids or severities, so every finding
/// carries the synthesized <see cref="RuleId"/> and the single severity
/// level the auditor maps to <see cref="CodeyBox.Core.AuditSeverity.Error"/>.
/// <para>Blocks describing a tool or configuration failure rather than a
/// validation verdict — a missing file, an unresolved import, a registry
/// fetch failure, a bad flag — throw <see cref="ExternalToolParseException"/>,
/// which the base reports as infrastructure, never as a pass and never as a
/// finding. An exit without any parseable diagnostic block likewise fails
/// closed: with the pre-scan input verification the auditor performs, a
/// silent exit 0 is a checked pass, but a noisy exit 1 with no diagnostic
/// is incomplete evidence.</para>
/// </summary>
internal sealed class CueVetOutputParser : IExternalToolOutputParser
{
    /// <summary>Synthesized rule id for every CUE validation diagnostic.</summary>
    internal const string ValidationRuleId = "cue/validation";

    /// <summary>Severity level carried by every finding; mapped to Error.</summary>
    internal const string ValidationSeverityLevel = "error";

    // Same per-document result bound the shared SARIF parser applies, so a
    // diagnostic-dense run cannot produce an unbounded in-memory list before
    // the base applies its own MaxFindings cap.
    private const int MaxResults = SarifToolOutputParser.DefaultMaxResults;

    // One diagnostic message can run long; bound a single finding's message
    // so one block cannot dominate the audit report.
    private const int MaxMessageChars = 4000;

    // A single block can reference many positions (deep value traces with
    // -E/--all-errors); keep a bounded prefix so one block cannot produce
    // an unbounded reference scan. Only one reference becomes the finding
    // location, so this bounds work, not findings.
    private const int MaxReferencesPerBlock = 16;

    // First-line markers (case-insensitive) identifying a block as a tool or
    // configuration failure rather than a validation verdict. Verified
    // against cue v0.17.1: missing files surface as `stat <path>: no such
    // file or directory`, bad flags as `unknown flag: ...`, and unresolvable
    // imports as `import failed: ...` (which still carries a file:line
    // reference and so must be classified by its message, not its shape).
    // Registry fetch failures surface with the same fetch vocabulary. Each
    // marker is a multi-word phrase specific to tool health — ordinary
    // validation messages name a field path and a violated constraint, so
    // they cannot contain these phrases without describing the tool itself.
    internal static readonly IReadOnlyList<string> ToolFailureMarkers =
    [
        "no such file or directory",
        "unknown flag",
        "unknown shorthand flag",
        "import failed",
        "cannot find package",
        "cannot load",
        "unknown import",
        "no cue.mod",
        "missing cue.mod",
        "failed to fetch",
        "cannot fetch",
        "fetch failed",
        "no registry",
        "connection refused",
    ];

    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var combined = string.Concat(input.Stderr ?? string.Empty, "\n", input.Stdout ?? string.Empty);

        var findings = new List<ExternalToolFinding>();
        foreach (var block in SplitBlocks(combined))
        {
            if (findings.Count >= MaxResults)
                break;
            var finding = ParseBlock(block, input.ToolName);
            if (finding is not null)
                findings.Add(finding);
        }

        if (findings.Count == 0 && input.ExitCode != 0)
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' exited {input.ExitCode} without a parseable 'cue vet' diagnostic. "
                + "A validation failure always prints at least its message (usually with file positions); "
                + "output without even that means the tool could not run (missing input, bad flags, "
                + "unresolved import) or its output was truncated.");

        return findings;
    }

    private static ExternalToolFinding? ParseBlock(IReadOnlyList<string> lines, string toolName)
    {
        var message = ToolOutputText.NullIfWhiteSpace(lines[0]);
        if (message is null)
            return null;
        if (IsToolFailure(message))
            throw new ExternalToolParseException(
                $"Tool '{toolName}' reported a tool failure rather than a validation verdict: "
                + ToolOutputText.SingleLine(Truncate(message)));

        string? path = null;
        int? line = null;
        var references = 0;
        string? firstReferencePath = null;
        int? firstReferenceLine = null;
        string? dataPath = null;
        int? dataLine = null;
        foreach (var candidate in lines.Skip(1))
        {
            if (references >= MaxReferencesPerBlock)
                break;
            if (!TryParseReference(candidate, out var referencePath, out var referenceLine))
                continue;
            references++;
            firstReferencePath ??= referencePath;
            firstReferenceLine ??= referenceLine;
            if (dataPath is null && !referencePath.EndsWith(".cue", StringComparison.OrdinalIgnoreCase))
            {
                dataPath = referencePath;
                dataLine = referenceLine;
            }
        }

        path = dataPath ?? firstReferencePath;
        line = dataPath is null ? firstReferenceLine : dataLine;
        if (path is null && TryParseMessagePrefix(message, out var prefixPath, out var prefixLine))
        {
            path = prefixPath;
            line = prefixLine;
        }

        return new ExternalToolFinding(
            SeverityLevel: ValidationSeverityLevel,
            RuleId: ValidationRuleId,
            Message: Truncate(message),
            Path: ToolOutputText.NullIfWhiteSpace(path),
            Line: line);
    }

    private static bool IsToolFailure(string message)
        => ToolFailureMarkers.Any(marker =>
            message.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<IReadOnlyList<string>> SplitBlocks(string text)
    {
        var current = new List<string>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (line.Length == 0)
            {
                if (current.Count > 0)
                {
                    yield return current;
                    current = [];
                }
                continue;
            }
            current.Add(line);
        }

        if (current.Count > 0)
            yield return current;
    }

    private static readonly Regex ReferencePattern = new(
        @"^(.*?):(\d+)(?::(\d+))?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex MessagePrefixPattern = new(
        @"^(.*?):(\d+):\s",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Single-line diagnostics carry their location as a message prefix
    // (`/path/broken.yaml:1: did not find expected ',' or ']'`) with no
    // reference lines. The head must look like a file path (a separator or
    // an extension dot) so a field-named message (`port: invalid value
    // 808080: out of range`) is never misread as a location.
    private static bool TryParseMessagePrefix(string message, out string path, out int? lineNumber)
    {
        path = string.Empty;
        lineNumber = null;
        var match = MessagePrefixPattern.Match(message);
        if (!match.Success)
            return false;
        var head = match.Groups[1].Value;
        if (!head.Contains('/') && !head.Contains('\\') && !head.Contains('.'))
            return false;
        if (!int.TryParse(match.Groups[2].Value, out var parsedLine) || parsedLine <= 0)
            return false;
        path = NormalizeReferencePath(head);
        if (path.Length == 0)
            return false;
        lineNumber = parsedLine;
        return true;
    }

    private static bool TryParseReference(string line, out string path, out int? lineNumber)
    {
        path = string.Empty;
        lineNumber = null;
        var match = ReferencePattern.Match(line.Trim());
        if (!match.Success)
            return false;
        // The trailing token must be a line (or line:col) pair, not prose
        // that happens to end in a number (e.g. `error count: 3`): a real
        // reference names a file, so the head must contain a path separator
        // or an extension dot.
        var head = match.Groups[1].Value;
        if (!head.Contains('/') && !head.Contains('\\') && !head.Contains('.'))
            return false;
        if (!int.TryParse(match.Groups[2].Value, out var parsedLine) || parsedLine <= 0)
            return false;
        if (match.Groups[3].Success
            && (!int.TryParse(match.Groups[3].Value, out var column) || column <= 0))
            return false;
        path = NormalizeReferencePath(head);
        if (path.Length == 0)
            return false;
        lineNumber = parsedLine;
        return true;
    }

    private static string NormalizeReferencePath(string raw)
    {
        var normalized = raw.Trim().Replace('\\', '/');
        if (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        return normalized.Trim();
    }

    private static string Truncate(string message)
        => message.Length <= MaxMessageChars ? message : message[..MaxMessageChars];
}
