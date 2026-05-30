using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Agents.Claude;

/// <summary>
/// Drives the Claude Code CLI ("claude") in non-interactive mode. The agent
/// is expected to be installed in the sandbox image; the host injects only
/// the API token via tmpfs/env.
///
/// <para>This runner deliberately does NOT implement
/// <see cref="ITextOnlyAgentRunner"/>. The previous text-only path POSTed
/// directly to <c>https://api.anthropic.com/v1/messages</c> with the
/// subscription OAuth token, which Anthropic can flag as a wrong-client-shape
/// usage of the credential and terminate the account. The pickup-time rebase
/// and merge-phase conflict resolvers now run inside the same sandbox via
/// <see cref="IAgentRunner.RunAsync"/> (the normal CLI shape), so no
/// text-only Claude path is needed. The advisory merge security review
/// gracefully skips when the chosen agent does not implement text-only
/// review.</para>
/// </summary>
public sealed class ClaudeAgentRunner : CliAgentRunnerBase, IStructuredStreamAgentRunner, IAgentDefaultModelProvider
{
    private readonly IClaudeTokenRotationPusher? _rotationPusher;
    private readonly AgentDefaultsSnapshot? _defaults;

    public ClaudeAgentRunner() : this(defaults: null, rotationPusher: null) { }

    public ClaudeAgentRunner(AgentDefaultsSnapshot? defaults) : this(defaults, rotationPusher: null) { }

    /// <summary>
    /// Optional <paramref name="rotationPusher"/> hooks the runner into the
    /// host-side credential watcher: while a Claude invocation is running in a
    /// sandbox, host-side rotations of <c>~/.claude/.credentials.json</c> are
    /// pushed into the VM's <c>~/.claude/.credentials.json</c> so the in-VM
    /// CLI does not 401 on its next Anthropic call. Disposing the registration
    /// (handled automatically by the <c>using</c> wrapper) removes the sandbox
    /// from the active set on completion of the run.
    /// </summary>
    public ClaudeAgentRunner(AgentDefaultsSnapshot? defaults, IClaudeTokenRotationPusher? rotationPusher)
    {
        _defaults = defaults;
        _rotationPusher = rotationPusher;
    }

    public override AgentKind Kind => AgentKind.Claude;

    /// <summary>
    /// Path to the claude binary inside the sandbox. Override only if the
    /// sandbox image installs it elsewhere.
    /// </summary>
    public string Binary { get; init; } = "claude";

    /// <summary>
    /// Default model passed to <c>--model</c> when no per-item override is
    /// provided. Sourced live from <see cref="AgentDefaultsSnapshot"/> so
    /// operator edits take effect on the next dispatched run without restart.
    /// </summary>
    public string? DefaultModelId => _defaults?.GetDefault(Kind.Value);

    protected override IReadOnlyList<string> ScratchpadHomeDirectories => [".claude/projects", ".claude/todos"];

    protected override string PreemptProcessPattern => Binary;

