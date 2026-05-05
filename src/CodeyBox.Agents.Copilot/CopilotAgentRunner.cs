using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Copilot;

/// <summary>
/// Drives the GitHub Copilot CLI. Copilot CLI uses the GitHub OAuth token
/// from GH_TOKEN/GITHUB_TOKEN; the orchestrator must inject ONLY a
/// least-privilege token (or a Copilot-only token if the org allows it).
/// </summary>
public sealed class CopilotAgentRunner : CliAgentRunnerBase
{
    public override AgentKind Kind => AgentKind.Copilot;

    public string Binary { get; init; } = "copilot";

    protected override AgentInvocation BuildInvocation(string prompt, AgentCredential? credential, string? modelId = null, string? reasoningMode = null)
    {
        // The Copilot CLI accepts a one-shot prompt with `-p`. Argument shape
        // may need adjusting per Copilot CLI version; centralised here so
        // updates don't ripple into the orchestrator.
        // modelId and reasoningMode are intentionally ignored: Copilot CLI does not
        // expose --model or reasoning flags.
        return new AgentInvocation([Binary, "-p", prompt]);
    }
}
