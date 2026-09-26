using System.Text.RegularExpressions;
using CodeyBox.PluginSdk.Tools;

namespace CodeyBox.CargoSemverChecksAuditorPlugin;

/// <summary>
/// Parses the cargo-semver-checks <c>check-release</c> report on stdout into
/// <see cref="ExternalToolFinding"/> records. The tool has no JSON or SARIF
/// output mode (verified against v0.50.0): its verdict is a structured human
/// report — one section per triggered lint,
/// <c>--- failure &lt;lint-id&gt;: &lt;name&gt; ---</c> or
/// <c>--- warning &lt;lint-id&gt;: &lt;name&gt; ---</c>, whose
/// <c>Description:</c>/<c>ref:</c>/<c>impl:</c> block is followed by a
/// <c>Failed in:</c> block listing one templated line per offending API
/// item, e.g. <c>function my_crate::gone, previously in file
/// src/lib.rs:12</c>.
///
/// <para>Each <c>Failed in:</c> line becomes one finding carrying the lint id
/// as the rule id and the section level (<c>failure</c>/<c>warning</c>) as
/// the tool severity token — mapped by the auditor's declared
/// <see cref="ExternalToolSeverityMapping"/>, never passed through. A
/// trailing <c>path:line</c> (the dominant template convention) supplies the
/// location; a trailing manifest/source path without a line
/// (<c>… in the package's Cargo.toml</c>) supplies path only; anything else
/// leaves the location unset rather than guessing. A triggered section with
/// no parseable <c>Failed in:</c> entries still yields one finding — a lint
/// that fired must never be silently dropped because its result lines took
/// an unfamiliar shape.</para>
///
/// <para>The exit code and the report cross-check each other: exit
/// <c>100</c> asserts deny-level findings exist, so a 100 exit whose stdout
/// carries no <c>failure</c> section contradicts the contract (suppressed
/// verbosity, foreign build, truncated report) and throws
/// <see cref="ExternalToolParseException"/> — infrastructure, never a pass.
/// Exit <c>0</c> with only <c>warning</c> sections yields advisory findings
/// and still passes.</para>
/// </summary>
internal sealed class CargoSemverChecksReportParser : IExternalToolOutputParser
{
    /// <summary>Section level for deny-level lint findings (maps to <see cref="CodeyBox.Core.AuditSeverity.Error"/>).</summary>
    internal const string FailureLevel = "failure";

    /// <summary>Section level for warn-level lint findings (maps to <see cref="CodeyBox.Core.AuditSeverity.Warning"/>).</summary>
    internal const string WarningLevel = "warning";

    // Same per-report result bound the shared SARIF parser applies.
    private const int MaxResults = SarifToolOutputParser.DefaultMaxResults;

