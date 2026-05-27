using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Agents.Opencode;

/// <summary>
/// Drives the sst/opencode CLI (<c>opencode run</c>) in non-interactive mode.
///
/// <para>opencode bundles access to multiple model providers (DeepSeek,
/// Anthropic, OpenAI, …) under a single subscription credential — the
/// "opencode Go" tier. The default model picked here is intentionally a
/// DeepSeek variant because that is the differentiating capability opencode
/// adds versus the other registered agents; operators override <c>ModelId</c>
/// per agent-class member to route through any other provider the
/// subscription supports.</para>
///
/// <para>Auth: opencode hard-reads a credentials file written by
/// <c>opencode auth login</c>. Path is set per-deployment via
/// <c>CODEYBOX_OPENCODE_AUTH_FILE</c>; <see cref="OpencodeAgentRunner"/>
/// materialises the file from <c>OPENCODE_AUTH_JSON</c> in the credential
/// bundle before invoking the CLI, mirroring the Codex pattern.</para>
/// </summary>
public sealed class OpencodeAgentRunner : CliAgentRunnerBase, IAgentDefaultModelProvider
{
    public override AgentKind Kind => AgentKind.Opencode;

    /// <summary>Path to the opencode binary inside the sandbox.</summary>
    public string Binary { get; init; } = "opencode";

    /// <summary>
    /// Default model passed to <c>--model</c> when the agent-class member
    /// does not override it. A DeepSeek-coder variant; operators tune the
    /// exact id to match what their opencode subscription enumerates as the
    /// best DeepSeek-coder option (run <c>opencode models</c> on the host
    /// to confirm).
    /// </summary>
    public string? DefaultModelId { get; init; } = "deepseek/deepseek-coder";

    protected override IReadOnlyList<string> ScratchpadHomeDirectories => [".local/share/opencode", ".config/opencode"];

    protected override string PreemptProcessPattern => Binary;

    /// <summary>
    /// Materialises the opencode credentials file from
    /// <c>OPENCODE_AUTH_JSON</c> if present, mirroring Codex's
    /// <c>~/.codex/auth.json</c> pattern. The destination path inside the
    /// sandbox is supplied by the credential bundle as
    /// <c>OPENCODE_AUTH_DEST_PATH</c>; if unset the runner falls back to
    /// <c>~/.local/share/opencode/auth.json</c>, which matches opencode's
    /// XDG-default location at the time of writing. Operators verify the
    /// real path via <c>opencode auth login</c> and override
    /// <c>CODEYBOX_OPENCODE_AUTH_DEST</c> on the host if needed.
    /// </summary>
    protected override async Task<AgentResult?> PrepareSandboxAsync(
        ISandbox sandbox,
        string workingDirectory,
        AgentCredential? credential,
        AgentResumeContext? resume,
        CancellationToken ct = default)
    {
        if (credential is null
            || !credential.EnvironmentVariables.ContainsKey("OPENCODE_AUTH_JSON"))
            return null;

        // Defensive: write under XDG default unless the caller supplied an
        // explicit destination via the credential bundle (set by the
        // credential provider on the host from CODEYBOX_OPENCODE_AUTH_DEST).
        // The runner does not parse the JSON; opencode owns its schema and
        // any drift there is the operator's to verify with `opencode auth`.
        //
        // Order matters:
        //   1. umask 077 BEFORE mkdir -p, so the parent directory is 0700
        //      (not 0755 inherited from the system umask).
        //   2. printf truncate-rewrite, then explicit chmod 600 — umask
        //      only affects NEWLY created files; if a destination auth.json
        //      already exists with looser modes (e.g. 0644 from a prior
        //      `opencode auth login`), the truncate does not change the
        //      mode. chmod pins 0600 regardless of pre-existing state.
        var script =
            "set -eu\n" +
            "dest=\"${OPENCODE_AUTH_DEST_PATH:-$HOME/.local/share/opencode/auth.json}\"\n" +
            "umask 077\n" +
            "mkdir -p \"$(dirname \"$dest\")\"\n" +
            "if [ -n \"${OPENCODE_AUTH_JSON:-}\" ]; then\n" +
            "  printf '%s' \"$OPENCODE_AUTH_JSON\" > \"$dest\"\n" +
            "  chmod 600 \"$dest\"\n" +
            "fi\n";
        var write = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["bash", "-c", script],
        }, ct);
        if (!write.Success)
        {
            return new AgentResult(
                Success: false,
                Summary: $"failed to materialise opencode auth: exit {write.ExitCode}",
                Stdout: write.Stdout,
                Stderr: write.Stderr);
        }
        return null;
    }

    /// <summary>
    /// Builds the <c>opencode run</c> argv. The <paramref name="captureStructuredStream"/>
    /// parameter is currently discarded — opencode's structured stream
    /// format has not been verified against a live invocation in this
    /// environment, so the runner does not implement
    /// <see cref="IStructuredStreamAgentRunner"/>. If you flip a caller to
    /// request structured stream capture, expect plain stdout/stderr back
    /// rather than parsed events.
    /// </summary>
    protected override AgentInvocation BuildInvocation(
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        bool captureStructuredStream = false)
    {
        // `opencode run <prompt>` is the documented non-interactive entry
        // point. Pass the prompt via stdin (matches the Codex / Gemini
        // pattern) to dodge the 128 KiB MAX_ARG_STRLEN ceiling that rework
        // prompts can blow through.
        var argv = new List<string> { Binary, "run" };

        var effectiveModel = !string.IsNullOrEmpty(modelId) ? modelId : DefaultModelId;
        if (!string.IsNullOrEmpty(effectiveModel))
        {
            argv.Add("--model");
            argv.Add(effectiveModel);
        }

        // Reasoning effort: DeepSeek-R1 / Anthropic via opencode both have a
        // reasoning knob, but the exact CLI flag has not been verified
        // against `opencode run --help` in this environment. Operators that
        // need it can set the OPENCODE_REASONING_FLAG env var on the host
        // to the correct flag name (e.g. "--reasoning-effort"); when set we
        // append it followed by the requested mode. Without verification we
        // do NOT speculate on the flag name. See docs/agents.md.
        if (!string.IsNullOrEmpty(reasoningMode))
        {
            var flag = Environment.GetEnvironmentVariable("OPENCODE_REASONING_FLAG");
            if (!string.IsNullOrEmpty(flag))
            {
                argv.Add(flag);
                argv.Add(reasoningMode);
            }
        }

        _ = captureStructuredStream;
        return new AgentInvocation(argv, Stdin: prompt);
    }
}
