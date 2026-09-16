using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Kilo;

/// <summary>
/// Parser for kilo's <c>run --auto --format json</c> NDJSON events
/// (verified against @kilocode/cli 7.7.2 live runs).
///
/// <para>Observed frame vocabulary (OpenCode-family envelope — kilo is an
/// OpenCode fork):</para>
/// <list type="bullet">
/// <item><c>{"type":"step_start",…,"part":{"type":"step-start",…}}</c> —
/// run/step boundary (no token semantics; recognised so the summary counts
/// the line as understood).</item>
/// <item><c>{"type":"text",…,"part":{"type":"text","text":"…",…}}</c> —
/// assistant text; the content lives on the nested <c>part</c>, not the
/// top-level frame the base parse reads, so it is lifted explicitly.</item>
/// <item><c>{"type":"step_finish",…,"part":{"type":"step-finish",
/// "reason":"stop","tokens":{"total":…,"input":…,"output":…,"reasoning":…,
/// "cache":{"read":…,"write":…}},…}}</c> — terminal usage frame; usage is
/// mapped onto the shared token buckets (see below).</item>
/// <item><c>{"type":"tool_use",…,"part":{"type":"tool","callID":…,
/// "tool":"bash","state":{"status":"completed","input":{…},"output":"…",
/// "time":{"start":…,"end":…}}}}</c> — completed tool call; mapped to a
/// start + result pair (the merged frame carries both).</item>
/// <item><c>{"type":"error",…,"error":{"name":…,"data":{…}}}</c> — terminal
/// failure; parsed for the summary but deliberately NOT claimed (see
/// below).</item>
/// </list>
///
/// <para><b>Claim discipline.</b> The claim requires the OpenCode-family
/// envelope: a <c>type</c> in <c>step_start/step_finish/text/tool_use</c>
/// AND a <c>sessionID</c> string in <c>ses_…</c> form AND a <c>part</c>
/// object carrying its own <c>sessionID</c> + <c>messageID</c>. No other registered
/// parser claims this envelope (opencode's own parser claims nothing — its
/// runner speaks no structured stream — and Claude/Codex/Gemini key on
/// disjoint vocabularies), so the envelope is kilo-distinctive today. Bare
/// <c>type: "error"</c> is never claimed: it has no kilo-unique marker
/// beyond the envelope, and misattributing another agent's error line would
/// corrupt stream-file attribution — the runner's own failure path lifts the
/// message through <see cref="KiloTerminalDiagnoser"/> independently of
/// sniffing (same discipline as the autohand parser).</para>
///
/// <para><b>Usage mapping.</b> The shared <c>ParseUsage</c> only knows
/// <c>input_tokens/prompt_tokens</c>-style names; kilo reports
/// <c>part.tokens {input, output, reasoning, cache:{read, write}}</c>, so
/// this parser supplements the base parse with kilo's names (base results
/// win when both are present). Verified live: the success frame's
/// <c>total</c> equals <c>input + cache.read + output + reasoning</c>, so
/// <c>input</c> is fresh (non-cached) input and maps directly onto
/// <c>InputTokens</c>. <c>reasoning</c> and <c>cache.write</c> have no
/// cost-bucket in <see cref="AgentCostSnapshot"/> and are ignored for token
/// accounting — same treatment as the other providers.</para>
/// </summary>
public sealed class KiloStreamParser : FlexibleAgentStreamParser
{
    public KiloStreamParser(AgentStreamParserOptions? options = null)
        : base(AgentKind.Kilo, options)
    {
    }

    public override bool TryClaim(JsonElement line) =>
        IsKiloStreamJsonEvent(line);

    internal static bool IsKiloStreamJsonEvent(JsonElement line)
    {
        if (line.ValueKind != JsonValueKind.Object) return false;
        if (!line.TryGetProperty("type", out var typeProp)
            || typeProp.ValueKind != JsonValueKind.String)
            return false;

        var type = typeProp.GetString();
        if (!string.Equals(type, "step_start", StringComparison.Ordinal)
            && !string.Equals(type, "step_finish", StringComparison.Ordinal)
            && !string.Equals(type, "text", StringComparison.Ordinal)
            && !string.Equals(type, "tool_use", StringComparison.Ordinal))
            return false;

        // The ses_-prefixed session id plus the nested part envelope
        // (sessionID + messageID) is the OpenCode-family marker no other
        // registered parser claims.
        if (!line.TryGetProperty("sessionID", out var sessionId)
            || sessionId.ValueKind != JsonValueKind.String
            || !sessionId.GetString()!.StartsWith("ses_", StringComparison.Ordinal))
            return false;

        if (!line.TryGetProperty("part", out var part)
            || part.ValueKind != JsonValueKind.Object)
            return false;

        return part.TryGetProperty("sessionID", out var partSession)
            && partSession.ValueKind == JsonValueKind.String
            && part.TryGetProperty("messageID", out var messageId)
            && messageId.ValueKind == JsonValueKind.String;
    }

