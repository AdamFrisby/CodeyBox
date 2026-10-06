using System.Text.Json;
using CodeyBox.PluginSdk.Tools;
using static CodeyBox.PluginSdk.Tools.ExternalToolJsonHelpers;

namespace CodeyBox.PlutoAuditorPlugin;

/// <summary>
/// Parses <c>pluto detect-files -o json</c> output into
/// <see cref="ExternalToolFinding"/> records. The verified report shape
/// (FairwindsOps/pluto <c>pkg/api/output.go</c> + <c>versions.go</c>) is
/// <c>{ "items": [ { "name", "filePath", "namespace", "api": { "version",
/// "kind", "deprecated-in", "removed-in", "replacement-api",
/// "replacement-available-in", "component" }, "deprecated", "removed",
/// "replacementAvailable" } ], "target-versions": { "k8s": "vX.Y.Z" } }</c>.
///
/// <para>Each listed item is one object using a known-deprecated apiVersion:
/// pluto's <c>FilterOutput</c> already drops every clean object before
/// marshalling, so any item that survives to the report is actionable. The
/// item booleans (<c>deprecated</c>, <c>removed</c>,
/// <c>replacementAvailable</c>) are computed by pluto against the requested
/// target versions and drive the classification:</para>
///
/// <list type="bullet">
/// <item><c>removed</c> → rule <c>pluto/removed</c>, level
/// <c>removed</c>: the apiVersion no longer exists on the target — a
/// candidate deployment carrying it fails at apply time.</item>
/// <item>deprecated with an available replacement → rule
/// <c>pluto/deprecated</c>, level <c>deprecated</c>: still served on the
/// target, but slated for removal.</item>
/// <item>deprecated without an available replacement → rule
/// <c>pluto/replacement-unavailable</c>, level
/// <c>replacement-unavailable</c>: deprecated and no upgrade path exists on
/// the target. The raw level tokens are mapped by the auditor's declared
/// <see cref="ExternalToolSeverityMapping"/> — raw tool vocabulary never
/// reaches findings.</item>
/// </list>
///
/// <para>An item whose flags cannot be determined (a report shape this parser
/// predates) is still reported — rule <c>pluto/api</c>, level
/// <c>unknown</c> — so an unfamiliar pluto build fails the audit instead of
/// silently passing. An item explicitly marked neither deprecated nor
/// removed is skipped: it carries no problem to report.</para>
///
/// <para>Empty findings are only accepted with proof the scan ran: the
/// <c>target-versions</c> block must be present and non-empty. pluto omits
/// <c>items</c> entirely on a clean scan (<c>omitempty</c> over a nil slice),
/// so a missing <c>items</c> array means "nothing deprecated" — but a missing
/// or empty <c>target-versions</c> block means the stdout is not a completed
/// <c>detect-files</c> report (a finder error, a flag error, or truncation)
/// and throws <see cref="ExternalToolParseException"/>, which the base
/// reports as infrastructure, never as a pass.</para>
/// </summary>
internal sealed class PlutoJsonOutputParser : IExternalToolOutputParser
{
    /// <summary>Rule id for apiVersions removed on the target version.</summary>
    internal const string RemovedRuleId = "pluto/removed";

    /// <summary>Rule id for apiVersions deprecated (but still served) on the target version.</summary>
    internal const string DeprecatedRuleId = "pluto/deprecated";

    /// <summary>Rule id for deprecated apiVersions with no replacement available on the target version.</summary>
    internal const string ReplacementUnavailableRuleId = "pluto/replacement-unavailable";

    /// <summary>Rule id fallback for items whose deprecation flags cannot be determined.</summary>
    internal const string UnknownRuleId = "pluto/api";

    /// <summary>Severity level for removed apiVersions.</summary>
    internal const string RemovedLevel = "removed";

    /// <summary>Severity level for deprecated apiVersions with an available replacement.</summary>
    internal const string DeprecatedLevel = "deprecated";

    /// <summary>Severity level for deprecated apiVersions without an available replacement.</summary>
    internal const string ReplacementUnavailableLevel = "replacement-unavailable";

