using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Copilot;

/// <summary>
/// Stream parser slot for the GitHub Copilot CLI. Copilot CLI emits plaintext
/// stdout in non-interactive mode (no structured stream-json), so the
/// captured stream file is plaintext and <see cref="FlexibleAgentStreamParser.ParseAsync"/>
/// returns <see cref="AgentStreamSummary.Unsupported"/>. This parser exists so
/// that <see cref="AgentStreamParserSelection.ResolveKind"/> resolves Copilot
/// runs to <c>AgentKind.Copilot</c> rather than <c>unknown</c>;
/// <see cref="StreamAnalysisService"/> then re-runs the file through the
/// plaintext-fallback summariser while preserving the Copilot attribution.
/// </summary>
public sealed class CopilotStreamParser : FlexibleAgentStreamParser
{
    public CopilotStreamParser(AgentStreamParserOptions? options = null)
        : base(AgentKind.Copilot, options)
    {
    }

    public override bool TryClaim(JsonElement line) => false;
}
