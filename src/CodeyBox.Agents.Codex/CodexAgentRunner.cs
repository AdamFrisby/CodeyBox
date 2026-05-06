using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Agents.Codex;

/// <summary>
/// Drives the OpenAI Codex CLI. Two auth modes:
/// <list type="bullet">
///   <item>API key: <c>OPENAI_API_KEY</c> env var (injected via credential bundle).</item>
///   <item>ChatGPT subscription: <c>~/.codex/auth.json</c> — codex CLI hard-reads
///         this path and offers no env-var override. The runner writes it from the
///         <c>CODEX_AUTH_JSON</c> credential env var before invoking codex.</item>
/// </list>
/// </summary>
public sealed class CodexAgentRunner : CliAgentRunnerBase
{
    public override AgentKind Kind => AgentKind.Codex;

    public string Binary { get; init; } = "codex";

    protected override IReadOnlyList<string> ScratchpadHomeDirectories => [".codex/sessions", ".codex/history.jsonl"];

    protected override string PreemptProcessPattern => Binary;

    /// <summary>
    /// Default model passed to <c>--model</c> when no per-item override is provided.
    /// </summary>
    public string? DefaultModelId { get; init; } = "gpt-5.5";

    protected override async Task<AgentResult?> PrepareSandboxAsync(
        ISandbox sandbox,
        string workingDirectory,
        AgentCredential? credential,
        AgentResumeContext? resume,
        CancellationToken ct = default)
    {
        // ChatGPT-subscription auth: write ~/.codex/auth.json into the sandbox
        // from the credential's CODEX_AUTH_JSON env var. The codex CLI reads
        // ONLY that file path; there's no env-var equivalent. API-key mode
        // works via env var alone, so skip silently when absent. This hook is
        // used for both fresh and preempt-resumed runs.
        if (credential is not null
            && credential.EnvironmentVariables.TryGetValue("CODEX_AUTH_JSON", out var authJson)
            && !string.IsNullOrEmpty(authJson))
        {
            var write = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["bash", "-c", "set -e; mkdir -p \"$HOME/.codex\"; umask 077; cat > \"$HOME/.codex/auth.json\""],
                Stdin = authJson,
            }, ct);
            if (!write.Success)
            {
                return new AgentResult(
                    Success: false,
                    Summary: $"failed to materialise codex auth: exit {write.ExitCode}",
                    Stdout: write.Stdout,
                    Stderr: write.Stderr);
            }
        }

        return null;
    }

    protected override AgentInvocation BuildInvocation(string prompt, AgentCredential? credential, string? modelId = null, string? reasoningMode = null)
    {
        // `codex exec <prompt>` runs a non-interactive turn and exits.
        //
        // --dangerously-bypass-approvals-and-sandbox: codex would otherwise
        // wrap each tool call in bubblewrap, which fails inside our Multipass
        // VM with "bwrap: loopback: Failed RTM_NEWADDR: Operation not permitted"
        // (no nested user-ns + networking allowed). The Multipass VM IS the
        // sandbox boundary; codex's docs explicitly recommend this flag for
        // "environments that are externally sandboxed".
        var argv = new List<string>
        {
            Binary, "exec",
            "--dangerously-bypass-approvals-and-sandbox",
        };
        var effectiveModel = !string.IsNullOrEmpty(modelId) ? modelId : DefaultModelId;
        if (!string.IsNullOrEmpty(effectiveModel))
        {
            argv.Add("--model");
            argv.Add(effectiveModel);
        }
        // Reasoning effort is a config-key on the codex CLI rather than a
        // dedicated flag; pass through `-c` overrides. Accepted values per
        // the OpenAI Responses API: "minimal" | "low" | "medium" | "high".
        if (!string.IsNullOrEmpty(reasoningMode))
        {
            argv.Add("-c");
            argv.Add($"model_reasoning_effort={reasoningMode}");
        }
        argv.Add(prompt);
        return new AgentInvocation(argv);
    }
}
