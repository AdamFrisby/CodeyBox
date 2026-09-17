using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Cmd;

/// <summary>
/// Parser for cmd's <c>-p --output-format json</c> NDJSON events (verified
/// against command-code 1.54.2 live runs).
///
/// <para>Observed frame vocabulary (top-level <c>type</c> is always
/// <c>event</c> or <c>result</c>; the lifecycle verb rides the nested
/// <c>event.type</c>):</para>
/// <list type="bullet">
/// <item><c>run_start {sessionId}</c> / <c>turn_start {turnNumber}</c> /
/// <c>message_start</c> — run/turn/message boundaries (no token semantics;
/// recognised so the summary counts the line as understood).</item>
/// <item><c>model_request_start {model}</c> — the full
/// <c>openrouter/…</c> dispatch id (cost attribution reads it; see
/// <see cref="CmdCostExtractor"/>).</item>
/// <item><c>thinking_start</c> / <c>thinking_delta {delta}</c> /
/// <c>thinking_end {text}</c> / <c>text_delta {delta}</c> — streaming
/// deltas; the full text arrives on <c>message_end</c>/<c>run_end</c>/
/// the result line, so deltas are recognised but not accumulated.</item>
/// <item><c>message_update {content:[{type,text}…]}</c> — cumulative
/// content snapshot; <c>message_end {content:[…]}</c> — final content.
/// Text parts are lifted (thinking parts are not: they would drown the
/// summary).</item>
/// <item><c>model_request_end {model, usage, stopReason}</c> /
/// <c>turn_end {turnNumber, hadToolCalls, usage}</c> — per-turn usage
/// (<c>inputTokens/outputTokens/cacheReadTokens</c>; verified: turn usages
/// sum to the run total). Mapped onto the shared token buckets.</item>
/// <item><c>tool_queued {toolCallId, toolName, input}</c> — tool start;
/// <c>tool_running {toolCallId, toolName}</c> — already-started progress
/// marker (no new start emitted); <c>tool_completed {toolCallId, toolName,
/// result:[{type,text}…]}</c> — success with output bytes;
/// <c>tool_hook_blocked {toolCallId, toolName, hookOutput}</c> — the
/// headless permission gate refusing the call (mapped to a failed result
/// so the summary shows the refusal).</item>
/// <item><c>run_end {result:{finalText, stopReason, turnCount, usage}}</c> —
/// authoritative final text plus the run-total usage.</item>
/// <item><c>run_error {error:{name, message}}</c> — terminal failure;
/// parsed for the summary (message on the stderr tail) but deliberately NOT
/// claimed (see below).</item>
/// <item><c>{"type":"result","subtype":"success"|"error"|"max_turns",…}</c> —
/// terminal line with <c>finalText</c>, <c>usage</c>, and <c>error</c> on
/// failure; claimed only with the cmd markers below (see below).</item>
/// </list>
///
/// <para><b>Claim discipline.</b> The <c>type: "event"</c> + nested
/// <c>event.type</c> envelope is cmd-distinctive: no other registered CLI
/// emits it (cline's envelope is <c>agent_event</c>/<c>hook_event</c>;
/// Codex uses <c>event_msg</c>), so it is always claimed. The bare result
/// line overlaps Claude (whose parser claims every <c>type: "result"</c>
/// line and is registered first), so the result claim additionally requires
/// the cmd markers — <c>subtype</c> in <c>success/error/max_turns</c> plus
/// a <c>usage</c> object and <c>finalText</c> — keeping it from stealing
/// real Claude lines; in production sniffing a cmd stream always opens
/// with <c>run_start</c> event lines anyway, so an error-only capture
/// sniffing as Claude is the documented edge (same honesty posture as the
/// autohand/cline error-only disciplines). The <c>run_error</c> event is
/// never claimed on its own: misattributing another agent's error line
/// would corrupt stream-file attribution — the runner's own failure path
/// lifts the message through <see cref="CmdTerminalDiagnoser"/>
/// independently of sniffing.</para>
///
/// <para><b>Usage mapping.</b> The shared <c>ParseUsage</c> only knows
/// snake_case names; cmd reports camelCase
/// (<c>inputTokens/outputTokens/cacheReadTokens</c>), so this parser
/// supplements the base parse with cmd's names (base results win when both
/// are present). The base summary keeps the latest non-empty values, and
/// the terminal frames (run-total usage, authoritative final text) sort
/// last — so per-turn frames never corrupt the totals.
/// <c>cacheWriteTokens</c> has no cost bucket and is ignored.</para>
/// </summary>
public sealed class CmdStreamParser : FlexibleAgentStreamParser
{
    public CmdStreamParser(AgentStreamParserOptions? options = null)
        : base(AgentKind.Cmd, options)
    {
    }

