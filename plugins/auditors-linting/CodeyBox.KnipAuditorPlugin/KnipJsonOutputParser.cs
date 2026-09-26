using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using CodeyBox.PluginSdk.Tools;

namespace CodeyBox.KnipAuditorPlugin;

/// <summary>
/// Parses knip's built-in <c>--reporter json</c> report —
/// <c>{ "issues": [ { "file", "&lt;issueType&gt;": [ … ] } ] }</c> — into
/// <see cref="ExternalToolFinding"/> records. Verified against knip v6.38.0:
/// one object per affected file, with a key per enabled issue type; empty
/// arrays mean "none of this type in this file". Item objects carry
/// <c>name</c> and optional <c>line</c>/<c>col</c>/<c>pos</c>/<c>namespace</c>.
/// <c>duplicates</c> and <c>cycles</c> nest one array per group.
///
/// <para>The JSON reporter does not emit a per-item severity. Each finding's
/// tool level is therefore knip's documented default rule for that issue
/// type — <c>error</c> for unused files/exports/dependencies (and every
/// other type except <c>cycles</c>), <c>warn</c> for circular dependencies —
/// unless an item actually carries a <c>severity</c> string (honoured so a
/// later reporter shape is not silently dropped).
/// <see cref="ExternalToolAuditorBase"/> then maps those tokens through the
/// auditor's declared <see cref="ExternalToolSeverityMapping"/>; raw levels
/// never reach findings.</para>
///
/// <para>Malformed output — empty stdout, non-JSON, or a JSON document
/// without an <c>issues</c> array (usage text, a help dump, a config-load
/// hint) — throws <see cref="ExternalToolParseException"/>, which the base
/// reports as infrastructure, never as a pass.</para>
/// </summary>
internal sealed class KnipJsonOutputParser : IExternalToolOutputParser
{
    // Same per-document result bound the shared SARIF parser applies.
    private const int MaxResults = SarifToolOutputParser.DefaultMaxResults;
    private const int MessageMaxChars = 512;

    internal const string FilesIssueType = "files";
    internal const string ExportsIssueType = "exports";
    internal const string DependenciesIssueType = "dependencies";
    internal const string CyclesIssueType = "cycles";

    /// <summary>
    /// knip's documented default rule for circular dependencies: reported,
    /// but not counted toward a non-zero exit. Every other issue type
    /// defaults to <c>error</c>.
    /// </summary>
    internal const string WarnSeverity = "warn";

    /// <summary>knip's documented default rule for unused files/exports/dependencies.</summary>
    internal const string ErrorSeverity = "error";

