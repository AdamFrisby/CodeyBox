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
/// <item><c>(PATH has correct #includes/fwd-decls)</c> — a file needing no
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
/// infrastructure rather than letting a vacuous run read as a pass. When
/// the input carries <see cref="ExternalToolParseInput.ExpectedVerdictFiles"/>,
/// every selected unit's file must carry a verdict record: iwyu_tool's
/// <c>max()</c> exit aggregation cannot raise a signal-killed child's
/// negative returncode above 0, so the record is the only evidence a
/// missing unit leaves — and reconciliation is by file identity, not
/// record count, because a surviving unit's associated-header verdicts
/// emit extra records that would mask the missing one in a count.
/// Compiler diagnostics, iwyu's own notes, and any other unrecognised line
/// in the general state are not include findings and are ignored — they stay
/// visible in the raw output.</para>
///
/// <para>Paths are normalized through the shared
/// <see cref="ExternalToolJsonHelpers.NormalizeReportedPath"/> policy:
/// absolute paths are relativized against the probed scan root, anything
/// that stays absolute or still carries a <c>..</c> escape is re-marked with
/// a <c>file://</c> prefix so it can never read as a repository-relative
/// location. Relative spellings are first resolved against
/// <see cref="ExternalToolParseInput.RelativePathAnchors"/> — iwyu_tool runs
/// each unit with <c>cwd = entry.directory</c> and IWYU prints the source
/// path as the compile command spelled it, so <c>../src/a.cc</c> from a
/// build-directory entry resolves to the file it actually names rather than
/// reading as an out-of-tree escape (and a bare <c>src/a.cc</c> spelled from
/// a different directory is not mistaken for the repository-relative one).
/// Unanchored spellings — e.g. associated headers IWYU resolved through
/// include search paths — keep the shared policy.</para>
/// </summary>
internal sealed class IwyuTextOutputParser : IExternalToolOutputParser
{
    /// <summary>Rule id attached to "should add" findings (missing direct include/fwd-declare).</summary>
    internal const string AddRuleId = "iwyu-add";

    /// <summary>Rule id attached to "should remove" findings (unneeded include/fwd-declare).</summary>
    internal const string RemoveRuleId = "iwyu-remove";

    /// <summary>Rule id attached to the single per-run coverage finding.</summary>
    internal const string CoverageRuleId = "iwyu-coverage";

    /// <summary>
    /// Rule id attached to the parser-cap record emitted when the report
    /// carried more suggestions than <see cref="MaxResults"/> — the drop is
    /// otherwise invisible to the base's finding-level truncation marker.
    /// </summary>
    internal const string TruncatedRuleId = "iwyu-truncated";

    /// <summary>Severity reported for include findings — advisory by design.</summary>
    internal const string ViolationLevel = "warning";

    /// <summary>Severity reported for the coverage record — informational.</summary>
    internal const string CoverageLevel = "note";

    private const int MaxResults = ExternalToolReportLimits.DefaultMaxResults;
    private const int MaxReportedPathChars = 1024;
    private const int MaxReportedLineChars = 2048;

