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
public sealed class CodexAgentRunner : CliAgentRunnerBase, IStructuredStreamAgentRunner, IAgentDefaultModelProvider, ITextOnlyAgentRunner
{
    private static readonly AsyncLocal<string?> CurrentStructuredStreamFlag = new();
    private static readonly HttpClient TextOnlyHttp = new();

    public override AgentKind Kind => AgentKind.Codex;

    public string Binary { get; init; } = "codex";

    protected override IReadOnlyList<string> ScratchpadHomeDirectories => [".codex/sessions", ".codex/history.jsonl"];

    protected override string PreemptProcessPattern => Binary;

    /// <summary>
    /// Default model passed to <c>--model</c> when no per-item override is provided.
    /// </summary>
    public string? DefaultModelId { get; init; } = "gpt-5.5";

    public async Task<bool> SupportsStructuredStreamAsync(ISandbox sandbox, CancellationToken ct = default) =>
        await DetectStructuredStreamFlagAsync(sandbox, ct).ConfigureAwait(false) is not null;

    /// <summary>
    /// ChatGPT-subscription auth: ensure <c>~/.codex/auth.json</c> is present
    /// inside the sandbox. The codex CLI reads ONLY that file path; there's no
    /// env-var equivalent.
    ///
    /// <para>Two paths, decided by what's already in the sandbox:</para>
    /// <list type="bullet">
    ///   <item><b>Mount path</b> (preferred, multipass): the credential
    ///   provider declared a bind-mount that already exposes the host's
    ///   <c>~/.codex/</c> at <c>$HOME/.codex/</c>. We detect this by stat-ing
    ///   the file, and skip the write entirely. Critical for correctness:
    ///   overwriting would clobber any refresh-token rotation the host has
    ///   done since the credential was read, and would re-introduce the
    ///   refresh-token-reuse cascade the mount is designed to prevent.</item>
    ///   <item><b>Env-var snapshot path</b> (fallback, bwrap/process): no
    ///   mount in scope, so materialise the file from <c>CODEX_AUTH_JSON</c>.
    ///   Snapshot is fine here because these providers either share the host
    ///   FS (process) or tear down per-run state anyway (bwrap tmpfs HOME).
    ///   </item>
    /// </list>
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
        // If a bind-mount already supplied auth.json, leave it alone — writing
        // would clobber the host's latest refresh-token rotation.
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
        CancellationToken ct = default)
    {
        var apiKey = ResolveOpenAiApiKey(credential);
        if (string.IsNullOrEmpty(apiKey))
            return new TextOnlyAgentResult(false, "missing Codex text-only credential", null, "OPENAI_API_KEY is required for text-only calls");

        try
        {
            var body = new Dictionary<string, object?>
            {
                ["model"] = string.IsNullOrWhiteSpace(modelId) ? DefaultModelId ?? "gpt-4o-mini" : modelId,
                ["input"] = prompt,
                ["max_output_tokens"] = 8192,
            };
            if (!string.IsNullOrWhiteSpace(reasoningMode))
                body["reasoning"] = new Dictionary<string, string> { ["effort"] = reasoningMode };

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            using var response = await TextOnlyHttp.SendAsync(request, ct).ConfigureAwait(false);
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
        var argv = new List<string>
        {
            Binary, "exec",
            "--dangerously-bypass-approvals-and-sandbox",
        };
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
        // Pass the prompt via stdin rather than as a positional argv. Linux's
        // MAX_ARG_STRLEN is 128 KiB per single argv element; rework prompts that
        // include many audit findings can exceed that and surface as exit 126
        // from the sandbox wrapper's `exec "$@"`. `codex exec` reads instructions
        // from stdin when no positional prompt is given (per its --help). The
        // sandbox wrapper forwards stdin automatically when SandboxExec.Stdin is
        // non-null, via its --keep-stdin path.
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
}