    private static readonly FrozenSet<string> IssueTypeKeys = FrozenSet.ToFrozenSet(
        [
            FilesIssueType,
            DependenciesIssueType,
            "devDependencies",
            "optionalPeerDependencies",
            "unlisted",
            "binaries",
            "unresolved",
            ExportsIssueType,
            "nsExports",
            "types",
            "nsTypes",
            "enumMembers",
            "namespaceMembers",
            "duplicates",
            "catalog",
            "catalogReferences",
            CyclesIssueType,
        ],
        StringComparer.Ordinal);

    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Stdout))
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced no knip JSON output on stdout.");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(input.Stdout);
        }
        catch (JsonException ex)
        {
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced output that is not valid knip JSON: {SingleLine(ex.Message)}.",
                ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("issues"u8, out var issues)
                || issues.ValueKind != JsonValueKind.Array)
            {
                throw new ExternalToolParseException(
                    $"Tool '{input.ToolName}' produced JSON that is not a knip report "
                    + "(missing the 'issues' array).");
            }

            var findings = new List<ExternalToolFinding>();
            foreach (var entry in issues.EnumerateArray())
            {
                if (findings.Count >= MaxResults)
                    break;
                if (entry.ValueKind == JsonValueKind.Object)
                    CollectEntry(entry, findings);
            }

            return findings;
        }
    }

    private static void CollectEntry(JsonElement entry, List<ExternalToolFinding> findings)
    {
        var path = NormalizeFilePath(GetString(entry, "file"u8));
        foreach (var property in entry.EnumerateObject())
        {
            if (findings.Count >= MaxResults)
                return;
            if (!IssueTypeKeys.Contains(property.Name)
                || property.Value.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var item in property.Value.EnumerateArray())
            {
                if (findings.Count >= MaxResults)
                    return;
                if (item.ValueKind == JsonValueKind.Array)
                    findings.Add(ParseGroup(property.Name, item, path));
                else if (item.ValueKind == JsonValueKind.Object)
                    findings.Add(ParseItem(property.Name, item, path));
            }
        }
    }

    private static ExternalToolFinding ParseItem(string issueType, JsonElement item, string? path)
    {
        var name = NullIfWhiteSpace(GetString(item, "name"u8));
        var ns = NullIfWhiteSpace(GetString(item, "namespace"u8));
        var severity = NullIfWhiteSpace(GetString(item, "severity"u8))
            ?? DefaultSeverityForIssueType(issueType);

        int? line = null;
        if (item.TryGetProperty("line"u8, out var lineElement)
            && lineElement.ValueKind == JsonValueKind.Number
            && lineElement.TryGetInt32(out var lineValue)
            && lineValue > 0)
            line = lineValue;

        return new ExternalToolFinding(
            SeverityLevel: severity,
            RuleId: issueType,
            Message: BuildItemMessage(issueType, name, ns),
            Path: path,
            Line: line);
    }

    private static ExternalToolFinding ParseGroup(string issueType, JsonElement group, string? path)
    {
        var names = new List<string>();
        int? line = null;
        string? severity = null;
        foreach (var member in group.EnumerateArray())
        {
            if (member.ValueKind != JsonValueKind.Object)
                continue;
            var name = NullIfWhiteSpace(GetString(member, "name"u8));
            if (name is not null)
                names.Add(name);
            severity ??= NullIfWhiteSpace(GetString(member, "severity"u8));
            if (line is null
                && member.TryGetProperty("line"u8, out var lineElement)
                && lineElement.ValueKind == JsonValueKind.Number
                && lineElement.TryGetInt32(out var lineValue)
                && lineValue > 0)
                line = lineValue;
        }

        return new ExternalToolFinding(
            SeverityLevel: severity ?? DefaultSeverityForIssueType(issueType),
            RuleId: issueType,
            Message: BuildGroupMessage(issueType, names),
            Path: path,
            Line: line);
    }

    /// <summary>
    /// knip's documented default <c>rules</c> values: <c>cycles</c> is
    /// <c>warn</c>; every other issue type is <c>error</c>.
    /// </summary>
    internal static string DefaultSeverityForIssueType(string issueType)
        => string.Equals(issueType, CyclesIssueType, StringComparison.Ordinal)
            ? WarnSeverity
            : ErrorSeverity;

    private static string BuildItemMessage(string issueType, string? name, string? ns)
    {
        var prefix = IssueTypeTitle(issueType);
        if (string.IsNullOrEmpty(name))
            return prefix;
        var labelled = string.IsNullOrEmpty(ns) ? name : ns + "." + name;
        return Truncate($"{prefix} '{labelled}'", MessageMaxChars);
    }

    private static string BuildGroupMessage(string issueType, List<string> names)
    {
        var prefix = IssueTypeTitle(issueType);
        if (names.Count == 0)
            return prefix;
        var separator = string.Equals(issueType, CyclesIssueType, StringComparison.Ordinal)
            ? " → "
            : ", ";
        return Truncate($"{prefix}: {string.Join(separator, names)}", MessageMaxChars);
    }

    private static string IssueTypeTitle(string issueType)
        => issueType switch
        {
            FilesIssueType => "Unused file",
            DependenciesIssueType => "Unused dependency",
            "devDependencies" => "Unused devDependency",
            "optionalPeerDependencies" => "Referenced optional peerDependency",
            "unlisted" => "Unlisted dependency",
            "binaries" => "Unlisted binary",
            "unresolved" => "Unresolved import",
            ExportsIssueType => "Unused export",
            "nsExports" => "Unused namespace export",
            "types" => "Unused exported type",
            "nsTypes" => "Unused namespace type",
            "enumMembers" => "Unused enum member",
            "namespaceMembers" => "Unused namespace member",
            "duplicates" => "Duplicate export",
            "catalog" => "Unused catalog entry",
            "catalogReferences" => "Unresolved catalog reference",
            CyclesIssueType => "Circular dependency",
            _ => "knip finding",
        };

    private static string? NormalizeFilePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var path = raw.Trim().Replace('\\', '/');
        return path.StartsWith("./", StringComparison.Ordinal) ? path[2..] : path;
    }

    private static string? GetString(JsonElement element, ReadOnlySpan<byte> name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string Truncate(string value, int maxChars)
        => value.Length <= maxChars ? value : value[..maxChars] + "...";

    private static string SingleLine(string message)
    {
        var builder = new StringBuilder(message.Length);
        foreach (var c in message)
            builder.Append(char.IsControl(c) ? ' ' : c);
        return builder.ToString().Trim();
    }
}
