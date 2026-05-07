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
public sealed class CodexAgentRunner : CliAgentRunnerBase, IStructuredStreamAgentRunner, IAgentDefaultModelProvider
{
    private static readonly AsyncLocal<string?> CurrentStructuredStreamFlag = new();

    public override AgentKind Kind => AgentKind.Codex;

    public string Binary { get; init; } = "codex";

    protected override IReadOnlyList<string> ScratchpadHomeDirectories => [".codex/sessions", ".codex/history.jsonl"];

    protected override string PreemptProcessPattern => Binary;

    /// <summary>
    /// Default model passed to <c>--model</c> when no per-item override is provided.
    /// </summary>
    public string? DefaultModelId { get; init; } = "gpt-5.5";

    public async Task<bool> SupportsStructuredStreamAsync(ISandbox sandbox, CancellationToken ct = default) =>
        await DetectStructuredStreamFlagAsync(sandbox, ct).ConfigureAwait(false) is not null;

    /// <summary>
    /// ChatGPT-subscription auth: write <c>~/.codex/auth.json</c> into the sandbox.
    /// The codex CLI reads ONLY that file path; there's no env-var equivalent.
    ///
    /// We always materialise from the in-sandbox <c>CODEX_AUTH_JSON</c> env var
    /// (injected at sandbox boot from the agent credential) rather than from
    /// the credential parameter, because LlmReviewAuditor and similar
    /// call-sites pass credential=null on the assumption that env-var auth is
    /// sufficient — true for Claude (env-var-based), false for Codex
    /// (file-based). Reading from the in-sandbox env covers both code paths
    /// without requiring auditor changes; if the env var is absent, this is
    /// a no-op and codex falls back to OPENAI_API_KEY (api-key mode).
    /// </summary>
    protected override async Task<AgentResult?> PrepareSandboxAsync(
        ISandbox sandbox,
        string workingDirectory,
        AgentCredential? credential,
        AgentResumeContext? resume,
        CancellationToken ct = default)
    {
        var write = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["bash", "-c", "set -eu; if [ -n \"${CODEX_AUTH_JSON:-}\" ]; then mkdir -p \"$HOME/.codex\"; umask 077; printf '%s' \"$CODEX_AUTH_JSON\" > \"$HOME/.codex/auth.json\"; fi"],
        }, ct);
        if (!write.Success)
        {
            return new AgentResult(
                Success: false,
                Summary: $"failed to materialise codex auth: exit {write.ExitCode}",
                Stdout: write.Stdout,
                Stderr: write.Stderr);
        }
        return null;
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
        var structuredStreamFlag = captureStructuredStream
            ? await DetectStructuredStreamFlagAsync(sandbox, ct).ConfigureAwait(false)
            : null;
        var effectiveCaptureStructuredStream = captureStructuredStream && structuredStreamFlag is not null;

        var previousFlag = CurrentStructuredStreamFlag.Value;
        CurrentStructuredStreamFlag.Value = structuredStreamFlag;
        try
        {
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

            if (!captureStructuredStream || structuredStreamFlag is not null)
                return result;

            var warning = $"Warning: Codex CLI at '{Binary}' does not advertise --json or --json-stream; structured stream capture was disabled.";
            var stderr = string.IsNullOrEmpty(result.Stderr) ? warning : $"{warning}\n{result.Stderr}";
            return result with { Stderr = stderr };
        }
        finally
        {
            CurrentStructuredStreamFlag.Value = previousFlag;
        }
    }

    protected override AgentInvocation BuildInvocation(
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        bool captureStructuredStream = false)
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
        if (captureStructuredStream)
            argv.Add(CurrentStructuredStreamFlag.Value ?? "--json");
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

    private async Task<string?> DetectStructuredStreamFlagAsync(ISandbox sandbox, CancellationToken ct)
    {
        var help = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = [Binary, "exec", "--help"],
        }, ct).ConfigureAwait(false);

        if (!help.Success)
            return null;

        var output = string.Concat(help.Stdout, "\n", help.Stderr);
        if (output.Contains("--json-stream", StringComparison.Ordinal))
            return "--json-stream";
        if (output.Contains("--json", StringComparison.Ordinal))
            return "--json";
        return null;
    }
}
