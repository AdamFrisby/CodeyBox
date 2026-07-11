using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Sandbox;
using System.Text;
using System.Text.Json;

namespace CodeyBox.Agents.Gemini;

/// <summary>
/// Drives the Google Gemini CLI (@google/gemini-cli) in non-interactive mode.
/// The agent is expected to be installed in the sandbox image; the host
/// injects the API key via GEMINI_API_KEY.
/// </summary>
public sealed class GeminiAgentRunner : CliAgentRunnerBase, IStructuredStreamAgentRunner, ITextOnlyAgentRunner
{
    private static readonly HttpClient SharedTextOnlyHttp = new();
    private static readonly EnvBackedCredentialFile OAuthCredentialFile = new(
        CodeyBox.Core.GeminiConstants.OAuthCredsEnvVar,
        ".gemini/oauth_creds.json",
        "gemini auth");
    private static readonly EnvBackedCredentialFile SettingsCredentialFile = new(
        CodeyBox.Core.GeminiConstants.SettingsEnvVar,
        ".gemini/settings.json",
        "gemini settings");
    private const string DefaultTextOnlyModel = "gemini-2.5-pro";
    private const int TextOnlyMaxOutputTokens = 8192;

    private readonly HttpClient _textOnlyHttp;

    public GeminiAgentRunner() : this(textOnlyHttp: null) { }

    /// <summary>
    /// Internal test seam: lets unit tests inject an <see cref="HttpClient"/>
    /// backed by a fake <see cref="HttpMessageHandler"/> so the text-only
    /// path can be exercised offline without reaching the Code Assist /
    /// public Gemini endpoints. Production wiring uses the process-wide
    /// shared HttpClient.
    /// </summary>
    internal GeminiAgentRunner(HttpClient? textOnlyHttp)
    {
        _textOnlyHttp = textOnlyHttp ?? SharedTextOnlyHttp;
    }

    // @google/gemini-cli emits ANSI colour codes and progress spinners to
    // stderr (and occasionally stdout) even in non-TTY mode. Strip them so
    // the audit log stays clean and SIEM tools are not confused.
    private static readonly Regex AnsiEscape = new(
        @"\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex WhitespaceRun = new(
        @"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Cap embedded in the failure summary so the orchestrator's TerminalQuotaError /
    // InvalidOperationException messages don't balloon past what audit log sinks
    // and webhooks accept. The full stderr is still on AgentResult.Stderr for
    // any caller that needs it.
    internal const int FailureSummaryTailMaxChars = 240;

    public override AgentKind Kind => AgentKind.Gemini;

    /// <summary>
    /// Path to the gemini binary inside the sandbox. Override only if the
    /// sandbox image installs it elsewhere.
    /// </summary>
    /// <summary>Default gemini binary name on the sandbox PATH. The in-VM smoke probe pins to this so the probe and runner can never drift.</summary>
    public const string DefaultBinary = "gemini";

    public string Binary { get; init; } = DefaultBinary;

    protected override IReadOnlyList<string> ScratchpadHomeDirectories => [".gemini/tmp", ".gemini/history"];

    protected override IReadOnlyList<EnvBackedCredentialFile> EnvBackedCredentialFiles =>
        [OAuthCredentialFile, SettingsCredentialFile];

    protected override IReadOnlyList<string> DirectCredentialEnvironmentVariables => ["GEMINI_API_KEY"];

    protected override string PreemptProcessPattern => Binary;

    public async Task<bool> SupportsStructuredStreamAsync(ISandbox sandbox, CancellationToken ct = default)
    {
        var help = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = [Binary, "--help"],
        }, ct);

        if (!help.Success)
            return false;

