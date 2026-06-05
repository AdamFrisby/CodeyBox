using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Copilot;

/// <summary>
/// Cost extractor for Copilot CLI output.
///
/// Copilot does not currently expose a stable usage footer in the captured CLI
/// streams. Returning <c>null</c> lets the orchestrator record the standard
/// elapsed-time fallback row: zero tokens, zero cost, but visible activity and
/// run counts for completed phases.
/// </summary>
public sealed class CopilotCostExtractor : IAgentCostExtractor
{
    public AgentKind Kind => AgentKind.Copilot;

    public ModelRateConfig? DefaultPricing => null;

    public AgentCostSnapshot? TryExtract(string? agentStdout, string? agentStderr)
        => null;
}
