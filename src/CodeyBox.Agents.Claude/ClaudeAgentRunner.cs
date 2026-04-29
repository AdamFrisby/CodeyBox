using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Claude;

/// <summary>
/// Drives the Claude Code CLI ("claude") in non-interactive mode. The agent
/// is expected to be installed in the sandbox image; the host injects only
/// the API token via tmpfs/env.
/// </summary>
public sealed class ClaudeAgentRunner : CliAgentRunnerBase
{
    public override AgentKind Kind => AgentKind.Claude;

    /// <summary>
    /// Path to the claude binary inside the sandbox. Override only if the
    /// sandbox image installs it elsewhere.
    /// </summary>
    public string Binary { get; init; } = "claude";

    protected override AgentInvocation BuildInvocation(string prompt, AgentCredential? credential, string? modelId = null)
    {
        // claude --print sends a single prompt and exits. --dangerously-skip-permissions
        // is appropriate inside the sandbox: the VM boundary IS the permission boundary.
        var argv = new List<string> { Binary, "--print", "--dangerously-skip-permissions" };
        if (!string.IsNullOrEmpty(modelId))
        {
            argv.Add("--model");
            argv.Add(modelId);
        }
        argv.Add(prompt);
        return new AgentInvocation(argv);
    }
}
