using System.Text.Json;
using CodeyBox.PluginSdk.Tools;
using static CodeyBox.PluginSdk.Tools.ExternalToolJsonHelpers;

namespace CodeyBox.ReuseAuditorPlugin;

/// <summary>
/// Parses <c>reuse lint --json</c> output into
/// <see cref="ExternalToolFinding"/> records. The report (verified against
/// reuse v6.2.0, <c>src/reuse/report.py:ProjectReport.to_dict_lint</c>) is an
/// object whose <c>non_compliant</c> member carries one list per compliance
/// criterion, plus a per-file <c>files</c> array:
///
/// <code>
/// {
///   "non_compliant": {
///     "bad_licenses": ["Foo-1.0"],
///     "deprecated_licenses": [],
///     "licenses_without_extension": [],
///     "missing_licenses": ["MIT"],
///     "unused_licenses": [],
///     "read_errors": [],
///     "missing_copyright_info": ["src/bad.py"],
///     "missing_licensing_info": ["src/bad.py"]
///   },
///   "files": [
///     { "path": "src/bad.py", "copyrights": [], "spdx_expressions": [] }
///   ],
///   "summary": { "compliant": false, … }
/// }
/// </code>
///
/// Each non-empty list entry becomes one finding whose rule id is the stable
/// <c>reuse/&lt;criterion&gt;</c> name (e.g.
/// <c>reuse/missing-licensing-info</c>), so the shared
/// <c>IncludedRules</c>/<c>ExcludedRules</c> configuration selects criteria
/// by exact match. Path-carrying criteria (<c>read_errors</c>,
/// <c>missing_copyright_info</c>, <c>missing_licensing_info</c>) keep the
/// reported file; license-level criteria (<c>bad_licenses</c>,
/// <c>deprecated_licenses</c>, <c>licenses_without_extension</c>,
/// <c>missing_licenses</c>, <c>unused_licenses</c>) name the license in the
/// message because the JSON report carries only the identifier, not the
/// <c>LICENSES/</c> file path. Invalid SPDX expressions are not listed under
/// <c>non_compliant</c> at all — they surface per file as
/// <c>spdx_expressions</c> entries with <c>is_valid: false</c>, which become
/// <c>reuse/invalid-spdx-expression</c> findings on that file. Severity stays
/// in the tool's own criterion vocabulary and the auditor's declared
/// <see cref="ExternalToolSeverityMapping"/> decides its meaning; raw levels
/// never reach findings. The tool reports files only — no line numbers — so
/// findings carry a file location with no line.
///
/// <para>Reported paths are normalized with the shared
/// <see cref="ExternalToolJsonHelpers.NormalizeReportedPath"/> policy:
/// <c>./</c> prefixes and dot segments collapse, absolute paths relativize
/// against the scan root and working directory, and anything still escaping
/// the tree is re-marked with the <c>file://</c> scheme so the shared
/// <c>ExcludePaths</c> prefix filter cannot mistake it for a
/// repository-relative location.</para>
///
/// <para>Malformed output — empty stdout, non-JSON, a non-object document, a
/// missing or non-object <c>non_compliant</c> member, a non-array criterion
/// list, a non-array <c>files</c> member, or an <i>unknown</i> criterion key
/// inside <c>non_compliant</c> — throws
/// <see cref="ExternalToolParseException"/>, which the base reports as
/// infrastructure, never as a pass. Unknown keys fail closed deliberately: a
/// report-shape change must never silently shrink the verdict. An empty
/// <c>non_compliant</c> section (every list empty, no invalid expressions)
/// is a clean verdict, not malformed output.</para>
/// </summary>
internal sealed class ReuseJsonOutputParser : IExternalToolOutputParser
{
    // Same per-document result bound the shared SARIF parser applies.
    private const int MaxResults = SarifToolOutputParser.DefaultMaxResults;
    private const int MessageMaxChars = 512;

    /// <summary>Rule id for license texts that are not valid SPDX identifiers.</summary>
    public const string BadLicensesRuleId = "reuse/bad-licenses";

    /// <summary>Rule id for SPDX-deprecated license identifiers.</summary>
    public const string DeprecatedLicensesRuleId = "reuse/deprecated-licenses";

    /// <summary>Rule id for LICENSES/ files missing a file extension.</summary>
    public const string LicensesWithoutExtensionRuleId = "reuse/licenses-without-extension";

    /// <summary>Rule id for referenced licenses with no text in LICENSES/.</summary>
    public const string MissingLicensesRuleId = "reuse/missing-licenses";

