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

    public async Task<bool> SupportsStructuredStreamAsync(ISandbox sandbox, CancellationToken ct = default)
    {
        var help = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = [Binary, "--help"],
        }, ct);

        if (!help.Success)
            return false;

        var output = string.Concat(help.Stdout, "\n", help.Stderr);
        return output.Contains("--json", StringComparison.Ordinal);
    }

    protected override AgentInvocation BuildInvocation(
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        bool captureStructuredStream = false)
    {
        // gemini --yolo -p "<prompt>": sends a single non-interactive prompt and exits.
        // --yolo skips all tool-use confirmation prompts — appropriate inside the
        // sandbox where the VM boundary is the permission boundary.
        var argv = new List<string> { Binary, "--yolo" };
        if (captureStructuredStream)
            argv.Add("--json");
        if (!string.IsNullOrEmpty(modelId))
        {
            argv.Add("--model");
            argv.Add(modelId);
        }
        // ReasoningMode="high" maps to --thinking, which enables Gemini's extended
        // thinking (high-quality reasoning) mode. Requires @google/gemini-cli ≥ 0.1.9.
        // Config validation rejects Gemini members with QualityScore >= 90 without
        // ReasoningMode="high", so this branch fires for all frontier-adjacent Gemini slots.
        if (string.Equals(reasoningMode, "high", StringComparison.OrdinalIgnoreCase))
            argv.Add("--thinking");
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
            var warning = $"Warning: Gemini CLI at '{Binary}' does not advertise --json; structured stream capture was disabled.";
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
