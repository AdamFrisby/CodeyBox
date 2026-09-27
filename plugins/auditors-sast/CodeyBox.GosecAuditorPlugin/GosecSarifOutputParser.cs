using System.Text.Json;
using CodeyBox.PluginSdk.Tools;

namespace CodeyBox.GosecAuditorPlugin;

/// <summary>
/// SARIF parser for gosec's stdout report (<c>-stdout -verbose sarif</c>).
/// Delegates the run/result → finding shape to the shared
/// <see cref="SarifToolOutputParser"/> and adds the two things the shared
/// parser cannot express for this tool:
///
/// <para><b>Native severity recovery.</b> gosec flattens its LOW/MEDIUM/HIGH
/// severity into the SARIF <c>level</c> (LOW → <c>warning</c>, MEDIUM and
/// HIGH → <c>error</c>), so <c>level</c> alone would make MEDIUM findings
/// block like HIGH ones. The native severity survives in each rule
/// descriptor's <c>properties.tags</c> (<c>["security", "HIGH"]</c>); this
/// parser resolves <c>ruleId</c> → descriptor and substitutes the native
/// token so the auditor's declared severity map sees the tool's real
/// vocabulary. When the descriptor carries no recognizable severity the
/// finding keeps its SARIF <c>level</c>, which the map also covers.</para>
///
/// <para><b>Exit-1 discrimination and the error channel.</b> gosec exits 1
/// both for "found unsuppressed issues" and for "recorded
/// per-file/per-package analysis errors". Its SARIF report carries no
/// error detail, so the scan is invoked to write the JSON report — the
/// only format containing the <c>"Golang errors"</c> map — to stderr
/// (<c>-fmt json -out /dev/stderr</c>, logger parked at
/// <c>-log /dev/null</c>). Two failure shapes fail closed here: exit 1
/// with a valid-but-empty results array (the run recorded errors and no
/// issues), and any run whose error channel is non-empty — findings can
/// never mask a package the scan could not load, because a deliberately
/// broken package would otherwise ride a passing verdict. A missing or
/// unreadable channel fails closed too: no evidence of empty is not
/// evidence of absence. All three throw
/// <see cref="ExternalToolParseException"/> — reported by the base as
/// infrastructure, never a pass. The shared parser's result bound fails
/// closed the same way: a report exceeding
/// <see cref="SarifToolOutputParser.DefaultMaxResults"/> throws rather
/// than silently truncating, so gate-relevant results can never be pushed
/// past the cap into silence.</para>
/// </summary>
internal sealed class GosecSarifOutputParser : IExternalToolOutputParser
{
    // A scan's rule set is the ~150 built-in rules; a bound guards against a
    // malformed document carrying a huge descriptor list.
    private const int MaxRuleDescriptors = 4096;

    // The JSON ReportInfo member holding gosec's per-file analysis errors:
    // "Golang errors": { "<file>": [{ "line", "column", "error" }] }.
    private const string AnalysisErrorsProperty = "Golang errors";

    // How many failing paths to name in the failure message.
    private const int MaxErrorPathsInMessage = 3;
    private const int ErrorPathMaxChars = 160;

