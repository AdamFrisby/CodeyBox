using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Agents;

/// <summary>
/// Shared scaffolding for agent runners that drive a one-shot CLI binary
/// inside the sandbox. Subclasses describe how to invoke their CLI; this base
/// handles credential staging and result wrapping uniformly.
/// </summary>
public abstract class CliAgentRunnerBase : IPreemptibleAgentRunner, IResumableAgentRunner
{
    public abstract AgentKind Kind { get; }

    /// <summary>
    /// Build the argv to execute inside the sandbox for a given prompt. The
    /// prompt may be passed via argv, stdin, or a file; subclasses choose.
    /// </summary>
    protected abstract AgentInvocation BuildInvocation(string prompt, AgentCredential? credential, string? modelId = null, string? reasoningMode = null);

    /// <summary>
    /// CLI state paths under HOME whose presence is useful to note during
    /// graceful preemption. The default preempt hook intentionally records only
    /// a manifest for these paths; opaque CLI transcript/history content is not
    /// copied into git-backed checkpoints.
    /// </summary>
    protected virtual IReadOnlyList<string> ScratchpadHomeDirectories => [];

    /// <summary>
    /// Pattern used only after scratchpad capture to ask the running CLI to stop.
    /// </summary>
    protected virtual string PreemptProcessPattern => Kind.Value;

    /// <summary>
    /// Build the invocation used after a checkpoint restore. The default restores
    /// CLI state and reruns the normal one-shot command; CLIs with a native resume
    /// mode can override this to add the relevant flag.
    /// </summary>
    protected virtual AgentInvocation BuildResumeInvocation(
        string prompt,
        AgentCredential? credential,
        AgentResumeContext resume,
        string? modelId = null,
        string? reasoningMode = null)
        => BuildInvocation(prompt, credential, modelId, reasoningMode);

    /// <summary>
    /// Gives subclasses a chance to materialise non-argv CLI prerequisites
    /// immediately before invoking the binary. Returning a result short-circuits
    /// the run with that failure.
    /// </summary>
    protected virtual Task<AgentResult?> PrepareSandboxAsync(
        ISandbox sandbox,
        string workingDirectory,
        AgentCredential? credential,
        AgentResumeContext? resume,
        CancellationToken ct)
        => Task.FromResult<AgentResult?>(null);

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
        var preparation = await PrepareSandboxAsync(sandbox, workingDirectory, credential, resume: null, ct);
        if (preparation is not null)
            return preparation;

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

    public virtual async Task<AgentResult> RunResumedAsync(
        ISandbox sandbox,
        string workingDirectory,
        string prompt,
        AgentCredential? credential,
        AgentResumeContext resume,
        string? modelId = null,
        string? reasoningMode = null,
        CancellationToken ct = default,
        Action<string>? stdoutChunkCallback = null)
    {
        await RestoreScratchpadAsync(sandbox, workingDirectory, resume, ct);

        var preparation = await PrepareSandboxAsync(sandbox, workingDirectory, credential, resume, ct);
        if (preparation is not null)
            return preparation;

        var invocation = BuildResumeInvocation(prompt, credential, resume, modelId, reasoningMode);
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
        var dirs = string.Join(" ", ScratchpadHomeDirectories.Select(ShellQuote));
        var pattern = PreemptProcessPattern;
        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv =
            [
                "sh", "-c",
                $$"""
                set -eu
                mkdir -p .codeybox
                scratch_tmp="$(mktemp -d .codeybox/preempt-scratchpad.XXXXXX)"
                manifest="$scratch_tmp/manifest.txt"
                printf '%s\n' "Preempt requested at $(date -u +%FT%TZ)." > "$manifest"
                captured=0
                for rel in {{dirs}} ""; do
                  [ -n "$rel" ] || continue
                  rel="${rel#/}"
                  if [ -e "$HOME/$rel" ]; then
                    printf '%s\n' "observed HOME/$rel; content not captured in git-backed preempt checkpoint" >> "$manifest"
                    captured=1
                  fi
                  if [ -e "$rel" ] && [ "$PWD/$rel" != "$HOME/$rel" ]; then
                    printf '%s\n' "observed WORK/$rel; content not captured in git-backed preempt checkpoint" >> "$manifest"
                    captured=1
                  fi
                done
                if [ "$captured" -eq 0 ]; then
                  printf '%s\n' "No known CLI scratchpad directory existed at preempt time." >> "$manifest"
                fi
                tar -czf .codeybox/preempt-scratchpad.tgz -C "$scratch_tmp" .
                cp "$manifest" .codeybox/preempt-scratchpad.md
                rm -rf "$scratch_tmp"
                for pid in $(pgrep -f "$1" 2>/dev/null || true); do
                  [ "$pid" = "$$" ] && continue
                  kill -TERM "$pid" 2>/dev/null || true
                done
                """,
                "codeybox-preempt",
                pattern,
            ],
            WorkingDirectory = workingDirectory,
        }, ct);
        if (!result.Success)
            throw new InvalidOperationException($"agent preempt signal failed (exit {result.ExitCode}): {result.Stderr}");
    }

    private static async Task RestoreScratchpadAsync(
        ISandbox sandbox,
        string workingDirectory,
        AgentResumeContext resume,
        CancellationToken ct)
    {
        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv =
            [
                "sh", "-c",
                """
                set -eu
                archive="$1"
                [ -f "$archive" ] || exit 0
                scratch_tmp="$(mktemp -d .codeybox/resume-scratchpad.XXXXXX)"
                tar -xzf "$archive" -C "$scratch_tmp"
                if [ -d "$scratch_tmp/home" ]; then
                  cp -a "$scratch_tmp/home/." "$HOME/"
                fi
                if [ -d "$scratch_tmp/work" ]; then
                  cp -a "$scratch_tmp/work/." "."
                fi
                rm -rf "$scratch_tmp"
                """,
                "codeybox-resume",
                resume.ScratchpadArchivePath,
            ],
            WorkingDirectory = workingDirectory,
        }, ct);
        if (!result.Success)
            throw new InvalidOperationException($"agent scratchpad restore failed (exit {result.ExitCode}): {result.Stderr}");
    }

    private static string ShellQuote(string value) =>
        "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    protected sealed record AgentInvocation(
        IReadOnlyList<string> Argv,
        IReadOnlyDictionary<string, string>? ExtraEnvironment = null,
        string? Stdin = null);
}
