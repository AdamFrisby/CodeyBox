using System.Text.RegularExpressions;
using CodeyBox.PluginSdk.Tools;
using YamlDotNet.Serialization;

namespace CodeyBox.ClangTidyAuditorPlugin;

/// <summary>
/// Parses clang-tidy's <c>--export-fixes</c> YAML report into
/// <see cref="ExternalToolFinding"/> records.
///
/// <para>Each analysed translation unit appends one document of the form
/// <c>MainSourceFile / Diagnostics[]</c>, where every entry carries
/// <c>DiagnosticName</c> (the check id, e.g.
/// <c>modernize-use-nullptr</c>), a <c>DiagnosticMessage</c> bundle
/// (<c>Message</c>, <c>FilePath</c>, <c>FileOffset</c>), and the tool's own
/// <c>Level</c> (<c>Warning</c>/<c>Error</c>). The level token is preserved
/// verbatim in <see cref="ExternalToolFinding.SeverityLevel"/> and translated
/// by the auditor's declared <see cref="ExternalToolSeverityMapping"/> — raw
/// levels never reach findings.</para>
///
/// <para>Two properties of the format shape this parser:</para>
/// <list type="bullet">
/// <item>Text diagnostics share stdout with the YAML report (clang-tidy
/// always prints them), so documents are located by <c>---</c>/<c>...</c>
/// delimiters and everything else is ignored. A source line consisting of
/// exactly <c>---</c> is not valid C or C++, so the delimiter cannot collide
/// with echoed source context.</item>
/// <item>The report carries a byte <c>FileOffset</c>, not a line number —
/// findings therefore locate <c>path</c> without <c>:line</c>. The offset is
/// kept parser-internally as part of the duplicate-suppression key (the same
/// header diagnostic is re-emitted once per including translation unit).</item>
/// </list>
///
/// <para>Absence of any report document means one of two things, split on the
/// exit code the base already classified as findings-producing: exit
/// <c>0</c> with no text diagnostics is a clean scan (clang-tidy emits no
/// YAML when no diagnostic fired); anything else — a non-zero exit, or an
/// exit <c>0</c> whose stdout still shows text diagnostics (the report sink
/// was overridden, e.g. an operator <c>--export-fixes</c> pointed elsewhere)
/// — throws <see cref="ExternalToolParseException"/>, which the base reports
/// as infrastructure, never as a pass.</para>
/// </summary>
internal sealed class ClangTidyYamlOutputParser : IExternalToolOutputParser
{
    // Same per-document result bound the shared SARIF parser applies.
    private const int MaxResults = SarifToolOutputParser.DefaultMaxResults;

    // Text diagnostics look like "src/a.cpp:1:10: warning: use nullptr
    // [modernize-use-nullptr]". Driver errors ("error: no input files")
    // carry no location and never match; they fail closed through the
    // no-document rule above.
    private static readonly Regex TextDiagnosticPattern = new(
        @":\d+:\d+:\s+(?:fatal\s+error|error|warning):",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // The report schema is fixed by the tool release the auditor pins; new
    // keys (e.g. Ranges) are ignored so additive drift keeps parsing, while
    // duplicate keys fail closed. Deserializers are shared process-wide like
    // the preset loader's.
    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithDuplicateKeyChecking()
        .IgnoreUnmatchedProperties()
        .Build();

    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var documents = SplitReportDocuments(input.Stdout);
        if (documents.Count == 0)
        {
            if (input.ExitCode == 0 && !TextDiagnosticPattern.IsMatch(input.Stdout ?? string.Empty))
                return [];
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced no clang-tidy YAML report on stdout (exit {input.ExitCode}).");
        }

        var findings = new List<ExternalToolFinding>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var document in documents)
        {
            foreach (var diagnostic in ParseDocument(input.ToolName, document))
            {
                if (findings.Count >= MaxResults)
                    break;
                if (seen.Add(IdentityKey(diagnostic)))
                    findings.Add(ToFinding(diagnostic));
            }
        }