    /// <summary>
    /// Maps kilo's part-envelope vocabulary onto the shared summary model on
    /// top of the base parse: <c>part.text</c> becomes the content text for
    /// <c>type: "text"</c> frames, <c>part.tokens</c> supplements the base
    /// usage parse without overriding it, and <c>type: "tool_use"</c> frames
    /// (which already carry the completed call: <c>callID</c>, tool name,
    /// <c>state.status/input/output/time</c>) become a start + result pair.
    /// </summary>
    protected override ParsedEvent ParseEvent(JsonElement root)
    {
        var parsed = base.ParseEvent(root);
        if (!IsKiloStreamJsonEvent(root))
            return parsed;

        // The OpenCode-family type vocabulary (step_start/text/step_finish/
        // tool_use) is unknown to the base parse, so claim-envelope lines
        // would otherwise count as unrecognized (and a kilo-only stream as
        // Unsupported). The envelope itself is the recognition signal.
        parsed = parsed with { IsRecognized = true };

        string? partText = null;
        if (root.TryGetProperty("part", out var part) && part.ValueKind == JsonValueKind.Object
            && part.TryGetProperty("text", out var textProp) && textProp.ValueKind == JsonValueKind.String)
            partText = textProp.GetString();

        if (!string.IsNullOrEmpty(partText)
            && !string.Equals(parsed.FinalText, partText, StringComparison.Ordinal))
        {
            parsed = parsed with
            {
                FinalText = string.IsNullOrEmpty(parsed.FinalText)
                    ? partText
                    : parsed.FinalText + "\n" + partText,
            };
        }

        if (root.TryGetProperty("part", out var usagePart) && usagePart.ValueKind == JsonValueKind.Object
            && usagePart.TryGetProperty("tokens", out var tokens)
            && tokens.ValueKind == JsonValueKind.Object)
        {
            var input = parsed.InputTokens ?? TryReadTokenCounter(tokens, "input");
            var output = parsed.OutputTokens ?? TryReadTokenCounter(tokens, "output");
            int? cached = parsed.CachedInputTokens;
            if (tokens.TryGetProperty("cache", out var cache) && cache.ValueKind == JsonValueKind.Object)
                cached ??= TryReadTokenCounter(cache, "read");
            if (input != parsed.InputTokens || output != parsed.OutputTokens || cached != parsed.CachedInputTokens)
                parsed = parsed with { InputTokens = input, OutputTokens = output, CachedInputTokens = cached };
        }

        if (string.Equals(FirstString(root, "type"), "tool_use", StringComparison.Ordinal)
            && TryParseToolCall(root, TryTimestamp(root)) is { } toolCall)
        {
            // The base parse fires its generic tool_use branch on the
            // top-level type (fabricating an id-less "unknown" start from a
            // frame whose real identity lives on the nested part). Drop that
            // phantom before adding the mapped call.
            parsed = parsed with
            {
                ToolStarts = [toolCall.Start],
                ToolResults = [toolCall.Result],
                IsAssistant = true,
            };
        }

        return parsed;
    }

    private readonly record struct ParsedToolCall(ToolBuilder Start, ToolResultBuilder Result);

    private static ParsedToolCall? TryParseToolCall(JsonElement root, DateTimeOffset? frameTimestamp)
    {
        if (!root.TryGetProperty("part", out var part) || part.ValueKind != JsonValueKind.Object)
            return null;
        if (!IsStringEqual(part, "type", "tool"))
            return null;
        var id = FirstString(part, "callID", "callId", "id");
        var name = FirstString(part, "tool", "name");
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name))
            return null;
        if (!part.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.Object)
            return null;

        var status = FirstString(state, "status");
        bool? succeeded = string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
            ? true
            : string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "error", StringComparison.OrdinalIgnoreCase)
                ? false
                : null;

        DateTimeOffset? startedAt = null;
        DateTimeOffset? endedAt = null;
        TimeSpan? duration = null;
        if (state.TryGetProperty("time", out var time) && time.ValueKind == JsonValueKind.Object)
        {
            startedAt = TryReadUnixMs(time, "start") ?? frameTimestamp;
            endedAt = TryReadUnixMs(time, "end");
            if (startedAt.HasValue && endedAt.HasValue && endedAt.Value >= startedAt.Value)
                duration = endedAt.Value - startedAt.Value;
        }

        var inputSummary = SummarizeInput(state);
        var outputBytes = 0;
        if (state.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.String)
            outputBytes = System.Text.Encoding.UTF8.GetByteCount(output.GetString() ?? string.Empty);

        return new ParsedToolCall(
            new ToolBuilder(id!, name!, inputSummary, startedAt ?? frameTimestamp),
            new ToolResultBuilder(id!, succeeded, outputBytes, endedAt, duration));
    }

    private static string SummarizeInput(JsonElement state)
    {
        if (!state.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object)
            return string.Empty;
        var pieces = new List<string>();
        foreach (var prop in input.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String)
                pieces.Add($"{prop.Name}={prop.Value.GetString()}");
            if (pieces.Count >= 4)
                break;
        }
        var summary = string.Join("; ", pieces);
        return summary.Length <= 200 ? summary : summary[..200];
    }

    private static DateTimeOffset? TryReadUnixMs(JsonElement obj, string name)
    {
        if (obj.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var ms)
            && ms >= 0)
        {
            try
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(ms);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        return null;
    }

    private static bool IsStringEqual(JsonElement obj, string name, string expected) =>
        obj.TryGetProperty(name, out var prop)
        && prop.ValueKind == JsonValueKind.String
        && string.Equals(prop.GetString(), expected, StringComparison.Ordinal);

    private static int? TryReadTokenCounter(JsonElement obj, string name)
    {
        if (obj.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var n)
            && n >= 0)
        {
            return n;
        }

        return null;
    }
}
