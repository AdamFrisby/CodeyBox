using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Sandbox;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

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
public sealed class CodexAgentRunner : CliAgentRunnerBase, IStructuredStreamAgentRunner, ICliSessionResumableAgentRunner, IAgentDefaultModelProvider, ITextOnlyAgentRunner
{
    private static readonly AsyncLocal<string?> CurrentStructuredStreamFlag = new();
    private static readonly HttpClient SharedTextOnlyHttp = new();

    private readonly AgentDefaultsSnapshot? _defaults;
    private readonly AgentNetworkToleranceSnapshot? _networkTolerance;
    private readonly HttpClient _textOnlyHttp;
    private readonly IQuotaFailureClassifier _sessionResumeQuotaClassifier;

    public CodexAgentRunner() : this(defaults: null) { }

    public CodexAgentRunner(AgentDefaultsSnapshot? defaults) : this(defaults, networkTolerance: null) { }

    public CodexAgentRunner(AgentDefaultsSnapshot? defaults, AgentNetworkToleranceSnapshot? networkTolerance)
        : this(defaults, networkTolerance, textOnlyHttp: null)
    {
    }

    public CodexAgentRunner(
        AgentDefaultsSnapshot? defaults,
        AgentNetworkToleranceSnapshot? networkTolerance,
        IQuotaFailureClassifier? quotaFailureClassifier)
        : this(defaults, networkTolerance, textOnlyHttp: null, quotaFailureClassifier)
    {
    }

    /// <summary>
    /// Internal test seam: lets unit tests inject an <see cref="HttpClient"/>
    /// backed by a fake <see cref="HttpMessageHandler"/> so the text-only
    /// path can be exercised offline without reaching api.openai.com.
    /// Production wiring uses the process-wide shared HttpClient.
    /// </summary>
    internal CodexAgentRunner(AgentDefaultsSnapshot? defaults, AgentNetworkToleranceSnapshot? networkTolerance, HttpClient? textOnlyHttp)
        : this(defaults, networkTolerance, textOnlyHttp, quotaFailureClassifier: null)
    {
    }

    internal CodexAgentRunner(
        AgentDefaultsSnapshot? defaults,
        AgentNetworkToleranceSnapshot? networkTolerance,
        HttpClient? textOnlyHttp,
        IQuotaFailureClassifier? quotaFailureClassifier)
    {
        _defaults = defaults;
        _networkTolerance = networkTolerance;
        _textOnlyHttp = textOnlyHttp ?? SharedTextOnlyHttp;
        _sessionResumeQuotaClassifier = quotaFailureClassifier ?? CodexSessionResumeQuotaClassifier.Instance;
    }

    public override AgentKind Kind => AgentKind.Codex;

    /// <summary>Default codex binary name on the sandbox PATH. The in-VM smoke probe pins to this so the probe and runner can never drift.</summary>
    public const string DefaultBinary = "codex";

    public string Binary { get; init; } = DefaultBinary;

    protected override IReadOnlyList<string> ScratchpadHomeDirectories => [".codex/sessions", ".codex/history.jsonl"];

    protected override string PreemptProcessPattern => Binary;

    /// <summary>
    /// Default model passed to <c>--model</c> when no per-item override is
    /// provided. Sourced live from <see cref="AgentDefaultsSnapshot"/>.
    /// </summary>
    public string? DefaultModelId => _defaults?.GetDefault(Kind.Value);

    public async Task<bool> SupportsStructuredStreamAsync(ISandbox sandbox, CancellationToken ct = default) =>
        await DetectStructuredStreamFlagAsync(sandbox, ct).ConfigureAwait(false) is not null;

