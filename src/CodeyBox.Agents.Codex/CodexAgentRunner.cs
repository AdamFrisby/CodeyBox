using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Codex;

/// <summary>
/// Drives the OpenAI Codex CLI. Reads the API key from OPENAI_API_KEY which
/// the orchestrator injects via the credential bundle.
/// </summary>
public sealed class CodexAgentRunner : CliAgentRunnerBase
{
    public override AgentKind Kind => AgentKind.Codex;

    public string Binary { get; init; } = "codex";

    protected override AgentInvocation BuildInvocation(string prompt, AgentCredential? credential)
    {
        // Codex CLI: `codex exec <prompt>` runs a non-interactive turn and exits.
        var argv = new List<string>
        {
            Binary,
            "exec",
            "--full-auto",
            prompt,
        };
        return new AgentInvocation(argv);
    }
}
