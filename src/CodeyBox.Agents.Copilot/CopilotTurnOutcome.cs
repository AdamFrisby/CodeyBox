using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodeyBox.Agents.Copilot;

/// <summary>
/// Pure reading of one Copilot CLI turn's captured stdout: the native session
/// id, the reported exit code, whether the turn produced a final assistant
/// message, and whether it recorded edits. Understands both output modes the
/// CLI speaks — <c>--output-format json</c> NDJSON events and the default
/// plaintext footer (<c>Changes +A -D / Duration … / Resume copilot
/// --resume=&lt;id&gt;</c>).
///
/// <para>Unknown is a first-class answer: <c>HasFinalMessage</c> and
/// <c>HasEdits</c> are null when the capture carries no trustworthy signal
/// for them (no trailing <c>result</c> event, no footer). Callers must treat
/// null as "do not act" — guessing would either burn continuation turns on
/// healthy runs or misclassify a dead turn as verification.</para>
///
/// <para>Session ids are accepted only from end-anchored CLI positions: the
/// trailing <c>result</c> event (the CLI appends nothing after it) or the
/// trailing <c>Resume</c> footer line (trailing whitespace only after it). A
/// model that prints a fake <c>copilot --resume=&lt;id&gt;</c> line mid-turn
/// can never satisfy either anchor, so it cannot redirect a resume into an
/// attacker-chosen session. Every candidate must additionally parse as a
/// UUID; anything else is discarded.</para>
/// </summary>
internal sealed record CopilotTurnOutcome(
    bool IsStructured,
    string? SessionId,
    int? ExitCode,
    bool? HasFinalMessage,
    bool? HasEdits)
{
    /// <summary>
    /// True only when the capture proves BOTH halves of the dead-turn
    /// signature: no final assistant message AND no recorded edits.
    /// </summary>
    public bool RequiresContinuation => HasFinalMessage == false && HasEdits == false;

    internal const int MaxTailBytes = 128 * 1024;
    internal const int MaxParsedJsonLines = 1024;

    /// <summary>
    /// Matches the plaintext footer's change counters, e.g.
    /// <c>Changes    +0 -0</c>. Anchored to a full line so model prose
    /// mentioning changes never matches.
    /// </summary>
    internal static readonly Regex ChangesFooter = new(
        @"^[ \t]*Changes[ \t]+\+(\d+)[ \t]+-(\d+)[ \t]*\r?$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// Matches the plaintext footer's resume hint, e.g.
    /// <c>Resume     copilot --resume=1221b5a6-…</c>. NOT line-anchored on
    /// the right on purpose: callers additionally require the match to sit
    /// at the very end of the capture (trailing whitespace only), which is
    /// what keeps a model-printed fake line from matching.
    /// </summary>
    internal static readonly Regex ResumeFooter = new(
        @"^[ \t]*Resume[ \t]+copilot[ \t]+--resume=(\S+)[ \t]*\r?$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    public static CopilotTurnOutcome Extract(string? stdout)
    {
        if (string.IsNullOrEmpty(stdout))
            return new CopilotTurnOutcome(
                IsStructured: false,
                SessionId: null,
                ExitCode: null,
                HasFinalMessage: null,
                HasEdits: null);

        var tail = TailSlice(stdout, MaxTailBytes);
        var structured = TryExtractStructured(tail);
        if (structured is not null)
            return structured;

        return ExtractPlaintext(tail);
    }

    private static CopilotTurnOutcome? TryExtractStructured(string tail)
    {
        string? sessionId = null;
        int? exitCode = null;
        var hasFinalMessage = false;
        var sawResult = false;
        var sawStructuredEvent = false;
        bool? hasEdits = null;
        var parsedLines = 0;
        var lineIndex = 0;
        var lastResultLineIndex = -1;
        var lastNonEmptyLineIndex = -1;

        foreach (var rawLine in tail.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0)
            {
                lineIndex++;
                continue;
            }
            lastNonEmptyLineIndex = lineIndex;
            lineIndex++;
            if (line[0] != '{')
                continue;
            if (++parsedLines > MaxParsedJsonLines)
                break;

            JsonDocument? doc = null;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("type", out var typeProp)
                    || typeProp.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var type = typeProp.GetString();
                if (!IsCopilotEventType(type))
                    continue;
                sawStructuredEvent = true;

                if (string.Equals(type, "assistant.message", StringComparison.OrdinalIgnoreCase))
                {
                    if (root.TryGetProperty("data", out var data)
                        && data.ValueKind == JsonValueKind.Object
                        && data.TryGetProperty("content", out var content)
                        && content.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(content.GetString()))
                    {
                        hasFinalMessage = true;
                    }
                }
                else if (string.Equals(type, "result", StringComparison.OrdinalIgnoreCase))
                {
                    sawResult = true;
                    lastResultLineIndex = lastNonEmptyLineIndex;
                    if (root.TryGetProperty("sessionId", out var idProp)
                        && idProp.ValueKind == JsonValueKind.String
                        && IsUuid(idProp.GetString()))
                    {
                        sessionId = idProp.GetString();
                    }
                    if (root.TryGetProperty("exitCode", out var exitProp)
                        && exitProp.ValueKind == JsonValueKind.Number
                        && exitProp.TryGetInt32(out var code))
                    {
                        exitCode = code;
                    }
                    hasEdits = ReadCodeChanges(root);
                }
            }
        }

        if (!sawStructuredEvent)
            return null;

        // The session id is trusted only from the trailing result event: the
        // CLI appends nothing after it, so a model-printed fake result line
        // earlier in the capture cannot satisfy this. A stream whose window
        // holds no result event (truncated capture) yields no id rather than
        // a possibly-spoofed one.
        if (!sawResult)
            return new CopilotTurnOutcome(
                IsStructured: true,
                SessionId: null,
                ExitCode: null,
                HasFinalMessage: hasFinalMessage ? true : null,
                HasEdits: null);

        var anchoredSessionId = lastResultLineIndex == lastNonEmptyLineIndex ? sessionId : null;
        return new CopilotTurnOutcome(
            IsStructured: true,
            SessionId: anchoredSessionId,
            ExitCode: exitCode,
            HasFinalMessage: hasFinalMessage ? true : false,
            HasEdits: hasEdits);
    }

    private static CopilotTurnOutcome ExtractPlaintext(string tail)
    {
        // The footer hint is used repeatedly (once per line); take the LAST
        // end-anchored match — a model-printed fake hint earlier in the
        // capture never sits at the very end, so it can never win.
        string? sessionId = null;
        try
        {
            foreach (Match candidate in ResumeFooter.Matches(tail))
            {
                if (IsUuid(candidate.Groups[1].Value) && IsAtEnd(tail, candidate))
                    sessionId = candidate.Groups[1].Value;
            }
        }
        catch (RegexMatchTimeoutException)
        {
        }

        bool? hasEdits = null;
        try
        {
            var changesMatch = ChangesFooter.Match(tail);
            if (changesMatch.Success
                && int.TryParse(changesMatch.Groups[1].Value, out var added)
                && int.TryParse(changesMatch.Groups[2].Value, out var removed))
            {
                hasEdits = added != 0 || removed != 0;
            }
        }
        catch (RegexMatchTimeoutException)
        {
        }

        // Plaintext cannot separate tool transcripts from assistant prose, so
        // a non-blank body is "unknown", never "no message". Only a
        // footer-only capture (blank body) positively proves no message —
        // the plaintext analogue of a blank-stdout early-ended turn.
        var body = StripFooterLines(tail);
        bool? hasFinalMessage = hasEdits is null && sessionId is null
            ? null
            : string.IsNullOrWhiteSpace(body) ? false : null;

        return new CopilotTurnOutcome(
            IsStructured: false,
            SessionId: sessionId,
            ExitCode: null,
            HasFinalMessage: hasFinalMessage,
            HasEdits: hasEdits);
    }

    private static bool? ReadCodeChanges(JsonElement resultEvent)
    {
        if (!resultEvent.TryGetProperty("usage", out var usage)
            || usage.ValueKind != JsonValueKind.Object
            || !usage.TryGetProperty("codeChanges", out var changes)
            || changes.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var added = changes.TryGetProperty("linesAdded", out var addedProp)
            && addedProp.ValueKind == JsonValueKind.Number
            && addedProp.TryGetInt32(out var addedValue)
            ? addedValue : 0;
        var removed = changes.TryGetProperty("linesRemoved", out var removedProp)
            && removedProp.ValueKind == JsonValueKind.Number
            && removedProp.TryGetInt32(out var removedValue)
            ? removedValue : 0;
        var filesModified = changes.TryGetProperty("filesModified", out var filesProp)
            && filesProp.ValueKind == JsonValueKind.Array
            ? filesProp.GetArrayLength() : 0;
        return added != 0 || removed != 0 || filesModified != 0;
    }

    internal static bool IsCopilotEventType(string? type)
    {
        if (string.IsNullOrEmpty(type))
            return false;
        return type.StartsWith("session.", StringComparison.OrdinalIgnoreCase)
            || type.StartsWith("assistant.", StringComparison.OrdinalIgnoreCase)
            || type.StartsWith("model.", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "user.message", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "instruction.update", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "session.info", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "session.error", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "result", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUuid(string? value)
        => !string.IsNullOrEmpty(value)
            && Guid.TryParseExact(value, "D", out _);

    private static bool IsAtEnd(string tail, Match match)
    {
        var rest = tail.AsSpan(match.Index + match.Length);
        foreach (var ch in rest)
        {
            if (!char.IsWhiteSpace(ch))
                return false;
        }
        return true;
    }

    private static string StripFooterLines(string tail)
    {
        var lines = tail.Split('\n');
        var kept = new List<string>(lines.Length);
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;
            try
            {
                if (ChangesFooter.IsMatch(line)
                    || ResumeFooter.IsMatch(line)
                    || IsDurationFooter(line)
                    || IsTokensFooter(line))
                {
                    continue;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // A classification timeout keeps the line: the body then
                // reads non-blank and the outcome degrades to unknown rather
                // than throwing on the dispatch path.
            }
            kept.Add(line);
        }
        return string.Join('\n', kept);
    }

    private static bool IsDurationFooter(string line)
    {
        var trimmed = line.Trim();
        return trimmed.StartsWith("Duration", StringComparison.Ordinal)
            && trimmed.Length < 64;
    }

    private static bool IsTokensFooter(string line)
    {
        var trimmed = line.Trim();
        return trimmed.StartsWith("Tokens", StringComparison.Ordinal)
            && trimmed.Length < 128;
    }

    private static string TailSlice(string value, int maxBytes)
    {
        var bytes = 0;
        var start = value.Length;
        for (var i = value.Length - 1; i >= 0; i--)
        {
            var ch = value[i];
            bytes += ch <= 0x7F ? 1 : ch <= 0x7FF ? 2 : char.IsSurrogate(ch) ? 2 : 3;
            if (bytes > maxBytes)
            {
                start = i + 1;
                break;
            }
            start = i;
        }
        if (start > 0 && start < value.Length && char.IsLowSurrogate(value[start]))
            start++;
        return value.Substring(start);
    }
}