    /// <summary>Rule id for LICENSES/ texts no file refers to.</summary>
    public const string UnusedLicensesRuleId = "reuse/unused-licenses";

    /// <summary>Rule id for files the tool could not read.</summary>
    public const string ReadErrorsRuleId = "reuse/read-errors";

    /// <summary>Rule id for files without copyright information.</summary>
    public const string MissingCopyrightInfoRuleId = "reuse/missing-copyright-info";

    /// <summary>Rule id for files without licensing information.</summary>
    public const string MissingLicensingInfoRuleId = "reuse/missing-licensing-info";

    /// <summary>Rule id for unparsable SPDX license expressions.</summary>
    public const string InvalidSpdxExpressionRuleId = "reuse/invalid-spdx-expression";

    /// <summary>Tool criterion token for <see cref="BadLicensesRuleId"/>.</summary>
    public const string BadLicensesLevel = "bad-licenses";

    /// <summary>Tool criterion token for <see cref="DeprecatedLicensesRuleId"/>.</summary>
    public const string DeprecatedLicensesLevel = "deprecated-licenses";

    /// <summary>Tool criterion token for <see cref="LicensesWithoutExtensionRuleId"/>.</summary>
    public const string LicensesWithoutExtensionLevel = "licenses-without-extension";

    /// <summary>Tool criterion token for <see cref="MissingLicensesRuleId"/>.</summary>
    public const string MissingLicensesLevel = "missing-licenses";

    /// <summary>Tool criterion token for <see cref="UnusedLicensesRuleId"/>.</summary>
    public const string UnusedLicensesLevel = "unused-licenses";

    /// <summary>Tool criterion token for <see cref="ReadErrorsRuleId"/>.</summary>
    public const string ReadErrorsLevel = "read-errors";

    /// <summary>Tool criterion token for <see cref="MissingCopyrightInfoRuleId"/>.</summary>
    public const string MissingCopyrightInfoLevel = "missing-copyright-info";

    /// <summary>Tool criterion token for <see cref="MissingLicensingInfoRuleId"/>.</summary>
    public const string MissingLicensingInfoLevel = "missing-licensing-info";

