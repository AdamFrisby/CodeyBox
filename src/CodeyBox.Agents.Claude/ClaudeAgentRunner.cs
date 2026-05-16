using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Sandbox;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CodeyBox.Agents.Claude;

/// <summary>
/// Drives the Claude Code CLI ("claude") in non-interactive mode. The agent
/// is expected to be installed in the sandbox image; the host injects only
/// the API token via tmpfs/env.
/// </summary>
public sealed class ClaudeAgentRunner : CliAgentRunnerBase, IStructuredStreamAgentRunner, IAgentDefaultModelProvider, ITextOnlyAgentRunner
{
    private static readonly HttpClient TextOnlyHttp = new();

    public override AgentKind Kind => AgentKind.Claude;

    /// <summary>
    /// Path to the claude binary inside the sandbox. Override only if the
    /// sandbox image installs it elsewhere.
    /// </summary>
    public string Binary { get; init; } = "claude";

    /// <summary>
    /// Default model passed to <c>--model</c> when no per-item override is provided.
    /// Pinned to Opus to avoid the CLI defaulting to a lighter model.
    /// </summary>
    public string? DefaultModelId { get; init; } = "claude-opus-4-7";

    protected override IReadOnlyList<string> ScratchpadHomeDirectories => [".claude/projects", ".claude/todos"];

    protected override string PreemptProcessPattern => Binary;

    /// <summary>
    /// Materialises the host's <c>~/.claude/.credentials.json</c> inside the
    /// sandbox if the env-var bundle is present (set by
    /// <c>ClaudeOAuthFileCredentialProvider</c>). The in-VM <c>claude</c> CLI
    /// reads the file to obtain both access_token and refresh_token, so it can
    /// auto-rotate without 401-ing when the host's Claude Code rotates the
    /// access_token mid-run. The legacy <c>CLAUDE_CODE_OAUTH_TOKEN</c> env var
    /// remains the primary auth path; this hook is purely additive.
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

    public async Task<TextOnlyAgentResult> RunTextOnlyAsync(
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        CancellationToken ct = default)
    {
        _ = reasoningMode;
        string? oauthToken = null;
        string? apiKey = null;
        credential?.EnvironmentVariables.TryGetValue("CLAUDE_CODE_OAUTH_TOKEN", out oauthToken);
        credential?.EnvironmentVariables.TryGetValue("ANTHROPIC_API_KEY", out apiKey);
        if (string.IsNullOrEmpty(oauthToken) && string.IsNullOrEmpty(apiKey))
            return new TextOnlyAgentResult(false, "missing Claude text-only credential", null, "CLAUDE_CODE_OAUTH_TOKEN or ANTHROPIC_API_KEY is required");

        try
        {
            var body = JsonSerializer.Serialize(new
            {
                model = string.IsNullOrWhiteSpace(modelId) ? DefaultModelId ?? "claude-haiku-4-5-20251001" : modelId,
                max_tokens = 8192,
                messages = new[] { new { role = "user", content = prompt } },
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
            if (!string.IsNullOrEmpty(oauthToken))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", oauthToken);
            else
                request.Headers.Add("x-api-key", apiKey!);
            request.Headers.Add("anthropic-version", "2023-06-01");
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var response = await TextOnlyHttp.SendAsync(request, ct).ConfigureAwait(false);
            var responseText = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new TextOnlyAgentResult(false, $"Claude text-only call failed: HTTP {(int)response.StatusCode}", null, responseText);

            using var doc = JsonDocument.Parse(responseText);
            var output = string.Concat(doc.RootElement
                .GetProperty("content")
                .EnumerateArray()
                .Where(static c => c.TryGetProperty("type", out var type)
                    && string.Equals(type.GetString(), "text", StringComparison.Ordinal)
                    && c.TryGetProperty("text", out _))
                .Select(static c => c.GetProperty("text").GetString()));
            return new TextOnlyAgentResult(true, "ok", output, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new TextOnlyAgentResult(false, "Claude text-only call failed", null, ex.Message);
        }
    }

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
