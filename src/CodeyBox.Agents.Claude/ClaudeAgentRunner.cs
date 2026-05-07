using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Agents.Claude;

/// <summary>
/// Drives the Claude Code CLI ("claude") in non-interactive mode. The agent
/// is expected to be installed in the sandbox image; the host injects only
/// the API token via tmpfs/env.
/// </summary>
public sealed class ClaudeAgentRunner : CliAgentRunnerBase, IStructuredStreamAgentRunner, IAgentDefaultModelProvider
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

    public async Task<bool> SupportsStructuredStreamAsync(ISandbox sandbox, CancellationToken ct = default)
    {
        var probe = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = [Binary, "--help"],
        }, ct).ConfigureAwait(false);

        var output = string.Concat(probe.Stdout, "\n", probe.Stderr);
        if (ContainsUnsupportedFlagMessage(output) || ContainsMissingBinaryMessage(output))
            return false;

        return probe.Success
            && output.Contains("--output-format", StringComparison.Ordinal)
            && output.Contains("stream-json", StringComparison.Ordinal)
            && output.Contains("--verbose", StringComparison.Ordinal);
    }

    public override async Task<AgentResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        CancellationToken ct = default,
        Action<string>? stdoutChunkCallback = null,
        bool captureStructuredStream = false)
    {
        var structuredStreamSupported = !captureStructuredStream
            || await SupportsStructuredStreamAsync(sandbox, ct).ConfigureAwait(false);
        var effectiveCaptureStructuredStream = captureStructuredStream && structuredStreamSupported;

        var result = await base.RunAsync(
            sandbox,
            workingDirectory,
            prompt,
            credential,
            modelId,
            reasoningMode,
            ct,
            stdoutChunkCallback,
            effectiveCaptureStructuredStream).ConfigureAwait(false);

        if (!captureStructuredStream || structuredStreamSupported)
            return result;

        var warning = $"Warning: Claude CLI at '{Binary}' does not support --output-format stream-json --verbose; structured stream capture was disabled.";
        var stderr = string.IsNullOrEmpty(result.Stderr) ? warning : $"{warning}\n{result.Stderr}";
        return result with { Stderr = stderr };
    }

    protected override AgentInvocation BuildInvocation(
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        bool captureStructuredStream = false)
        => BuildClaudeInvocation(prompt, modelId, resume: false, captureStructuredStream);

    protected override AgentInvocation BuildResumeInvocation(
        string prompt,
        AgentCredential? credential,
        AgentResumeContext resume,
        string? modelId = null,
        string? reasoningMode = null)
        => BuildClaudeInvocation(prompt, modelId, resume: true, captureStructuredStream: false);

    private AgentInvocation BuildClaudeInvocation(string prompt, string? modelId, bool resume, bool captureStructuredStream)
    {
        // claude --print sends a single prompt and exits. --dangerously-skip-permissions
        // is appropriate inside the sandbox: the VM boundary IS the permission boundary.
        var argv = new List<string> { Binary, "--print", "--dangerously-skip-permissions" };
        if (captureStructuredStream)
        {
            argv.Add("--output-format");
            argv.Add("stream-json");
            argv.Add("--verbose");
        }
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

    private static bool ContainsUnsupportedFlagMessage(string output) =>
        output.Contains("unknown option", StringComparison.OrdinalIgnoreCase)
        || output.Contains("unknown argument", StringComparison.OrdinalIgnoreCase)
        || output.Contains("unrecognized option", StringComparison.OrdinalIgnoreCase)
        || output.Contains("unrecognized argument", StringComparison.OrdinalIgnoreCase)
        || output.Contains("invalid option", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsMissingBinaryMessage(string output) =>
        output.Contains("not found", StringComparison.OrdinalIgnoreCase)
        || output.Contains("no such file", StringComparison.OrdinalIgnoreCase);
}
