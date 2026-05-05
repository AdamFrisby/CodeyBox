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

    /// <summary>
    /// Default model passed to <c>--model</c> when no per-item override is provided.
    /// </summary>
    public string? DefaultModelId { get; init; } = "gpt-5.5";

    protected override AgentInvocation BuildInvocation(string prompt, AgentCredential? credential, string? modelId = null, string? reasoningMode = null)
    {
        // Codex CLI: `codex exec <prompt>` runs a non-interactive turn and exits.
        var argv = new List<string> { Binary, "exec", "--full-auto" };
        var effectiveModel = modelId ?? DefaultModelId;
        if (!string.IsNullOrEmpty(effectiveModel))
        {
            argv.Add("--model");
            argv.Add(effectiveModel);
        }
        argv.Add(prompt);
        return new AgentInvocation(argv);
    }
}