        return findings;
    }

    private static IReadOnlyList<ParsedDiagnostic> ParseDocument(string toolName, string document)
    {
        ExportDocument? parsed;
        try
        {
            parsed = YamlDeserializer.Deserialize<ExportDocument>(document);
        }
        catch (Exception ex) when (ex is YamlDotNet.Core.YamlException || ex is InvalidOperationException)
        {
            throw new ExternalToolParseException(
                $"Tool '{toolName}' produced a clang-tidy YAML document that could not be parsed: {SingleLine(ex.Message)}.",
                ex);
        }

        var results = new List<ParsedDiagnostic>();
        if (parsed?.Diagnostics is null)
            return results;

        foreach (var entry in parsed.Diagnostics)
        {
            if (entry is null)
                continue;
            var message = NullIfWhiteSpace(entry.DiagnosticMessage?.Message);
            var rule = NullIfWhiteSpace(entry.DiagnosticName);
            var rawPath = NullIfWhiteSpace(entry.DiagnosticMessage?.FilePath);
            if (rule is null && message is null && rawPath is null)
                continue;
            results.Add(new ParsedDiagnostic(
                SeverityLevel: NullIfWhiteSpace(entry.Level),
                RuleId: rule,
                Message: message ?? "(no message)",
                Path: RelativizePath(rawPath, entry.BuildDirectory),
                FileOffset: entry.DiagnosticMessage?.FileOffset));
        }

        return results;
    }

    private static IReadOnlyList<string> SplitReportDocuments(string? stdout)
    {
        var documents = new List<string>();
        List<string>? current = null;
        foreach (var line in (stdout ?? string.Empty).Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Equals("---", StringComparison.Ordinal))
            {
                current = [];
                continue;
            }
            if (trimmed.Equals("...", StringComparison.Ordinal))
            {
                if (current is not null)
                {
                    if (ContainsReportMarker(current))
                        documents.Add(string.Join('\n', current));
                    current = null;
                }
                continue;
            }
            current?.Add(line);
        }

        // A final document without its "..." terminator only occurs when the
        // stream was cut mid-report; the YAML parse then fails closed below.
        if (current is not null && ContainsReportMarker(current))
            documents.Add(string.Join('\n', current));

        return documents;
    }

    private static bool ContainsReportMarker(List<string> candidate)
    {
        foreach (var line in candidate)
        {
            if (line.Contains("MainSourceFile", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    // FilePath is sandbox-absolute; the report's own per-diagnostic
    // BuildDirectory (the invocation working directory) is what it is
    // relativized against — no out-of-band state. Paths outside the build
    // directory (system headers, out-of-tree files from a compilation
    // database) are preserved as-is, matching the shared SARIF parser's
    // absolute-path posture.
    private static string? RelativizePath(string? rawPath, string? buildDirectory)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return null;
        var path = rawPath.Trim().Replace('\\', '/');
        var root = buildDirectory?.Trim().Replace('\\', '/').TrimEnd('/');
        if (!string.IsNullOrEmpty(root)
            && path.Length > root.Length + 1
            && path.StartsWith(root + "/", StringComparison.Ordinal))
            path = path[(root.Length + 1)..];
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    private static ExternalToolFinding ToFinding(ParsedDiagnostic diagnostic) => new(
        SeverityLevel: diagnostic.SeverityLevel,
        RuleId: diagnostic.RuleId,
        Message: diagnostic.Message,
        Path: diagnostic.Path,
        Line: null);

    // Unit separator delimiting the fields of the duplicate-suppression identity
    // key below: a control character that cannot appear in tool output, so the
    // concatenation can never collide across field boundaries.
    private const string IdentityFieldSeparator = "\u001f";

    private static string IdentityKey(ParsedDiagnostic diagnostic)
        => string.Concat(
            diagnostic.RuleId ?? string.Empty, IdentityFieldSeparator,
            diagnostic.Path ?? string.Empty, IdentityFieldSeparator,
            diagnostic.FileOffset?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty, IdentityFieldSeparator,
            diagnostic.Message);

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string SingleLine(string message)
        => message.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private sealed record ParsedDiagnostic(
        string? SeverityLevel,
        string? RuleId,
        string Message,
        string? Path,
        int? FileOffset);

    private sealed class ExportDocument
    {
        [YamlMember(Alias = "MainSourceFile")]
        public string? MainSourceFile { get; set; }

        [YamlMember(Alias = "Diagnostics")]
        public List<ExportDiagnostic?>? Diagnostics { get; set; }
    }

    private sealed class ExportDiagnostic
    {
        [YamlMember(Alias = "DiagnosticName")]
        public string? DiagnosticName { get; set; }

        [YamlMember(Alias = "DiagnosticMessage")]
        public ExportMessage? DiagnosticMessage { get; set; }

        [YamlMember(Alias = "Level")]
        public string? Level { get; set; }

        [YamlMember(Alias = "BuildDirectory")]
        public string? BuildDirectory { get; set; }
    }

    private sealed class ExportMessage
    {
        [YamlMember(Alias = "Message")]
        public string? Message { get; set; }

        [YamlMember(Alias = "FilePath")]
        public string? FilePath { get; set; }

        [YamlMember(Alias = "FileOffset")]
        public int? FileOffset { get; set; }
    }
}