    /// <summary>Severity level for items whose flags cannot be determined.</summary>
    internal const string UnknownLevel = "unknown";

    private const int MaxResults = SarifToolOutputParser.DefaultMaxResults;

    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Stdout))
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced no pluto JSON report on stdout.");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(input.Stdout);
        }
        catch (JsonException ex)
        {
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced output that is not valid pluto JSON: {ToolOutputText.SingleLine(ex.Message)}.",
                ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new ExternalToolParseException(
                    $"Tool '{input.ToolName}' produced a JSON value without a pluto report object.");

            var targetVersions = ReadTargetVersions(document.RootElement, input.ToolName);

            // Clean scans omit "items" entirely (omitempty over a nil
            // slice): missing means "nothing deprecated", not "no report".
            // A present-but-wrong-typed "items" is a foreign shape, not an
            // empty scan — fail closed.
            var findings = new List<ExternalToolFinding>();
            if (document.RootElement.TryGetProperty("items"u8, out var items))
            {
                if (items.ValueKind != JsonValueKind.Array)
                    throw new ExternalToolParseException(
                        $"Tool '{input.ToolName}' produced a pluto report whose 'items' is not an array.");
                foreach (var item in items.EnumerateArray())
                {
                    if (findings.Count >= MaxResults)
                        break;
                    if (item.ValueKind != JsonValueKind.Object)
                        continue;
                    var finding = ParseItem(item, targetVersions, input);
                    if (finding is not null)
                        findings.Add(finding);
                }
            }

            return findings;
        }
    }

    private static string ReadTargetVersions(JsonElement root, string toolName)
    {
        foreach (var key in new[] { "target-versions", "target_versions", "targetVersions" })
        {
            if (!root.TryGetProperty(key, out var block) || block.ValueKind != JsonValueKind.Object)
                continue;
            var entries = new List<string>();
            foreach (var property in block.EnumerateObject())
            {
                var value = NullIfWhiteSpace(CoerceString(property.Value));
                if (value is not null)
                    entries.Add($"{property.Name}={value}");
            }
            if (entries.Count > 0)
                return string.Join(", ", entries);
        }

        throw new ExternalToolParseException(
            $"Tool '{toolName}' produced a pluto report without a 'target-versions' block — "
            + "the scan's target version cannot be confirmed, so an empty result is not accepted as clean.");
    }

    private static ExternalToolFinding? ParseItem(
        JsonElement item, string targetVersions, ExternalToolParseInput input)
    {
        var api = item.TryGetProperty("api"u8, out var nested) && nested.ValueKind == JsonValueKind.Object
            ? nested
            : (JsonElement?)null;

        string? Field(string name, string? nestedName = null)
        {
            if (item.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String
                && NullIfWhiteSpace(value.GetString()) is { } direct)
                return direct;
            if (nestedName is not null
                && api.HasValue
                && api.Value.TryGetProperty(nestedName, out var nestedValue)
                && nestedValue.ValueKind == JsonValueKind.String)
                return NullIfWhiteSpace(nestedValue.GetString());
            return null;
        }

        var kind = Field("kind", "kind");
        var apiVersion = Field("version", "version")
            ?? Field("apiVersion", "version")
            ?? Field("api-version", "version");
        var name = Field("name");
        var fileNamespace = Field("namespace");
        var rawPath = Field("filePath")
            ?? Field("file")
            ?? Field("filename");

        var deprecatedIn = Field("deprecated-in", "deprecated-in")
            ?? Field("deprecated_in", "deprecated-in");
        var removedIn = Field("removed-in", "removed-in")
            ?? Field("removed_in", "removed-in");
        var replacement = Field("replacement-api", "replacement-api")
            ?? Field("replacement_api", "replacement-api")
            ?? Field("replacement", "replacement-api");
        var replacementAvailableIn = Field(
            "replacement-available-in", "replacement-available-in");

        var deprecated = ReadBool(item, "deprecated")
            ?? (api.HasValue ? ReadBool(api.Value, "deprecated") : null);
        if (deprecated is null && deprecatedIn is not null)
            deprecated = true;
        var removed = ReadBool(item, "removed")
            ?? (api.HasValue ? ReadBool(api.Value, "removed") : null);
        if (removed is null && removedIn is not null)
            removed = true;
        var replacementAvailable = ReadBool(item, "replacementAvailable")
            ?? ReadBool(item, "replacement-available")
            ?? ReadBool(item, "replacement_available")
            ?? (api.HasValue ? ReadBool(api.Value, "replacementAvailable") : null);

        string level;
        string ruleId;
        if (removed == true)
        {
            level = RemovedLevel;
            ruleId = RemovedRuleId;
        }
        else if (deprecated == true)
        {
            // An explicit "available" verdict wins; otherwise a named
            // replacement implies an upgrade path and an empty one implies
            // none — matching pluto's own isReplacementAvailableIn semantics
            // closely enough to keep the finding honest when the boolean is
            // absent from a foreign report shape.
            var available = replacementAvailable ?? (replacement is not null);
            if (!available)
            {
                level = ReplacementUnavailableLevel;
                ruleId = ReplacementUnavailableRuleId;
            }
            else
            {
                level = DeprecatedLevel;
                ruleId = DeprecatedRuleId;
            }
        }
        else if (deprecated == false && removed == false)
        {
            return null;
        }
        else
        {
            level = UnknownLevel;
            ruleId = UnknownRuleId;
        }

        return new ExternalToolFinding(
            SeverityLevel: level,
            RuleId: ruleId,
            Message: BuildMessage(
                kind, apiVersion, name, fileNamespace, level, targetVersions,
                deprecatedIn, removedIn, replacement, replacementAvailableIn,
                unknownShape: level == UnknownLevel),
            Path: NormalizeReportedPath(rawPath, input.ScanRoot, input.WorkingDirectory),
            Line: null);
    }

    private static string BuildMessage(
        string? kind,
        string? apiVersion,
        string? name,
        string? fileNamespace,
        string level,
        string targetVersions,
        string? deprecatedIn,
        string? removedIn,
        string? replacement,
        string? replacementAvailableIn,
        bool unknownShape)
    {
        var subject = new System.Text.StringBuilder();
        if (kind is not null)
            subject.Append(kind);
        if (name is not null)
        {
            if (subject.Length > 0)
                subject.Append(' ');
            subject.Append('\'').Append(Truncate(name, 120)).Append('\'');
        }
        if (apiVersion is not null)
        {
            if (subject.Length > 0)
                subject.Append(' ');
            subject.Append("uses ").Append(Truncate(apiVersion, 120));
        }
        if (fileNamespace is not null)
            subject.Append(" in namespace '").Append(Truncate(fileNamespace, 120)).Append('\'');
        if (subject.Length == 0)
            subject.Append("Kubernetes object");
        subject.Append(": ");

        subject.Append(level switch
        {
            RemovedLevel => "apiVersion removed",
            DeprecatedLevel => "apiVersion deprecated",
            ReplacementUnavailableLevel => "apiVersion deprecated with no replacement available",
            _ => "apiVersion with unrecognized deprecation flags",
        });
        subject.Append(" on target ").Append(Truncate(targetVersions, 160)).Append('.');

        if (deprecatedIn is not null)
            subject.Append(" Deprecated in ").Append(Truncate(deprecatedIn, 32)).Append('.');
        if (removedIn is not null)
            subject.Append(" Removed in ").Append(Truncate(removedIn, 32)).Append('.');
        if (replacement is not null)
        {
            subject.Append(" Replacement: ").Append(Truncate(replacement, 120));
            if (replacementAvailableIn is not null)
                subject.Append(" (available in ").Append(Truncate(replacementAvailableIn, 32)).Append(')');
            subject.Append('.');
        }
        else if (level is ReplacementUnavailableLevel or UnknownLevel)
        {
            subject.Append(unknownShape
                ? " The report shape is unfamiliar — review the apiVersion manually."
                : " No replacement apiVersion is available on the target.");
        }

        return subject.ToString();
    }

    private static bool? ReadBool(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetInt32(out var number) => number != 0,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }
}
