using System.Text;
using System.Text.RegularExpressions;
using CodeyBox.PluginSdk.Tools;

namespace CodeyBox.GlslangAuditorPlugin;

/// <summary>
/// Parses <c>spirv-val</c> text diagnostics into
/// <see cref="ExternalToolFinding"/> records. The tool validates exactly
/// one binary per invocation and reports one line per diagnostic on stderr:
/// <code>
/// error: line 4: 2 Entry points cannot share the same name and ExecutionMode.
///   OpEntryPoint GLCompute %1 "main"
/// </code>
/// A diagnostic becomes a finding with the synthesized <see cref="RuleId"/>
/// on the module under validation. Detail lines the tool prints beneath a
/// diagnostic (indented disassembly of the offending instruction) are
/// attached to that finding's message, bounded, so no reported evidence is
/// dropped by line-splitting.
/// <para>Positions are instruction references, not source lines: the
/// validator numbers instructions inside the binary (<c>line 4</c> above
/// addresses the fourth assembly unit, not line 4 of any repository file),
/// so findings carry the module path with a null line — mapping the number
/// onto the <c>.spv</c> file would fabricate a source location the tool
/// never reported. The position text stays in the message verbatim.</para>
/// <para>Diagnostics that describe the tool operation rather than module
/// content (<c>file does not exist</c>, <c>Unrecognized target env</c>,
/// <c>More than one input file specified</c>) are infrastructure: when they
/// are the only diagnostics, parsing throws
/// <see cref="ExternalToolParseException"/> (never a finding against the
/// change, never a pass). An exit without any diagnostic line at all
/// (usage text from a bad flag, truncated output) likewise fails closed.
/// Malformed modules the tool did describe (<c>Invalid SPIR-V magic
/// number</c>, <c>file size should be a multiple of 4</c>) stay findings on
/// the module: the pre-scan probes normally reject such inputs first, so a
/// diagnostic here means the module changed under the scan and the evidence
/// belongs in the report.</para>
/// </summary>
internal sealed class SpirvValDiagnosticParser : IExternalToolOutputParser
{
    /// <summary>Synthesized rule id for every spirv-val validation diagnostic.</summary>
    internal const string ValidationRuleId = "spirv-val/validation";

    /// <summary>Severity level carried by error diagnostics; mapped to Error.</summary>
    internal const string ErrorLevel = "error";

    /// <summary>Severity level carried by warning diagnostics; mapped to Warning (advisory).</summary>
    internal const string WarningLevel = "warning";

    // Same per-document result bound the shared SARIF parser applies, so a
    // diagnostic-dense run cannot produce an unbounded in-memory list before
    // the base applies its own MaxFindings cap.
    private const int MaxResults = SarifToolOutputParser.DefaultMaxResults;

    // One diagnostic message can run long; bound a single finding's message
    // so one module cannot dominate the audit report.
    private const int MaxMessageChars = 4000;

    // Detail lines attached to one diagnostic: enough for the offending
    // instruction plus its operands, bounded so a pathological dump cannot
    // bloat a single finding before the message cap applies.
    private const int MaxContinuationLines = 5;
    private const int MaxContinuationLineChars = 1000;

    // Bound on tool-operation text quoted into the infrastructure failure, so
    // a pathological tool dump cannot bloat the failure reason.
    private const int MaxToolFailureExcerptChars = 500;

    // Tool-operation markers (case-insensitive): these lines describe the
    // invocation, never module content. Each marker is a multi-word clause
    // from the tool's own file handling — a module-content message names an
    // instruction and a violated validation rule, so it cannot contain these
    // clauses without describing the tool operation itself.
    internal static readonly IReadOnlyList<string> ToolFailureMarkers =
    [
        "file does not exist",
        "unrecognized target env",
        "more than one input file",
    ];

    private static readonly Regex DiagnosticPattern = new(
        @"^(ERROR|WARNING)\s*:\s*(.*)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly string _modulePath;

    /// <summary>
    /// Mints a parser attributing every diagnostic to <paramref name="modulePath"/>.
    /// The engine passes the contained module operand it invoked the tool
    /// with: reported filenames are untrusted tool output and are never
    /// parsed, so attribution comes from the argv the auditor built, not
    /// from anything the tool printed.
    /// </summary>
    internal SpirvValDiagnosticParser(string modulePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modulePath);
        _modulePath = modulePath;
    }

    /// <inheritdoc />
    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var combined = string.Concat(input.Stdout ?? string.Empty, "\n", input.Stderr ?? string.Empty);

        var findings = new List<ExternalToolFinding>();
        StringBuilder? current = null;
        var currentLevel = ErrorLevel;
        var continuations = 0;
        var sawToolFailure = false;
        var toolFailureExcerpt = string.Empty;

        void Flush()
        {
            if (current is null)
                return;
            findings.Add(new ExternalToolFinding(
                SeverityLevel: currentLevel,
                RuleId: ValidationRuleId,
                Message: Truncate(current.ToString()),
                Path: _modulePath));
            current = null;
        }

        foreach (var rawLine in combined.Split('\n'))
        {
            if (findings.Count >= MaxResults)
                break;
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;
            var match = DiagnosticPattern.Match(line);
            if (!match.Success)
            {
                if (current is not null && continuations < MaxContinuationLines)
                {
                    current.Append('\n').Append(Truncate(line, MaxContinuationLineChars));
                    continuations++;
                }
                continue;
            }
            Flush();
            var level = match.Groups[1].Value.ToLowerInvariant() == "warning" ? WarningLevel : ErrorLevel;
            var body = match.Groups[2].Value.Trim();
            if (body.Length == 0)
                continue;

            if (IsToolFailure(body))
            {
                sawToolFailure = true;
                if (toolFailureExcerpt.Length == 0)
                    toolFailureExcerpt = body;
                continue;
            }

            current = new StringBuilder(body);
            currentLevel = level;
            continuations = 0;
        }
        Flush();

        if (sawToolFailure && !findings.Any(static f => f.SeverityLevel == ErrorLevel))
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' could not validate the module: "
                + ToolOutputText.SingleLine(Truncate(toolFailureExcerpt, MaxToolFailureExcerptChars))
                + " The module was verified present before the scan, so a tool-operation failure "
                + "means the input vanished, the environment is unsupported, or the invocation was "
                + "rejected — infrastructure, never a verdict on the change.");

        if (findings.Count == 0 && input.ExitCode != 0)
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' exited {input.ExitCode} without a parseable 'spirv-val' "
                + "diagnostic. A module failure always prints at least its 'error:' line; "
                + "output without even that means the tool could not run (bad flags, unreadable "
                + "input) or its output was truncated.");

        return findings;
    }

    private static bool IsToolFailure(string body)
        => ToolFailureMarkers.Any(marker =>
            body.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static string Truncate(string message, int maxChars = MaxMessageChars)
        => message.Length <= maxChars ? message : message[..maxChars];
}
