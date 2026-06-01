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
    private static readonly HttpClient TextOnlyHttp = new();

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

    protected override string PreemptProcessPattern => Binary;

    /// <summary>
    /// Materialises the Gemini OAuth credentials and settings file into
    /// <c>~/.gemini/</c> inside the sandbox if the env-var bundle is present
    /// (set by <c>GeminiOAuthFileCredentialProvider</c>). The Gemini CLI
    /// hard-reads these paths and offers no env-var alternative for OAuth, so
    /// we shuttle them in via env vars and write them at sandbox-prepare time.
    /// </summary>
    protected override async Task<AgentResult?> PrepareSandboxAsync(
        ISandbox sandbox,
        string workingDirectory,
        AgentCredential? credential,
        AgentResumeContext? resume,
        CancellationToken ct = default)
    {
        // Skip the bash hook entirely when no OAuth bundle is present (e.g.
        // operators using GEMINI_API_KEY); the CLI will fall back to whichever
        // env-var auth path the credential pipeline plugged in.
        if (credential is null
            || !credential.EnvironmentVariables.ContainsKey(CodeyBox.Core.GeminiConstants.OAuthCredsEnvVar))
            return null;

        var script =
            "set -eu\n" +
            "mkdir -p \"$HOME/.gemini\"\n" +
            "umask 077\n" +
            "if [ -n \"${CODEYBOX_GEMINI_OAUTH_CREDS_JSON:-}\" ]; then\n" +
            "  printf '%s' \"$CODEYBOX_GEMINI_OAUTH_CREDS_JSON\" > \"$HOME/.gemini/oauth_creds.json\"\n" +
            "fi\n" +
            "if [ -n \"${CODEYBOX_GEMINI_SETTINGS_JSON:-}\" ]; then\n" +
            "  printf '%s' \"$CODEYBOX_GEMINI_SETTINGS_JSON\" > \"$HOME/.gemini/settings.json\"\n" +
            "fi\n";
        var write = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["bash", "-c", script],
        }, ct);
        if (!write.Success)
        {
            return new AgentResult(
                Success: false,
                Summary: $"failed to materialise gemini auth: exit {write.ExitCode}",
                Stdout: write.Stdout,
                Stderr: write.Stderr);
        }
        return null;
    }

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

        // API-key first preference: pay-per-use callers explicitly configured
        // GEMINI_API_KEY and expect that quota to be spent, not the OAuth one.
        if (TryGetApiKey(credential, out var apiKey))
            return await SendApiKeyAsync(prompt, apiKey, modelId, ct).ConfigureAwait(false);

        // OAuth subscription fallback: authorized for Gemini specifically (the
        // operator note explicitly permits subscription-OAuth usage against
        // Gemini's API directly; this is the resolver-cascade workaround until
        // the agentic in-VM resolver lands).
        if (TryGetOAuthAccessToken(credential, out var oauthToken))
            return await SendOAuthAsync(prompt, oauthToken, modelId, ct).ConfigureAwait(false);

        return new TextOnlyAgentResult(
            false,
            "missing Gemini text-only credential",
            null,
            "GEMINI_API_KEY or Gemini OAuth credentials are required");
    }

    private static async Task<TextOnlyAgentResult> SendApiKeyAsync(
        string prompt, string apiKey, string? modelId, CancellationToken ct)
    {
        try
        {
            var effectiveModel = string.IsNullOrWhiteSpace(modelId) ? "gemini-2.5-pro" : modelId;
            var body = JsonSerializer.Serialize(new
            {
                contents = new[]
                {
                    new
                    {
                        role = "user",
                        parts = new[] { new { text = prompt } },
                    },
                },
                generationConfig = new { maxOutputTokens = 8192 },
            });
            var endpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(effectiveModel)}:generateContent";
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("x-goog-api-key", apiKey);

            using var response = await TextOnlyHttp.SendAsync(request, ct).ConfigureAwait(false);
            var responseText = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
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
        string prompt, string accessToken, string? modelId, CancellationToken ct)
    {
        try
        {
            var effectiveModel = string.IsNullOrWhiteSpace(modelId) ? "gemini-2.5-pro" : modelId;
            // Code Assist wraps the GenerateContent body in {model, request}
            // (see GeminiQuotaProbe.ProbeOneAsync for the canonical shape).
            var body = JsonSerializer.Serialize(new
            {
                model = $"models/{effectiveModel}",
                request = new
                {
                    contents = new[]
                    {
                        new
                        {
                            role = "user",
                            parts = new[] { new { text = prompt } },
                        },
                    },
                    generationConfig = new { maxOutputTokens = 8192 },
                },
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, OAuthGenerateContentEndpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await TextOnlyHttp.SendAsync(request, ct).ConfigureAwait(false);
            var responseText = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new TextOnlyAgentResult(false, $"Gemini text-only call failed: HTTP {(int)response.StatusCode}", null, responseText);

            return new TextOnlyAgentResult(true, "ok", ExtractResponseText(responseText), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new TextOnlyAgentResult(false, "Gemini text-only call failed", null, ex.Message);
        }
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
