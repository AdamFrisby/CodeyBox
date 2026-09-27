using System.Xml;
using CodeyBox.PluginSdk.Tools;

namespace CodeyBox.PmdAuditorPlugin;

/// <summary>
/// Parses PMD's <c>-f xml</c> report (root <c>&lt;pmd&gt;</c>, namespace
/// <c>http://pmd.sourceforge.net/report/2.0.0</c>) into
/// <see cref="ExternalToolFinding"/> records. The report goes to
/// <b>stdout</b>; logs and progress stay on stderr. Each
/// <c>&lt;file name&gt;</c> element wraps its <c>&lt;violation&gt;</c>
/// children, so the file path comes from the enclosing element and the
/// violation supplies <c>rule</c> (rule id), <c>beginline</c>,
/// <c>priority</c> (the tool's severity vocabulary: <c>1</c> high through
/// <c>5</c> low), and the element text as the message.
///
/// <para><c>&lt;error&gt;</c> elements (per-file processing errors, e.g.
/// parse failures) and <c>&lt;configerror&gt;</c> elements (rule
/// configuration problems) are emitted as findings with the synthetic
/// severity tokens <c>processing-error</c>/<c>config-error</c> — the declared
/// severity map translates them like every other level. Under the default
/// invocation they are unreachable (recoverable errors exit 5, which the
/// auditor classifies as infrastructure before parsing); they matter only
/// when an operator passes <c>--no-fail-on-error</c> in
/// <c>ExtraArguments</c>, where they keep partial-analysis gaps visible
/// instead of silently zeroing coverage. <c>&lt;suppressedviolation&gt;</c>
/// elements (present only when an operator passes
/// <c>--show-suppressed</c>) are skipped: they describe violations the
/// repository already suppressed, not findings.</para>
///
/// <para>Severity stays in PMD's own vocabulary (<c>"1"</c>–<c>"5"</c>,
/// <c>processing-error</c>, <c>config-error</c>);
/// <see cref="ExternalToolAuditorBase"/> maps it through the auditor's
/// declared <see cref="ExternalToolSeverityMapping"/> — raw levels never
/// reach findings.</para>
///
/// <para>Malformed output throws <see cref="ExternalToolParseException"/>,
/// which the base reports as infrastructure, never as a pass: empty stdout,
/// non-XML, or XML without a <c>&lt;pmd&gt;</c> root.</para>
/// </summary>
internal sealed class PmdXmlOutputParser : IExternalToolOutputParser
{
    // Same per-document result bound the shared SARIF parser applies.
    private const int MaxResults = SarifToolOutputParser.DefaultMaxResults;

    /// <summary>Synthetic severity token for <c>&lt;error&gt;</c> (per-file processing failure) elements.</summary>
    internal const string ProcessingErrorLevel = "processing-error";

    /// <summary>Synthetic severity token for <c>&lt;configerror&gt;</c> (rule configuration failure) elements.</summary>
    internal const string ConfigErrorLevel = "config-error";

    /// <summary>Element name for violations suppressed by NOPMD/annotation markers — reported only, never findings.</summary>
    private const string SuppressedElementName = "suppressedviolation";

    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        // The XML report lives on stdout; stderr carries slf4j logs and
        // (unless --no-progress) the progress bar.
        if (string.IsNullOrWhiteSpace(input.Stdout))
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced no PMD XML output on stdout.");

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
            using var text = new StringReader(input.Stdout);
            using var reader = XmlReader.Create(text, settings);
            return ParseReport(reader, input.ToolName);
        }
        catch (XmlException ex)
        {
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced output that is not valid PMD XML: {SingleLine(ex.Message)}.",
                ex);
        }
    }

    private static List<ExternalToolFinding> ParseReport(XmlReader reader, string toolName)
    {
        // Advance to the <pmd> root; anything else (a text diagnostic the
        // tool printed instead of a report, e.g. a usage error) fails closed
        // as infrastructure rather than guessing.
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "pmd")
                break;
        }

        if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "pmd")
            throw new ExternalToolParseException(
                $"Tool '{toolName}' produced XML without a PMD 'pmd' root element.");

        var findings = new List<ExternalToolFinding>();
        string? currentFile = null;
        while (reader.Read())
        {
            if (findings.Count >= MaxResults)
                break;
            if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "file")
            {
                currentFile = null;
                continue;
            }
            if (reader.NodeType != XmlNodeType.Element)
                continue;

            switch (reader.LocalName)
            {
                case "file":
                    currentFile = NullIfWhiteSpace(reader.GetAttribute("name"));
                    break;
                case "violation":
                    findings.Add(ParseViolation(reader, currentFile));
                    break;
                case "error":
                    findings.Add(ParseProcessingError(reader));
                    break;
                case "configerror":
                    findings.Add(new ExternalToolFinding(
                        SeverityLevel: ConfigErrorLevel,
                        RuleId: NullIfWhiteSpace(reader.GetAttribute("rule")),
                        Message: NullIfWhiteSpace(reader.GetAttribute("msg"))
                            ?? "PMD rule configuration error (rule could not run — coverage is partial)"));
                    break;
                case SuppressedElementName:
                    // Suppressed violations are not findings; present only
                    // when the operator passed --show-suppressed.
                    break;
            }
        }

        return findings;
    }

    // Element bodies are read through a subtree reader: consuming content on
    // the main reader (ReadElementContentAsString) can strand the walk on a
    // child or sibling node and silently skip findings.
    private static ExternalToolFinding ParseViolation(XmlReader reader, string? currentFile)
    {
        using var violation = reader.ReadSubtree();
        violation.Read();

        var ruleId = NullIfWhiteSpace(violation.GetAttribute("rule"));
        var priority = NullIfWhiteSpace(violation.GetAttribute("priority"));
        var line = ParseLine(violation.GetAttribute("beginline"));
        var ruleset = NullIfWhiteSpace(violation.GetAttribute("ruleset"));
        var externalInfo = NullIfWhiteSpace(violation.GetAttribute("externalInfoUrl"));

        // The violation element's text content is the violation message.
        string? message = null;
        while (violation.Read())
        {
            if (violation.NodeType is XmlNodeType.Text or XmlNodeType.CDATA)
                message = (message ?? string.Empty) + violation.Value;
        }

        message = NullIfWhiteSpace(message) ?? "(no message)";
        if (ruleset is not null)
            message = $"{message} [ruleset: {ruleset}]";
        if (externalInfo is not null)
            message = $"{message} [see: {externalInfo}]";

        return new ExternalToolFinding(
            SeverityLevel: priority,
            RuleId: ruleId,
            Message: message,
            Path: currentFile,
            Line: line);
    }

    private static ExternalToolFinding ParseProcessingError(XmlReader reader)
    {
        using var error = reader.ReadSubtree();
        error.Read();

        var path = NullIfWhiteSpace(error.GetAttribute("filename"));
        // The element body is the error detail (stack trace); keep findings
        // to the one-line message.
        var message = NullIfWhiteSpace(error.GetAttribute("msg"))
            ?? "PMD processing error (file could not be analysed — coverage is partial)";

        return new ExternalToolFinding(
            SeverityLevel: ProcessingErrorLevel,
            RuleId: null,
            Message: message,
            Path: path);
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
