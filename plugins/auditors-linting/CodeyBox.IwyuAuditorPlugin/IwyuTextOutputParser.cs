using System.Text.RegularExpressions;
using CodeyBox.PluginSdk.Tools;

namespace CodeyBox.IwyuAuditorPlugin;

/// <summary>
/// Parses the aggregated text report that <c>iwyu_tool -p … .</c> writes to
/// stdout into <see cref="ExternalToolFinding"/> records. iwyu_tool merges
/// each per-translation-unit <c>include-what-you-use</c> run's stdout and
/// stderr into a single stream, so the input is a concatenation of
/// per-file verdict blocks (and, for a TU that emitted compiler diagnostics,
/// clang text diagnostics).
///
/// <para>The per-file grammar (verified against IWYU 0.21's
/// <c>iwyu_output.cc</c> — <c>CalculateAndReportIwyuViolations</c>):</para>
/// <list type="bullet">
/// <item><c>(PATH) has correct #includes/fwd-decls)</c> — a file needing no
/// edits. Records a verdict, produces no finding.</item>
/// <item><c>PATH should add these lines:</c> followed by the desired
/// include/fwd-declare lines (<c>#include &lt;vector&gt;  // for std::vector</c>,
/// <c>class Foo;</c>) — one <c>iwyu-add</c> finding per non-empty line.</item>
/// <item><c>PATH should remove these lines:</c> followed by
/// <c>- #include &lt;stdio.h&gt;  // lines N-M</c> entries — one
/// <c>iwyu-remove</c> finding per line, carrying the first line number N.</item>
/// <item><c>The full include-list for PATH:</c> — the desired end state;
/// informational only, closes with <c>---</c>. Records a verdict.</item>
/// </list>
///
/// <para>Exit <c>0</c> with zero verdict records is NOT a clean result: it
/// means iwyu_tool ran no translation unit (an empty database, a selection
/// that matched nothing, or output corruption) — the parser throws
/// <see cref="ExternalToolParseException"/>, which the base reports as
/// infrastructure rather than letting a vacuous run read as a pass.
/// Compiler diagnostics, iwyu's own notes, and any other unrecognised line
/// in the general state are not include findings and are ignored — they stay
/// visible in the raw output.</para>
///
/// <para>Paths are normalized through the shared
/// <see cref="ExternalToolJsonHelpers.NormalizeReportedPath"/> policy:
/// absolute paths are relativized against the probed scan root, anything
/// that stays absolute or still carries a <c>..</c> escape is re-marked with
/// a <c>file://</c> prefix so it can never read as a repository-relative
/// location.</para>
/// </summary>
internal sealed class IwyuTextOutputParser : IExternalToolOutputParser
{
    /// <summary>Rule id attached to "should add" findings (missing direct include/fwd-declare).</summary>
    internal const string AddRuleId = "iwyu-add";

    /// <summary>Rule id attached to "should remove" findings (unneeded include/fwd-declare).</summary>
    internal const string RemoveRuleId = "iwyu-remove";

    /// <summary>Rule id attached to the single per-run coverage finding.</summary>
    internal const string CoverageRuleId = "iwyu-coverage";

    /// <summary>Severity reported for include findings — advisory by design.</summary>
    internal const string ViolationLevel = "warning";

    /// <summary>Severity reported for the coverage record — informational.</summary>
    internal const string CoverageLevel = "note";

    private const int MaxResults = ExternalToolReportLimits.DefaultMaxResults;
    private const int MaxReportedPathChars = 1024;
    private const int MaxReportedLineChars = 2048;

    // Unit separator for the duplicate-suppression key (the same header
    // diagnostic is re-emitted once per translation unit that includes it).
    private const string IdentityFieldSeparator = "\u001f";

