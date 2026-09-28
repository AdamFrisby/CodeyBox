using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeyBox.PluginSdk.Tools;

namespace CodeyBox.OasdiffAuditorPlugin;

/// <summary>
/// Parses the <c>oasdiff breaking-files --format json</c> report on stdout
/// into <see cref="ExternalToolFinding"/> records (verified against oasdiff
/// v1.32.x). The command renders its output once per spec argument —
/// <c>=== &lt;path&gt; ===</c> headers are unconditional, format
/// independent — so stdout is a sequence of named sections whose body is
/// either the single-line JSON array of change objects
/// (<c>[{"id":…,"text":…,"level":3,"operation":…,"path":…,
/// "baseSource":{"file","line",…},"revisionSource":{…},…}]</c>, or
/// <c>[]</c> when clean) or the literal notice
/// <c>new file, not in base ref, skipped</c> for a spec with no baseline.
///
/// <para>Each change becomes one finding: <c>id</c> is the rule id (e.g.
/// <c>api-path-removed-without-deprecation</c>), the numeric
/// <c>level</c> is translated to oasdiff's own level token
/// (<c>ERR</c>/<c>WARN</c>/<c>INFO</c>) for the auditor's declared
/// <see cref="ExternalToolSeverityMapping"/> — raw numbers never reach
/// findings — and the location prefers the change's
/// <c>revisionSource</c> file/line (where the new spec carries the change),
/// falls back to <c>baseSource</c>, then to the section's spec path. The
/// message carries the operation, API path, description text, and
/// comment.</para>
///
/// <para>Unrecognised output fails closed: content before the first
/// section header, an empty section body, a body that is neither the skip
/// notice nor a JSON array, malformed JSON, or a non-array document all
/// throw <see cref="ExternalToolParseException"/> — infrastructure, never a
/// pass. The exit code and the report cross-check each other: exit
/// <c>1</c> asserts at least one finding at or above the run's
/// <c>--fail-on</c> level, so a 1 exit whose report carries no change
/// contradicts the contract (suppressed output, foreign build, truncated
/// report) and is likewise infrastructure. Exit <c>0</c> sections may still
/// carry changes when an operator raised <c>--fail-on</c> past them, so
/// they are parsed normally rather than asserted empty.</para>
/// </summary>
internal sealed class OasdiffBreakingFilesReportParser : IExternalToolOutputParser
{
    // Same per-report result bound the shared SARIF parser applies.
    private const int MaxResults = SarifToolOutputParser.DefaultMaxResults;

    private const string SkippedNotice = "new file, not in base ref, skipped";

    // breaking-files prints "=== <path> ===" before each spec's rendered
    // report. Greedy inner match keeps a path that itself contains " === ".
    private static readonly Regex SectionHeader = new(
        @"^=== (.+) ===\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var findings = new List<ExternalToolFinding>();
        string? sectionPath = null;
        var sectionBody = new StringBuilder();
        var sawSection = false;

        foreach (var rawLine in SplitLines(input.Stdout))
        {
            var header = SectionHeader.Match(rawLine);
            if (header.Success)
            {
                FlushSection(input.ToolName, sectionPath, sectionBody, findings);
                sectionPath = header.Groups[1].Value.Trim();
                sectionBody.Clear();
                sawSection = true;
                continue;
            }

            if (!sawSection && rawLine.Trim().Length > 0)
                throw new ExternalToolParseException(
                    $"Tool '{input.ToolName}' produced output before the first '=== <spec> ===' "
                    + "section header — not a breaking-files report.");
            sectionBody.AppendLine(rawLine);
        }

        FlushSection(input.ToolName, sectionPath, sectionBody, findings);

        if (!sawSection)
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced no '=== <spec> ===' report sections — not a "
                + "breaking-files report.");