    // "--- failure function_missing: pub fn removed or renamed ---"
    private static readonly Regex SectionHeader = new(
        @"^--- (failure|warning) ([^\s:]+): (.+?) ---\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Result lines end with "in file <path>:<line>" / "in <path>:<line>" /
    // "at <path>:<line>"; the last path:line-shaped token wins. The path must
    // contain a letter so a version-like token ("1.2.3:4") is not misread.
    private static readonly Regex PathWithLine = new(
        @"([\p{L}\p{N}_.\-/\\]+\.[A-Za-z0-9]+):(\d{1,9})(?![\d:])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // "feature X in the package's Cargo.toml" — a file mention with no line.
    private static readonly Regex PathOnly = new(
        @"([\p{L}\p{N}_.\-/\\]+\.(?:rs|toml))(?![\w:/\\])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var findings = new List<ExternalToolFinding>();
        var failureSections = 0;
        ParseState? state = null;

        foreach (var rawLine in SplitLines(input.Stdout))
        {
            var line = rawLine.TrimEnd('\r');
            var header = SectionHeader.Match(line);
            if (header.Success)
            {
                FlushSection(findings, state);
                state = new ParseState(
                    header.Groups[1].Value,
                    header.Groups[2].Value,
                    header.Groups[3].Value.Trim());
                if (state.Level == FailureLevel)
                    failureSections++;
                continue;
            }

            if (state is null)
                continue;
            state.Consume(line);
        }

        FlushSection(findings, state);

        if (input.ExitCode == CargoSemverChecksAuditor.LintFindingsExitCode && failureSections == 0)
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' exited {CargoSemverChecksAuditor.LintFindingsExitCode} "
                + "(deny-level findings) but its stdout carried no '--- failure' report section — "
                + "the check claims findings the report does not show.");

        return findings;
    }

    private static void FlushSection(List<ExternalToolFinding> findings, ParseState? state)
    {
        if (state is null)
            return;
        foreach (var message in state.ResultMessages)
        {
            if (findings.Count >= MaxResults)
                return;
            var (path, lineNumber) = ExtractLocation(message);
            findings.Add(new ExternalToolFinding(
                SeverityLevel: state.Level,
                RuleId: state.RuleId,
                Message: string.IsNullOrWhiteSpace(state.Description)
                    ? message
                    : message + "\n\n" + state.Description,
                Path: path,
                Line: lineNumber));
        }

        // A lint that fired but produced no parseable result lines still
        // reports once — unfamiliar output must surface, not vanish.
        if (state.ResultMessages.Count == 0 && findings.Count < MaxResults)
            findings.Add(new ExternalToolFinding(
                SeverityLevel: state.Level,
                RuleId: state.RuleId,
                Message: string.IsNullOrWhiteSpace(state.Description)
                    ? state.Name
                    : state.Name + "\n\n" + state.Description));
    }

    private static (string? Path, int? Line) ExtractLocation(string message)
    {
        var trimmed = message.Trim();
        var withLine = PathWithLine.Matches(trimmed);
        if (withLine.Count > 0)
        {
            var last = withLine[^1];
            if (last.Groups[1].Value.Any(char.IsLetter))
                return (NormalizePath(last.Groups[1].Value),
                    int.Parse(last.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));
        }

        var withoutLine = PathOnly.Matches(trimmed);
        if (withoutLine.Count > 0 && withoutLine[^1].Groups[1].Value.Any(char.IsLetter))
            return (NormalizePath(withoutLine[^1].Groups[1].Value), null);

        return (null, null);
    }

    private static string NormalizePath(string path)
        => path.Replace('\\', '/').Trim().TrimStart('/');

    private static IEnumerable<string> SplitLines(string text)
    {
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
            yield return line;
    }

    /// <summary>
    /// Accumulates one report section: the description body and the
    /// per-result lines under <c>Failed in:</c>. Everything before the marker
    /// (minus the <c>Description:</c> label and <c>ref:</c>/<c>impl:</c>
    /// metadata lines) is the lint's static explanation; everything after is
    /// one result message per line.
    /// </summary>
    private sealed class ParseState(string level, string ruleId, string name)
    {
        private readonly List<string> _descriptionLines = [];
        private bool _inResults;

        public string Level { get; } = level;
        public string RuleId { get; } = ruleId;
        public string Name { get; } = name;
        public List<string> ResultMessages { get; } = [];
        public string Description => string.Join('\n', _descriptionLines).Trim();

        public void Consume(string line)
        {
            if (_inResults)
            {
                var result = line.Trim();
                if (result.Length > 0 && ResultMessages.Count < MaxResults)
                    ResultMessages.Add(result);
                return;
            }

            var trimmed = line.Trim();
            if (trimmed.Equals("Failed in:", StringComparison.Ordinal))
            {
                _inResults = true;
                return;
            }
            if (trimmed.Length == 0 || trimmed.Equals("Description:", StringComparison.Ordinal))
                return;
            if (trimmed.StartsWith("ref:", StringComparison.Ordinal)
                || trimmed.StartsWith("impl:", StringComparison.Ordinal))
                return;
            _descriptionLines.Add(trimmed);
        }
    }
}
