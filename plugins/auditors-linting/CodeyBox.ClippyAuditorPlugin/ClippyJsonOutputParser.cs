using System.Text.Json;
using CodeyBox.PluginSdk.Tools;
using static CodeyBox.PluginSdk.Tools.ExternalToolJsonHelpers;

namespace CodeyBox.ClippyAuditorPlugin;

/// <summary>
/// Parses <c>cargo-clippy</c>'s <c>--message-format=json</c> report — one JSON
/// object per line on stdout, e.g.
/// <c>{"reason":"compiler-message","message":{"message":"…",
/// "code":{"code":"clippy::unnecessary_literal_unwrap"},"level":"warning",
/// "spans":[{"file_name":"src/main.rs","line_start":6,…}]}}</c> — into
/// <see cref="ExternalToolFinding"/> records. Verified against clippy 0.1.99
/// (cargo 1.99.0): the scan emits <c>compiler-artifact</c> records for built
/// units, <c>compiler-message</c> records for diagnostics, and a terminal
/// <c>build-finished</c> record; only <c>compiler-message</c> records with at
/// least one span become findings.
///
/// <para>Severity is kept in the tool's own vocabulary — the diagnostic's
/// <c>level</c> string (<c>warning</c>, <c>error</c>, …) — and
/// <see cref="ExternalToolAuditorBase"/> maps it through the auditor's
/// declared <see cref="ExternalToolSeverityMapping"/>; raw levels never reach
/// findings. The rule id is the diagnostic's <c>code.code</c> (e.g.
/// <c>clippy::needless_return</c>, <c>E0308</c>); the location is the first
/// primary span's <c>file_name</c> plus <c>line_start</c> when positive —
/// resolved through the shared
/// <see cref="ExternalToolJsonHelpers.NormalizeReportedPath"/> policy
/// against <see cref="ExternalToolParseInput.ScanRoot"/> (the directory the
/// scan actually ran in): clippy emits paths relative to the process working
/// directory, which the auditor sets to the audited worktree root, so
/// in-tree paths arrive repository-relative while absolute or
/// traversal-carrying span paths the audited repository can influence
/// (Cargo <c>#[path]</c> attributes, symlinks, out-of-tree manifests) are
/// collapsed and — when they cannot be made repository-relative — re-marked
/// with an explicit <c>file://</c> scheme so the base's finding-path filter
/// cannot misread them as repository-relative and silently drop or
/// misattribute findings.</para>
///
/// <para>Identical diagnostics emitted for several targets (e.g. a binary and
/// its test harness both compiling <c>src/main.rs</c> under
/// <c>--all-targets</c>) are reported once: repeats of the same
/// rule/path/line/message are dropped.</para>
///
/// <para>Fail-closed on malformed output: empty stdout, a non-blank line that
/// is not a JSON object, or a findings exit (non-zero) that yields no
/// diagnostics throws <see cref="ExternalToolParseException"/>, which the
/// base reports as infrastructure, never as a pass. A clean run emits
/// artifact and build-finished records but no compiler-message diagnostics —
/// that is a pass, not malformed output.</para>
/// </summary>
internal sealed class ClippyJsonOutputParser : IExternalToolOutputParser
{
    private const int MaxResults = SarifToolOutputParser.DefaultMaxResults;
    private const int MessageMaxChars = 512;

    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Stdout))
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced no cargo-clippy JSON output on stdout.");

        var findings = new List<ExternalToolFinding>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rawLine in input.Stdout.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;
            if (findings.Count >= MaxResults)
                break;

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException ex)
            {
                throw new ExternalToolParseException(
                    $"Tool '{input.ToolName}' produced a line that is not valid cargo-clippy JSON: {ToolOutputText.SingleLine(line)}.",
                    ex);
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    throw new ExternalToolParseException(
                        $"Tool '{input.ToolName}' produced a JSON line that is not a cargo-clippy record object: {ToolOutputText.SingleLine(line)}.");
                if (!root.TryGetProperty("reason"u8, out var reasonElement)
                    || reasonElement.ValueKind != JsonValueKind.String)
                    throw new ExternalToolParseException(
                        $"Tool '{input.ToolName}' produced a JSON line without a cargo-clippy 'reason': {ToolOutputText.SingleLine(line)}.");

                if (!string.Equals(reasonElement.GetString(), "compiler-message", StringComparison.Ordinal))
                    continue;

                var finding = ParseCompilerMessage(root, input);
                if (finding is null)
                    continue;

                var key = string.Join("\0", finding.RuleId ?? string.Empty, finding.Path ?? string.Empty, finding.Line?.ToString() ?? string.Empty, finding.Message);
                if (seen.Add(key))
                    findings.Add(finding);
            }
        }

        // Cargo exits non-zero both for error-level diagnostics and for runs
        // that never compiled (missing manifest, toolchain failure). The
        // discriminator is the report: a findings exit with no parseable
        // diagnostics means the run did not produce its declared report —
        // not a clean pass.
        if (input.ExitCode != 0 && findings.Count == 0)
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' exited {input.ExitCode} but produced no cargo-clippy diagnostics on stdout.");
        return findings;
    }

    private static ExternalToolFinding? ParseCompilerMessage(JsonElement root, ExternalToolParseInput input)
    {
        if (!root.TryGetProperty("message"u8, out var message)
            || message.ValueKind != JsonValueKind.Object)
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced a compiler-message record without a 'message' object.");

        var text = GetString(message, "message"u8);
        var body = ToolOutputText.NullIfWhiteSpace(text) ?? "(no message)";
        var severity = ToolOutputText.NullIfWhiteSpace(GetString(message, "level"u8));
        var ruleId = ToolOutputText.NullIfWhiteSpace(ExtractCode(message));

        if (!message.TryGetProperty("spans"u8, out var spans)
            || spans.ValueKind != JsonValueKind.Array
            || spans.GetArrayLength() == 0)
            return null;

        JsonElement? primary = null;
        JsonElement? first = null;
        foreach (var span in spans.EnumerateArray())
        {
            if (span.ValueKind != JsonValueKind.Object)
                continue;
            first ??= span;
            if (span.TryGetProperty("is_primary"u8, out var isPrimary)
                && isPrimary.ValueKind == JsonValueKind.True)
            {
                primary = span;
                break;
            }
        }

        var chosen = primary ?? first;
        if (chosen is null)
            return null;

        var path = NormalizeReportedPath(
            GetString(chosen.Value, "file_name"u8), input.ScanRoot, input.WorkingDirectory);
        int? line = null;
        if (chosen.Value.TryGetProperty("line_start"u8, out var lineElement)
            && lineElement.ValueKind == JsonValueKind.Number
            && lineElement.TryGetInt32(out var lineValue)
            && lineValue > 0)
            line = lineValue;

        return new ExternalToolFinding(
            SeverityLevel: severity,
            RuleId: ruleId,
            Message: Truncate(body, MessageMaxChars),
            Path: path,
            Line: line);
    }

    private static string? ExtractCode(JsonElement message)
    {
        if (!message.TryGetProperty("code"u8, out var code))
            return null;
        if (code.ValueKind == JsonValueKind.String)
            return code.GetString();
        if (code.ValueKind == JsonValueKind.Object
            && code.TryGetProperty("code"u8, out var inner)
            && inner.ValueKind == JsonValueKind.String)
            return inner.GetString();
        return null;
    }
}
