using System.Xml;
using CodeyBox.PluginSdk.Tools;

namespace CodeyBox.CppcheckAuditorPlugin;

/// <summary>
/// Parses cppcheck's <c>--xml --xml-version=2</c> report into
/// <see cref="ExternalToolFinding"/> records. Unlike most wrapped tools,
/// cppcheck writes its machine report to <b>stderr</b> (progress lines go to
/// stdout), so the report is read from <see cref="ExternalToolParseInput.Stderr"/>.
/// Each <c>&lt;error&gt;</c> element contributes one finding carrying the
/// tool's own severity, the <c>id</c> attribute as the rule id, the
/// <c>verbose</c> (falling back to <c>msg</c>) text with any <c>cwe</c>
/// attribute appended for triage, and the first <c>&lt;location&gt;</c>
/// child's <c>file</c>/<c>line</c> (falling back to <c>file</c>/<c>line</c>
/// attributes on the <c>&lt;error&gt;</c> element itself, which covers
/// location-less entries such as <c>checkersReport</c>).
///
/// <para>Severity stays in cppcheck's own vocabulary (<c>error</c>,
/// <c>warning</c>, <c>style</c>, <c>performance</c>, <c>portability</c>,
/// <c>information</c>); <see cref="ExternalToolAuditorBase"/> maps it through
/// the auditor's declared <see cref="ExternalToolSeverityMapping"/> — raw
/// levels never reach findings.</para>
///
/// <para>Malformed output throws <see cref="ExternalToolParseException"/>,
/// which the base reports as infrastructure, never as a pass: empty stderr,
/// non-XML, or XML without a <c>&lt;results&gt;</c> root. One narrow
/// exception: exit 1 with cppcheck's exact "could not find or open any of
/// the paths given." diagnostic on stdout and no XML on stderr means the
/// tool ran but there was nothing checkable (a tree with no C/C++ files) —
/// that is a clean pass with zero findings, not a failure.</para>
/// </summary>
internal sealed class CppcheckXmlOutputParser : IExternalToolOutputParser
{
    // Same per-document result bound the shared SARIF parser applies.
    private const int MaxResults = SarifToolOutputParser.DefaultMaxResults;

    /// <summary>
    /// Exact stdout diagnostic cppcheck 2.13.0 prints (exit 1, no XML) when
    /// none of the given paths contain checkable files.
    /// </summary>
    internal const string NoInputFilesSentinel = "could not find or open any of the paths given.";

    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        // The XML report lives on stderr; stdout carries only progress lines
        // ("Checking ...") and usage diagnostics.
        if (string.IsNullOrWhiteSpace(input.Stderr))
        {
            if (input.ExitCode == 1
                && (input.Stdout ?? string.Empty).Contains(NoInputFilesSentinel, StringComparison.Ordinal))
                return [];
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced no cppcheck XML output on stderr.");
        }

        // Prohibit DTDs explicitly: the report is tool output from the audit
        // sandbox, and findings must never depend on external entities.
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreComments = true,
            IgnoreWhitespace = true,
        };

        try
        {
            using var text = new StringReader(input.Stderr);
            using var reader = XmlReader.Create(text, settings);
            return ParseResults(reader, input.ToolName);
        }
        catch (XmlException ex)
        {
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced output that is not valid cppcheck XML: {SingleLine(ex.Message)}.",
                ex);
        }
    }

    private static List<ExternalToolFinding> ParseResults(XmlReader reader, string toolName)
    {
        // Advance to the <results> root; anything else (a text diagnostic
        // the tool printed instead of a report, e.g. a usage error) fails
        // closed as infrastructure rather than guessing.
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Name == "results")
                break;
        }

        if (reader.NodeType != XmlNodeType.Element || reader.Name != "results")
            throw new ExternalToolParseException(
                $"Tool '{toolName}' produced XML without a cppcheck 'results' root.");

        var findings = new List<ExternalToolFinding>();
        while (reader.Read())
        {
            if (findings.Count >= MaxResults)
                break;
            if (reader.NodeType == XmlNodeType.Element && reader.Name == "error")
            {
                using var error = reader.ReadSubtree();
                findings.Add(ParseError(error));
            }
        }

        return findings;
    }

    private static ExternalToolFinding ParseError(XmlReader error)
    {
        error.Read();

        var ruleId = NullIfWhiteSpace(error.GetAttribute("id"));
        var severity = NullIfWhiteSpace(error.GetAttribute("severity"));
        var message = NullIfWhiteSpace(error.GetAttribute("verbose"))
            ?? NullIfWhiteSpace(error.GetAttribute("msg"))
            ?? "(no message)";
        var cwe = NullIfWhiteSpace(error.GetAttribute("cwe"));
        if (cwe is not null)
            message = $"{message} [CWE-{cwe}]";

        // The first <location> child is the finding position (matches the
        // shared SARIF parser's first-physical-location convention);
        // location-less entries fall back to file/line attributes on the
        // <error> element itself, else carry no position.
        string? path = NullIfWhiteSpace(error.GetAttribute("file"));
        int? line = ParseLine(error.GetAttribute("line"));
        while (path is null && error.Read())
        {
            if (error.NodeType == XmlNodeType.Element && error.Name == "location")
            {
                path = NullIfWhiteSpace(error.GetAttribute("file"));
                line = ParseLine(error.GetAttribute("line"));
                break;
            }
        }

        return new ExternalToolFinding(
            SeverityLevel: severity,
            RuleId: ruleId,
            Message: message,
            Path: path,
            Line: line);
    }

    private static int? ParseLine(string? value)
        => int.TryParse(
                value,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed) && parsed > 0
            ? parsed
            : null;

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string SingleLine(string message)
        => message.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