    /// <summary>
    /// ChatGPT-subscription auth: ensure <c>~/.codex/auth.json</c> is present
    /// inside the sandbox. The codex CLI reads ONLY that file path; there's no
    /// env-var equivalent.
    ///
    /// The runner preserves any non-empty auth file already present in the
    /// sandbox, then falls back to writing a private snapshot from
    /// <c>CODEX_AUTH_JSON</c>. Credential providers intentionally do not
    /// bind-mount the host <c>~/.codex</c> directory into untrusted agent
    /// sandboxes.
    ///
    /// We always read from the in-sandbox env var (rather than the credential
    /// parameter) because LlmReviewAuditor and similar call-sites pass
    /// credential=null on the assumption that env-var auth is sufficient —
    /// true for Claude (env-var-based), false for Codex (file-based). If the
    /// env var is absent and no file is present, this is a no-op and codex
    /// falls back to OPENAI_API_KEY (api-key mode).
    /// </summary>
    protected override async Task<AgentResult?> PrepareSandboxAsync(
        ISandbox sandbox,
        string workingDirectory,
        AgentCredential? credential,
        AgentResumeContext? resume,
        CancellationToken ct = default)
    {
        // If sandbox setup or restored home state already supplied auth.json,
        // leave it alone; otherwise materialise the private snapshot.
        var write = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["bash", "-c", "set -eu; if [ -s \"$HOME/.codex/auth.json\" ]; then exit 0; fi; if [ -n \"${CODEX_AUTH_JSON:-}\" ]; then mkdir -p \"$HOME/.codex\"; umask 077; printf '%s' \"$CODEX_AUTH_JSON\" > \"$HOME/.codex/auth.json\"; fi"],
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

    public string? GetTextOnlyUnavailabilityReason(AgentCredential? credential)
        => string.IsNullOrEmpty(ResolveOpenAiApiKey(credential))
            ? "OPENAI_API_KEY is required for text-only calls"
            : null;

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
        var apiKey = ResolveOpenAiApiKey(credential);
        if (string.IsNullOrEmpty(apiKey))
            return new TextOnlyAgentResult(false, "missing Codex text-only credential", null, "OPENAI_API_KEY is required for text-only calls");

        try
        {
            var effectiveModel = string.IsNullOrWhiteSpace(modelId) ? DefaultModelId : modelId;
            if (string.IsNullOrWhiteSpace(effectiveModel))
                return new TextOnlyAgentResult(false, "missing model id for Codex text-only call", null, "No model id available (no default configured); set a default in CodeyBox:AgentDefaults or supply an explicit modelId.");

            var body = new Dictionary<string, object?>
            {
                ["model"] = effectiveModel,
                ["input"] = prompt,
                ["max_output_tokens"] = 8192,
            };
            if (!string.IsNullOrWhiteSpace(reasoningMode))
                body["reasoning"] = new Dictionary<string, string> { ["effort"] = reasoningMode };

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            using var response = await _textOnlyHttp.SendAsync(request, ct).ConfigureAwait(false);
            var responseText = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new TextOnlyAgentResult(false, $"Codex text-only call failed: HTTP {(int)response.StatusCode}", null, responseText);

            return new TextOnlyAgentResult(true, "ok", ExtractResponseText(responseText), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new TextOnlyAgentResult(false, "Codex text-only call failed", null, ex.Message);
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
        return BuildCodexInvocation(
            prompt,
            modelId,
            reasoningMode,
            sessionIdForResume: null,
            captureStructuredStream);
    }

    /// <summary>
    /// Codex emits its resumable session id only in structured <c>--json</c>
    /// metadata, so orchestrator call sites must force structured output when
    /// they want crash recovery independent of AgentStreams.
    /// </summary>
    public bool RequiresStructuredStreamForSessionId => true;

    public IQuotaFailureClassifier SessionResumeQuotaClassifier => _sessionResumeQuotaClassifier;

    public string? TryExtractSessionId(string? stdout)
        => CodexSessionIdExtractor.Extract(stdout);

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
        return BuildCodexInvocation(
            SessionResumePrompt,
            modelId,
            reasoningMode,
            sessionIdForResume: sessionId,
            captureStructuredStream);
    }

    internal const string SessionResumePrompt =
        "Continue from the restored session after the interrupted run. Do not restart completed work or repeat the original instructions.";

    private AgentInvocation BuildCodexInvocation(
        string prompt,
        string? modelId,
        string? reasoningMode,
        string? sessionIdForResume,
        bool captureStructuredStream)
    {
        var argv = new List<string> { Binary, "exec" };
        if (!string.IsNullOrEmpty(sessionIdForResume))
            argv.Add("resume");

        argv.Add("--dangerously-bypass-approvals-and-sandbox");
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

        // Network tolerance overrides. The snapshot owns the documented
        // CodeyBox defaults; this runner only maps the typed options onto the
        // Codex CLI's provider-scoped config keys.
        var tolerance = AgentNetworkToleranceOptions.WithCodexDefaults(
            _networkTolerance?.GetTolerance(Kind.Value));
        var reqRetries = tolerance.RequestMaxRetries!.Value;
        var streamRetries = tolerance.StreamMaxRetries!.Value;

        var providerId = ResolveProviderId(effectiveModel, tolerance.Provider);

        argv.Add("-c");
        argv.Add($"model_providers.{providerId}.request_max_retries={reqRetries}");

        argv.Add("-c");
        argv.Add($"model_providers.{providerId}.stream_max_retries={streamRetries}");

        if (tolerance.StreamIdleTimeoutMs.HasValue)
        {
            argv.Add("-c");
            argv.Add($"model_providers.{providerId}.stream_idle_timeout_ms={tolerance.StreamIdleTimeoutMs.Value}");
        }

        // Pass the prompt via stdin rather than as a positional argv. Linux's
        // MAX_ARG_STRLEN is 128 KiB per single argv element; rework prompts that
        // include many audit findings can exceed that and surface as exit 126
        // from the sandbox wrapper's `exec "$@"`. `codex exec` reads instructions
        // from stdin when no positional prompt is given (per its --help). The
        // sandbox wrapper forwards stdin automatically when SandboxExec.Stdin is
        // non-null, via its --keep-stdin path.
        if (!string.IsNullOrEmpty(sessionIdForResume))
        {
            // `--` halts clap's option parsing so a session id that somehow
            // starts with '-' cannot be interpreted as a flag. The extractor
            // also rejects leading-'-' ids; this is defense-in-depth.
            argv.Add("--");
            argv.Add(sessionIdForResume);
            argv.Add("-");
        }

        return new AgentInvocation(argv, Stdin: prompt);
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

    private static string? ResolveOpenAiApiKey(AgentCredential? credential)
    {
        string? apiKey = null;
        credential?.EnvironmentVariables.TryGetValue("OPENAI_API_KEY", out apiKey);
        if (!string.IsNullOrEmpty(apiKey))
            return apiKey;

        string? authJson = null;
        credential?.EnvironmentVariables.TryGetValue("CODEX_AUTH_JSON", out authJson);
        if (string.IsNullOrEmpty(authJson))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(authJson);
            if (doc.RootElement.TryGetProperty("OPENAI_API_KEY", out var key)
                && key.ValueKind == JsonValueKind.String)
                return key.GetString();
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static string ExtractResponseText(string responseText)
    {
        using var doc = JsonDocument.Parse(responseText);
        if (doc.RootElement.TryGetProperty("output_text", out var outputText)
            && outputText.ValueKind == JsonValueKind.String)
            return outputText.GetString() ?? string.Empty;

        var parts = new List<string>();
        if (doc.RootElement.TryGetProperty("output", out var output)
            && output.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in output.EnumerateArray())
            {
                if (!item.TryGetProperty("content", out var content)
                    || content.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var chunk in content.EnumerateArray())
                {
                    if (chunk.TryGetProperty("text", out var text)
                        && text.ValueKind == JsonValueKind.String)
                        parts.Add(text.GetString() ?? string.Empty);
                }
            }
        }

        return string.Concat(parts);
    }

    private static string ResolveProviderId(string? effectiveModel, string? configuredProvider)
    {
        if (!string.IsNullOrWhiteSpace(configuredProvider))
        {
            return ResolveSafeProviderId(configuredProvider);
        }

        if (!string.IsNullOrEmpty(effectiveModel))
        {
            var slashIdx = effectiveModel.IndexOf('/');
            if (slashIdx > 0)
            {
                return ResolveSafeProviderId(effectiveModel.Substring(0, slashIdx));
            }
        }

        return "openai";
    }

    private static string ResolveSafeProviderId(string providerId) =>
        AgentNetworkToleranceOptions.IsValidCodexProviderId(providerId) ? providerId : "openai";

    private sealed class CodexSessionResumeQuotaClassifier : IQuotaFailureClassifier
    {
        public static readonly CodexSessionResumeQuotaClassifier Instance = new();

        private readonly IAgentQuotaFailureDetector _detector = new CodexQuotaFailureDetector();

        private CodexSessionResumeQuotaClassifier() { }

        public QuotaFailureClassification Classify(AgentKind agent, string? stderr, string? stdout)
        {
            if (agent != AgentKind.Codex || (string.IsNullOrEmpty(stderr) && string.IsNullOrEmpty(stdout)))
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
