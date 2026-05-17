using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Claude;

public sealed class ClaudeStreamParser : FlexibleAgentStreamParser
{
    public ClaudeStreamParser(AgentStreamParserOptions? options = null)
        : base(AgentKind.Claude, options)
    {
    }

    /// <summary>
    /// Claude Code's <c>--output-format stream-json</c> NDJSON events use the
    /// top-level <c>type</c> values below. Owning this recognition here keeps
    /// the orchestrator's sniffer free of Claude-specific vocabulary.
    /// </summary>
    public override bool TryClaim(JsonElement line)
    {
        if (line.ValueKind != JsonValueKind.Object) return false;
        if (!line.TryGetProperty("type", out var typeProp)
            || typeProp.ValueKind != JsonValueKind.String)
            return false;
        var type = typeProp.GetString();
        return type is "assistant" or "user" or "result" or "tool_use" or "tool_result";
    }
}
