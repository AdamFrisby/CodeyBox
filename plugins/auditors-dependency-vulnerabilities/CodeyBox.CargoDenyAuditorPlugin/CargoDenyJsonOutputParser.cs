using System.Text;
using System.Text.Json;
using CodeyBox.PluginSdk.Tools;

namespace CodeyBox.CargoDenyAuditorPlugin;

/// <summary>
/// Parses cargo-deny's <c>--format json</c> stream — newline-delimited JSON
/// records on <b>stderr</b> — into <see cref="ExternalToolFinding"/> records.
/// In JSON mode cargo-deny emits three record kinds to stderr:
/// <c>{"type":"log",…}</c> for log messages,
/// <c>{"type":"diagnostic","fields":{severity,code?,message,labels?,notes?,
/// graphs?,advisory?}}</c> for findings, and a terminal
/// <c>{"type":"summary","fields":{advisories?,bans?,licenses?,sources?}}</c>
/// written by <c>print_stats</c> only when <c>check</c> runs to completion.
/// stdout carries nothing (except under <c>--audit-compatible-output</c>,
/// which this auditor never passes).
///
/// <para>The summary record is the run-completed discriminator, not the exit
/// code: cargo-deny's check exit is a bitset of checks with errors
/// (advisories 0x1, bans 0x2, licenses 0x4, sources 0x8), but every hard
/// failure — a bad flag, an unparseable <c>deny.toml</c>, a failed
/// <c>cargo metadata</c>, an unreachable advisory database — also exits
/// <c>1</c> via <c>anyhow</c> after emitting only a <c>"log"</c> ERROR record.
/// A missing summary therefore throws
/// <see cref="ExternalToolParseException"/>, which the base reports as
/// infrastructure, never as a pass. Diagnostics cannot precede a bail-out —
/// they are emitted only from the final print phase — so a completed stream
/// always ends with its summary.</para>
///
/// <para>cargo-deny's JSON labels carry <c>line</c>, <c>column</c>, and
/// <c>span</c> (the matched source text) but never the file name — the
/// internal file-id table is not serialized — so findings cannot carry a
/// trustworthy <c>Path</c>/<c>Line</c>. Label detail (line, column, span
/// text, message) is folded into the finding message instead, bounded.</para>
///
/// <para>The raw <c>code</c> becomes the rule id (<c>cargo-deny/&lt;code&gt;</c>,
/// e.g. <c>cargo-deny/vulnerability</c>, <c>cargo-deny/unlicensed</c>) so
/// <c>IncludedRules</c>/<c>ExcludedRules</c> can select by lint; the raw
/// <c>severity</c> token is carried for the auditor's declared
/// <see cref="ExternalToolSeverityMapping"/> — tool vocabulary never reaches
/// findings unmapped.</para>
/// </summary>
internal sealed class CargoDenyJsonOutputParser : IExternalToolOutputParser
{
    /// <summary>Rule id prefix for diagnostics that carry a lint code.</summary>
    internal const string RuleIdPrefix = "cargo-deny/";

    /// <summary>Rule id for diagnostics with no lint code.</summary>
    internal const string FallbackRuleId = "cargo-deny/diagnostic";

    // Same per-document result bound the shared SARIF parser applies.
    private const int MaxResults = SarifToolOutputParser.DefaultMaxResults;