    public override bool TryClaim(JsonElement line) =>
        IsCmdStreamJsonEvent(line);

    internal static bool IsCmdStreamJsonEvent(JsonElement line)
    {
        if (line.ValueKind != JsonValueKind.Object) return false;
        if (!line.TryGetProperty("type", out var typeProp)
            || typeProp.ValueKind != JsonValueKind.String)
            return false;

        var type = typeProp.GetString();
        if (string.Equals(type, "event", StringComparison.Ordinal))
        {
            // The nested lifecycle verb is the cmd marker: no other
            // registered CLI emits {"type":"event","event":{"type":…}}.
            return line.TryGetProperty("event", out var inner)
                && inner.ValueKind == JsonValueKind.Object
                && inner.TryGetProperty("type", out var innerType)
                && innerType.ValueKind == JsonValueKind.String
                && EventTypes.Contains(innerType.GetString()!);
        }

        if (string.Equals(type, "result", StringComparison.Ordinal))
        {
            // Claude's parser claims every type:result line and is
            // registered first, so this branch only matters for result-only
            // captures. Require the cmd markers (subtype vocabulary + usage
            // object + finalText) so a real Claude result is never stolen.
            if (!line.TryGetProperty("subtype", out var subtype)
                || subtype.ValueKind != JsonValueKind.String
                || !ResultSubtypes.Contains(subtype.GetString()!))
            {
                return false;
            }

            return line.TryGetProperty("usage", out var usage)
                && usage.ValueKind == JsonValueKind.Object
                && line.TryGetProperty("finalText", out var finalText)
                && finalText.ValueKind == JsonValueKind.String;
        }

        return false;
    }