    /// <summary>Tool criterion token for <see cref="InvalidSpdxExpressionRuleId"/>.</summary>
    public const string InvalidSpdxExpressionLevel = "invalid-spdx-expression";

    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Stdout))
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced no reuse JSON output on stdout.");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(input.Stdout);
        }
        catch (JsonException ex)
        {
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced output that is not valid JSON: {SingleLine(ex.Message)}.",
                ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new ExternalToolParseException(
                    $"Tool '{input.ToolName}' produced JSON that is not a reuse report "
                    + "(the report is an object with a 'non_compliant' member).");

            if (!root.TryGetProperty("non_compliant"u8, out var nonCompliant)
                || nonCompliant.ValueKind != JsonValueKind.Object)
                throw new ExternalToolParseException(
                    $"Tool '{input.ToolName}' produced JSON that is not a reuse report "
                    + "(missing or invalid 'non_compliant' object).");

            var findings = new List<ExternalToolFinding>();
            ParseNonCompliant(input, nonCompliant, findings);
            if (findings.Count >= MaxResults)
                return findings;

            if (root.TryGetProperty("files"u8, out var files))
            {
                if (files.ValueKind != JsonValueKind.Array)
                    throw new ExternalToolParseException(
                        $"Tool '{input.ToolName}' produced JSON that is not a reuse report "
                        + "(the 'files' member is not an array).");
                ParseFiles(input, files, findings);
            }

            return findings;
        }
    }

    private static void ParseNonCompliant(
        ExternalToolParseInput input,
        JsonElement nonCompliant,
        List<ExternalToolFinding> findings)
    {
        foreach (var property in nonCompliant.EnumerateObject())
        {
            if (findings.Count >= MaxResults)
                break;
            if (!TryDescribeCriterion(property.Name, out var ruleId, out var level, out var pathBearing))
                throw new ExternalToolParseException(
                    $"Tool '{input.ToolName}' reported an unknown reuse criterion "
                    + $"'{SingleLine(property.Name)}': the report shape changed and the "
                    + "verdict cannot be trusted.");
            if (property.Value.ValueKind != JsonValueKind.Array)
                throw new ExternalToolParseException(
                    $"Tool '{input.ToolName}' produced JSON that is not a reuse report "
                    + $"(criterion '{SingleLine(property.Name)}' is not an array).");
            foreach (var entry in property.Value.EnumerateArray())
            {
                if (findings.Count >= MaxResults)
                    break;
                if (entry.ValueKind != JsonValueKind.String)
                    continue;
                var value = entry.GetString();
                if (string.IsNullOrWhiteSpace(value))
                    continue;
                string? path = null;
                string message;
                if (pathBearing)
                {
                    path = NormalizeReportedPath(value, input.ScanRoot, input.WorkingDirectory);
                    message = DescribePathFinding(ruleId, value);
                }
                else
                {
                    message = DescribeLicenseFinding(ruleId, value);
                }

                findings.Add(new ExternalToolFinding(
                    SeverityLevel: level,
                    RuleId: ruleId,
                    Message: Truncate(message, MessageMaxChars),
                    Path: path));
            }
        }
    }

    private static void ParseFiles(
        ExternalToolParseInput input,
        JsonElement files,
        List<ExternalToolFinding> findings)
    {
        foreach (var file in files.EnumerateArray())
        {
            if (findings.Count >= MaxResults)
                break;
            if (file.ValueKind != JsonValueKind.Object)
                continue;
            var rawPath = NullIfWhiteSpace(GetString(file, "path"u8));
            var path = NormalizeReportedPath(rawPath, input.ScanRoot, input.WorkingDirectory);
            if (!file.TryGetProperty("spdx_expressions"u8, out var expressions)
                || expressions.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var expression in expressions.EnumerateArray())
            {
                if (findings.Count >= MaxResults)
                    break;
                if (expression.ValueKind != JsonValueKind.Object)
                    continue;
                if (!expression.TryGetProperty("is_valid"u8, out var isValid)
                    || isValid.ValueKind != JsonValueKind.False)
                    continue;
                var value = NullIfWhiteSpace(GetString(expression, "value"u8)) ?? "(unknown expression)";
                findings.Add(new ExternalToolFinding(
                    SeverityLevel: InvalidSpdxExpressionLevel,
                    RuleId: InvalidSpdxExpressionRuleId,
                    Message: Truncate(
                        $"File '{rawPath ?? "(unknown file)"}' contains an invalid SPDX license expression '{value}'.",
                        MessageMaxChars),
                    Path: path));
            }
        }
    }

    private static bool TryDescribeCriterion(
        string name,
        out string ruleId,
        out string level,
        out bool pathBearing)
    {
        (ruleId, level, pathBearing) = name switch
        {
            "bad_licenses" => (BadLicensesRuleId, BadLicensesLevel, false),
            "deprecated_licenses" => (DeprecatedLicensesRuleId, DeprecatedLicensesLevel, false),
            "licenses_without_extension" => (LicensesWithoutExtensionRuleId, LicensesWithoutExtensionLevel, false),
            "missing_licenses" => (MissingLicensesRuleId, MissingLicensesLevel, false),
            "unused_licenses" => (UnusedLicensesRuleId, UnusedLicensesLevel, false),
            "read_errors" => (ReadErrorsRuleId, ReadErrorsLevel, true),
            "missing_copyright_info" => (MissingCopyrightInfoRuleId, MissingCopyrightInfoLevel, true),
            "missing_licensing_info" => (MissingLicensingInfoRuleId, MissingLicensingInfoLevel, true),
            _ => (string.Empty, string.Empty, false),
        };
        return ruleId.Length > 0;
    }

    private static string DescribeLicenseFinding(string ruleId, string license)
        => ruleId switch
        {
            BadLicensesRuleId =>
                $"License '{license}' is not a valid SPDX license identifier (and does not start with 'LicenseRef-').",
            DeprecatedLicensesRuleId =>
                $"License '{license}' has been deprecated by SPDX; migrate to its successor identifier.",
            LicensesWithoutExtensionRuleId =>
                $"License text '{license}' in LICENSES/ has no file extension; rename it with a '.txt' extension.",
            MissingLicensesRuleId =>
                $"License '{license}' is referenced by project files but has no license text in LICENSES/.",
            UnusedLicensesRuleId =>
                $"License '{license}' has a text in LICENSES/ but no project file refers to it.",
            _ => $"License '{license}' was reported under '{ruleId}'.",
        };

    private static string DescribePathFinding(string ruleId, string path)
        => ruleId switch
        {
            ReadErrorsRuleId =>
                $"File '{path}' could not be read, so its licensing information could not be verified.",
            MissingCopyrightInfoRuleId =>
                $"File '{path}' has no copyright information (no SPDX-FileCopyrightText notice).",
            MissingLicensingInfoRuleId =>
                $"File '{path}' has no licensing information (no SPDX-License-Identifier).",
            _ => $"File '{path}' was reported under '{ruleId}'.",
        };
}
