using System.Text.RegularExpressions;
using CodeyBox.PluginSdk.Tools;

namespace CodeyBox.GraphqlInspectorAuditorPlugin;

/// <summary>
/// Parses the <c>graphql-inspector diff</c> human-readable report on stdout into
/// <see cref="ExternalToolFinding"/> records. Verified against
/// <c>@graphql-inspector/cli</c> 7.0.0: the <c>diff</c> command exposes no JSON
/// (or SARIF) output mode — its builder accepts only <c>--rule</c>,
/// <c>--onComplete</c> and <c>--onUsage</c> — so the verdict is the logger's
/// line report: a <c>Detected the following changes (N) between schemas:</c>
/// header, one symbol-prefixed line per change (<c>✖</c> breaking,
/// <c>⚠</c> dangerous, <c>✔</c> non-breaking — the <c>log-symbols</c> glyphs
/// the tool emits on Linux sandboxes), and a footer of either
/// <c>Detected N breaking change(s)</c>, <c>No breaking changes
/// detected</c>, or — when the schemas are identical — <c>No changes
/// detected</c>.
///
/// <para>Each symbol-prefixed line becomes one finding. The tool prints only
/// the change <em>message</em> — never the upstream <c>ChangeType</c> and
/// never a file/line — so the rule id is recovered from the message shape
/// through the template table below (each template mirrors a message builder
/// in <c>@graphql-inspector/core</c>'s <c>diff/changes</c> sources), with a
/// level-derived fallback (<c>BREAKING_CHANGE</c> / <c>DANGEROUS_CHANGE</c> /
/// <c>NON_BREAKING_CHANGE</c>) so an unfamiliar message is still reported,
/// never dropped. The severity token is the canonical criticality level for
/// the symbol — mapped by the auditor's declared
/// <see cref="ExternalToolSeverityMapping"/>, never passed through.</para>
///
/// <para>The exit code and the report cross-check each other, because exit
/// <c>1</c> is ambiguous: <c>failOnBreakingChanges</c> calls
/// <c>process.exit(1)</c> when breaking changes exist, but a thrown load
/// error (unresolvable pointer, invalid SDL) also exits <c>1</c> via yargs.
/// Exit <c>1</c> with breaking lines is the findings verdict; exit <c>1</c>
/// with no change lines (an error dump), or with change lines but none
/// breaking, contradicts the contract and throws
/// <see cref="ExternalToolParseException"/> — infrastructure, never a pass.
/// Empty or unrecognizable stdout on any exit is likewise infrastructure:
/// a silent tool is never a passing audit.</para>
/// </summary>
internal sealed class GraphqlInspectorReportParser(Func<string?> newSchemaPathProvider)
    : IExternalToolOutputParser
{
    /// <summary>Canonical tool level for <c>✖</c> lines (maps to <see cref="CodeyBox.Core.AuditSeverity.Error"/>).</summary>
    internal const string BreakingLevel = "breaking";

    /// <summary>Canonical tool level for <c>⚠</c> lines (maps to <see cref="CodeyBox.Core.AuditSeverity.Warning"/>).</summary>
    internal const string DangerousLevel = "dangerous";

    /// <summary>Canonical tool level for <c>✔</c> lines (maps to <see cref="CodeyBox.Core.AuditSeverity.Info"/>).</summary>
    internal const string NonBreakingLevel = "non-breaking";

    /// <summary>Fallback rule id when a breaking message matches no known template.</summary>
    internal const string BreakingFallbackRuleId = "BREAKING_CHANGE";

    /// <summary>Fallback rule id when a dangerous message matches no known template.</summary>
    internal const string DangerousFallbackRuleId = "DANGEROUS_CHANGE";

    /// <summary>Fallback rule id when a non-breaking message matches no known template.</summary>
    internal const string NonBreakingFallbackRuleId = "NON_BREAKING_CHANGE";

    // Same per-report result bound the shared SARIF parser applies.
    private const int MaxResults = SarifToolOutputParser.DefaultMaxResults;
    private const int MessageMaxChars = 512;

    private static readonly Regex AnsiEscape = new(
        @"\x1B\[[0-9;?]*[A-Za-z]",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Non-TTY logger form: "[log] <msg>". On a TTY the tag is absent and
    // chalk emits ANSI colours — both are normalized before classification.
    private static readonly Regex LogTag = new(
        @"^\[(log|success|error|info|warn|warning)\]\s?",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ChangesHeader = new(
        @"^Detected the following changes \(\d{1,9}\) between schemas:\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex BreakingSummary = new(
        @"^Detected \d{1,9} breaking changes?\.?\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex NoChanges = new(
        @"^No changes detected\.?\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex NoBreakingChanges = new(
        @"^No breaking changes detected\.?\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // log-symbols glyphs (plus the figures fallbacks) the tool emits for
    // each criticality on Linux sandboxes.
    private static readonly Regex BreakingLine = new(
        @"^[✖×✗]\s+(?<message>.+)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex DangerousLine = new(
        @"^[⚠‼]\s+(?<message>.+)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SafeLine = new(
        @"^[✔✓√]\s+(?<message>.+)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SchemePrefix = new(
        @"^[A-Za-z][A-Za-z0-9+.\-]*:",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly Func<string?> _newSchemaPathProvider =
        newSchemaPathProvider ?? throw new ArgumentNullException(nameof(newSchemaPathProvider));

    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var findings = new List<ExternalToolFinding>();
        var sawHeader = false;
        var sawBreakingSummary = false;
        var sawCleanMarker = false;

        foreach (var rawLine in SplitLines(input.Stdout))
        {
            var line = LogTag.Replace(AnsiEscape.Replace(rawLine, string.Empty), string.Empty).Trim();
            if (line.Length == 0)
                continue;

            if (ChangesHeader.IsMatch(line))
            {
                sawHeader = true;
                continue;
            }

            if (BreakingSummary.IsMatch(line))
            {
                sawBreakingSummary = true;
                continue;
            }

            if (NoChanges.IsMatch(line) || NoBreakingChanges.IsMatch(line))
            {
                sawCleanMarker = true;
                continue;
            }

            var change = ClassifyChangeLine(line);
            if (change is not { } parsed || findings.Count >= MaxResults)
                continue;

            var message = NormalizeMessage(parsed.Message);
            if (message.Length == 0)
                continue;

            findings.Add(new ExternalToolFinding(
                SeverityLevel: parsed.Level,
                RuleId: ChangeRuleId(message, parsed.Level),
                Message: message,
                Path: NormalizeNewSchemaPath(_newSchemaPathProvider()),
                Line: null));
        }

        if (findings.Count == 0)
        {
            if (input.ExitCode == 0 && sawCleanMarker && !sawHeader && !sawBreakingSummary)
                return findings;

            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' exited {input.ExitCode} without a recognizable "
                + "graphql-inspector diff report on stdout (no change lines and no clean "
                + "marker) — the check claims nothing the report shows.");
        }

        if (input.ExitCode != 0 && input.ExitCode != 1)
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' exited {input.ExitCode} with a diff report on "
                + "stdout, but only exits 0 and 1 are declared findings-producing for "
                + "graphql-inspector diff.");

        if (input.ExitCode == 1
            && findings.All(f => !string.Equals(f.SeverityLevel, BreakingLevel, StringComparison.Ordinal)))
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' exited 1 (breaking changes) but its stdout "
                + "carried no breaking change line — the exit claims findings the "
                + "report does not show.");

        return findings;
    }

    private static (string Level, string Message)? ClassifyChangeLine(string line)
    {
        var breaking = BreakingLine.Match(line);
        if (breaking.Success)
            return (BreakingLevel, breaking.Groups["message"].Value);
        var dangerous = DangerousLine.Match(line);
        if (dangerous.Success)
            return (DangerousLevel, dangerous.Groups["message"].Value);
        var safe = SafeLine.Match(line);
        if (safe.Success)
            return (NonBreakingLevel, safe.Groups["message"].Value);
        return null;
    }

    private static string NormalizeMessage(string message)
    {
        // The tool's bolderize step already strips the single/double quotes
        // its message builders emit around names; tolerate any survivors so
        // a future release that keeps them still matches the table below.
        var stripped = message.Replace("'", string.Empty).Replace("\"", string.Empty);
        return ExternalToolJsonHelpers.Truncate(
            ToolOutputText.SingleLine(stripped), MessageMaxChars);
    }

    /// <summary>
    /// Recovers the upstream <c>ChangeType</c> from a report message. Every
    /// template mirrors a message builder in
    /// <c>@graphql-inspector/core</c> 7.0.0 (<c>diff/changes/*.js</c>);
    /// anything unrecognized keeps the symbol-derived fallback id so the
    /// change is still selectable by rule and never silently dropped.
    /// </summary>
    internal static string ChangeRuleId(string message, string levelToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(levelToken);

        if (FieldRemoved.IsMatch(message))
            return "FIELD_REMOVED";
        if (FieldTypeChanged.IsMatch(message))
            return "FIELD_TYPE_CHANGED";
        if (FieldArgumentRemoved.IsMatch(message))
            return "FIELD_ARGUMENT_REMOVED";
        if (FieldArgumentAdded.IsMatch(message))
            return "FIELD_ARGUMENT_ADDED";
        if (TypeRemoved.IsMatch(message))
            return "TYPE_REMOVED";
        if (TypeAdded.IsMatch(message))
            return "TYPE_ADDED";
        if (EnumValueRemoved.IsMatch(message))
            return "ENUM_VALUE_REMOVED";
        if (EnumValueAdded.IsMatch(message))
            return "ENUM_VALUE_ADDED";
        if (UnionMemberRemoved.IsMatch(message))
            return "UNION_MEMBER_REMOVED";
        if (UnionMemberAdded.IsMatch(message))
            return "UNION_MEMBER_ADDED";
        if (InputFieldRemoved.IsMatch(message))
            return "INPUT_FIELD_REMOVED";
        if (InputFieldAdded.IsMatch(message))
            return "INPUT_FIELD_ADDED";
        if (ObjectInterfaceRemoved.IsMatch(message))
            return "OBJECT_TYPE_INTERFACE_REMOVED";
        if (ObjectInterfaceAdded.IsMatch(message))
            return "OBJECT_TYPE_INTERFACE_ADDED";
        var schemaChange = SchemaRootChanged.Match(message);
        if (schemaChange.Success)
            return schemaChange.Groups["root"].Value.ToUpperInvariant() switch
            {
                "QUERY" => "SCHEMA_QUERY_TYPE_CHANGED",
                "MUTATION" => "SCHEMA_MUTATION_TYPE_CHANGED",
                _ => "SCHEMA_SUBSCRIPTION_TYPE_CHANGED",
            };
        if (DirectiveRemoved.IsMatch(message))
            return "DIRECTIVE_REMOVED";
        if (DirectiveAdded.IsMatch(message))
            return "DIRECTIVE_ADDED";
        if (DirectiveArgumentRemoved.IsMatch(message))
            return "DIRECTIVE_ARGUMENT_REMOVED";
        if (DirectiveArgumentAdded.IsMatch(message))
            return "DIRECTIVE_ARGUMENT_ADDED";
        if (FieldDeprecationReasonRemoved.IsMatch(message))
            return "FIELD_DEPRECATION_REASON_REMOVED";
        if (FieldDeprecationRemoved.IsMatch(message))
            return "FIELD_DEPRECATION_REMOVED";
        if (FieldAdded.IsMatch(message))
            return "FIELD_ADDED";

        return levelToken switch
        {
            BreakingLevel => BreakingFallbackRuleId,
            DangerousLevel => DangerousFallbackRuleId,
            _ => NonBreakingFallbackRuleId,
        };
    }

    private static readonly Regex FieldRemoved = new(
        @"^Field \S+?(\s+\(deprecated\))? was removed from .+ \S+$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex FieldTypeChanged = new(
        @"^Field \S+ changed type from \S+ to \S+$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex FieldArgumentRemoved = new(
        @"^Argument \S+: \S+ was removed from field \S+$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex FieldArgumentAdded = new(
        @"^Argument \S+: \S+(\s+\(with default value\))? added to field \S+$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex TypeRemoved = new(
        @"^Type \S+ was removed$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex TypeAdded = new(
        @"^Type \S+ was added$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex EnumValueRemoved = new(
        @"^Enum value \S+ was removed from enum \S+$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex EnumValueAdded = new(
        @"^Enum value \S+ was added to enum \S+$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex UnionMemberRemoved = new(
        @"^Member \S+ was removed from Union type \S+$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex UnionMemberAdded = new(
        @"^Member \S+ was added to Union type \S+$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex InputFieldRemoved = new(
        @"^Input field \S+ was removed from input object type \S+$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex InputFieldAdded = new(
        @"^Input field \S+ of type \S+(\s+with default value \S+)? was added to input object type \S+$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ObjectInterfaceRemoved = new(
        @"^\S+ object type no longer implements \S+ interface$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ObjectInterfaceAdded = new(
        @"^\S+ object type now implements \S+ interface$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SchemaRootChanged = new(
        @"^Schema (?<root>query|mutation|subscription) root has changed from \S+ to \S+$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex DirectiveRemoved = new(
        @"^Directive \S+ was removed$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex DirectiveAdded = new(
        @"^Directive \S+ was added$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex DirectiveArgumentRemoved = new(
        @"^Argument \S+ was removed from directive \S+$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex DirectiveArgumentAdded = new(
        @"^Argument \S+ was added to directive \S+$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex FieldDeprecationReasonRemoved = new(
        @"^Deprecation reason was removed from field \S+$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex FieldDeprecationRemoved = new(
        @"^Field \S+ is no longer deprecated$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex FieldAdded = new(
        @"^Field \S+ was added to .+ \S+$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Echoes the configured new-schema pointer as the finding path when it
    /// names a repository-relative file; URL, <c>git:</c>/<c>github:</c>, and
    /// absolute pointers carry no repository location, so the path stays
    /// unset rather than guessed.
    /// </summary>
    internal static string? NormalizeNewSchemaPath(string? pointer)
    {
        if (string.IsNullOrWhiteSpace(pointer))
            return null;
        var candidate = pointer.Trim();
        if (candidate.Length == 0 || candidate.Length > MessageMaxChars)
            return null;
        if (candidate.StartsWith("/", StringComparison.Ordinal)
            || candidate.StartsWith("\\", StringComparison.Ordinal))
            return null;
        if (candidate.Length > 2 && char.IsLetter(candidate[0]) && candidate[1] == ':')
            return null;
        if (candidate.Contains("://", StringComparison.Ordinal) || SchemePrefix.IsMatch(candidate))
            return null;
        candidate = candidate.Replace('\\', '/');
        if (candidate.StartsWith("./", StringComparison.Ordinal))
            candidate = candidate[2..];
        return string.IsNullOrWhiteSpace(candidate) ? null : candidate;
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
            yield return line;
    }
}
