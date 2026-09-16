using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Cline;

/// <summary>
/// Parser for cline's <c>--json</c> NDJSON events (verified against cline
/// 3.0.62 <c>--json</c> headless runs).
///
/// <para>Observed envelope vocabulary — every line carries a root
/// <c>type</c> plus a <c>ts</c> timestamp:</para>
/// <list type="bullet">
/// <item><c>{"type":"hook_event","hookEventName":"agent_start|tool_call|tool_result|agent_end|agent_error",…}</c> —
/// lifecycle hooks (no token/tool semantics; recognised so the summary
/// counts the line as understood).</item>
/// <item><c>{"type":"agent_event","event":{"type":"iteration_start",…}}</c> —
/// iteration boundary.</item>
/// <item><c>{"type":"agent_event","event":{"type":"content_start","contentType":"reasoning|text|tool","toolName":…,"toolCallId":…,"input":{…}}}</c> —
/// reasoning/text/tool-call start, mapped to tool starts for
/// <c>contentType: tool</c>.</item>
/// <item><c>{"type":"agent_event","event":{"type":"content_end","contentType":"tool","toolName":…,"toolCallId":…,"output":{…},"durationMs":…}}</c> —
/// tool result (also carries the final text for <c>contentType: text</c>).</item>
/// <item><c>{"type":"agent_event","event":{"type":"usage","inputTokens":…,"outputTokens":…,"cacheReadTokens":…,"totalCost":…}}</c> —
/// per-iteration usage (camelCase — the base parser's snake_case usage scan
/// does not see these, so they are mapped here).</item>
/// <item><c>{"type":"agent_event","event":{"type":"done","reason":"completed","text":"…","usage":{…}}}</c> —
/// final assistant text.</item>
/// <item><c>{"type":"agent_event","event":{"type":"error","error":{"message":"…","errorClass":"auth"}}}</c> —
/// terminal failure; parsed for the summary but deliberately NOT claimed
/// (see below).</item>
/// <item><c>{"type":"run_result","finishReason":"completed|error","usage":{…},"aggregateUsage":{…},"text":"…","model":{"id":…,"provider":…}}</c> —
/// terminal frame with cumulative usage, final text, and the dispatch model.
/// </item>
/// </list>
///
/// <para><b>Claim discipline.</b> The <c>type: "error"</c> stderr line
/// (<c>{"type":"error","message":"…"}</c>) is never claimed: it has no
/// cline-unique marker and misattributing another agent's error line would
/// corrupt stream-file attribution, while the runner's own failure path
/// lifts the message through <see cref="ClineTerminalDiagnoser"/>
/// independently of sniffing. Real cline streams open with
/// <c>hook_event agent_start</c> / <c>agent_event iteration_start</c> lines,
/// which ARE distinctive, so an error-only capture sniffing as unknown is
/// the honest outcome.</para>
/// </summary>
public sealed class ClineStreamParser : FlexibleAgentStreamParser
{
    public ClineStreamParser(AgentStreamParserOptions? options = null)
        : base(AgentKind.Cline, options)
    {
    }

    public override bool TryClaim(JsonElement line) =>
        IsClineStreamJsonEvent(line);

    internal static bool IsClineStreamJsonEvent(JsonElement line)
    {
        if (line.ValueKind != JsonValueKind.Object) return false;
        if (!line.TryGetProperty("type", out var typeProp)
            || typeProp.ValueKind != JsonValueKind.String)
            return false;

        var type = typeProp.GetString();
        switch (type)
        {
            case "hook_event":
                // hookEventName agent_start/tool_call/tool_result/agent_end
                // is cline's lifecycle vocabulary (verified: no other
                // registered CLI emits it).
                return HasString(line, "hookEventName");
            case "agent_event":
                // The inner event object with a type member is the cline
                // shape; require it so a coincidental root type string from
                // another CLI cannot claim.
                return line.TryGetProperty("event", out var inner)
                    && inner.ValueKind == JsonValueKind.Object
                    && inner.TryGetProperty("type", out var innerType)
                    && innerType.ValueKind == JsonValueKind.String
                    && !string.IsNullOrEmpty(innerType.GetString())
                    // Terminal error frames stay unclaimed (see class docs).
                    && !string.Equals(innerType.GetString(), "error", StringComparison.OrdinalIgnoreCase);
            case "run_result":
                // Terminal frame: finishReason + usage/aggregateUsage. The
                // combination never appears in other registered CLIs.
                return HasString(line, "finishReason")
                    && (line.TryGetProperty("usage", out var usage)
                        && usage.ValueKind == JsonValueKind.Object
                        || line.TryGetProperty("aggregateUsage", out var aggregate)
                        && aggregate.ValueKind == JsonValueKind.Object);
            default:
                return false;
        }
    }

