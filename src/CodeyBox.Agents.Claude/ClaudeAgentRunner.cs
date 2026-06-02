using System.Text;
using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Agents.Claude;

/// <summary>
/// Drives the Claude Code CLI ("claude") in non-interactive mode. The agent
/// is expected to be installed in the sandbox image; the host injects only
/// the API token via tmpfs/env.
///
/// <para>The text-only path (<see cref="ITextOnlyAgentRunner"/>) is restricted
/// to the raw-API credential (<c>ANTHROPIC_API_KEY</c>): subscription OAuth
/// against <c>/v1/messages</c> is the wrong-client-shape usage that risks
/// account termination, so <see cref="GetTextOnlyUnavailabilityReason"/>
/// declines OAuth-only credentials and the rebase router walks past Claude
/// to the next class member. The configured model id (e.g.
/// <c>claude-opus-4-7</c>) is an undated alias that the <c>claude</c> CLI
/// resolves internally; the Messages API only accepts the dated canonical id
/// (e.g. <c>claude-opus-4-7-YYYYMMDD</c>) and answers an undated alias with
/// HTTP 404. The text-only call therefore resolves the alias via
/// <c>GET /v1/models</c> before posting to <c>/v1/messages</c>; a probe
/// failure leaves the requested id untouched (best-effort, so we never
/// degrade a working call).</para>
/// </summary>
public sealed class ClaudeAgentRunner : CliAgentRunnerBase, IStructuredStreamAgentRunner, IAgentDefaultModelProvider, ITextOnlyAgentRunner
{
    private static readonly HttpClient SharedTextOnlyHttp = new();

    internal const string MessagesEndpoint = "https://api.anthropic.com/v1/messages";
    // /v1/models endpoint and anthropic-version pin live in ClaudeModelListProbe;
    // re-export so the text-only path (which fetches /v1/models for alias→canonical
    // resolution) shares a single source of truth with the startup probe.
    internal const string ModelsEndpoint = ClaudeModelListProbe.ModelsEndpoint;
    internal const string AnthropicVersion = ClaudeModelListProbe.AnthropicVersion;
    internal const int TextOnlyMaxTokens = 8192;
    internal const string MissingApiKeyReason =
        "ANTHROPIC_API_KEY is required for Claude text-only calls (subscription OAuth declined for account-safety)";

    private readonly IClaudeTokenRotationPusher? _rotationPusher;
    private readonly AgentDefaultsSnapshot? _defaults;
    private readonly ClaudeThinkingBlockSanitizerConfig? _sanitizerConfig;
    private readonly HttpClient _textOnlyHttp;
    private static readonly ClaudeQuotaFailureDetector SessionResumeQuotaDetector = new();

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
        : this(defaults, rotationPusher, sanitizerConfig, textOnlyHttp: null)
    {
    }

