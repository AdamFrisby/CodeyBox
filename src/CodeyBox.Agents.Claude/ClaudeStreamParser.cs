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
    public override bool TryClaim(JsonElement line) =>
        AgentStreamEventShapes.IsClaudeStreamJsonEvent(line);
}