    private static bool HasString(JsonElement line, string name) =>
        line.TryGetProperty(name, out var prop)
        && prop.ValueKind == JsonValueKind.String
        && !string.IsNullOrEmpty(prop.GetString());

    /// <summary>
    /// Maps cline's enveloped vocabulary onto the shared summary model on
    /// top of the base parse. The envelope is unwrapped first: hook events
    /// are recognised without further semantics; agent_event payloads map
    /// tool starts/results, camelCase usage, and final text; run_result
    /// contributes cumulative usage, final text, and cost.
    /// </summary>
    protected override ParsedEvent ParseEvent(JsonElement root)
    {
        var timestamp = TryTimestamp(root, "ts");
        var type = FirstString(root, "type");

        if (string.Equals(type, "hook_event", StringComparison.OrdinalIgnoreCase))
        {
            return ParseScalars(
                root,
                FirstString(root, "hookEventName") ?? type!,
                timestamp, [], [], isAssistant: false, contentText: null);
        }

        if (string.Equals(type, "agent_event", StringComparison.OrdinalIgnoreCase)
            && root.TryGetProperty("event", out var inner)
            && inner.ValueKind == JsonValueKind.Object)
        {
            return ParseAgentEvent(inner, timestamp);
        }

        if (string.Equals(type, "run_result", StringComparison.OrdinalIgnoreCase))
        {
            var usage = ClineJson.FirstObject(root, "aggregateUsage", "usage");
            var input = usage is { } u ? FirstInt(u, "inputTokens", "totalInputTokens") : null;
            var output = usage is { } u2 ? FirstInt(u2, "outputTokens", "totalOutputTokens") : null;
            var cached = usage is { } u3 ? FirstInt(u3, "cacheReadTokens", "totalCacheReadTokens") : null;
            var cost = usage is { } u4 ? FirstDecimal(u4, "totalCost", "cost") : null;
            // durationMs is wall time for the whole run; surface it as the
            // total duration so the summary reflects the measured run.
            var totalDuration = FirstDuration(root, "durationMs");
            var parsed = ParseScalars(
                root, type!, timestamp, [], [], isAssistant: false, contentText: null);
            var finalText = FirstString(root, "text");
            return parsed with
            {
                InputTokens = parsed.InputTokens ?? input,
                OutputTokens = parsed.OutputTokens ?? output,
                CachedInputTokens = parsed.CachedInputTokens ?? cached,
                EstimatedUsd = parsed.EstimatedUsd ?? cost,
                TotalDuration = parsed.TotalDuration ?? totalDuration,
                FinalText = string.Equals(FirstString(root, "finishReason"), "completed", StringComparison.OrdinalIgnoreCase)
                    ? finalText ?? parsed.FinalText
                    : parsed.FinalText,
                // Token/cost-bearing lines are understood even though the
                // base snake_case scan cannot see cline's camelCase usage
                // keys — without this a usage-only capture would report
                // Unsupported despite carrying real measurements.
                IsRecognized = parsed.IsRecognized
                    || input.HasValue || output.HasValue || cached.HasValue || cost.HasValue,
            };
        }

        return base.ParseEvent(root);
    }