    // Per-path cap when naming uncovered units in the reconciliation
    // failure, and the number of missing paths listed — the message is
    // bounded even though the paths are auditor-computed, not raw output.
    private const int MaxMissingPathChars = 160;
    private const int MaxMissingPathsListed = 3;

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
        var dropped = 0;
        var verdictRecords = 0;
        var coveredFiles = new HashSet<string>(StringComparer.Ordinal);
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
                verdictRecords++;
                RecordCoveredFile(coveredFiles, sectionPath, input);
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
                verdictRecords++;
                RecordCoveredFile(coveredFiles, correct.Groups["path"].Value, input);
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
                        Path: path),
                    ref dropped);
            }
            else if (state == Section.Remove)
            {
                // Removal entries carry "// lines N-M"; the cheap substring
                // gate plus the length bound keep the regex's lazy match off
                // pathological whitespace-dense lines.
                var removal = line.Length <= MaxReportedLineChars
                    && line.Contains("// lines ", StringComparison.Ordinal)
                        ? RemovalPattern.Match(line)
                        : Match.Empty;
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
                        Line: lineNumber),
                    ref dropped);
            }
        }

        if (verdictRecords == 0)
        {
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced output (exit {input.ExitCode}) containing no "
                + "include-what-you-use verdict records — the stream held no 'should add these "
                + "lines', 'should remove these lines', 'full include-list', or 'has correct "
                + "#includes/fwd-decls' block, so no translation unit's verdict can be trusted.");
        }

        if (input.ExpectedVerdictFiles is { } expected)
        {
            var missing = expected.Where(file => !coveredFiles.Contains(file)).ToList();
            if (missing.Count > 0)
                throw new ExternalToolParseException(
                    $"Tool '{input.ToolName}' produced no verdict record for {missing.Count} of the "
                    + $"{expected.Count} translation units selected from the compilation database "
                    + $"(no verdict covers {FormatMissing(missing)}) — fewer verdicts than selected "
                    + "units means a unit exited without producing one (iwyu_tool's exit aggregation "
                    + "folds a signal-killed child into exit 0), and extra records for associated "
                    + "headers cannot stand in for it, so coverage is unverifiable: this is "
                    + "infrastructure, not a pass.");
        }

        // Coverage record: how many distinct files the run produced a
        // verdict for. Emitted at note level so it never gates and can be
        // filtered via MinimumSeverity or ExcludedRules.
        findings.Add(new ExternalToolFinding(
            SeverityLevel: CoverageLevel,
            RuleId: CoverageRuleId,
            Message: $"include-what-you-use produced include verdicts for {coveredFiles.Count} file(s)."));

        // The parser-side cap drops suggestions the base's truncation marker
        // cannot see — surface the drop as its own note record so a capped
        // report is never mistaken for the complete one.
        if (dropped > 0)
            findings.Add(new ExternalToolFinding(
                SeverityLevel: CoverageLevel,
                RuleId: TruncatedRuleId,
                Message: $"the report carried {dropped} further suggestion(s) beyond the "
                    + $"{MaxResults}-result parser bound — they were dropped."));

        return findings;
    }

    // A verdict record's path joins the covered set under the same
    // normalization findings use — anchors first, then the shared
    // relativize-or-file:// policy — so reconciliation compares each
    // expected unit against the location a finding would carry.
    private static void RecordCoveredFile(
        HashSet<string> coveredFiles,
        string? rawPath,
        ExternalToolParseInput input)
    {
        if (NormalizeReportedPath(rawPath, input) is { } path)
            coveredFiles.Add(path);
    }

    private static string FormatMissing(IReadOnlyList<string> missing)
        => string.Join(", ", missing.Take(MaxMissingPathsListed).Select(
                static m => $"'{ExternalToolJsonHelpers.Truncate(m, MaxMissingPathChars)}'"))
            + (missing.Count > MaxMissingPathsListed ? ", …" : string.Empty);

    private static void AddFinding(
        List<ExternalToolFinding> findings,
        HashSet<string> seen,
        ExternalToolFinding finding,
        ref int dropped)
    {
        if (findings.Count >= MaxResults)
        {
            dropped++;
            return;
        }
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

        // A relative spelling is anchored at the directory iwyu_tool ran the
        // unit in (entry.directory), not the scan root — resolve it through
        // the per-entry anchors the auditor computed from the database
        // before the shared policy can read it as worktree-relative. The
        // file:// strip mirrors the shared policy so a prefixed spelling
        // resolves identically.
        var normalized = ExternalToolJsonHelpers.NormalizePath(rawPath);
        if (normalized.StartsWith(
                ExternalToolJsonHelpers.FileSchemePrefix, StringComparison.OrdinalIgnoreCase))
            normalized = normalized[ExternalToolJsonHelpers.FileSchemePrefix.Length..];
        var collapsed = ExternalToolJsonHelpers.CollapseDotSegments(normalized);
        if (collapsed.Length > 0 && !collapsed.StartsWith("/", StringComparison.Ordinal)
            && input.RelativePathAnchors is { } anchors
            && anchors.TryGetValue(collapsed, out var anchored))
            return anchored;

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
