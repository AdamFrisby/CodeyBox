using System.Text.RegularExpressions;
using CodeyBox.PluginSdk.Tools;

namespace CodeyBox.ApiCompatAuditorPlugin;

/// <summary>
/// Parses apicompat console output into <see cref="ExternalToolFinding"/>
/// records. The tool has no SARIF or JSON mode (verified against
/// Microsoft.DotNet.ApiCompat.Tool 10.0.401): each unsuppressed difference
/// is written as a <c>&lt;diagnostic-id&gt;: &lt;message&gt;</c> line —
/// <c>CP####</c> for assembly API differences and <c>PKV###</c>/<c>PKV####</c>
/// for package-level differences — and the stream the line lands on carries
/// the severity: breaking-change differences are logged as errors on
/// <b>stderr</b> under an <c>API compatibility errors between
/// '&lt;left&gt;' (left) and '&lt;right&gt;' (right):</c> header, while
/// warning-level diagnostics (e.g. <c>CP1003</c> unresolved reference
/// search directories, <c>PKV0008</c> ignored baseline frameworks) are
/// written to <b>stdout</b>, and informational differences are written to
/// stdout with an <c>info</c> prefix. The severity token handed to the
/// auditor's declared <see cref="ExternalToolSeverityMapping"/> is that
/// channel (<c>error</c>/<c>warning</c>/<c>info</c>) — never a raw tool
/// string.
///
/// <para><b>Exit-code cross-check.</b> apicompat's exit code is ambiguous by
/// construction: <c>1</c> is returned for findings
/// (<c>HasLoggedErrorSuppressions</c>) <em>and</em> for every failure — a
/// System.CommandLine usage error, a missing assembly
/// (<c>FileNotFoundException</c>), a bad suppression file. So exit
/// <c>1</c> is a verdict only when stderr carries at least one
/// error-channel diagnostic line; an exit-1 run whose stderr holds none
/// (usage errors, crashes) throws <see cref="ExternalToolParseException"/>
/// — infrastructure, never a pass. Exit <c>0</c> diagnostics (none are
/// possible when differences are suppressed into a file — those are never
/// logged) are still surfaced as findings, fail-closed.</para>
///
/// <para><b>Locations.</b> apicompat has no notion of source file:line —
/// its messages embed the compared <em>assembly/package paths</em>
/// verbatim as passed on the command line (e.g. <c>Member '…' exists on
/// baseline/Lib.dll but not on bin/Lib.dll</c>). Each finding's location is
/// the message path matching a configured right-side operand (the
/// implementation under audit); when no operand matches, the single
/// path-shaped token wins when exactly one exists, then the current
/// error header's right side. Absolute paths under the exec working
/// directory are relativized; anything else yields no location rather than
/// a guessed one.</para>
/// </summary>
internal sealed class ApiCompatReportParser : IExternalToolOutputParser
{
    /// <summary>Severity token for error-channel (stderr) diagnostics — maps to <see cref="CodeyBox.Core.AuditSeverity.Error"/>.</summary>
    internal const string ErrorLevel = "error";

    /// <summary>Severity token for stdout diagnostics — maps to <see cref="CodeyBox.Core.AuditSeverity.Warning"/>.</summary>
    internal const string WarningLevel = "warning";

    /// <summary>Severity token for stdout <c>info</c>-prefixed diagnostics — maps to <see cref="CodeyBox.Core.AuditSeverity.Info"/>.</summary>
    internal const string InfoLevel = "info";

    // Same per-report result bound the shared SARIF parser applies.
    private const int MaxResults = SarifToolOutputParser.DefaultMaxResults;