    /// <summary>
    /// Materialises the host's <c>~/.claude/.credentials.json</c> inside the
    /// sandbox if the env-var bundle is present (set by
    /// <c>ClaudeOAuthFileCredentialProvider</c>). The bundle is sanitised — it
    /// carries the access_token (plus the expires_at hint when available) but
    /// <em>omits</em> the refresh_token, so the in-VM <c>claude</c> CLI cannot
    /// initiate its own refresh. This is deliberate: Anthropic's refresh tokens
    /// are single-use, and the host CLI is the sole party allowed to refresh
    /// (see <c>ClaudeOAuthFileCredentialProvider</c>'s class summary for the
    /// race rationale). An in-VM iteration that outlives the access_token's
    /// expiry surfaces as a 401, which is treated as transient/auth (not a
    /// quota event) and audit-logged via
    /// <c>AuditLog.ClaudeUnauthorizedObserved</c>; the next iteration picks up
    /// the host's currently-fresh token. The legacy
    /// <c>CLAUDE_CODE_OAUTH_TOKEN</c> env var remains the primary auth path;
    /// this hook is purely additive.
    /// </summary>
    protected override async Task<AgentResult?> PrepareSandboxAsync(
        ISandbox sandbox,
        string workingDirectory,
        AgentCredential? credential,
        AgentResumeContext? resume,
        CancellationToken ct = default)
    {
        // Skip the bash hook entirely when no OAuth bundle is present (e.g.
        // ANTHROPIC_API_KEY flows); the CLI uses whichever env-var auth path
        // the credential pipeline plugged in.
        if (credential is null
            || !credential.EnvironmentVariables.ContainsKey("CODEYBOX_CLAUDE_OAUTH_JSON"))
            return null;

        // umask 077 ensures the new file is 0600; the explicit chmod is belt-
        // and-braces in case the sandbox image overrides umask elsewhere.
        var script =
            "set -eu\n" +
            "umask 077\n" +
            "mkdir -p \"$HOME/.claude\"\n" +
            "if [ -n \"${CODEYBOX_CLAUDE_OAUTH_JSON:-}\" ]; then\n" +
            "  printf '%s' \"$CODEYBOX_CLAUDE_OAUTH_JSON\" > \"$HOME/.claude/.credentials.json\"\n" +
            "  chmod 600 \"$HOME/.claude/.credentials.json\"\n" +
            "fi\n";
        var write = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["bash", "-c", script],
        }, ct).ConfigureAwait(false);
        if (!write.Success)
        {
            return new AgentResult(
                Success: false,
                Summary: $"failed to materialise claude auth: exit {write.ExitCode}",
                Stdout: write.Stdout,
                Stderr: write.Stderr);
        }
        return null;
    }

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
        // Register the sandbox so a host-side credential rotation while this
        // iteration is running pushes the fresh access_token into the VM
        // before its next Anthropic call goes 401. Unregistration is deferred
        // until the run completes (success or failure path).
        using var _ = _rotationPusher?.RegisterActiveSandbox(sandbox);

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

    public override async Task<AgentResult> RunResumedAsync(
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
        // Same rationale as RunAsync — the resumed iteration runs the CLI in
        // the same sandbox and is equally vulnerable to a mid-run host
        // rotation invalidating its access_token.
        using var _ = _rotationPusher?.RegisterActiveSandbox(sandbox);
        return await base.RunResumedAsync(
            sandbox,
            workingDirectory,
            prompt,
            credential,
            resume,
            modelId,
            reasoningMode,
            ct,
            stdoutChunkCallback).ConfigureAwait(false);
    }

    protected override AgentInvocation BuildInvocation(
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        bool captureStructuredStream = false)
        => BuildClaudeInvocation(prompt, modelId, reasoningMode, resume: false, captureStructuredStream);

    protected override AgentInvocation BuildResumeInvocation(
        string prompt,
        AgentCredential? credential,
        AgentResumeContext resume,
        string? modelId = null,
        string? reasoningMode = null)
        => BuildClaudeInvocation(prompt, modelId, reasoningMode, resume: true, captureStructuredStream: false);

    private AgentInvocation BuildClaudeInvocation(string prompt, string? modelId, string? reasoningMode, bool resume, bool captureStructuredStream)
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
        // NOTE: We intentionally do NOT pass claude's `--resume` flag, even on
        // the preempt-recovery path (where `resume == true`). claude's --resume
        // requires a valid session ID; the previous implementation supplied
        // none and relied on claude parsing the prompt-positional as a (bogus)
        // session ID, which masked the bug. Once the prompt moved to stdin
        // (see comment below), --resume started failing loudly with
        // "Error: --resume requires a valid session ID". And there's no real
        // claude-side session to resume anyway — every sandbox is a fresh
        // clone with no ~/.claude/sessions content from prior iterations. The
        // CodeyBox-level "resume" semantic (re-dispatch the same iteration
        // after preemption) is handled entirely by the orchestrator;
        // the agent CLI sees a brand-new conversation with the full prompt.
        _ = resume;
        var effectiveModel = modelId ?? DefaultModelId;
        if (!string.IsNullOrEmpty(effectiveModel))
        {
            argv.Add("--model");
            argv.Add(effectiveModel);
        }
        // claude --effort accepts: low | medium | high | xhigh | max. Pass
        // through verbatim when set; the CLI rejects unknown values.
        if (!string.IsNullOrEmpty(reasoningMode))
        {
            argv.Add("--effort");
            argv.Add(reasoningMode);
        }
        // Pass the prompt via stdin rather than as a positional argv. Linux's
        // MAX_ARG_STRLEN is 128 KiB per single argv element; rework prompts that
        // include many audit findings can exceed that and surface as exit 126
        // from the sandbox's `exec "$@"`. Stdin has no such limit. `claude --print`
        // reads stdin when no positional prompt is given. (The sandbox wrapper
        // forwards stdin automatically when SandboxExec.Stdin is non-null, via
        // its --keep-stdin path.)
        return new AgentInvocation(argv, Stdin: prompt);
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
