using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Devin;

/// <summary>
/// Stream parser for devin ACP-mode output. The dispatch shim
/// (<c>Resources/devin-acp-client.py</c>) drives <c>devin acp</c> and folds
/// every inbound frame into a <c>{"type":"devin.acp","event":…}</c> NDJSON
/// envelope, so the capture file carries structured events this parser can
/// claim and summarise: <c>session_update</c> envelopes wrap the ACP
/// <c>session/update</c> notification (tool calls, message chunks, usage
/// ticks) and <c>turn_complete</c>/<c>turn_error</c>/<c>fatal</c> carry the
/// terminal outcome.
/// </summary>
public sealed class DevinStreamParser : FlexibleAgentStreamParser
{
    public DevinStreamParser(AgentStreamParserOptions? options = null)
        : base(AgentKind.Devin, options)
    {
    }

    /// <summary>
    /// The devin.acp envelope is emitted only by CodeyBox's own devin shim,
    /// so claiming by type tag is unambiguous — no other agent's stream can
    /// carry it.
    /// </summary>
    public override bool TryClaim(JsonElement line) =>
        line.ValueKind == JsonValueKind.Object
        && line.TryGetProperty("type", out var type)
        && type.ValueKind == JsonValueKind.String
        && string.Equals(type.GetString(), DevinAcpOutcome.EnvelopeType, StringComparison.Ordinal);

    protected override ParsedEvent ParseEvent(JsonElement root)
    {
        if (!TryClaim(root))
            return base.ParseEvent(root);

        var eventName = FirstString(root, "event") ?? "unknown";
        var timestamp = TryTimestamp(root);
        return eventName switch
        {
            "session_update" => ParseSessionUpdate(root, timestamp),
            "turn_complete" => ParseTurnComplete(root, timestamp),
            "turn_error" or "fatal" => new ParsedEvent(
                EventType: eventName,
                Timestamp: timestamp,
                IsAssistant: false,
                ToolStarts: [],
                ToolResults: [],
                InputTokens: null,
                OutputTokens: null,
                CachedInputTokens: null,
                EstimatedUsd: null,
                TotalDuration: null,
                TimeToFirstToken: null,
                FinalText: FirstString(root, "message"),
                IsRecognized: true),
            _ => new ParsedEvent(
                EventType: $"devin.acp.{eventName}",
                Timestamp: timestamp,
                IsAssistant: false,
                ToolStarts: [],
                ToolResults: [],
                InputTokens: null,
                OutputTokens: null,
                CachedInputTokens: null,
                EstimatedUsd: null,
                TotalDuration: null,
                TimeToFirstToken: null,
                FinalText: null,
                IsRecognized: true),
        };
    }

    private static ParsedEvent ParseSessionUpdate(JsonElement root, DateTimeOffset? timestamp)
    {
        var starts = new List<ToolBuilder>();
        var results = new List<ToolResultBuilder>();
        var isAssistant = false;
        int? inputTokens = null, outputTokens = null, cachedInputTokens = null;

        if (TryGet(root, out var update, "update") && update.ValueKind == JsonValueKind.Object)
        {
            switch (FirstString(update, "sessionUpdate"))
            {
                case "tool_call":
                {
                    var id = FirstString(update, "toolCallId") ?? "unknown";
                    var name = FirstString(update, "title")
                        ?? FirstString(update, "kind")
                        ?? "unknown";
                    starts.Add(new ToolBuilder(id, name, InputSummary(update), timestamp));
                    break;
                }
                case "tool_call_update":
                {
                    // Only terminal statuses close a tool call; in_progress /
                    // pending ticks are liveness envelopes.
                    var status = FirstString(update, "status");
                    if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
                    {
                        var id = FirstString(update, "toolCallId") ?? "unknown";
                        results.Add(new ToolResultBuilder(
                            id,
                            string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase),
                            OutputBytes(update),
                            timestamp,
                            FirstDuration(update)));
                    }
                    break;
                }
                case "agent_message_chunk":
                    isAssistant = true;
                    break;
                case "usage_update":
                    // `used`/`size` are context-window occupancy, not
                    // cumulative billing; the _meta counters carry the
                    // per-turn token totals.
                    if (TryGet(update, out var meta, "_meta"))
                    {
                        inputTokens = FirstInt(meta, "cognition.ai/inputTokens");
                        outputTokens = FirstInt(meta, "cognition.ai/outputTokens");
                        cachedInputTokens = FirstInt(meta, "cognition.ai/cachedReadTokens", "cognition.ai/cached_input_tokens");
                    }
                    break;
            }
        }

        var recognized = starts.Count > 0
            || results.Count > 0
            || isAssistant
            || inputTokens.HasValue
            || outputTokens.HasValue
            || cachedInputTokens.HasValue;
        return new ParsedEvent(
            EventType: isAssistant ? "assistant" : "devin.acp.session_update",
            Timestamp: timestamp,
            IsAssistant: isAssistant,
            ToolStarts: starts,
            ToolResults: results,
            InputTokens: inputTokens,
            OutputTokens: outputTokens,
            CachedInputTokens: cachedInputTokens,
            EstimatedUsd: null,
            TotalDuration: null,
            TimeToFirstToken: null,
            FinalText: null,
            IsRecognized: recognized);
    }

    private static ParsedEvent ParseTurnComplete(JsonElement root, DateTimeOffset? timestamp)
    {
        int? inputTokens = null, outputTokens = null;
        if (TryGet(root, out var usage, "usage"))
        {
            inputTokens = FirstInt(usage, "inputTokens", "input_tokens");
            outputTokens = FirstInt(usage, "outputTokens", "output_tokens");
        }

        return new ParsedEvent(
            EventType: "result",
            Timestamp: timestamp,
            IsAssistant: false,
            ToolStarts: [],
            ToolResults: [],
            InputTokens: inputTokens,
            OutputTokens: outputTokens,
            CachedInputTokens: null,
            EstimatedUsd: null,
            TotalDuration: null,
            TimeToFirstToken: null,
            FinalText: FirstString(root, "finalText"),
            IsRecognized: true);
    }
}
