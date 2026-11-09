using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Copilot;

/// <summary>
/// Stream parser for the GitHub Copilot CLI's <c>--output-format json</c>
/// NDJSON events (<c>session.*</c>, <c>assistant.*</c>, <c>model.*</c>,
/// <c>user.message</c>, <c>result</c>). Owning this recognition here keeps
/// the orchestrator's sniffer free of Copilot-specific vocabulary.
///
/// <para>Claiming the trailing <c>result</c> event (with its
/// <c>sessionId</c>/<c>exitCode</c>/<c>usage.codeChanges</c>) and the
/// <c>assistant.message</c> contents is what lets the harness distinguish a
/// completed turn from one that died mid-investigation: the stop/finish
/// reason the plaintext footer never carried.</para>
/// </summary>
public sealed class CopilotStreamParser : FlexibleAgentStreamParser
{
    public CopilotStreamParser(AgentStreamParserOptions? options = null)
        : base(AgentKind.Copilot, options)
    {
    }

    public override bool TryClaim(JsonElement line)
    {
        if (line.ValueKind != JsonValueKind.Object)
            return false;

        // The host's stderr envelope can share the capture file on
        // structured runs; it is decoded by the base parser, never claimed
        // as a provider event.
        if (line.TryGetProperty("type", out var typeProp)
            && typeProp.ValueKind == JsonValueKind.String
            && string.Equals(typeProp.GetString(), CliAgentRunnerBase.StderrEnvelopeType, StringComparison.Ordinal))
        {
            return false;
        }

        return line.TryGetProperty("type", out var claimed)
            && claimed.ValueKind == JsonValueKind.String
            && CopilotTurnOutcome.IsCopilotEventType(claimed.GetString());
    }

    protected override ParsedEvent ParseEvent(JsonElement root)
    {
        var type = FirstString(root, "type", "event", "name") ?? "unknown";
        if (string.Equals(type, "assistant.message", StringComparison.OrdinalIgnoreCase)
            && TryGet(root, out var data, "data")
            && data.ValueKind == JsonValueKind.Object)
        {
            var content = FirstString(data, "content");
            var timestamp = TryTimestamp(root);
            return ParseScalars(
                data,
                type,
                timestamp,
                Array.Empty<ToolBuilder>(),
                Array.Empty<ToolResultBuilder>(),
                isAssistant: true,
                contentText: content);
        }

        var parsed = base.ParseEvent(root);
        // Every line TryClaim owns IS a Copilot stream event by construction,
        // so it counts toward the summary's recognised total even when it
        // carries no assistant text, tools, or usage (session lifecycle and
        // model-call bookkeeping lines). Without this a tool-only turn with
        // no final message would summarise as Unsupported and hide the very
        // stop/finish evidence the structured mode exists to surface.
        return CopilotTurnOutcome.IsCopilotEventType(type)
            ? parsed with { IsRecognized = true }
            : parsed;
    }
}