        var output = string.Concat(help.Stdout, "\n", help.Stderr);
        return output.Contains("--output-format", StringComparison.Ordinal)
            && output.Contains("stream-json", StringComparison.Ordinal);
    }

    protected override AgentInvocation BuildInvocation(
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        bool captureStructuredStream = false)
    {
        // gemini --yolo --skip-trust -p "<prompt>": sends a single non-interactive
        // prompt and exits. --yolo skips all tool-use confirmation prompts;
        // --skip-trust grants workspace trust for this session (otherwise
        // gemini-cli silently demotes --yolo to "default" approval-mode and
        // prompts on every tool call, which deadlocks in non-interactive use).
        // Both are appropriate inside the sandbox where the VM boundary is the
        // real permission boundary.
        var argv = new List<string> { Binary, "--yolo", "--skip-trust" };
        if (captureStructuredStream)
        {
            argv.Add("--output-format");
            argv.Add("stream-json");
        }
        if (!string.IsNullOrEmpty(modelId))
        {
            argv.Add("--model");
            argv.Add(modelId);
        }
        // Gemini CLI 0.40+ has no --reasoning/--thinking/--effort flag.
        // Reasoning level is encoded in the model config: gemini-3-* preset
        // configs (e.g. gemini-3-flash-preview, gemini-3-pro-preview) extend
        // chat-base-3 which sets thinkingLevel: HIGH. gemini-2.5-* uses the
        // default thinking budget. So ReasoningMode is informational only on
        // this runner — picking a gemini-3-* ModelId is what gives "high".
        _ = reasoningMode;
        // Pass the prompt via stdin rather than as a positional argv. Linux's
        // MAX_ARG_STRLEN is 128 KiB per single argv element; rework prompts that
        // include many audit findings can exceed that and surface as exit 126
        // from the sandbox wrapper's `exec "$@"`. gemini-cli's -p flag's docstring
        // says "Appended to input on stdin (if any)" — so we drop -p entirely and
        // feed the prompt as stdin, which the CLI treats as the primary prompt.
        // The sandbox wrapper forwards stdin automatically when SandboxExec.Stdin
        // is non-null, via its --keep-stdin path.
        return new AgentInvocation(argv, Stdin: prompt);
    }

    // Code Assist generateContent endpoint used by the OAuth subscription path
    // (the same v1internal family GeminiQuotaProbe / GeminiModelListProbe hit).
    // The API-key path stays on the public generativelanguage.googleapis.com
    // surface because that endpoint does not authenticate OAuth bearer tokens.
    internal const string OAuthGenerateContentEndpoint =
        "https://cloudcode-pa.googleapis.com/v1internal:generateContent";

    public string? GetTextOnlyUnavailabilityReason(AgentCredential? credential)
    {
        if (TryGetApiKey(credential, out _)) return null;
        if (TryGetOAuthAccessToken(credential, out _)) return null;
        return "GEMINI_API_KEY or Gemini OAuth credentials are required";
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

        // API-key first preference: pay-per-use callers explicitly configured
        // GEMINI_API_KEY and expect that quota to be spent, not the OAuth one.
        if (TryGetApiKey(credential, out var apiKey))
            return await SendApiKeyAsync(_textOnlyHttp, systemPrompt, userPrompt, apiKey, modelId, ct).ConfigureAwait(false);

        // OAuth subscription fallback: authorized for Gemini specifically (the
        // operator note explicitly permits subscription-OAuth usage against
        // Gemini's API directly; this is the resolver-cascade workaround until
        // the agentic in-VM resolver lands).
        if (TryGetOAuthAccessToken(credential, out var oauthToken))
            return await SendOAuthAsync(_textOnlyHttp, systemPrompt, userPrompt, oauthToken, modelId, ct).ConfigureAwait(false);

        return new TextOnlyAgentResult(
            false,
            "missing Gemini text-only credential",
            null,
            "GEMINI_API_KEY or Gemini OAuth credentials are required");
    }

    private static async Task<TextOnlyAgentResult> SendApiKeyAsync(
        HttpClient http,
        string? systemPrompt,
        string userPrompt,
        string apiKey,
        string? modelId,
        CancellationToken ct)
    {
        try
        {
            var effectiveModel = string.IsNullOrWhiteSpace(modelId) ? DefaultTextOnlyModel : modelId;
            var requestBody = BuildGenerateContentRequest(systemPrompt, userPrompt);
            var body = JsonSerializer.Serialize(requestBody);
            var endpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(effectiveModel)}:generateContent";
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("x-goog-api-key", apiKey);

            var response = await BoundedHttpResponseReader.SendAsync(http, request, ct: ct).ConfigureAwait(false);
            if (response.BodyTooLarge)
                return new TextOnlyAgentResult(false, "Gemini text-only call failed: response too large", null, "Response size exceeded 256 KiB limit.");
            var responseText = response.Body ?? string.Empty;
            if (!response.IsSuccessStatusCode)
                return new TextOnlyAgentResult(false, $"Gemini text-only call failed: HTTP {(int)response.StatusCode}", null, responseText);

            return new TextOnlyAgentResult(true, "ok", ExtractResponseText(responseText), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new TextOnlyAgentResult(false, "Gemini text-only call failed", null, ex.Message);
        }
    }

    private static async Task<TextOnlyAgentResult> SendOAuthAsync(
        HttpClient http,
        string? systemPrompt,
        string userPrompt,
        string accessToken,
        string? modelId,
        CancellationToken ct)
    {
        try
        {
            var effectiveModel = string.IsNullOrWhiteSpace(modelId) ? DefaultTextOnlyModel : modelId;
            // Code Assist wraps the GenerateContent body in {model, request}
            // (see GeminiQuotaProbe.ProbeOneAsync for the canonical shape).
            var generateContentRequest = BuildGenerateContentRequest(systemPrompt, userPrompt);
            var body = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["model"] = $"models/{effectiveModel}",
                ["request"] = generateContentRequest,
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, OAuthGenerateContentEndpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var response = await BoundedHttpResponseReader.SendAsync(http, request, ct: ct).ConfigureAwait(false);
            if (response.BodyTooLarge)
                return new TextOnlyAgentResult(false, "Gemini text-only call failed: response too large", null, "Response size exceeded 256 KiB limit.");
            var responseText = response.Body ?? string.Empty;
            if (!response.IsSuccessStatusCode)
                return new TextOnlyAgentResult(false, $"Gemini text-only call failed: HTTP {(int)response.StatusCode}", null, responseText);

            return new TextOnlyAgentResult(true, "ok", ExtractResponseText(responseText), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new TextOnlyAgentResult(false, "Gemini text-only call failed", null, ex.Message);
        }
    }

    private static Dictionary<string, object?> BuildGenerateContentRequest(
        string? systemPrompt,
        string userPrompt)
    {
        var request = new Dictionary<string, object?>
        {
            ["contents"] = new[]
            {
                new
                {
                    role = "user",
                    parts = new[] { new { text = userPrompt } },
                },
            },
            ["generationConfig"] = new { maxOutputTokens = TextOnlyMaxOutputTokens },
        };
        if (systemPrompt is not null)
            request["systemInstruction"] = new { parts = new[] { new { text = systemPrompt } } };
        return request;
    }

    private static bool TryGetApiKey(AgentCredential? credential, out string apiKey)
    {
        apiKey = "";
        if (credential is null) return false;
        if (!credential.EnvironmentVariables.TryGetValue("GEMINI_API_KEY", out var v) || string.IsNullOrEmpty(v))
            return false;
        apiKey = v;
        return true;
    }

    private static bool TryGetOAuthAccessToken(AgentCredential? credential, out string accessToken)
    {
        accessToken = "";
        if (credential is null) return false;
        if (!credential.EnvironmentVariables.TryGetValue(CodeyBox.Core.GeminiConstants.OAuthCredsEnvVar, out var bundle)
            || string.IsNullOrEmpty(bundle))
            return false;
        var token = GeminiSmokeProbe.ExtractAccessToken(bundle);
        if (string.IsNullOrEmpty(token)) return false;
        accessToken = token!;
        return true;
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
        var structuredStreamSupported = !captureStructuredStream
            || await SupportsStructuredStreamAsync(sandbox, ct).ConfigureAwait(false);

        var effectiveCaptureStructuredStream = captureStructuredStream && structuredStreamSupported;

        // Preserve raw stdout chunks only when they are being persisted as the
        // structured stream. Live stdout clients keep the historical ANSI-free
        // Gemini output when capture is disabled or unavailable.
        var effectiveStdoutCallback = effectiveCaptureStructuredStream || stdoutChunkCallback is null
            ? stdoutChunkCallback
            : chunk => stdoutChunkCallback(Strip(chunk) ?? string.Empty);

        var result = await base.RunAsync(
            sandbox,
            workingDirectory,
            prompt,
            credential,
            modelId,
            reasoningMode,
            ct,
            effectiveStdoutCallback,
            effectiveCaptureStructuredStream);

        var stderr = Strip(result.Stderr);
        if (captureStructuredStream && !structuredStreamSupported)
        {
            var warning = $"Warning: Gemini CLI at '{Binary}' does not advertise --output-format stream-json; structured stream capture was disabled.";
            stderr = string.IsNullOrEmpty(stderr) ? warning : $"{warning}\n{stderr}";
        }

        var stdout = Strip(result.Stdout);
        return result with
        {
            Summary = EnrichFailureSummary(result.Success, result.Summary, stderr, stdout),
            Stdout = stdout,
            Stderr = stderr,
        };
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
        Action<string>? strippingCallback = stdoutChunkCallback is null
            ? null
            : chunk => stdoutChunkCallback(Strip(chunk) ?? string.Empty);
        var result = await base.RunResumedAsync(sandbox, workingDirectory, prompt, credential, resume, modelId, reasoningMode, ct, strippingCallback);
        var stdout = Strip(result.Stdout);
        var stderr = Strip(result.Stderr);
        return result with
        {
            Summary = EnrichFailureSummary(result.Success, result.Summary, stderr, stdout),
            Stdout = stdout,
            Stderr = stderr,
        };
    }

    private static string? Strip(string? s) =>
        s is null ? null : AnsiEscape.Replace(s, string.Empty);

    /// <summary>
    /// On failure, appends a single-line tail of stderr (or stdout, when stderr
    /// is empty) to the base "agent exited N" summary so downstream
    /// <c>TerminalQuotaError</c> messages and <c>WorkItem.LastError</c> carry
    /// the diagnostic text instead of just the exit code. Gemini exits 1 for
    /// every authentication, network, quota, and CLI-internal failure shape,
    /// so the exit code alone is uninformative — without this enrichment
    /// operators cannot tell quota from auth from transport from a stale
    /// model id. Returns the unchanged summary when the run succeeded or when
    /// neither stream produced usable text.
    /// </summary>
    internal static string EnrichFailureSummary(bool success, string baseSummary, string? stderr, string? stdout)
    {
        if (success) return baseSummary;
        var tail = ExtractDiagnosticTail(stderr) ?? ExtractDiagnosticTail(stdout);
        return tail is null ? baseSummary : $"{baseSummary}: {tail}";
    }

    /// <summary>
    /// Collapses internal whitespace and control characters in <paramref name="text"/>
    /// to a single-line tail of at most <see cref="FailureSummaryTailMaxChars"/>
    /// characters, preserving the most recent (right-hand) bytes — provider
    /// errors typically arrive on the final lines of CLI output. Returns null
    /// when the input has no printable content.
    /// </summary>
    internal static string? ExtractDiagnosticTail(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var collapsed = WhitespaceRun.Replace(text.Trim(), " ").Trim();
        if (collapsed.Length == 0) return null;
        return collapsed.Length <= FailureSummaryTailMaxChars
            ? collapsed
            : "…" + collapsed[^(FailureSummaryTailMaxChars - 1)..];
    }

    internal static string ExtractResponseText(string responseText)
    {
        using var doc = JsonDocument.Parse(responseText);
        var parts = new List<string>();
        // Code Assist's v1internal:generateContent wraps its payload in
        // {"response": {"candidates": [...]}}; the public v1beta endpoint
        // returns {"candidates": [...]} directly. Accept either.
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("response", out var wrapped)
            && wrapped.ValueKind == JsonValueKind.Object)
        {
            root = wrapped;
        }
        if (!root.TryGetProperty("candidates", out var candidates)
            || candidates.ValueKind != JsonValueKind.Array)
            return string.Empty;

        foreach (var candidate in candidates.EnumerateArray())
        {
            if (!candidate.TryGetProperty("content", out var content)
                || !content.TryGetProperty("parts", out var contentParts)
                || contentParts.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var part in contentParts.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var text)
                    && text.ValueKind == JsonValueKind.String)
                    parts.Add(text.GetString() ?? string.Empty);
            }
        }

        return string.Concat(parts);
    }
}
