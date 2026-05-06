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

    /// <summary>
    /// Default model passed to <c>--model</c> when no per-item override is provided.
    /// Pinned to Opus to avoid the CLI defaulting to a lighter model.
    /// </summary>
    public string? DefaultModelId { get; init; } = "claude-opus-4-7";

    protected override IReadOnlyList<string> ScratchpadHomeDirectories => [".claude/projects", ".claude/todos"];

    protected override string PreemptProcessPattern => Binary;

    protected override AgentInvocation BuildInvocation(string prompt, AgentCredential? credential, string? modelId = null, string? reasoningMode = null)
        => BuildClaudeInvocation(prompt, modelId, resume: false);

    protected override AgentInvocation BuildResumeInvocation(
        string prompt,
        AgentCredential? credential,
        AgentResumeContext resume,
        string? modelId = null,
        string? reasoningMode = null)
        => BuildClaudeInvocation(prompt, modelId, resume: true);

    private AgentInvocation BuildClaudeInvocation(string prompt, string? modelId, bool resume)
    {
        // claude --print sends a single prompt and exits. --dangerously-skip-permissions
        // is appropriate inside the sandbox: the VM boundary IS the permission boundary.
        var argv = new List<string> { Binary, "--print", "--dangerously-skip-permissions" };
        if (resume)
            argv.Add("--resume");
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
