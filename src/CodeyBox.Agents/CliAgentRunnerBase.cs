using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Agents;

/// <summary>
/// Shared scaffolding for agent runners that drive a one-shot CLI binary
/// inside the sandbox. Subclasses describe how to invoke their CLI; this base
/// handles credential staging and result wrapping uniformly.
/// </summary>
public abstract class CliAgentRunnerBase : IPreemptibleAgentRunner
{
    public abstract AgentKind Kind { get; }

    /// <summary>
    /// Build the argv to execute inside the sandbox for a given prompt. The
    /// prompt may be passed via argv, stdin, or a file; subclasses choose.
    /// </summary>
    protected abstract AgentInvocation BuildInvocation(string prompt, AgentCredential? credential, string? modelId = null, string? reasoningMode = null);

    public virtual async Task<AgentResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        CancellationToken ct = default,
        Action<string>? stdoutChunkCallback = null)
    {
        // The credential env is set on the container at boot via SandboxSpec.Environment
        // so secrets don't land on per-exec argv. We deliberately do NOT merge
        // credential.EnvironmentVariables into the per-exec ExtraEnvironment.
        var invocation = BuildInvocation(prompt, credential, modelId, reasoningMode);
        var exec = new SandboxExec
        {
            Argv = invocation.Argv,
            WorkingDirectory = workingDirectory,
            ExtraEnvironment = invocation.ExtraEnvironment,
            Stdin = invocation.Stdin,
            StdoutChunkCallback = stdoutChunkCallback,
        };

        var result = await sandbox.ExecAsync(exec, ct);
        return new AgentResult(
            Success: result.Success,
            Summary: result.Success ? "ok" : $"agent exited {result.ExitCode}",
            Stdout: result.Stdout,
            Stderr: result.Stderr);
    }

    public virtual async Task RequestPreemptAsync(ISandbox sandbox, string workingDirectory, CancellationToken ct = default)
    {
        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv =
            [
                "sh", "-c",
                """
                set -eu
                mkdir -p .codeybox
                printf '%s\n' "Preempt requested at $(date -u +%FT%TZ)." > .codeybox/preempt-scratchpad.md
                printf '%s\n' "CLI-specific scratchpad export is unavailable; checkpointed git state is authoritative." >> .codeybox/preempt-scratchpad.md
                pkill -TERM -f "$0" 2>/dev/null || true
                """,
                Kind.Value,
            ],
            WorkingDirectory = workingDirectory,
        }, ct);
        if (!result.Success)
            throw new InvalidOperationException($"agent preempt signal failed (exit {result.ExitCode}): {result.Stderr}");
    }

    protected sealed record AgentInvocation(
        IReadOnlyList<string> Argv,
        IReadOnlyDictionary<string, string>? ExtraEnvironment = null,
        string? Stdin = null);
}