    // A diagnostic can carry a label per affected span and arbitrary notes;
    // bound how many are folded into the message so one diagnostic cannot
    // produce an unbounded finding description.
    private const int MaxLabelsInMessage = 8;
    private const int MaxNotesInMessage = 4;
    private const int MaxSpanChars = 200;

    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Stderr))
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced no JSON stream on stderr — "
                + "cargo-deny's --format json diagnostics, log records, and summary are all "
                + "written to stderr; an empty stream means the run did not complete.");

        var findings = new List<ExternalToolFinding>();
        var sawSummary = false;
        var lastIsSummary = false;

        foreach (var rawLine in input.Stderr.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                // Not a JSON record — a panic dump, a truncated final line, or
                // foreign output. Tolerated here; the summary gate below is
                // what decides whether the run completed. Any such trailing
                // line means the summary was not the final record.
                lastIsSummary = false;
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    lastIsSummary = false;
                    continue;
                }
                var type = GetString(root, "type"u8);
                if (string.Equals(type, "diagnostic", StringComparison.Ordinal)
                    && root.TryGetProperty("fields"u8, out var fields)
                    && fields.ValueKind == JsonValueKind.Object)
                {
                    if (findings.Count < MaxResults)
                        findings.Add(ParseDiagnostic(fields));
                    lastIsSummary = false;
                }
                else if (string.Equals(type, "summary", StringComparison.Ordinal))
                {
                    sawSummary = true;
                    lastIsSummary = true;
                }
                else
                {
                    // "log" records and unknown types carry no findings.
                    lastIsSummary = false;
                }
            }
        }

        if (!sawSummary || !lastIsSummary)
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced no 'summary' record — cargo-deny writes the "
                + "summary only when the check run completes, so its absence means a run failure "
                + "(bad arguments, unparseable deny.toml, failed cargo metadata, unreachable "
                + "advisory database) or truncated output, not a verdict.");

        return findings;
    }

    private static ExternalToolFinding ParseDiagnostic(JsonElement fields)
    {
        var severity = NullIfWhiteSpace(GetString(fields, "severity"u8));
        var code = NullIfWhiteSpace(GetString(fields, "code"u8));
        var message = NullIfWhiteSpace(GetString(fields, "message"u8)) ?? "(no message)";

        var builder = new StringBuilder(message);

        // Advisory diagnostics embed the full advisory record; surface its id
        // (e.g. RUSTSEC-2024-0001) so the finding names the actual advisory.
        if (fields.TryGetProperty("advisory"u8, out var advisory)
            && advisory.ValueKind == JsonValueKind.Object
            && NullIfWhiteSpace(GetString(advisory, "id"u8)) is { } advisoryId)
        {
            builder.Append("; advisory ").Append(advisoryId);
            if (NullIfWhiteSpace(GetString(advisory, "title"u8)) is { } advisoryTitle)
                builder.Append(" (").Append(advisoryTitle).Append(')');
        }

        if (fields.TryGetProperty("labels"u8, out var labels)
            && labels.ValueKind == JsonValueKind.Array)
        {
            AppendBoundedArray(
                builder,
                labels,
                MaxLabelsInMessage,
                static element => element.ValueKind == JsonValueKind.Object
                    ? BuildLabelDetail(element)
                    : null,
                static detail => "; " + detail,
                static omitted => "; +" + omitted + " more label(s)");
        }

        if (fields.TryGetProperty("notes"u8, out var notes)
            && notes.ValueKind == JsonValueKind.Array)
        {
            AppendBoundedArray(
                builder,
                notes,
                MaxNotesInMessage,
                static element => element.ValueKind == JsonValueKind.String
                    && NullIfWhiteSpace(element.GetString()) is { } text
                    ? "note: " + text
                    : null,
                static detail => "; " + detail,
                static omitted => "; +" + omitted + " more note(s)");
        }

        return new ExternalToolFinding(
            SeverityLevel: severity,
            RuleId: code is not null ? RuleIdPrefix + code : FallbackRuleId,
            Message: builder.ToString(),
            Path: null,
            Line: null);
    }

    private static void AppendBoundedArray(
        StringBuilder builder,
        JsonElement array,
        int cap,
        Func<JsonElement, string?> render,
        Func<string, string> prefix,
        Func<int, string> remainder)
    {
        var appended = 0;
        var omitted = 0;
        foreach (var element in array.EnumerateArray())
        {
            var detail = render(element);
            if (detail is null)
                continue;
            if (appended >= cap)
            {
                omitted++;
                continue;
            }
            builder.Append(prefix(detail));
            appended++;
        }
        if (omitted > 0)
            builder.Append(remainder(omitted));
    }

    private static string? BuildLabelDetail(JsonElement label)
    {
        var message = NullIfWhiteSpace(GetString(label, "message"u8));
        var span = NullIfWhiteSpace(GetString(label, "span"u8));
        var line = ReadPositiveInt(label, "line"u8);
        var column = ReadPositiveInt(label, "column"u8);

        if (message is null && span is null && line is null)
            return null;

        var builder = new StringBuilder();
        if (line is not null)
        {
            builder.Append("line ").Append(line.Value);
            if (column is not null)
                builder.Append(", column ").Append(column.Value);
            builder.Append(": ");
        }
        if (message is not null)
            builder.Append(message);
        if (span is not null)
        {
            if (builder.Length > 0)
                builder.Append(' ');
            builder.Append("[span: ")
                .Append(span.Length > MaxSpanChars ? span[..MaxSpanChars] + "…" : span)
                .Append(']');
        }
        return builder.ToString();
    }

    private static int? ReadPositiveInt(JsonElement element, ReadOnlySpan<byte> name)
        => element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var parsed)
            && parsed > 0
            ? parsed
            : null;

    private static string? GetString(JsonElement element, ReadOnlySpan<byte> name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}