    private static readonly Regex DiagnosticLine = new(
        @"^\s*(?<code>(?:CP|PKV)\d{3,4})\s*:\s*(?<message>.+?)\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex InfoDiagnosticLine = new(
        @"^\s*info\s+(?<code>(?:CP|PKV)\d{3,4})\s*:\s*(?<message>.+?)\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // "API compatibility errors between 'left.dll' (left) and 'right.dll' (right):"
    private static readonly Regex ErrorsHeader = new(
        @"^API compatibility errors between '(?<left>.+)' \(left\) and '(?<right>.+)' \(right\):\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Path-shaped tokens ending in a compared-artifact extension.
    private static readonly Regex AssemblyPathToken = new(
        @"[\p{L}\p{N}_.\-\\/]+\.(?:dll|exe|nupkg)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly Func<IReadOnlyList<string>> _rightOperands;

    /// <param name="rightOperands">
    /// Resolves the right-side (implementation) operands the scan was
    /// invoked with — scoped config or ExtraArguments values, normalized to
    /// forward slashes — so embedded paths can be attributed to the side
    /// under audit.
    /// </param>
    internal ApiCompatReportParser(Func<IReadOnlyList<string>> rightOperands)
    {
        ArgumentNullException.ThrowIfNull(rightOperands);
        _rightOperands = rightOperands;
    }

    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var findings = new List<ExternalToolFinding>();
        var errorDiagnostics = 0;
        var headerRight = (string?)null;

        // stderr: the error channel — the pair header plus one CP#### /
        // PKV### line per unsuppressed difference and the "API breaking
        // changes found" trailer (no code — context only).
        foreach (var line in SplitLines(input.Stderr))
        {
            var header = ErrorsHeader.Match(line);
            if (header.Success)
            {
                headerRight = header.Groups["right"].Value;
                continue;
            }

            var diagnostic = DiagnosticLine.Match(line);
            if (!diagnostic.Success)
                continue;
            errorDiagnostics++;
            if (findings.Count < MaxResults)
            {
                findings.Add(new ExternalToolFinding(
                    SeverityLevel: ErrorLevel,
                    RuleId: diagnostic.Groups["code"].Value,
                    Message: diagnostic.Groups["message"].Value,
                    Path: ResolveLocation(diagnostic.Groups["message"].Value, headerRight, input.WorkingDirectory)));
            }
        }

        // stdout: warning-channel diagnostics and `info`-prefixed
        // informational differences; the clean-run "APICompat ran
        // successfully without finding any breaking changes." line carries
        // no code and is ignored.
        foreach (var line in SplitLines(input.Stdout))
        {
            var info = InfoDiagnosticLine.Match(line);
            var diagnostic = info.Success ? info : DiagnosticLine.Match(line);
            if (!diagnostic.Success)
                continue;
            if (findings.Count >= MaxResults)
                break;
            findings.Add(new ExternalToolFinding(
                SeverityLevel: info.Success ? InfoLevel : WarningLevel,
                RuleId: diagnostic.Groups["code"].Value,
                Message: diagnostic.Groups["message"].Value,
                Path: ResolveLocation(diagnostic.Groups["message"].Value, headerRight, input.WorkingDirectory)));
        }

        // apicompat returns 1 for findings AND for every failure (usage
        // error, missing assembly, bad suppression file) — exit 1 without a
        // single error-channel diagnostic is "could not run", not a verdict.
        if (input.ExitCode == ApiCompatAuditor.FindingsExitCode && errorDiagnostics == 0)
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' exited {ApiCompatAuditor.FindingsExitCode} but stderr carried no "
                + "CP####/PKV### diagnostic lines — exit 1 is shared between findings, usage errors, and "
                + "crashes, so a diagnostic-free failure is 'could not run', not a verdict.");

        return findings;
    }

    /// <summary>
    /// Picks the finding's location from the paths embedded in
    /// <paramref name="message"/>: a token matching a configured right-side
    /// operand first (exact, or beneath a right-side directory operand),
    /// then the message's only path token, then the error header's right
    /// side. Absolute paths are relativized against
    /// <paramref name="workingDirectory"/> when contained; a path that
    /// cannot be attributed yields null rather than a guess.
    /// </summary>
    private string? ResolveLocation(string message, string? headerRight, string? workingDirectory)
    {
        var tokens = AssemblyPathToken.Matches(message)
            .Select(m => NormalizePathToken(m.Value))
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var rightOperands = _rightOperands();

        var match = tokens.FirstOrDefault(t => rightOperands.Any(o => OperandMatches(o, t)));
        var chosen = match
            ?? (tokens.Count == 1 ? tokens[0] : null)
            ?? (headerRight is null ? null : NormalizePathToken(headerRight));
        if (string.IsNullOrWhiteSpace(chosen))
            return null;

        if (chosen.StartsWith("/", StringComparison.Ordinal))
        {
            // An absolute path only has meaning as a location when it lands
            // inside the exec working directory.
            if (string.IsNullOrWhiteSpace(workingDirectory))
                return null;
            var root = NormalizePathToken(workingDirectory!).TrimEnd('/') + "/";
            if (!chosen.StartsWith(root, StringComparison.Ordinal))
                return null;
            return chosen[root.Length..];
        }

        return chosen;
    }

    /// <summary>
    /// True when a message-embedded path refers to a configured operand: an
    /// exact match, or the operand names a directory (a trailing '/' or no
    /// artifact extension) the path sits beneath.
    /// </summary>
    private static bool OperandMatches(string operand, string path)
    {
        var normalized = NormalizePathToken(operand).TrimEnd('/');
        if (normalized.Length == 0)
            return false;

        var wildcard = normalized.IndexOfAny(['*', '?']);
        if (wildcard >= 0)
        {
            // A glob operand matches paths under the directory portion that
            // precedes the first wildcard.
            var prefix = normalized[..(wildcard + 1)];
            var slash = prefix.LastIndexOf('/');
            var directory = slash >= 0 ? prefix[..slash] : string.Empty;
            return directory.Length > 0
                && path.StartsWith(directory + "/", StringComparison.Ordinal);
        }

        if (HasArtifactExtension(normalized))
            return string.Equals(path, normalized, StringComparison.Ordinal);
        return path.Equals(normalized, StringComparison.Ordinal)
            || path.StartsWith(normalized + "/", StringComparison.Ordinal);
    }

    private static bool HasArtifactExtension(string path)
        => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase);

    private static string NormalizePathToken(string path)
        => path.Replace('\\', '/').Trim();

    private static IEnumerable<string> SplitLines(string text)
    {
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
            yield return line;
    }
}
