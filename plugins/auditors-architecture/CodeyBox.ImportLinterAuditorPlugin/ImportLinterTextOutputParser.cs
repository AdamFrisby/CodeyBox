using System.Text;
using System.Text.RegularExpressions;
using CodeyBox.PluginSdk.Tools;

namespace CodeyBox.ImportLinterAuditorPlugin;

/// <summary>
/// Parses the plain-text report <c>lint-imports</c> writes to stdout into
/// <see cref="ExternalToolFinding"/> records. Verified against import-linter
/// 2.15, whose rendered report looks like:
///
/// <code>
/// ---------
/// Contracts
/// ---------
///
/// Analyzed 3 files, 2 dependencies.
/// ---------------------------------
///
/// Layering KEPT
/// No low in high BROKEN
///
/// Contracts: 1 kept, 1 broken.
///
/// ----------------
/// Broken contracts
/// ----------------
///
/// No low in high
/// --------------
///
/// mypkg.high is not allowed to import mypkg.low:
///
/// -   mypkg.high -> mypkg.low (l.1)
/// </code>
///
/// <para><b>The exit code alone cannot classify the run.</b> lint-imports has
/// only two exits: <c>0</c> (all contracts kept) and <c>1</c> — which covers
/// broken contracts <em>and</em> every "could not run" outcome (missing or
/// unreadable config, invalid contract options rendered as a could-not-run
/// report, unknown <c>--contract</c> id, crashes). The reliable verdict marker
/// is the summary line <c>Contracts: N kept, M broken.</c>, printed by
/// <c>render_report</c> only after checks complete; every failure path skips
/// it. A finished run without that line — or a contradictory one (non-zero
/// exit with <c>0 broken</c>) — throws <see cref="ExternalToolParseException"/>,
/// which the base reports as infrastructure, never as a pass.</para>
///
/// <para>Findings come from the <c>Broken contracts</c> section: one per
/// rendered import link (<c>importer -&gt; imported (l.N)</c>), one per
/// undeclared-layer module bullet, plus a per-contract fallback so a broken
/// contract whose details render in an unrecognised shape still produces a
/// finding rather than vanishing. The tool reports Python <em>module</em>
/// names — never file paths — so finding paths carry the module in
/// slash-separated form (<c>mypkg.low</c> → <c>mypkg/low</c>, which may be
/// <c>mypkg/low.py</c> or <c>mypkg/low/__init__.py</c>); the first
/// <c>l.N</c> in the detail is the line. Entries under the
/// <c>Warnings</c> section become advisory <c>warning</c>-level findings.
/// Raw levels stay in the tool's vocabulary —
/// <see cref="ExternalToolAuditorBase"/> maps them through the auditor's
/// declared <see cref="ExternalToolSeverityMapping"/>.</para>
/// </summary>
internal sealed class ImportLinterTextOutputParser : IExternalToolOutputParser
{
    // Same per-report result bound the shared SARIF parser applies.
    private const int MaxResults = SarifToolOutputParser.DefaultMaxResults;
    private const int FailureTailMaxChars = 512;
    private const int FallbackDetailMaxChars = 300;

