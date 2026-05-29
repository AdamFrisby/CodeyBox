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
    private readonly ClaudeThinkingBlockSanitizerConfig? _sanitizerConfig;

    public ClaudeAgentRunner() : this(defaults: null, rotationPusher: null, sanitizerConfig: null) { }

    public ClaudeAgentRunner(AgentDefaultsSnapshot? defaults) : this(defaults, rotationPusher: null, sanitizerConfig: null) { }

    /// <summary>
    /// Primary constructor.
    /// </summary>
    /// <param name="defaults">Live snapshot of per-agent default model IDs (see <see cref="AgentDefaultsSnapshot"/>).</param>
    /// <param name="rotationPusher">
    /// Optional host-side credential rotation bridge. When non-null, each
    /// <see cref="RunAsync"/> / <see cref="RunResumedAsync"/> call registers
    /// the active sandbox so a host-side token rotation during the run pushes
    /// the fresh access_token into the VM before its next Anthropic call goes
    /// 401. Registration is scoped with <c>using</c> — disposal unregisters
    /// the sandbox when the run completes (success or failure path). This is
    /// purely additive: the legacy <c>CLAUDE_CODE_OAUTH_TOKEN</c> env var
    /// remains the primary auth path.
    /// </param>
    /// <param name="sanitizerConfig">
    /// Hot-reloadable config snapshot gating transcript sanitisation. Null
    /// (e.g. when the hot-reload infrastructure isn't wired) defaults to
    /// enabled — see <see cref="ClaudeThinkingBlockSanitizerConfig.Enabled"/>.
    /// </param>
    public ClaudeAgentRunner(
        AgentDefaultsSnapshot? defaults,
        IClaudeTokenRotationPusher? rotationPusher,
        ClaudeThinkingBlockSanitizerConfig? sanitizerConfig = null)
    {
        _defaults = defaults;
        _rotationPusher = rotationPusher;
        _sanitizerConfig = sanitizerConfig;
    }

    public override AgentKind Kind => AgentKind.Claude;

    /// <summary>Default claude binary name on the sandbox PATH. The in-VM smoke probe pins to this so the probe and runner can never drift.</summary>
    public const string DefaultBinary = "claude";

    /// <summary>
    /// Path to the claude binary inside the sandbox. Override only if the
    /// sandbox image installs it elsewhere.
    /// </summary>
    public string Binary { get; init; } = DefaultBinary;

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
    ///
    /// <para>
    /// When a <paramref name="resume"/> context is supplied (preempt-recovery
    /// path), this method also sanitises the restored session JSONL transcripts
    /// under <c>~/.claude/projects/**/*.jsonl</c> so a replayed conversation
    /// cannot 400 with "thinking blocks cannot be modified"
    /// (anthropics/claude-code #63335). Gated by
    /// <see cref="ClaudeThinkingBlockSanitizerConfig.Enabled"/>.
    /// </para>
    /// </summary>
    protected override async Task<AgentResult?> PrepareSandboxAsync(
        ISandbox sandbox,
        string workingDirectory,
        AgentCredential? credential,
        AgentResumeContext? resume,
        CancellationToken ct = default)
    {
        // Preventive transcript sanitisation — runs before the CLI sees the
        // restored session files. Sanitisation is opportunistic: a failure
        // is logged but does not short-circuit the run (a busted sanitiser
        // shouldn't be more fatal than the 400 it is meant to prevent).
        if (resume is not null && (_sanitizerConfig is null || _sanitizerConfig.Enabled))
        {
            var sanitized = await ClaudeSessionSanitizer.SanitizeTranscriptsAsync(sandbox, ct)
                .ConfigureAwait(false);
            if (sanitized is not null)
            {
                AuditLog.ClaudeTranscriptSanitizerFailed(sanitized.Summary, sanitized.Stderr);
            }
        }

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

        result = await TryReactiveRetryAsync(
            sandbox, workingDirectory, prompt, credential, modelId, reasoningMode,
            ct, stdoutChunkCallback, effectiveCaptureStructuredStream,
            result,
            resumeContext: null).ConfigureAwait(false);

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
        using var _ = _rotationPusher?.RegisterActiveSandbox(sandbox);
        var result = await base.RunResumedAsync(
            sandbox,
            workingDirectory,
            prompt,
            credential,
            resume,
            modelId,
            reasoningMode,
            ct,
            stdoutChunkCallback).ConfigureAwait(false);

        result = await TryReactiveRetryAsync(
            sandbox, workingDirectory, prompt, credential, modelId, reasoningMode,
            ct, stdoutChunkCallback, captureStructuredStream: false,
            result,
            resumeContext: resume).ConfigureAwait(false);

        return result;
    }

    /// <summary>
    /// Reactive thinking-block 400 retry: if the result carries the thinking-block
    /// signature and the sanitiser is enabled, sanitise transcripts once and retry
    /// the underlying invocation. Returns the retried result on success, or the
    /// original result when retry is not applicable / the sanitiser itself fails.
    /// </summary>
    private async Task<AgentResult> TryReactiveRetryAsync(
        ISandbox sandbox,
        string workingDirectory,
        string prompt,
        AgentCredential? credential,
        string? modelId,
        string? reasoningMode,
        CancellationToken ct,
        Action<string>? stdoutChunkCallback,
        bool captureStructuredStream,
        AgentResult result,
        AgentResumeContext? resumeContext)
    {
        if (!result.Success
            && (_sanitizerConfig is null || _sanitizerConfig.Enabled)
            && ClaudeSessionSanitizer.IsThinkingBlockFailure(result))
        {
            var sanitized = await ClaudeSessionSanitizer.SanitizeTranscriptsAsync(sandbox, ct)
                .ConfigureAwait(false);
            if (sanitized is null)
            {
                // Sanitiser succeeded — retry the underlying invocation.
                if (resumeContext is not null)
                {
                    result = await base.RunResumedAsync(
                        sandbox,
                        workingDirectory,
                        prompt,
                        credential,
                        resumeContext,
                        modelId,
                        reasoningMode,
                        ct,
                        stdoutChunkCallback).ConfigureAwait(false);
                }
                else
                {
                    result = await base.RunAsync(
                        sandbox,
                        workingDirectory,
                        prompt,
                        credential,
                        modelId,
                        reasoningMode,
                        ct,
                        stdoutChunkCallback,
                        captureStructuredStream).ConfigureAwait(false);
                }
            }
            else
            {
                // Sanitiser itself failed — fold its detail into the result so
                // the operator sees why the workaround could not be applied.
                result = result with
                {
                    Summary = $"{result.Summary}; sanitiser failed: {sanitized.Summary}",
                    Stderr = string.Concat(result.Stderr, "\n", sanitized.Stderr),
                };
            }
        }

        return result;
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

    /// <summary>
    /// Picks the latest date-stamped variant whose id is
    /// <c>requested + "-" + &lt;date&gt;</c>; otherwise an exact match;
    /// otherwise the requested id unchanged.
    /// </summary>
    internal static string ResolveCanonicalModelId(string requested, IReadOnlyList<string> available)
    {
        var prefix = requested + "-";
        var datedMatch = available
            .Where(id => id.StartsWith(prefix, StringComparison.Ordinal))
            .OrderByDescending(static id => id, StringComparer.Ordinal)
            .FirstOrDefault();
        if (datedMatch is not null)
            return datedMatch;

        if (available.Contains(requested, StringComparer.Ordinal))
            return requested;

        return requested;
    }

    private AgentInvocation BuildClaudeInvocation(string prompt, string? modelId, string? reasoningMode, bool resume, bool captureStructuredStream)
    {
        var argv = new List<string> { Binary, "--print", "--dangerously-skip-permissions" };
        if (captureStructuredStream)
        {
            argv.Add("--output-format");
            argv.Add("stream-json");
            argv.Add("--verbose");
        }
        _ = resume;
        var effectiveModel = modelId ?? DefaultModelId;
        if (!string.IsNullOrEmpty(effectiveModel))
        {
            argv.Add("--model");
            argv.Add(effectiveModel);
        }
        if (!string.IsNullOrEmpty(reasoningMode))
        {
            argv.Add("--effort");
            argv.Add(reasoningMode);
        }
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