    private static readonly HashSet<string> SeverityTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "LOW", "MEDIUM", "HIGH",
    };

    private readonly SarifToolOutputParser _inner = new();

    /// <inheritdoc />
    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Throws on empty or malformed stdout — an exit-1 run that wrote no
        // report is an operational failure, surfaced as infrastructure.
        var findings = _inner.Parse(input);

        if (input.ExitCode == GosecAuditor.IssuesOrErrorsExitCode && findings.Count == 0)
        {
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' exited {GosecAuditor.IssuesOrErrorsExitCode} with a SARIF report "
                + "carrying no findings. gosec raises that exit for analysis errors (packages it could "
                + "not load or parse) as well as for issues — an empty report here means the scan "
                + "errored, not that the tree is clean.");
        }

        ThrowOnAnalysisErrors(input);

        var severities = ExtractRuleSeverities(input.Stdout);
        if (severities.Count == 0)
            return findings;

        var remapped = new List<ExternalToolFinding>(findings.Count);
        foreach (var finding in findings)
        {
            remapped.Add(
                finding.RuleId is not null
                && severities.TryGetValue(finding.RuleId, out var severity)
                    ? finding with { SeverityLevel = severity }
                    : finding);
        }

        return remapped;
    }

    /// <summary>
    /// Fails closed when the run's error channel — the JSON report gosec
    /// writes to stderr — records analysis errors, or when the channel
    /// cannot be read at all. A SARIF report with findings can coexist
    /// with packages gosec never analyzed (the audited repository controls
    /// loadability: a broken <c>go.mod</c>, an uncached module, a package
    /// that does not compile). Exit 1 with findings is a verdict only when
    /// the channel proves every package was analyzed; anything else is
    /// infrastructure, never a pass. On exit 0 the map is provably empty
    /// (<c>computeExitCode</c> would have raised 1), but the channel is
    /// still required readable so a broken invocation fails loudly.
    /// </summary>
    private static void ThrowOnAnalysisErrors(ExternalToolParseInput input)
    {
        if (!TryReadAnalysisErrorPaths(input.Stderr, out var errorPaths))
        {
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced a SARIF report but the analysis-error channel on "
                + "stderr was absent or unreadable — gosec reports per-package analysis failures only "
                + "in the JSON report the scan writes there, and without it a broken package could "
                + "hide behind the findings.");
        }

        if (errorPaths.Count > 0)
        {
            var named = ToolOutputText.FormatBoundedList(
                errorPaths, MaxErrorPathsInMessage, ErrorPathMaxChars);
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' recorded {errorPaths.Count} analysis error(s) alongside its "
                + $"findings — packages it could not load or parse ({named}) — so part of "
                + "the tree was never analyzed and the findings cannot stand as a verdict.");
        }
    }

    /// <summary>
    /// Reads the file paths carrying entries in the JSON report's
    /// <c>"Golang errors"</c> map written to stderr. Returns false when the
    /// stream is empty, malformed, or carries no readable map — the caller
    /// treats that as an unverifiable channel, not an empty one. A
    /// <see langword="null"/> map is never produced by a healthy run
    /// (gosec initializes the map in <c>NewReportInfo</c>), so it fails
    /// closed like every other non-object shape.
    /// </summary>
    private static bool TryReadAnalysisErrorPaths(string stderr, out IReadOnlyList<string> errorPaths)
    {
        errorPaths = [];
        if (string.IsNullOrWhiteSpace(stderr))
            return false;
        try
        {
            using var document = JsonDocument.Parse(stderr);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty(AnalysisErrorsProperty, out var errors))
                return false;
            if (errors.ValueKind != JsonValueKind.Object)
                return false;

            var paths = new List<string>();
            foreach (var entry in errors.EnumerateObject())
                paths.Add(entry.Name);
            errorPaths = paths;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Maps each declared rule id to its gosec severity token
    /// (<c>HIGH</c>/<c>MEDIUM</c>/<c>LOW</c>), recovered from
    /// <c>runs[].tool.driver.rules[].properties.tags</c>. Returns an empty
    /// map when no descriptor severities are readable — findings then keep
    /// their SARIF <c>level</c>. Never throws on descriptor oddities: a
    /// missing severity annotation loses the native-level precision and the
    /// finding keeps its stricter SARIF <c>level</c> — not a parse failure.
    /// </summary>
    private static IReadOnlyDictionary<string, string> ExtractRuleSeverities(string sarif)
    {
        var severities = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(sarif);
            if (!document.RootElement.TryGetProperty("runs"u8, out var runs)
                || runs.ValueKind != JsonValueKind.Array)
                return severities;

            foreach (var run in runs.EnumerateArray())
            {
                if (run.ValueKind != JsonValueKind.Object
                    || !run.TryGetProperty("tool"u8, out var tool)
                    || tool.ValueKind != JsonValueKind.Object
                    || !tool.TryGetProperty("driver"u8, out var driver)
                    || driver.ValueKind != JsonValueKind.Object
                    || !driver.TryGetProperty("rules"u8, out var rules)
                    || rules.ValueKind != JsonValueKind.Array)
                    continue;

                var count = 0;
                foreach (var rule in rules.EnumerateArray())
                {
                    if (++count > MaxRuleDescriptors)
                        break;
                    if (rule.ValueKind != JsonValueKind.Object
                        || !rule.TryGetProperty("id"u8, out var id)
                        || id.ValueKind != JsonValueKind.String)
                        continue;

                    var severity = SeverityOf(rule);
                    if (severity is not null)
                        severities.TryAdd(id.GetString()!, severity);
                }
            }
        }
        catch (JsonException)
        {
            // The shared parser already reported malformed JSON; severity
            // recovery is best-effort on top of it.
        }

        return severities;
    }

    /// <summary>
    /// Reads the gosec severity from a rule descriptor: the element of
    /// <c>properties.tags</c> that is a gosec severity token
    /// (<c>HIGH</c>/<c>MEDIUM</c>/<c>LOW</c>). Null when absent or
    /// unrecognised.
    /// </summary>
    private static string? SeverityOf(JsonElement rule)
    {
        if (!rule.TryGetProperty("properties"u8, out var properties)
            || properties.ValueKind != JsonValueKind.Object
            || !properties.TryGetProperty("tags"u8, out var tags)
            || tags.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var tag in tags.EnumerateArray())
        {
            if (tag.ValueKind == JsonValueKind.String
                && tag.GetString() is { } value
                && SeverityTags.Contains(value))
                return value.ToUpperInvariant();
        }

        return null;
    }
}
