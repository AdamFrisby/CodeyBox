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
/// <remarks>
/// Usage envelopes are recognised for stream liveness but their token
/// counters are deliberately NOT folded into the summary: summary tokens are
/// promoted into <c>work_item_costs</c> rows with
/// <c>has_extracted_token_usage = 1</c>, which settle quota reservations and
/// feed the burn estimator — and envelope payloads are agent-influenceable
/// telemetry (<see cref="DevinAcpEnvelope.IsEnvelope"/>), so a forged
/// zero-usage envelope would release a paid-quota escrow. The counters stay
/// visible in the raw capture for diagnostics.
/// </remarks>
public sealed class DevinStreamParser : FlexibleAgentStreamParser
{
    public DevinStreamParser(AgentStreamParserOptions? options = null)
        : base(AgentKind.Devin, options)
    {
    }

    /// <summary>
    /// Claims by the devin.acp type tag; see
    /// <see cref="DevinAcpEnvelope.IsEnvelope"/> for which injection vectors
    /// the claim closes (stderr folding, transport pinning) and the
    /// same-uid /proc-fd residual it cannot.
    /// </summary>
    public override bool TryClaim(JsonElement line) =>
        DevinAcpEnvelope.IsEnvelope(line);

    protected override ParsedEvent ParseEvent(JsonElement root)
    {
        if (!TryClaim(root))
            return base.ParseEvent(root);

        var eventName = FirstString(root, DevinAcpEnvelope.EventPropertyName) ?? "unknown";
        var timestamp = TryTimestamp(root);
        return eventName switch
        {
            DevinAcpEnvelope.EventSessionUpdate => ParseSessionUpdate(root, timestamp),
            DevinAcpEnvelope.EventTurnComplete => ParseTurnComplete(root, timestamp),
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
                // Terminal failure envelopes carry the shim's diagnostic in
                // `message`; every other envelope leaves FinalText unset.
                FinalText: eventName is DevinAcpEnvelope.EventTurnError or DevinAcpEnvelope.EventFatal
                    ? FirstString(root, "message")
                    : null,
                IsRecognized: true),
        };
    }

    private static ParsedEvent ParseSessionUpdate(JsonElement root, DateTimeOffset? timestamp)
    {
        var starts = new List<ToolBuilder>();
        var results = new List<ToolResultBuilder>();
        var isAssistant = false;
        var sawUsageTick = false;

        if (DevinAcpEnvelope.TryGetUpdate(root, out var update))
        {
            switch (FirstString(update, DevinAcpEnvelope.SessionUpdatePropertyName))
            {
                case DevinAcpEnvelope.UpdateKindToolCall:
                {
                    var id = FirstString(update, "toolCallId") ?? "unknown";
                    var name = FirstString(update, "title")
                        ?? FirstString(update, "kind")
                        ?? "unknown";
                    starts.Add(new ToolBuilder(id, name, InputSummary(update), timestamp));
                    break;
                }
                case DevinAcpEnvelope.UpdateKindToolCallUpdate:
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
                case DevinAcpEnvelope.UpdateKindAgentMessageChunk:
                    isAssistant = true;
                    break;
                case DevinAcpEnvelope.UpdateKindUsageUpdate:
                    // Recognised for liveness, but the counters are
                    // agent-influenceable telemetry and must never reach
                    // the summary (see the class remarks) — they would be
                    // promoted into work_item_costs rows that settle quota
                    // escrow and feed the burn estimator.
                    sawUsageTick = true;
                    break;
            }
        }

        var recognized = starts.Count > 0
            || results.Count > 0
            || isAssistant
            || sawUsageTick;
        return new ParsedEvent(
            EventType: isAssistant ? "assistant" : "devin.acp.session_update",
            Timestamp: timestamp,
            IsAssistant: isAssistant,
            ToolStarts: starts,
            ToolResults: results,
            InputTokens: null,
            OutputTokens: null,
            CachedInputTokens: null,
            EstimatedUsd: null,
            TotalDuration: null,
            TimeToFirstToken: null,
            FinalText: null,
            IsRecognized: recognized);
    }

    private static ParsedEvent ParseTurnComplete(JsonElement root, DateTimeOffset? timestamp)
    {
        // The terminal envelope's `usage` object is agent-influenceable
        // telemetry like every other envelope payload; it is deliberately
        // not folded into the summary (see the class remarks).
        return new ParsedEvent(
            EventType: "result",
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
            FinalText: FirstString(root, DevinAcpEnvelope.FinalTextPropertyName),
            IsRecognized: true);
    }
}
