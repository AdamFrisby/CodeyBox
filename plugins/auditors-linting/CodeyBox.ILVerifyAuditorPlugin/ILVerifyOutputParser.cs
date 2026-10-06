using System.Text.RegularExpressions;
using CodeyBox.PluginSdk.Tools;

namespace CodeyBox.ILVerifyAuditorPlugin;

/// <summary>
/// Parses <c>ilverify</c> (dotnet-ilverify) stdout into verification findings.
///
/// <para>Verified report shapes (against <c>dotnet-ilverify 10.0.12</c> — not
/// assumed from the common table): exit <c>0</c> prints one <c>All Classes
/// and Methods in &lt;assembly&gt; Verified.</c> line per input assembly and
/// nothing else; exit <c>2</c> prints one <c>[IL]: Error
/// [&lt;code&gt;]: [&lt;assembly&gt; : &lt;type&gt;::&lt;method&gt;]…</c> line
/// per verification failure plus a <c>N Error(s) Verifying &lt;assembly&gt;</c>
/// summary per failing input. The whole report rides stdout; stderr stays
/// empty on both verdicts.</para>
///
/// <para>Loader failures are not findings. An incomplete reference closure
/// surfaces either before the scan (exit <c>1</c>: <c>Error: Assembly or
/// module not found: …</c> plus a <c>VerifierException</c> stack — the base
/// already reports that exit as infrastructure) or as an <c>[IL]: Error</c>
/// line whose code is <c>FileLoadErrorGeneric</c> or whose message reports an
/// unloadable assembly (exit <c>2</c>, e.g. <c>Failed to load assembly
/// 'lib'</c>). Those lines throw <see cref="ExternalToolParseException"/>
/// (reported as infrastructure by the base): a missing reference means the
/// verifier could not see the dependency, so there is no verdict on the
/// candidate — never a pass, never a code defect. Exit <c>0</c> without the
/// <c>Verified.</c> marker (a hijacked or truncated invocation, e.g. an
/// operator <c>--help</c> in <c>ExtraArguments</c>) likewise throws instead
/// of passing vacuously.</para>
/// </summary>
internal sealed class ILVerifyOutputParser : IExternalToolOutputParser
{
    // [IL]: Error [StackUnexpected]: [/work/artifacts/invalid.dll : Fixture.Class1::GetName()][offset 0x00000001][found Int32][expected ref 'string'] Unexpected type on the stack.
    private static readonly Regex ErrorLinePattern = new(
        @"^\[IL\]: Error \[(?<code>[^\[\]]+)\]:(?<rest>.*)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex VerifiedMarkerPattern = new(
        @"^All Classes and Methods in .+ Verified\.$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // The bracketed segments leading an error line: the first names the
    // assembly (and usually type::method), the rest carry offset/found/
    // expected/token details. The message follows the last segment.
    private static readonly Regex LeadingSegmentsPattern = new(
        @"^(?<segments>(?:\[[^\[\]]*\])+)(?<message>.*)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex BracketSegmentPattern = new(
        @"\[(?<segment>[^\[\]]*)\]",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private const string LoaderErrorCode = "FileLoadErrorGeneric";

    private static readonly string[] LoaderMessageMarkers =
    [
        "failed to load assembly",
        "assembly or module not found",
        "could not resolve",
        "could not be resolved",
        "failed to resolve",
        "unresolved assembly",
    ];

    private readonly int _maxResults;

    public ILVerifyOutputParser(int maxResults = ExternalToolReportLimits.DefaultMaxResults)
    {
        if (maxResults <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxResults), "Maximum ILVerify results must be positive.");
        _maxResults = maxResults;
    }

    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var stdout = input.Stdout ?? string.Empty;
        if (string.IsNullOrWhiteSpace(stdout))
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced no output on stdout.");

        var lines = stdout.Split('\n');
        var errors = new List<(string Code, string Tail)>();
        var verified = false;
        var toolLoaderError = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;
            var error = ErrorLinePattern.Match(line);
            if (error.Success)
            {
                errors.Add((error.Groups["code"].Value.Trim(), error.Groups["rest"].Value));
                continue;
            }
            if (VerifiedMarkerPattern.IsMatch(line))
                verified = true;
            else if (line.StartsWith("Error:", StringComparison.Ordinal))
                toolLoaderError = true;
        }

        if (errors.Count == 0)
        {
            if (input.ExitCode == 0 && verified && !toolLoaderError)
                return [];
            throw new ExternalToolParseException(
                toolLoaderError
                    ? $"Tool '{input.ToolName}' reported a loader failure (incomplete reference closure or unreadable input): configure a complete '{ILVerifyAuditor.ReferenceAssembliesKey}' set covering every dependency of '{ILVerifyAuditor.AssembliesKey}'. Missing references are infrastructure, never a pass."
                    : $"Tool '{input.ToolName}' exited {input.ExitCode} without a verifiable report (no '[IL]: Error' lines and no 'Verified.' marker).");
        }