    // "Contracts: 1 kept, 1 broken." — the only line lint-imports prints that
    // proves the checks ran to completion and produced a verdict.
    private static readonly Regex SummaryLine = new(
        @"^Contracts:\s+(?<kept>\d+)\s+kept,\s+(?<broken>\d+)\s+broken\.$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Rendered import links inside a "Broken contracts" block. Real shapes
    // (2.15): "-   mypkg.a -> mypkg.b (l.4)" (forbidden), "- mypkg.a ->
    // mypkg.b (l.4)" and indented continuations "  a -> b (l.4)"
    // (layers/independence/protected chains, "&" prefixes for multi-hop
    // heads/tails), and acyclic_siblings' "- .a -> .b (2 imports)".
    private static readonly Regex ImportLinkLine = new(
        @"^[\s\-&]*(?<importer>\S+?)\s*->\s*(?<imported>\S+?)\s*\((?<detail>[^()]*)\)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Arrow-less rendered links with line numbers — chain endpoints rendered
    // as "- mypkg.a (l.3)" / "  & mypkg.b (l.4)" by multi-hop chain output.
    private static readonly Regex ModuleWithLinesLine = new(
        @"^[\s\-&]+(?<module>[A-Za-z0-9_.]+)\s*\((?<detail>l\.[^()]*)\)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Bare module bullets — exhaustive layers' "not listed as layers" list.
    private static readonly Regex ModuleBulletLine = new(
        @"^\s*-\s+(?<module>[A-Za-z0-9_]+(?:\.[A-Za-z0-9_]+)*)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // acyclic_siblings renders relative module names (".foo -> .bar"); the
    // owning package comes from its "No cycles are allowed in <pkg>." line.
    private static readonly Regex CycleContextLine = new(
        @"^No cycles are allowed in (?<package>[A-Za-z0-9_]+(?:\.[A-Za-z0-9_]+)*)\.$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex LineNumber = new(
        @"l\.(?<line>\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Stdout))
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced no report on stdout — the check did not run.");

        var lines = input.Stdout.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        var brokenCount = FindSummary(lines, input.ToolName);
        if (input.ExitCode != 0 && brokenCount == 0)
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' exited {input.ExitCode} but its report shows no broken "
                + "contracts — a contradictory state, so the run cannot be read as a verdict.");

        var findings = new List<ExternalToolFinding>();
        var brokenStart = FindSectionStart(lines, "Broken contracts");
        var warningsStart = FindSectionStart(lines, "Warnings");

        if (warningsStart >= 0)
            ParseWarningsSection(lines, warningsStart, findings);
        if (brokenStart >= 0)
            ParseBrokenContractsSection(lines, brokenStart, findings);

        // A report that claims broken contracts but yields no broken-contract
        // finding — missing section or an unrecognised shape — would pass the
        // audit while the tool reported failure. Fail closed instead.
        if (brokenCount > 0 && !findings.Any(f => f.SeverityLevel == "error"))
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' reports {brokenCount} broken contract(s) but the "
                + "'Broken contracts' details could not be recognised — the report shape is "
                + "unrecognised, so the run cannot be read as a verdict.");

        return findings;
    }

    /// <summary>
    /// Locates the "Contracts: N kept, M broken." summary and returns the
    /// broken count. Throws when the line is absent — every failure path
    /// (missing config, invalid contract options, unknown --contract id,
    /// crash) exits 1 without printing it, so "no summary" is "could not
    /// run", never a clean report.
    /// </summary>
    private static int FindSummary(string[] lines, string toolName)
    {
        foreach (var raw in lines)
        {
            var match = SummaryLine.Match(raw.TrimEnd());
            if (match.Success)
                return int.Parse(match.Groups["broken"].Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        throw new ExternalToolParseException(
            $"Tool '{toolName}' produced no 'Contracts: N kept, N broken.' summary — the check did "
            + "not run (missing or unreadable import-linter configuration, invalid contract options, "
            + $"or a crash). Output tail: {Tail(lines)}");
    }

    /// <summary>
    /// Finds a level-two section heading — a line sandwiched between two
    /// all-dash lines — and returns the index of the line after it, or -1.
    /// </summary>
    private static int FindSectionStart(string[] lines, string name)
    {
        for (var i = 1; i + 1 < lines.Length; i++)
        {
            if (string.Equals(lines[i].Trim(), name, StringComparison.Ordinal)
                && IsUnderline(lines[i - 1])
                && IsUnderline(lines[i + 1]))
                return i + 1;
        }

        return -1;
    }

    /// <summary>
    /// True when <paramref name="index"/> is a level-two heading (dash line
    /// above and below) — the boundary that ends a section.
    /// </summary>
    private static bool IsSectionBoundary(string[] lines, int index)
        => index > 0
            && index + 1 < lines.Length
            && lines[index].Trim().Length > 0
            && IsUnderline(lines[index - 1])
            && IsUnderline(lines[index + 1]);

    /// <summary>
    /// True when <paramref name="index"/> is a level-three heading — a text
    /// line underlined by a dash line, with no dash line above (that would
    /// make it a level-two section heading).
    /// </summary>
    private static bool IsContractHeading(string[] lines, int index)
    {
        var text = lines[index].Trim();
        return text.Length > 0
            && index + 1 < lines.Length
            && IsUnderline(lines[index + 1])
            && (index == 0 || !IsUnderline(lines[index - 1]));
    }

    private static bool IsUnderline(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length < 2)
            return false;
        foreach (var c in trimmed)
        {
            if (c != '-')
                return false;
        }

        return true;
    }

    private static void ParseBrokenContractsSection(
        string[] lines, int sectionStart, List<ExternalToolFinding> findings)
    {
        var end = lines.Length;
        string? contractName = null;
        string? context = null;
        string? cyclePackage = null;
        var contractProducedFindings = false;
        var blockStart = -1;

        for (var i = sectionStart; i < end; i++)
        {
            if (findings.Count >= MaxResults)
                return;
            if (IsSectionBoundary(lines, i))
                break;
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0 || IsUnderline(lines[i]))
                continue;

            if (IsContractHeading(lines, i))
            {
                EmitFallbackIfNeeded(findings, contractProducedFindings, contractName, lines, blockStart, i);
                contractName = trimmed;
                context = null;
                cyclePackage = null;
                contractProducedFindings = false;
                blockStart = i;
                continue;
            }

            if (contractName is null)
                continue;

            var link = ImportLinkLine.Match(lines[i]);
            if (link.Success)
            {
                var importer = link.Groups["importer"].Value.TrimStart('-', '&').Trim();
                var imported = link.Groups["imported"].Value;
                var detail = link.Groups["detail"].Value.Trim();
                var path = ModuleToPath(
                    importer.StartsWith(".", StringComparison.Ordinal) && cyclePackage is not null
                        ? cyclePackage + importer
                        : importer);
                findings.Add(new ExternalToolFinding(
                    SeverityLevel: "error",
                    RuleId: contractName,
                    Message: Describe(context, $"{importer} -> {imported} ({detail})"),
                    Path: path,
                    Line: FirstLineNumber(detail)));
                contractProducedFindings = true;
                continue;
            }

            var moduleWithLines = ModuleWithLinesLine.Match(lines[i]);
            if (moduleWithLines.Success)
            {
                var module = moduleWithLines.Groups["module"].Value;
                var detail = moduleWithLines.Groups["detail"].Value.Trim();
                findings.Add(new ExternalToolFinding(
                    SeverityLevel: "error",
                    RuleId: contractName,
                    Message: Describe(context, $"{module} ({detail})"),
                    Path: ModuleToPath(module),
                    Line: FirstLineNumber(detail)));
                contractProducedFindings = true;
                continue;
            }

            var bullet = ModuleBulletLine.Match(lines[i]);
            if (bullet.Success)
            {
                var module = bullet.Groups["module"].Value;
                findings.Add(new ExternalToolFinding(
                    SeverityLevel: "error",
                    RuleId: contractName,
                    Message: Describe(context, module),
                    Path: ModuleToPath(module),
                    Line: null));
                contractProducedFindings = true;
                continue;
            }

            // Any other non-blank line inside a contract block — the
            // "x is not allowed to import y:", "Illegal imports of protected
            // package x:", "No cycles are allowed in x.", "It could be made
            // acyclic by removing N dependencies:", "The following modules
            // are not listed as layers:" context lines, plus custom-contract
            // text — prefixes the next finding's message for context.
            context = trimmed;
            var cycleMatch = CycleContextLine.Match(trimmed);
            if (cycleMatch.Success)
                cyclePackage = cycleMatch.Groups["package"].Value;
        }

        EmitFallbackIfNeeded(findings, contractProducedFindings, contractName, lines, blockStart, end);
    }

    private static void ParseWarningsSection(
        string[] lines, int sectionStart, List<ExternalToolFinding> findings)
    {
        var end = lines.Length;
        string? contractName = null;

        for (var i = sectionStart; i < end; i++)
        {
            if (findings.Count >= MaxResults)
                return;
            if (IsSectionBoundary(lines, i))
                break;
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0 || IsUnderline(lines[i]))
                continue;

            if (IsContractHeading(lines, i))
            {
                contractName = trimmed;
                continue;
            }

            // Warnings are rendered as "- <text>" bullets under level-three
            // contract headings.
            if (contractName is not null && trimmed.StartsWith('-'))
            {
                findings.Add(new ExternalToolFinding(
                    SeverityLevel: "warning",
                    RuleId: contractName,
                    Message: $"{contractName}: {trimmed.TrimStart('-').Trim()}",
                    Path: null,
                    Line: null));
            }
        }
    }

    /// <summary>
    /// A broken contract whose rendered block produced no recognised
    /// import/module lines (custom contracts render free text) still gets one
    /// finding — a BROKEN verdict must never parse to zero findings.
    /// </summary>
    private static void EmitFallbackIfNeeded(
        List<ExternalToolFinding> findings,
        bool contractProducedFindings,
        string? contractName,
        string[] lines,
        int blockStart,
        int blockEnd)
    {
        if (contractName is null || contractProducedFindings || findings.Count >= MaxResults)
            return;

        var detail = new StringBuilder();
        for (var i = blockStart + 1; i < blockEnd && detail.Length < FallbackDetailMaxChars; i++)
        {
            var text = lines[i].Trim();
            if (text.Length == 0 || IsUnderline(lines[i]))
                continue;
            if (detail.Length > 0)
                detail.Append(' ');
            detail.Append(text);
        }

        var suffix = detail.Length == 0
            ? "no per-import details rendered"
            : ExternalToolJsonHelpers.Truncate(detail.ToString(), FallbackDetailMaxChars);
        findings.Add(new ExternalToolFinding(
            SeverityLevel: "error",
            RuleId: contractName,
            Message: $"{contractName} reported BROKEN: {suffix}",
            Path: null,
            Line: null));
    }

    private static string Describe(string? context, string detail)
        => string.IsNullOrWhiteSpace(context) ? detail : $"{context} {detail}";

    /// <summary>
    /// Renders a Python module name in slash-separated form so the finding
    /// path reads like its location and prefix exclusions can match:
    /// <c>mypkg.low</c> → <c>mypkg/low</c> (the file is either
    /// <c>mypkg/low.py</c> or <c>mypkg/low/__init__.py</c> — the tool does
    /// not say which).
    /// </summary>
    private static string ModuleToPath(string module)
        => module.Trim('.').Replace('.', '/');

    private static int? FirstLineNumber(string detail)
    {
        var match = LineNumber.Match(detail);
        return match.Success && int.TryParse(match.Groups["line"].Value, out var line) && line > 0
            ? line
            : null;
    }

    private static string Tail(string[] lines)
    {
        var nonBlank = new List<string>();
        for (var i = lines.Length - 1; i >= 0 && nonBlank.Count < 5; i--)
        {
            var text = lines[i].Trim();
            if (text.Length > 0)
                nonBlank.Add(text);
        }

        nonBlank.Reverse();
        return ExternalToolJsonHelpers.Truncate(
            ExternalToolJsonHelpers.SingleLine(string.Join(" ", nonBlank)),
            FailureTailMaxChars);
    }
}