        if (input.ExitCode == OasdiffAuditor.BreakingChangesFoundExitCode && findings.Count == 0)
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' exited {OasdiffAuditor.BreakingChangesFoundExitCode} "
                + "(--fail-on tripped) but its report carried no changes — the exit contract "
                + "contradicts the output.");

        return findings;
    }

    private static void FlushSection(
        string tool,
        string? sectionPath,
        StringBuilder sectionBody,
        List<ExternalToolFinding> findings)
    {
        if (sectionPath is null)
            return;

        var body = sectionBody.ToString().Trim();
        if (body.Length == 0)
            throw new ExternalToolParseException(
                $"Tool '{tool}' emitted section '{Truncate(sectionPath)}' with no report body.");

        if (body.Equals(SkippedNotice, StringComparison.Ordinal))
            return; // newly added spec — nothing in the baseline to break

        if (body[0] != '[')
            throw new ExternalToolParseException(
                $"Tool '{tool}' emitted section '{Truncate(sectionPath)}' whose body is not the "
                + $"expected JSON changes array: '{Truncate(body)}'.");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new ExternalToolParseException(
                $"Tool '{tool}' emitted section '{Truncate(sectionPath)}' with malformed JSON: "
                + SingleLine(ex.Message) + ".",
                ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new ExternalToolParseException(
                    $"Tool '{tool}' emitted section '{Truncate(sectionPath)}' whose JSON document "
                    + "is not a changes array.");

            foreach (var change in document.RootElement.EnumerateArray())
            {
                if (findings.Count >= MaxResults)
                    return;
                if (change.ValueKind != JsonValueKind.Object)
                    continue;
                findings.Add(ParseChange(change, sectionPath));
            }
        }
    }

    private static ExternalToolFinding ParseChange(JsonElement change, string sectionPath)
    {
        var ruleId = GetString(change, "id"u8);
        var text = GetString(change, "text"u8) ?? "(no description)";
        var comment = GetString(change, "comment"u8);
        var operation = GetString(change, "operation"u8);
        var apiPath = GetString(change, "path"u8);

        var message = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(operation) || !string.IsNullOrWhiteSpace(apiPath))
        {
            message.Append(operation?.Trim());
            if (!string.IsNullOrWhiteSpace(operation) && !string.IsNullOrWhiteSpace(apiPath))
                message.Append(' ');
            message.Append(apiPath?.Trim());
            message.Append(" — ");
        }
        message.Append(text.Trim());
        if (!string.IsNullOrWhiteSpace(comment))
            message.Append('\n').Append(comment.Trim());

        var (file, line) = ChangeLocation(change);
        return new ExternalToolFinding(
            SeverityLevel: LevelToken(change),
            RuleId: string.IsNullOrWhiteSpace(ruleId) ? null : ruleId.Trim(),
            Message: message.ToString(),
            Path: file ?? sectionPath,
            Line: line);
    }

    /// <summary>
    /// Location preference: <c>revisionSource</c> (where the new spec
    /// carries the change) first, then <c>baseSource</c>. Either may point
    /// into a relative-$ref'd sibling file — legitimately, since in-repo
    /// refs resolve under <c>--allow-external-refs=false</c>.
    /// </summary>
    private static (string? File, int? Line) ChangeLocation(JsonElement change)
    {
        var revision = SourceLocation(change, "revisionSource"u8);
        if (revision.File is not null || revision.Line is not null)
            return revision;
        return SourceLocation(change, "baseSource"u8);
    }

    private static (string? File, int? Line) SourceLocation(JsonElement change, ReadOnlySpan<byte> key)
    {
        if (!change.TryGetProperty(key, out var source) || source.ValueKind != JsonValueKind.Object)
            return (null, null);
        var file = GetString(source, "file"u8);
        int? line = null;
        if (source.TryGetProperty("line"u8, out var lineElement)
            && lineElement.ValueKind == JsonValueKind.Number
            && lineElement.TryGetInt32(out var lineValue)
            && lineValue > 0)
            line = lineValue;
        if (string.IsNullOrWhiteSpace(file) && line is null)
            return (null, null);
        return (string.IsNullOrWhiteSpace(file) ? null : file.Trim(), line);
    }

    /// <summary>
    /// Translates oasdiff's numeric level (3=ERR, 2=WARN, 1=INFO — the
    /// JSON contract; the upstream Level type has no MarshalJSON) into the
    /// tool's own level token for the declared severity mapping. A
    /// string-valued level passes through (it already is the token); any
    /// other shape yields null so the mapping's declared default decides.
    /// </summary>
    private static string? LevelToken(JsonElement change)
    {
        if (!change.TryGetProperty("level"u8, out var level))
            return null;
        if (level.ValueKind == JsonValueKind.Number && level.TryGetInt32(out var numeric))
            return numeric switch
            {
                3 => "ERR",
                2 => "WARN",
                1 => "INFO",
                _ => null,
            };
        return ExternalToolJsonHelpers.CoerceString(level);
    }

    private static string? GetString(JsonElement element, ReadOnlySpan<byte> utf8Name)
        => ExternalToolJsonHelpers.GetString(element, utf8Name);

    private static string SingleLine(string message)
        => ExternalToolJsonHelpers.SingleLine(message);

    private static string Truncate(string? value)
        => ExternalToolJsonHelpers.Truncate(value ?? string.Empty, 120);

    private static IEnumerable<string> SplitLines(string text)
    {
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
            yield return line;
    }
}
