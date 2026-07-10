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
public sealed class ClaudeAgentRunner : CliAgentRunnerBase, IStructuredStreamAgentRunner, ICliSessionResumableAgentRunner, IAgentDefaultModelProvider, ITextOnlyAgentRunner, IPlanArtifactExtractor
{
    private static readonly HttpClient SharedTextOnlyHttp = new();
    private static readonly EnvBackedCredentialFile OAuthCredentialFile = new(
        "CODEYBOX_CLAUDE_OAUTH_JSON",
        ".claude/.credentials.json",
        "claude auth");

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
    private readonly AgentNetworkToleranceSnapshot? _networkTolerance;
    private readonly HttpClient _textOnlyHttp;
    private readonly IQuotaFailureClassifier _sessionResumeQuotaClassifier;

    public ClaudeAgentRunner() : this(defaults: null, rotationPusher: null, sanitizerConfig: null, networkTolerance: null, quotaFailureClassifier: null) { }

    public ClaudeAgentRunner(AgentDefaultsSnapshot? defaults) : this(defaults, rotationPusher: null, sanitizerConfig: null, networkTolerance: null, quotaFailureClassifier: null) { }

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
    /// <param name="quotaFailureClassifier">
    /// Shared quota classifier used for session-resume quota gating so recovery
    /// policy stays aligned with the orchestrator's quota fallback handling.
    /// </param>
    public ClaudeAgentRunner(
        AgentDefaultsSnapshot? defaults,
        IClaudeTokenRotationPusher? rotationPusher,
        ClaudeThinkingBlockSanitizerConfig? sanitizerConfig = null,
        AgentNetworkToleranceSnapshot? networkTolerance = null,
        IQuotaFailureClassifier? quotaFailureClassifier = null)
        : this(
            defaults: defaults,
            rotationPusher: rotationPusher,
            sanitizerConfig: sanitizerConfig,
            networkTolerance: networkTolerance,
            textOnlyHttp: null,
            quotaFailureClassifier: quotaFailureClassifier)
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
        AgentNetworkToleranceSnapshot? networkTolerance,
        HttpClient? textOnlyHttp,
        IQuotaFailureClassifier? quotaFailureClassifier = null)
    {
        _defaults = defaults;
        _rotationPusher = rotationPusher;
        _sanitizerConfig = sanitizerConfig;
        _networkTolerance = networkTolerance;
        _textOnlyHttp = textOnlyHttp ?? SharedTextOnlyHttp;
        _sessionResumeQuotaClassifier = quotaFailureClassifier ?? ClaudeSessionResumeQuotaClassifier.Instance;
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

    protected override IReadOnlyList<EnvBackedCredentialFile> EnvBackedCredentialFiles => [OAuthCredentialFile];

    protected override IReadOnlyList<string> DirectCredentialEnvironmentVariables =>
        ["ANTHROPIC_API_KEY", "CLAUDE_CODE_OAUTH_TOKEN"];

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
    protected override async Task<AgentResult?> PrepareAgentSandboxAsync(
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
        var effectiveUseStructuredStream = captureStructuredStream && structuredStreamSupported;

        var result = await base.RunAsync(
            sandbox,
            workingDirectory,
            prompt,
            credential,
            modelId,
            reasoningMode,
            ct,
            stdoutChunkCallback,
            effectiveUseStructuredStream).ConfigureAwait(false);

        result = await TryReactiveRetryAsync(
            sandbox, workingDirectory, prompt, credential, modelId, reasoningMode,
            ct, stdoutChunkCallback, effectiveUseStructuredStream,
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
    /// Claude emits the resumable CLI session id only in its structured
    /// stream-json init event, so orchestrator call sites must force structured
    /// output when they want crash recovery independent of AgentStreams.
    /// </summary>
    public bool RequiresStructuredStreamForSessionId => true;

    public IQuotaFailureClassifier SessionResumeQuotaClassifier => _sessionResumeQuotaClassifier;

    /// <summary>
    /// The Claude CLI prints a structured init event on its first stream-json
    /// line: <c>{"type":"system","subtype":"init","session_id":"...", ...}</c>.
    /// We pull the id from that line so a crashed run can be resumed in place
    /// via <c>claude --resume &lt;id&gt;</c>. Robust to ordering / extra
    /// whitespace and tolerant of <c>sessionId</c> camelCase that some CLI
    /// builds use; returns <c>null</c> when no recognisable id is present.
    /// </summary>
    public string? TryExtractSessionId(string? stdout)
        => ClaudeSessionIdExtractor.Extract(stdout);

    /// <summary>
    /// Unwraps the Claude CLI's stream-json envelope so the orchestrator's
    /// plan-artifact parser sees the agent-visible plan text rather than the
    /// raw NDJSON. Returns <c>null</c> when no stream-json events were observed
    /// — the caller then feeds the raw stdout to the parser directly.
    /// </summary>
    public string? ExtractPlanArtifactText(string rawStdout)
        => ClaudePlanArtifactExtractor.Extract(rawStdout);

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

        IReadOnlyDictionary<string, string>? extraEnv = null;
        var apiTimeout = BindApiTimeout();
        if (apiTimeout.HasValue)
        {
            extraEnv = new Dictionary<string, string>
            {
                ["API_TIMEOUT_MS"] = apiTimeout.Value.ToString()
            };
        }

        return new AgentInvocation(argv, ExtraEnvironment: extraEnv, Stdin: prompt);
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
        var preparation = await PrepareSandboxForRunAsync(
                sandbox,
                workingDirectory,
                credential,
                fakeResume,
                ct,
                preserveExistingCredentialFiles: false)
            .ConfigureAwait(false);
        if (preparation is not null)
            return preparation;

        var invocation = BuildClaudeSessionInvocation(
            prompt, modelId, reasoningMode, cliResumeSessionId, captureStructuredStream);
        var result = await ExecOnceAsync(sandbox, workingDirectory, invocation, credential, stdoutChunkCallback, ct)
            .ConfigureAwait(false);

        if (!result.Success
            && (_sanitizerConfig is null || _sanitizerConfig.Enabled)
            && ClaudeSessionSanitizer.IsThinkingBlockFailure(result))
        {
            var sanitized = await ClaudeSessionSanitizer.SanitizeTranscriptsAsync(sandbox, ct)
                .ConfigureAwait(false);
            if (sanitized is null)
            {
                result = await ExecOnceAsync(sandbox, workingDirectory, invocation, credential, stdoutChunkCallback, ct)
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
        AgentCredential? credential,
        Action<string>? stdoutChunkCallback,
        CancellationToken ct)
    {
        var exec = new SandboxExec
        {
            Argv = invocation.Argv,
            WorkingDirectory = workingDirectory,
            ExtraEnvironment = BuildExecEnvironment(invocation.ExtraEnvironment, credential),
            EnvironmentContainsSecrets = HasDirectCredentialEnvironment(credential),
            Stdin = invocation.Stdin,
            StdoutChunkCallback = stdoutChunkCallback,
            AgentOutputTransport = SelectBatchAgentOutputTransport(sandbox),
            LaunchMode = SelectBatchLaunchMode(sandbox),
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

    public bool SupportsSeparateSystemPrompt => true;

    public Task<TextOnlyAgentResult> RunTextOnlyAsync(
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        CancellationToken ct = default,
        ISandbox? sandbox = null,
        string? workingDirectory = null)
        => RunTextOnlyCoreAsync(null, prompt, credential, modelId, reasoningMode, ct, sandbox, workingDirectory);

    public Task<TextOnlyAgentResult> RunTextOnlyWithSystemPromptAsync(
        string systemPrompt,
        string userPrompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        CancellationToken ct = default,
        ISandbox? sandbox = null,
        string? workingDirectory = null)
        => RunTextOnlyCoreAsync(systemPrompt, userPrompt, credential, modelId, reasoningMode, ct, sandbox, workingDirectory);

    private async Task<TextOnlyAgentResult> RunTextOnlyCoreAsync(
        string? systemPrompt,
        string userPrompt,
        AgentCredential? credential,
        string? modelId,
        string? reasoningMode,
        CancellationToken ct,
        ISandbox? sandbox,
        string? workingDirectory)
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
            var bodyFields = new Dictionary<string, object?>
            {
                ["model"] = canonicalModel,
                ["max_tokens"] = TextOnlyMaxTokens,
                ["messages"] = new[]
                {
                    new { role = "user", content = userPrompt },
                },
            };
            if (systemPrompt is not null)
                bodyFields["system"] = systemPrompt;
            var body = JsonSerializer.Serialize(bodyFields);

            using var request = new HttpRequestMessage(HttpMethod.Post, MessagesEndpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("x-api-key", apiKey);
            request.Headers.Add("anthropic-version", AnthropicVersion);

            var response = await BoundedHttpResponseReader.SendAsync(_textOnlyHttp, request, ct: ct).ConfigureAwait(false);
            if (response.BodyTooLarge)
            {
                return new TextOnlyAgentResult(
                    false,
                    "Claude text-only call failed: response too large",
                    null,
                    "Response size exceeded 256 KiB limit.");
            }
            var responseText = response.Body ?? string.Empty;
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

            var response = await BoundedHttpResponseReader.SendAsync(_textOnlyHttp, request, ct: ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return requested;

            if (response.BodyTooLarge || response.Body is null)
                return requested;
            var ids = ParseModelIds(response.Body);
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

    private int? BindApiTimeout()
    {
        if (_networkTolerance == null) return null;
        return _networkTolerance.GetTolerance(Kind.Value)?.ApiTimeoutMs;
    }

    private sealed class ClaudeSessionResumeQuotaClassifier : IQuotaFailureClassifier
    {
        public static readonly ClaudeSessionResumeQuotaClassifier Instance = new();

        private readonly ClaudeQuotaFailureDetector _detector = new();

        private ClaudeSessionResumeQuotaClassifier() { }

        public QuotaFailureClassification Classify(AgentKind agent, string? stderr, string? stdout)
        {
            if (agent != AgentKind.Claude || (string.IsNullOrEmpty(stderr) && string.IsNullOrEmpty(stdout)))
                return QuotaFailureClassification.None;

            if (_detector.IsTerminalNonQuotaCrash(stderr, stdout))
                return QuotaFailureClassification.TerminalNonQuota;

            var scopedStdout = _detector.ScopeStdoutForQuotaDetection(stdout);
            var detection = _detector.Detect(stderr, scopedStdout);
            return detection is null
                ? QuotaFailureClassification.None
                : QuotaFailureClassification.Quota(detection);
        }

        public QuotaDetection? Detect(AgentKind agent, string? stderr, string? stdout)
            => Classify(agent, stderr, stdout).Detection;
    }
}