        var findings = new List<ExternalToolFinding>(Math.Min(errors.Count, _maxResults));
        foreach (var (code, tail) in errors)
        {
            if (findings.Count >= _maxResults)
                break;
            if (string.IsNullOrWhiteSpace(code))
                throw new ExternalToolParseException(
                    $"Tool '{input.ToolName}' emitted an error line without an error code.");
            if (IsLoaderFailure(code, tail))
                throw new ExternalToolParseException(
                    $"Tool '{input.ToolName}' reported loader error '{code}': a dependency assembly could not be loaded — configure a complete '{ILVerifyAuditor.ReferenceAssembliesKey}' set covering every dependency of '{ILVerifyAuditor.AssembliesKey}'. Missing references are infrastructure, never a pass and never a code defect.");
            findings.Add(BuildFinding(input, code, tail));
        }

        return findings;
    }

    private static bool IsLoaderFailure(string code, string rest)
    {
        if (string.Equals(code, LoaderErrorCode, StringComparison.Ordinal))
            return true;
        foreach (var marker in LoaderMessageMarkers)
        {
            if (rest.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static ExternalToolFinding BuildFinding(ExternalToolParseInput input, string code, string rest)
    {
        var trimmed = rest.Trim();
        string assemblyToken = string.Empty;
        var leading = LeadingSegmentsPattern.Match(trimmed);
        if (leading.Success)
        {
            foreach (Match segment in BracketSegmentPattern.Matches(leading.Groups["segments"].Value))
            {
                var text = segment.Groups["segment"].Value.Trim();
                if (assemblyToken.Length == 0)
                    assemblyToken = AssemblyToken(text);
            }
        }

        // The message keeps the tool's full report line (segments plus
        // text): type, method, offset, and found/expected details are what
        // make the failure actionable. Only the echoed assembly path is
        // rebased to its repository-relative form.
        var path = MapAssemblyPath(assemblyToken, input.ScanRoot);
        var message = trimmed;
        if (!string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(assemblyToken))
            message = RebaseAssemblyToken(message, assemblyToken, path);
        if (message.Length == 0)
            message = $"Verification error '{code}'.";

        return new ExternalToolFinding(
            SeverityLevel: "error",
            RuleId: code,
            Message: message,
            Path: string.IsNullOrEmpty(path) ? null : path);
    }

    private static string AssemblyToken(string firstSegment)
    {
        var separator = firstSegment.IndexOf(" : ", StringComparison.Ordinal);
        var token = (separator < 0 ? firstSegment : firstSegment[..separator]).Trim();
        return token.Trim('"');
    }

    // ILVerify echoes the assembly path it was given. The scan argv carries
    // "./"-prefixed repository-relative paths, so stripping that prefix
    // yields the finding location directly; an absolute token is relativized
    // against the probed scan root, and anything unmappable falls back to
    // the file name rather than an untrusted absolute path.
    private static string MapAssemblyPath(string assemblyToken, string? scanRoot)
    {
        if (string.IsNullOrWhiteSpace(assemblyToken))
            return string.Empty;
        var normalized = assemblyToken.Replace('\\', '/').Trim();
        if (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        if (normalized.Length == 0
            || normalized.Contains('\n', StringComparison.Ordinal)
            || normalized.Contains('\r', StringComparison.Ordinal))
            return string.Empty;
        if (!normalized.StartsWith("/", StringComparison.Ordinal))
            return normalized.Split('/').Contains("..", StringComparer.Ordinal)
                ? FileNameOf(normalized)
                : normalized;
        if (!string.IsNullOrEmpty(scanRoot))
        {
            var root = scanRoot.Replace('\\', '/');
            var prefix = root.EndsWith("/", StringComparison.Ordinal) ? root : root + "/";
            if (normalized.StartsWith(prefix, StringComparison.Ordinal))
            {
                var relative = normalized[prefix.Length..];
                if (relative.Length > 0 && !relative.Split('/').Contains("..", StringComparer.Ordinal))
                    return relative;
            }
        }

        return FileNameOf(normalized);
    }

    private static string FileNameOf(string path)
    {
        var slash = path.LastIndexOf('/');
        var name = slash < 0 ? path : path[(slash + 1)..];
        return string.IsNullOrWhiteSpace(name) ? string.Empty : name;
    }

    private static string RebaseAssemblyToken(string message, string assemblyToken, string mappedPath)
    {
        var index = message.IndexOf(assemblyToken, StringComparison.Ordinal);
        return index < 0
            ? message
            : message[..index] + mappedPath + message[(index + assemblyToken.Length)..];
    }
}