    /// <summary>
    /// Internal test seam: lets unit tests inject an <see cref="HttpClient"/>
    /// backed by a fake <see cref="HttpMessageHandler"/> so the text-only
    /// path can be exercised offline without reaching api.anthropic.com.
    /// Production wiring uses the process-wide shared HttpClient.
    /// </summary>
    internal ClaudeAgentRunner(
        AgentDefaultsSnapshot? defaults,
        IClaudeTokenRotationPusher? rotationPusher,
        ClaudeThinkingBlockSanitizerConfig? sanitizerConfig,
        HttpClient? textOnlyHttp)
    {
        _defaults = defaults;
        _rotationPusher = rotationPusher;
        _sanitizerConfig = sanitizerConfig;
        _textOnlyHttp = textOnlyHttp ?? SharedTextOnlyHttp;
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

        return WithStructuredStreamDisabledWarning(result);
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
        var structuredStreamSupported = await SupportsStructuredStreamAsync(sandbox, ct).ConfigureAwait(false);
        var result = await RunResumedCoreAsync(
            sandbox,
            workingDirectory,
            prompt,
            credential,
            resume,
            modelId,
            reasoningMode,
            ct,
            stdoutChunkCallback,
            captureStructuredStream: structuredStreamSupported).ConfigureAwait(false);

        result = await TryReactiveRetryAsync(
            sandbox, workingDirectory, prompt, credential, modelId, reasoningMode,
            ct, stdoutChunkCallback, structuredStreamSupported,
            result,
            resumeContext: resume).ConfigureAwait(false);

        return structuredStreamSupported
            ? result
            : WithStructuredStreamDisabledWarning(result);
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
                    result = await RunResumedCoreAsync(
                        sandbox,
                        workingDirectory,
                        prompt,
                        credential,
                        resumeContext,
                        modelId,
                        reasoningMode,
                        ct,
                        stdoutChunkCallback,
                        captureStructuredStream).ConfigureAwait(false);
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

    private AgentResult WithStructuredStreamDisabledWarning(AgentResult result)
    {
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
        => BuildClaudeInvocation(prompt, modelId, reasoningMode, sessionIdForResume: null, captureStructuredStream);

    protected override AgentInvocation BuildResumeInvocation(
        string prompt,
        AgentCredential? credential,
        AgentResumeContext resume,
        string? modelId = null,
        string? reasoningMode = null,
        bool captureStructuredStream = false)
        => BuildClaudeInvocation(prompt, modelId, reasoningMode, sessionIdForResume: null, captureStructuredStream);

    /// <summary>
    /// Opt this runner into the suspend-resilience loop's CLI-native session
    /// resume retry path. After a transient crash whose stdout carried a
    /// session id, the loop rebuilds the next attempt via
    /// <see cref="BuildSessionResumeInvocation"/> instead of restarting the
    /// run from scratch. Effective resume coverage additionally requires the
    /// caller to enable structured stream capture (the session id only lands
    /// in stdout when <c>--output-format stream-json --verbose</c> is set);
    /// without it the extractor returns null and the loop falls through to
    /// the legacy retry path.
    /// </summary>
    protected override bool SupportsSessionResume => true;

    protected override IAgentQuotaFailureDetector? SessionResumeQuotaFailureDetector => SessionResumeQuotaDetector;

    /// <summary>
    /// The Claude CLI prints a structured init event on its first stream-json
    /// line: <c>{"type":"system","subtype":"init","session_id":"...", ...}</c>.
    /// We pull the id from that line so a crashed run can be resumed in place
    /// via <c>claude --resume &lt;id&gt;</c>. Robust to ordering / extra
    /// whitespace and tolerant of <c>sessionId</c> camelCase that some CLI
    /// builds use; returns <c>null</c> when no recognisable id is present.
    /// </summary>
    protected override string? TryExtractSessionId(string? stdout)
        => ClaudeSessionIdExtractor.Extract(stdout);

    /// <summary>
    /// Builds the argv for a CLI-native session resume after a transient crash.
    /// <c>claude --resume &lt;id&gt; --print</c> requires a non-empty stdin turn,
    /// so the resumed process receives a short continuation instruction rather
    /// than the original task prompt again. The conversation restored by the
    /// CLI carries the original user prompt and in-progress context.
    /// </summary>
    protected override AgentInvocation BuildSessionResumeInvocation(
        string sessionId,
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        bool captureStructuredStream = false)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("sessionId must be non-empty", nameof(sessionId));
        _ = prompt;
        return BuildClaudeInvocation(SessionResumePrompt, modelId, reasoningMode, sessionIdForResume: sessionId, captureStructuredStream);
    }

    internal const string SessionResumePrompt =
        "Continue from the restored session after the interrupted run. Do not restart completed work or repeat the original instructions.";

    private AgentInvocation BuildClaudeInvocation(string prompt, string? modelId, string? reasoningMode, string? sessionIdForResume, bool captureStructuredStream)
        => BuildClaudeSessionInvocation(prompt, modelId, reasoningMode, cliResumeSessionId: sessionIdForResume, captureStructuredStream);

    /// <summary>
    /// Builds the claude CLI argv used by <see cref="ClaudeSessionWorker"/> for
    /// one turn in a multi-turn resumable session. Same shape as the one-shot
    /// invocation plus an optional <c>--resume &lt;session-id&gt;</c> when the
    /// session has a captured CLI session id from a prior turn.
    /// </summary>
    private AgentInvocation BuildClaudeSessionInvocation(
        string prompt,
        string? modelId,
        string? reasoningMode,
        string? cliResumeSessionId,
        bool captureStructuredStream)
    {
        var argv = new List<string> { Binary, "--print", "--dangerously-skip-permissions" };
        if (!string.IsNullOrWhiteSpace(cliResumeSessionId))
        {
            argv.Add("--resume");
            argv.Add(cliResumeSessionId);
        }
        if (captureStructuredStream)
        {
            argv.Add("--output-format");
            argv.Add("stream-json");
            argv.Add("--verbose");
        }
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

    /// <summary>
    /// Runs one turn of a Claude resumable session. Invoked exclusively by
    /// <see cref="ClaudeSessionWorker"/>; the public <see cref="RunAsync"/>
    /// remains the canonical one-shot path. Materialises credentials and (when
    /// <paramref name="cliResumeSessionId"/> is non-null) sanitises the stored
    /// session JSONL transcripts before invocation, then optionally retries the
    /// turn once after a thinking-block 400 — mirroring the one-shot path's
    /// safety nets.
    /// </summary>
    internal async Task<AgentResult> RunSessionTurnAsync(
        ISandbox sandbox,
        string workingDirectory,
        string prompt,
        AgentCredential? credential,
        string? cliResumeSessionId,
        string? modelId,
        string? reasoningMode,
        bool captureStructuredStream,
        CancellationToken ct,
        Action<string>? stdoutChunkCallback)
    {
        using var _ = _rotationPusher?.RegisterActiveSandbox(sandbox);

        // Treat a turn with a resume id like the post-checkpoint resume path:
        // the stored session JSONL needs preventive sanitisation before the
        // CLI replays it, otherwise interleaved/partial thinking blocks
        // produced by a prior turn surface as 400s.
        var fakeResume = cliResumeSessionId is null
            ? null
            : new AgentResumeContext(CheckpointRef: $"claude-session:{cliResumeSessionId}");
        var preparation = await PrepareSandboxAsync(sandbox, workingDirectory, credential, fakeResume, ct)
            .ConfigureAwait(false);
        if (preparation is not null)
            return preparation;

        var invocation = BuildClaudeSessionInvocation(
            prompt, modelId, reasoningMode, cliResumeSessionId, captureStructuredStream);

        var result = await ExecOnceAsync(sandbox, workingDirectory, invocation, stdoutChunkCallback, ct)
            .ConfigureAwait(false);

        if (!result.Success
            && (_sanitizerConfig is null || _sanitizerConfig.Enabled)
            && ClaudeSessionSanitizer.IsThinkingBlockFailure(result))
        {
            var sanitized = await ClaudeSessionSanitizer.SanitizeTranscriptsAsync(sandbox, ct)
                .ConfigureAwait(false);
            if (sanitized is null)
            {
                result = await ExecOnceAsync(sandbox, workingDirectory, invocation, stdoutChunkCallback, ct)
                    .ConfigureAwait(false);
            }
            else
            {
                result = result with
                {
                    Summary = $"{result.Summary}; sanitiser failed: {sanitized.Summary}",
                    Stderr = string.Concat(result.Stderr, "\n", sanitized.Stderr),
                };
            }
        }

        return result;
    }

    private async Task<AgentResult> ExecOnceAsync(
        ISandbox sandbox,
        string workingDirectory,
        AgentInvocation invocation,
        Action<string>? stdoutChunkCallback,
        CancellationToken ct)
    {
        var exec = new SandboxExec
        {
            Argv = invocation.Argv,
            WorkingDirectory = workingDirectory,
            ExtraEnvironment = invocation.ExtraEnvironment,
            Stdin = invocation.Stdin,
            StdoutChunkCallback = stdoutChunkCallback,
        };
        var execResult = await sandbox.ExecAsync(exec, ct).ConfigureAwait(false);
        return new AgentResult(
            Success: execResult.Success,
            Summary: execResult.Success ? "ok" : $"agent exited {execResult.ExitCode}",
            Stdout: execResult.Stdout,
            Stderr: execResult.Stderr);
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

    // ── Text-only API path ────────────────────────────────────────────────────

    public string? GetTextOnlyUnavailabilityReason(AgentCredential? credential)
    {
        if (TryGetApiKey(credential, out _)) return null;
        return MissingApiKeyReason;
    }

    public async Task<TextOnlyAgentResult> RunTextOnlyAsync(
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        CancellationToken ct = default,
        ISandbox? sandbox = null,
        string? workingDirectory = null)
    {
        _ = sandbox;
        _ = workingDirectory;
        _ = reasoningMode;

        if (!TryGetApiKey(credential, out var apiKey))
        {
            return new TextOnlyAgentResult(
                false,
                "missing Claude text-only credential",
                null,
                MissingApiKeyReason);
        }

        var requestedModel = !string.IsNullOrWhiteSpace(modelId) ? modelId! : DefaultModelId;
        if (string.IsNullOrWhiteSpace(requestedModel))
        {
            return new TextOnlyAgentResult(
                false,
                "missing model id for Claude text-only call",
                null,
                "No model id available (no default configured); set a default in CodeyBox:AgentDefaults or supply an explicit modelId.");
        }

        var canonicalModel = await TryResolveCanonicalModelIdAsync(requestedModel, apiKey, ct).ConfigureAwait(false);

        try
        {
            var body = JsonSerializer.Serialize(new
            {
                model = canonicalModel,
                max_tokens = TextOnlyMaxTokens,
                messages = new[]
                {
                    new { role = "user", content = prompt },
                },
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, MessagesEndpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("x-api-key", apiKey);
            request.Headers.Add("anthropic-version", AnthropicVersion);

            using var response = await _textOnlyHttp.SendAsync(request, ct).ConfigureAwait(false);
            var responseText = await ClaudeModelListProbe.ReadCappedAsync(response.Content, ct).ConfigureAwait(false);
            if (responseText is null)
            {
                return new TextOnlyAgentResult(
                    false,
                    "Claude text-only call failed: response too large",
                    null,
                    "Response size exceeded 256 KiB limit.");
            }
            if (!response.IsSuccessStatusCode)
            {
                var summary = canonicalModel == requestedModel
                    ? $"Claude text-only call failed: HTTP {(int)response.StatusCode} (model={canonicalModel})"
                    : $"Claude text-only call failed: HTTP {(int)response.StatusCode} (model={canonicalModel}, requested={requestedModel})";
                return new TextOnlyAgentResult(false, summary, null, responseText);
            }

            return new TextOnlyAgentResult(true, "ok", ExtractResponseText(responseText), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new TextOnlyAgentResult(false, "Claude text-only call failed", null, ex.Message);
        }
    }

    /// <summary>
    /// Best-effort alias→canonical resolution via <c>GET /v1/models</c>. The
    /// Messages API rejects undated aliases (e.g. <c>claude-opus-4-7</c>) with
    /// HTTP 404 even when <c>/v1/models</c> lists them, so a dated variant is
    /// preferred over an exact alias match. Returns the requested id unchanged
    /// when no dated match is found or when the probe itself fails.
    /// </summary>
    private async Task<string> TryResolveCanonicalModelIdAsync(string requested, string apiKey, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ModelsEndpoint);
            request.Headers.Add("x-api-key", apiKey);
            request.Headers.Add("anthropic-version", AnthropicVersion);

            using var response = await _textOnlyHttp.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return requested;

            var body = await ClaudeModelListProbe.ReadCappedAsync(response.Content, ct).ConfigureAwait(false);
            if (body is null)
                return requested;
            var ids = ParseModelIds(body);
            return ResolveCanonicalModelId(requested, ids);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return requested;
        }
    }

    /// <summary>
    /// Picks the latest date-stamped variant whose id is
    /// <c>requested + "-" + &lt;date&gt;</c> (lex-max on YYYYMMDD = newest);
    /// otherwise returns <paramref name="requested"/> unchanged. Preferring a
    /// dated variant over an exact alias match is deliberate: the Messages
    /// API has answered the undated alias with HTTP 404 even when
    /// <c>/v1/models</c> lists it.
    /// </summary>
    internal static string ResolveCanonicalModelId(string requested, IReadOnlyList<string> available)
    {
        var prefix = requested + "-";
        var datedMatch = available
            .Where(id => id.StartsWith(prefix, StringComparison.Ordinal))
            .OrderByDescending(static id => id, StringComparer.Ordinal)
            .FirstOrDefault();
        return datedMatch ?? requested;
    }

    /// <summary>
    /// Parses <c>{"data":[{"id":"..."}, ...]}</c> into the list of ids; returns
    /// an empty list on any parse failure. Delegates to
    /// <see cref="ClaudeModelListProbe.ParseResponse"/> — the startup model-list
    /// probe and this text-only alias resolver speak the same wire format and
    /// must not drift.
    /// </summary>
    internal static IReadOnlyList<string> ParseModelIds(string json) =>
        ClaudeModelListProbe.ParseResponse(json).ModelIds;

    /// <summary>
    /// Extracts the concatenated text payload from a <c>/v1/messages</c>
    /// response body: <c>{"content":[{"type":"text","text":"..."}, ...]}</c>.
    /// Returns an empty string when no text part is present.
    /// </summary>
    internal static string ExtractResponseText(string responseText)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseText);
            if (!doc.RootElement.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.Array)
                return string.Empty;
            var parts = new List<string>();
            foreach (var item in content.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (item.TryGetProperty("text", out var textEl)
                    && textEl.ValueKind == JsonValueKind.String)
                {
                    parts.Add(textEl.GetString() ?? string.Empty);
                }
            }
            return string.Concat(parts);
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private static bool TryGetApiKey(AgentCredential? credential, out string apiKey)
    {
        apiKey = "";
        if (credential is null) return false;
        if (!credential.EnvironmentVariables.TryGetValue("ANTHROPIC_API_KEY", out var v)
            || string.IsNullOrEmpty(v))
            return false;
        apiKey = v;
        return true;
    }
}
