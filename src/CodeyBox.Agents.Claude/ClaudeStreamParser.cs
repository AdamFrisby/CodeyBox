using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Claude;

public sealed class ClaudeStreamParser : FlexibleAgentStreamParser
{
    public ClaudeStreamParser(AgentStreamParserOptions? options = null)
        : base(AgentKind.Claude, options)
    {
    }
}
