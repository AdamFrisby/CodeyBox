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

    public override AgentKind Kind => AgentKind.Gemini;

    /// <summary>
    /// Path to the gemini binary inside the sandbox. Override only if the
    /// sandbox image installs it elsewhere.
    /// </summary>
    public string Binary { get; init; } = "gemini";

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
            || !credential.EnvironmentVariables.ContainsKey("CODEYBOX_GEMINI_OAUTH_CREDS_JSON"))
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
        argv.Add("-p");
        argv.Add(prompt);
        return new AgentInvocation(argv);
    }

    public async Task<TextOnlyAgentResult> RunTextOnlyAsync(
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        CancellationToken ct = default)
    {
        _ = reasoningMode;
        string? apiKey = null;
        credential?.EnvironmentVariables.TryGetValue("GEMINI_API_KEY", out apiKey);
        if (string.IsNullOrEmpty(apiKey))
            return new TextOnlyAgentResult(false, "missing Gemini text-only credential", null, "GEMINI_API_KEY is required");

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

        return result with
        {
            Stdout = Strip(result.Stdout),
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
        return result with
        {
            Stdout = Strip(result.Stdout),
            Stderr = Strip(result.Stderr),
        };
    }

    private static string? Strip(string? s) =>
        s is null ? null : AnsiEscape.Replace(s, string.Empty);

    private static string ExtractResponseText(string responseText)
    {
        using var doc = JsonDocument.Parse(responseText);
        var parts = new List<string>();
        if (!doc.RootElement.TryGetProperty("candidates", out var candidates)
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