    private ParsedEvent ParseAgentEvent(JsonElement inner, DateTimeOffset? timestamp)
    {
        var innerType = FirstString(inner, "type") ?? "unknown";

        if (string.Equals(innerType, "content_start", StringComparison.OrdinalIgnoreCase)
            && string.Equals(FirstString(inner, "contentType"), "tool", StringComparison.OrdinalIgnoreCase)
            && FirstString(inner, "toolCallId") is { } startId
            && FirstString(inner, "toolName") is { } startName)
        {
            var starts = new List<ToolBuilder>
            {
                new(startId, startName, InputSummary(inner), timestamp),
            };
            return ParseScalars(
                inner, innerType, timestamp, starts, [], isAssistant: true, contentText: null);
        }

        if (string.Equals(innerType, "content_end", StringComparison.OrdinalIgnoreCase)
            && string.Equals(FirstString(inner, "contentType"), "tool", StringComparison.OrdinalIgnoreCase)
            && FirstString(inner, "toolCallId") is { } endId)
        {
            var succeeded = TryToolSuccess(inner);
            var results = new List<ToolResultBuilder>
            {
                new(endId, succeeded, OutputBytes(inner), timestamp, FirstDuration(inner, "durationMs")),
            };
            return ParseScalars(
                inner, innerType, timestamp, [], results, isAssistant: false, contentText: null);
        }

        if (string.Equals(innerType, "content_end", StringComparison.OrdinalIgnoreCase)
            && string.Equals(FirstString(inner, "contentType"), "text", StringComparison.OrdinalIgnoreCase))
        {
            var text = FirstString(inner, "text");
            return ParseScalars(
                inner, innerType, timestamp, [], [], isAssistant: true, contentText: text);
        }

        if (string.Equals(innerType, "usage", StringComparison.OrdinalIgnoreCase))
        {
            var parsed = ParseScalars(
                inner, innerType, timestamp, [], [], isAssistant: false, contentText: null);
            // camelCase usage keys the base snake_case scan misses;
            // prefer per-iteration values, fall back to running totals.
            var usageInput = parsed.InputTokens
                ?? FirstInt(inner, "inputTokens")
                ?? FirstInt(inner, "totalInputTokens");
            var usageOutput = parsed.OutputTokens
                ?? FirstInt(inner, "outputTokens")
                ?? FirstInt(inner, "totalOutputTokens");
            var usageCached = parsed.CachedInputTokens
                ?? FirstInt(inner, "cacheReadTokens")
                ?? FirstInt(inner, "totalCacheReadTokens");
            var usageCost = parsed.EstimatedUsd
                ?? FirstDecimal(inner, "cost", "totalCost");
            return parsed with
            {
                InputTokens = usageInput,
                OutputTokens = usageOutput,
                CachedInputTokens = usageCached,
                EstimatedUsd = usageCost,
                IsRecognized = parsed.IsRecognized
                    || usageInput.HasValue || usageOutput.HasValue
                    || usageCached.HasValue || usageCost.HasValue,
            };
        }

        if (string.Equals(innerType, "done", StringComparison.OrdinalIgnoreCase))
        {
            var text = FirstString(inner, "text");
            var doneUsage = ClineJson.FirstObject(inner, "usage");
            var parsed = ParseScalars(
                inner, innerType, timestamp, [], [], isAssistant: true, contentText: text);
            return parsed with
            {
                InputTokens = parsed.InputTokens
                    ?? (doneUsage is { } du ? FirstInt(du, "inputTokens", "totalInputTokens") : null),
                OutputTokens = parsed.OutputTokens
                    ?? (doneUsage is { } du2 ? FirstInt(du2, "outputTokens", "totalOutputTokens") : null),
                CachedInputTokens = parsed.CachedInputTokens
                    ?? (doneUsage is { } du3 ? FirstInt(du3, "cacheReadTokens", "totalCacheReadTokens") : null),
            };
        }

        return ParseScalars(
            inner, innerType, timestamp, [], [], isAssistant: false, contentText: null);
    }

    private static bool? TryToolSuccess(JsonElement inner)
    {
        // content_end tool frames carry output.success (bool) or an output
        // array whose elements each carry success. A single false anywhere
        // means the tool failed; an absent signal stays unknown rather than
        // a fabricated true.
        if (inner.TryGetProperty("output", out var output))
        {
            if (output.ValueKind == JsonValueKind.Object
                && output.TryGetProperty("success", out var success)
                && success.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return success.GetBoolean();
            }

            if (output.ValueKind == JsonValueKind.Array)
            {
                bool? seen = null;
                foreach (var item in output.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object
                        && item.TryGetProperty("success", out var itemSuccess)
                        && itemSuccess.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        var value = itemSuccess.GetBoolean();
                        if (!value)
                            return false;
                        seen = true;
                    }
                }

                return seen;
            }
        }

        return null;
    }
}
