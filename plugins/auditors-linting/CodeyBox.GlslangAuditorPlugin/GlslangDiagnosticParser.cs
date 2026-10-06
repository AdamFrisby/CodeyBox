using System.Text.RegularExpressions;
using CodeyBox.PluginSdk.Tools;

namespace CodeyBox.GlslangAuditorPlugin;

/// <summary>
/// Parses <c>glslangValidator</c> text diagnostics into
/// <see cref="ExternalToolFinding"/> records. The tool reports one line per
/// diagnostic on either stream:
/// <code>
/// ERROR: shaders/water.vert:12: 'foo' : undeclared identifier
/// WARNING: shaders/water.frag:3: 'bar' : deprecated usage
/// ERROR: 0:12: 'foo' : undeclared identifier
/// </code>
/// A located diagnostic becomes a finding with the synthesized
/// <see cref="RuleId"/> and the tool's own severity level. Diagnostics the
/// tool reports without an asset location (shader-index heads such as
/// <c>0:12:</c>, which keep their line number but name no file) become
/// path-less findings so no reported diagnostic is ever dropped. Program-level
/// link failures (<c>Link failed</c>, <c>Linking ...</c>) name no asset and
/// describe scope (targets that do not link as one program), not a defect in
/// the diff — when they are the only errors, parsing throws
/// <see cref="ExternalToolParseException"/> (infrastructure with guidance),
/// never a finding against the change and never a pass.
/// <para>An exit without any diagnostic line at all (usage text, missing
/// input, bad flags) likewise fails closed: with the pre-scan input
/// verification the auditor performs, a silent exit 0 is a checked pass,
/// but a noisy non-zero exit with no diagnostic is incomplete evidence.</para>
/// </summary>
internal sealed class GlslangDiagnosticParser : IExternalToolOutputParser
{
    /// <summary>Synthesized rule id for every glslang validation diagnostic.</summary>
    internal const string ValidationRuleId = "glslang/validation";

    /// <summary>Severity level carried by error diagnostics; mapped to Error.</summary>
    internal const string ErrorLevel = "error";

    /// <summary>Severity level carried by warning diagnostics; mapped to Warning (advisory).</summary>
    internal const string WarningLevel = "warning";

    // Same per-document result bound the shared SARIF parser applies, so a
    // diagnostic-dense run cannot produce an unbounded in-memory list before
    // the base applies its own MaxFindings cap.
    private const int MaxResults = SarifToolOutputParser.DefaultMaxResults;

    // One diagnostic message can run long; bound a single finding's message
    // so one line cannot dominate the audit report.
    private const int MaxMessageChars = 4000;

    // Bound on link-failure text quoted into the infrastructure failure, so
    // a pathological program-level dump cannot bloat the failure reason.
    private const int MaxLinkExcerptChars = 500;

    // Program-level link-failure markers (case-insensitive): these lines
    // describe the whole link unit, never one asset. Each marker is a
    // multi-word phrase specific to linking — a per-asset compile message
    // names a declaration and a violated rule, so it cannot contain these
    // phrases without describing the link itself. Located lines are still
    // asset findings first (link errors carry no file:line); only
    // location-less link lines take this path.
    internal static readonly IReadOnlyList<string> LinkFailureMarkers =
    [
        "link failed",
        "linking ",
        "no code generated",
        "compilation errors",
    ];