    private static readonly Regex ShouldAddPattern = BlockPattern(@" should add these lines:$");
    private static readonly Regex ShouldRemovePattern = BlockPattern(@" should remove these lines:$");
    private static readonly Regex FullListPattern = new(
        @"^The full include-list for (?<path>.*?):$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex CorrectPattern = new(
        @"^\((?<path>.*?) has correct #includes/fwd-decls\)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Removal entries always carry "// lines N-M" (a range, even when the
    // bounds coincide). The first number is the finding's line.
    private static readonly Regex RemovalPattern = new(
        @"^- (?<line>.*?)\s+// lines (?<first>\d+)-\d+\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private enum Section { General, Add, Remove, List }

    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var stdout = input.Stdout ?? string.Empty;
        if (string.IsNullOrWhiteSpace(stdout))
        {
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced no output on stdout (exit {input.ExitCode}) — "
                + "a completed run reports at least one file verdict, so this is not a clean result.");
        }

        var findings = new List<ExternalToolFinding>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var verdictFiles = 0;
        var state = Section.General;
        string? sectionPath = null;

        foreach (var rawLine in stdout.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            var addHeader = ShouldAddPattern.Match(line);
            if (addHeader.Success)
            {
                state = Section.Add;
                sectionPath = addHeader.Groups["path"].Value;
                continue;
            }

            var removeHeader = ShouldRemovePattern.Match(line);
            if (removeHeader.Success)
            {
                state = Section.Remove;
                sectionPath = removeHeader.Groups["path"].Value;
                continue;
            }

            var listHeader = FullListPattern.Match(line);
            if (listHeader.Success)
            {
                state = Section.List;
                sectionPath = listHeader.Groups["path"].Value;
                verdictFiles++;
                continue;
            }

            if (line.TrimEnd() == "---")
            {
                state = Section.General;
                sectionPath = null;
                continue;
            }

            var correct = CorrectPattern.Match(line);
            if (correct.Success)
            {
                state = Section.General;
                sectionPath = null;
                verdictFiles++;
                continue;
            }

            if (line.Trim().Length == 0 || state is Section.General or Section.List)
                continue;

            var path = NormalizeReportedPath(sectionPath, input);
            if (path is null)
                continue;

            if (state == Section.Add)
            {
                var suggestion = TrimForReport(line.Trim());
                if (suggestion.Length == 0)
                    continue;
                AddFinding(findings, seen,
                    new ExternalToolFinding(
                        SeverityLevel: ViolationLevel,
                        RuleId: AddRuleId,
                        Message: $"add missing include or forward declaration: '{suggestion}'",
                        Path: path));
            }
            else if (state == Section.Remove)
            {
                var removal = RemovalPattern.Match(line);
                var suggestion = removal.Success
                    ? removal.Groups["line"].Value.Trim()
                    : line.Trim().TrimStart('-').Trim();
                if (suggestion.Length == 0)
                    continue;
                suggestion = TrimForReport(suggestion);
                int? lineNumber = removal.Success
                    && int.TryParse(removal.Groups["first"].Value, out var firstLine)
                    && firstLine > 0
                        ? firstLine
                        : null;
                AddFinding(findings, seen,
                    new ExternalToolFinding(
                        SeverityLevel: ViolationLevel,
                        RuleId: RemoveRuleId,
                        Message: $"remove unneeded include or forward declaration: '{suggestion}'",
                        Path: path,
                        Line: lineNumber));
            }
        }

        if (verdictFiles == 0)
        {
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced output (exit {input.ExitCode}) containing no "
                + "include-what-you-use verdict records — the stream held no 'should add these "
                + "lines', 'should remove these lines', 'full include-list', or 'has correct "
                + "#includes/fwd-decls' block, so no translation unit's verdict can be trusted.");
        }

        // Coverage record: exactly how many file verdicts the run produced.
        // Emitted at note level so it never gates and can be filtered via
        // MinimumSeverity or ExcludedRules.
        findings.Add(new ExternalToolFinding(
            SeverityLevel: CoverageLevel,
            RuleId: CoverageRuleId,
            Message: $"include-what-you-use produced include verdicts for {verdictFiles} file(s)."));

        return findings;
    }

    private static void AddFinding(
        List<ExternalToolFinding> findings,
        HashSet<string> seen,
        ExternalToolFinding finding)
    {
        if (findings.Count >= MaxResults)
            return;
        if (seen.Add(string.Concat(
                finding.RuleId ?? string.Empty, IdentityFieldSeparator,
                finding.Path ?? string.Empty, IdentityFieldSeparator,
                finding.Line?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                IdentityFieldSeparator,
                finding.Message)))
            findings.Add(finding);
    }

    private static string? NormalizeReportedPath(string? rawPath, ExternalToolParseInput input)
    {
        if (string.IsNullOrWhiteSpace(rawPath) || rawPath.Length > MaxReportedPathChars)
            return null;
        return ExternalToolJsonHelpers.NormalizeReportedPath(
            rawPath.Trim(), input.ScanRoot, input.WorkingDirectory);
    }

    private static string TrimForReport(string value)
        => value.Length <= MaxReportedLineChars ? value : value[..MaxReportedLineChars];

    private static Regex BlockPattern(string suffix)
        => new(
            @"^(?<path>.*?)" + suffix,
            RegexOptions.CultureInvariant | RegexOptions.Compiled);
}