    /// <summary>
    /// The nested <c>event.type</c> vocabulary observed on live
    /// command-code 1.54.2 runs (plus the <c>tool_errored</c> /
    /// <c>tool_denied</c> companions named in the CLI bundle's tool-result
    /// classification, handled defensively as failed results).
    /// <c>run_error</c> is deliberately absent: it is never claimed (see
    /// the claim discipline above) but is still parsed for the summary via
    /// the explicit branch in <see cref="ParseEvent"/>.
    /// </summary>
    internal static readonly IReadOnlySet<string> EventTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "run_start",
        "turn_start",
        "message_start",
        "model_request_start",
        "thinking_start",
        "thinking_delta",
        "thinking_end",
        "message_update",
        "text_delta",
        "model_request_end",
        "message_end",
        "turn_end",
        "run_end",
        "tool_use",
        "tool_queued",
        "tool_running",
        "tool_completed",
        "tool_errored",
        "tool_denied",
        "tool_hook_blocked",
    };

    internal static readonly IReadOnlySet<string> ResultSubtypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "success",
        "error",
        "max_turns",
    };

    /// <summary>
    /// Maps cmd's nested-envelope vocabulary onto the shared summary model
    /// on top of the base parse: the top-level <c>type</c>
    /// (<c>event</c>/<c>result</c>) is unknown to the base parse, so
    /// claim-envelope lines would otherwise count as unrecognized (and a
    /// cmd-only stream as Unsupported) — the envelope itself is the
    /// recognition signal. Text parts from <c>message_update</c> /
    /// <c>message_end</c> / <c>run_end.result</c> / the terminal result line
    /// become the content text (latest non-empty wins downstream);
    /// camelCase usage supplements the base usage parse; tool lifecycle
    /// events become start + result pairs.
    /// </summary>
    protected override ParsedEvent ParseEvent(JsonElement root)
    {
        var parsed = base.ParseEvent(root);

        // run_error is never claimed (see the claim discipline) but still
        // parsed: the message surfaces in the summary's stderr tail while
        // the runner's failure path lifts it through CmdTerminalDiagnoser
        // independently of sniffing.
        if (IsRunErrorFrame(root)
            && root.TryGetProperty("event", out var errorInner)
            && errorInner.ValueKind == JsonValueKind.Object
            && errorInner.TryGetProperty("error", out var errorObj)
            && errorObj.ValueKind == JsonValueKind.Object
            && errorObj.TryGetProperty("message", out var errorMessage)
            && errorMessage.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(errorMessage.GetString()))
        {
            return parsed with { IsRecognized = true, StderrText = errorMessage.GetString() };
        }

        if (!IsCmdStreamJsonEvent(root))
            return parsed;

        parsed = parsed with { IsRecognized = true };

        var topType = FirstString(root, "type");
        if (string.Equals(topType, "result", StringComparison.Ordinal))
            return ParseResultLine(root, parsed);

        if (!root.TryGetProperty("event", out var inner) || inner.ValueKind != JsonValueKind.Object)
            return parsed;

        var eventType = FirstString(inner, "type") ?? string.Empty;

        if (TryParseToolEvent(inner, eventType, TryTimestamp(root)) is { } toolCall)
        {
            parsed = parsed with
            {
                ToolStarts = toolCall.Start is { } start
                    ? parsed.ToolStarts.Concat([start]).ToList()
                    : parsed.ToolStarts,
                ToolResults = toolCall.Result is { } result
                    ? parsed.ToolResults.Concat([result]).ToList()
                    : parsed.ToolResults,
                IsAssistant = true,
            };
        }

        // message_update frames are cumulative snapshots (each carries the
        // full content so far), so latest non-empty text wins — concatenating
        // would duplicate every snapshot downstream.
        var text = ExtractText(inner, eventType);
        if (!string.IsNullOrEmpty(text))
        {
            parsed = parsed with { FinalText = text, IsAssistant = true };
        }

        if (ExtractUsage(inner) is { } usage)
        {
            parsed = parsed with
            {
                InputTokens = parsed.InputTokens ?? usage.Input,
                OutputTokens = parsed.OutputTokens ?? usage.Output,
                CachedInputTokens = parsed.CachedInputTokens ?? usage.Cached,
            };
        }

        return parsed;
    }

    private static bool IsRunErrorFrame(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("type", out var type)
        && type.ValueKind == JsonValueKind.String
        && string.Equals(type.GetString(), "event", StringComparison.Ordinal)
        && root.TryGetProperty("event", out var inner)
        && inner.ValueKind == JsonValueKind.Object
        && inner.TryGetProperty("type", out var innerType)
        && innerType.ValueKind == JsonValueKind.String
        && string.Equals(innerType.GetString(), "run_error", StringComparison.Ordinal);

    private ParsedEvent ParseResultLine(JsonElement root, ParsedEvent parsed)
    {
        var finalText = FirstString(root, "finalText");
        if (!string.IsNullOrWhiteSpace(finalText))
        {
            parsed = parsed with { FinalText = finalText, IsAssistant = true };
        }

        // On a non-success subtype the error body is the terminal signal:
        // surface it in the summary's stderr tail.
        var subtype = FirstString(root, "subtype") ?? string.Empty;
        if (!string.Equals(subtype, "success", StringComparison.OrdinalIgnoreCase)
            && FirstString(root, "error") is { } error
            && !string.IsNullOrWhiteSpace(error))
        {
            parsed = parsed with { StderrText = error };
        }

        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            var input = parsed.InputTokens ?? TryReadTokenCounter(usage, "inputTokens");
            var output = parsed.OutputTokens ?? TryReadTokenCounter(usage, "outputTokens");
            var cached = parsed.CachedInputTokens ?? TryReadTokenCounter(usage, "cacheReadTokens");
            if (input != parsed.InputTokens || output != parsed.OutputTokens || cached != parsed.CachedInputTokens)
                parsed = parsed with { InputTokens = input, OutputTokens = output, CachedInputTokens = cached };
        }

        return parsed;
    }

    private readonly record struct ParsedToolCall(ToolBuilder? Start, ToolResultBuilder? Result);

    private static ParsedToolCall? TryParseToolEvent(JsonElement inner, string eventType, DateTimeOffset? frameTimestamp)
    {
        if (string.Equals(eventType, "tool_queued", StringComparison.Ordinal))
        {
            var id = FirstString(inner, "toolCallId");
            var name = FirstString(inner, "toolName");
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name))
                return null;
            return new ParsedToolCall(
                new ToolBuilder(id!, name!, InputSummary(inner), frameTimestamp),
                Result: null);
        }

        if (string.Equals(eventType, "tool_completed", StringComparison.Ordinal)
            || string.Equals(eventType, "tool_errored", StringComparison.Ordinal)
            || string.Equals(eventType, "tool_denied", StringComparison.Ordinal)
            || string.Equals(eventType, "tool_hook_blocked", StringComparison.Ordinal))
        {
            var id = FirstString(inner, "toolCallId");
            if (string.IsNullOrEmpty(id))
                return null;
            var succeeded = string.Equals(eventType, "tool_completed", StringComparison.Ordinal);
            return new ParsedToolCall(
                Start: null,
                new ToolResultBuilder(id!, succeeded, ToolResultOutputBytes(inner), frameTimestamp, Duration: null));
        }

        return null;
    }

    private static string ExtractText(JsonElement inner, string eventType)
    {
        // message_update / message_end carry cumulative content arrays
        // ([{type:thinking,…},{type:text,…}]); only text parts surface.
        // run_end carries the authoritative final text on result.finalText.
        if (string.Equals(eventType, "message_update", StringComparison.Ordinal)
            || string.Equals(eventType, "message_end", StringComparison.Ordinal))
        {
            if (!inner.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                return string.Empty;
            var pieces = new List<string>();
            foreach (var part in content.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object)
                    continue;
                if (!IsStringEqual(part, "type", "text"))
                    continue;
                if (part.TryGetProperty("text", out var text)
                    && text.ValueKind == JsonValueKind.String
                    && !string.IsNullOrEmpty(text.GetString()))
                {
                    pieces.Add(text.GetString()!);
                }
            }

            return string.Join("\n", pieces);
        }

        if (string.Equals(eventType, "run_end", StringComparison.Ordinal)
            && inner.TryGetProperty("result", out var result)
            && result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("finalText", out var finalText)
            && finalText.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(finalText.GetString()))
        {
            return finalText.GetString()!;
        }

        return string.Empty;
    }

    private static Usage? ExtractUsage(JsonElement inner)
    {
        JsonElement usage;
        if (inner.TryGetProperty("result", out var result)
            && result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("usage", out var runUsage)
            && runUsage.ValueKind == JsonValueKind.Object)
        {
            usage = runUsage;
        }
        else if (inner.TryGetProperty("usage", out var eventUsage)
            && eventUsage.ValueKind == JsonValueKind.Object)
        {
            usage = eventUsage;
        }
        else
        {
            return null;
        }

        var input = TryReadTokenCounter(usage, "inputTokens");
        var output = TryReadTokenCounter(usage, "outputTokens");
        var cached = TryReadTokenCounter(usage, "cacheReadTokens");
        if (input is null && output is null && cached is null)
            return null;
        return new Usage(input, output, cached);
    }

    private readonly record struct Usage(int? Input, int? Output, int? Cached);

    /// <summary>
    /// Byte size of the tool result's text parts. The shared
    /// <c>OutputBytes</c> helper cannot be reused here: cmd's
    /// <c>result</c> is an array of <c>{type, text}</c> parts, which the
    /// shared helper would miss (falling back to the whole frame's raw
    /// text, including ids and timestamps).
    /// </summary>
    private static int ToolResultOutputBytes(JsonElement inner)
    {
        if (!inner.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
            return 0;
        var bytes = 0;
        foreach (var part in result.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.Object
                && part.TryGetProperty("text", out var text)
                && text.ValueKind == JsonValueKind.String)
            {
                bytes += System.Text.Encoding.UTF8.GetByteCount(text.GetString() ?? string.Empty);
            }
        }

        return bytes;
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