    private static readonly Regex DiagnosticPattern = new(
        @"^(ERROR|WARNING)\s*:\s*(.*)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Optional `path:line[:col]:` head in front of the message. The path
    // group is non-greedy and must contain a separator, an extension dot, or
    // be a bare shader index — so summary prose that happens to contain a
    // colon (e.g. `2 compilation errors. No code generated.`) never parses
    // as a location.
    private static readonly Regex LocationPattern = new(
        @"^(?<head>.+?)\s*:\s*(?<line>\d+)\s*(?::\s*(?<column>\d+)\s*)?:\s*(?<message>.*)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <inheritdoc />
    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var combined = string.Concat(input.Stdout ?? string.Empty, "\n", input.Stderr ?? string.Empty);

        var findings = new List<ExternalToolFinding>();
        var sawLinkFailure = false;
        var linkExcerpt = string.Empty;
        foreach (var rawLine in combined.Split('\n'))
        {
            if (findings.Count >= MaxResults)
                break;
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;
            var match = DiagnosticPattern.Match(line);
            if (!match.Success)
                continue;
            var level = match.Groups[1].Value.ToLowerInvariant() == "warning" ? WarningLevel : ErrorLevel;
            var body = match.Groups[2].Value.Trim();
            if (body.Length == 0)
                continue;

            if (TryParseLocated(body, input, out var located))
            {
                findings.Add(new ExternalToolFinding(
                    SeverityLevel: level,
                    RuleId: ValidationRuleId,
                    Message: Truncate(string.IsNullOrWhiteSpace(located.Message) ? body : located.Message),
                    Path: located.Path,
                    Line: located.Line));
                continue;
            }

            if (IsLinkFailure(body))
            {
                sawLinkFailure = true;
                if (linkExcerpt.Length == 0)
                    linkExcerpt = body;
                continue;
            }

            findings.Add(new ExternalToolFinding(
                SeverityLevel: level,
                RuleId: ValidationRuleId,
                Message: Truncate(body)));
        }

        if (sawLinkFailure && !findings.Any(static f => f.SeverityLevel == ErrorLevel))
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' reported a program-level link failure without a per-asset "
                + "diagnostic: "
                + ToolOutputText.SingleLine(Truncate(linkExcerpt, MaxLinkExcerptChars))
                + " The configured targets are compiled and linked together as one program — targets "
                + "that cannot link (independent same-stage entry points, mismatched interfaces) are "
                + "out of scope for one audit; configure one linkable program per audit.");

        if (findings.Count == 0 && input.ExitCode != 0)
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' exited {input.ExitCode} without a parseable 'glslangValidator' "
                + "diagnostic. A validation failure always prints at least its ERROR/WARNING line; "
                + "output without even that means the tool could not run (missing input, bad flags, "
                + "unreadable file) or its output was truncated.");

        return findings;
    }

    private static bool TryParseLocated(
        string body,
        ExternalToolParseInput input,
        out (string? Path, int? Line, string Message) located)
    {
        located = (null, null, body);
        var match = LocationPattern.Match(body);
        if (!match.Success)
            return false;

        var head = match.Groups["head"].Value.Trim();
        if (ToolOutputText.ParseLine(match.Groups["line"].Value) is not { } line)
            return false;
        var message = match.Groups["message"].Value.Trim();
        if (message.Length == 0)
            return false;

        // A bare integer head is the tool's shader-index form (`0:12:`):
        // the line number is real, but it names no asset, so the finding
        // stays path-less rather than inventing a mapping.
        if (IsShaderIndex(head))
        {
            located = (null, line, message);
            return true;
        }

        // The head must look like a file reference (a separator or an
        // extension dot) so prose ending in a number is never misread as a
        // location.
        if (!head.Contains('/') && !head.Contains('\\') && !head.Contains('.'))
            return false;

        var path = NormalizeReportedPath(head, input);
        located = (path, line, message);
        return true;
    }

    private static bool IsShaderIndex(string head)
        => head.Length > 0
            && head.Length <= 4
            && head.All(char.IsAsciiDigit);

    private static bool IsLinkFailure(string body)
        => LinkFailureMarkers.Any(marker =>
            body.Contains(marker, StringComparison.OrdinalIgnoreCase));

    // Reported paths mirror the argv the auditor passed (repo-relative), but
    // the sandbox may translate the worktree: relativize absolute reports
    // against the resolved scan root when one was supplied, and drop any
    // location that escapes the tree rather than emitting an uncontained
    // path. A null location keeps the diagnostic as a path-less finding.
    private static string? NormalizeReportedPath(string raw, ExternalToolParseInput input)
    {
        var normalized = raw.Trim().Replace('\\', '/');
        if (normalized.Length == 0 || normalized.Length > 1024)
            return null;
        if (normalized.Any(char.IsControl))
            return null;

        var root = input.ScanRoot ?? input.WorkingDirectory;
        if (Path.IsPathRooted(normalized) || normalized.StartsWith("/", StringComparison.Ordinal))
        {
            if (root is { Length: > 0 })
            {
                var canonicalRoot = root.Replace('\\', '/').TrimEnd('/');
                if (normalized.StartsWith(canonicalRoot + "/", StringComparison.Ordinal))
                    normalized = normalized[(canonicalRoot.Length + 1)..];
                else
                    return null;
            }
            else
            {
                return null;
            }
        }

        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        if (normalized.Length == 0
            || normalized.StartsWith("/", StringComparison.Ordinal)
            || normalized.Split('/').Contains("..", StringComparer.Ordinal))
            return null;
        return normalized;
    }

    private static string Truncate(string message, int maxChars = MaxMessageChars)
        => message.Length <= maxChars ? message : message[..maxChars];
}
