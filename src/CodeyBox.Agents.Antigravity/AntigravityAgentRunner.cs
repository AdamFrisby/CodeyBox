using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Agents.Antigravity;

/// <summary>
/// Drives the Google Antigravity CLI (binary <c>agy</c>) in non-interactive
/// mode. The CLI is shape-compatible with Claude Code: a one-shot
/// <c>--print</c> mode that accepts <c>--model</c>, a permission-skip flag
/// for sandboxed runs, and a native <c>--continue</c> / <c>--conversation</c>
/// resume path. The agent is expected to be installed in the sandbox image;
/// the host injects subscription OAuth via tmpfs/env per
/// <see cref="AntigravityConstants.OAuthCredsEnvVar"/>.
///
/// <para>Multi-model gateway: a single Google AI subscription quota fronts
/// Gemini, Claude, and GPT-OSS models. The orchestrator models each
/// acceptable model as its own <see cref="AgentMembership"/> so the existing
/// per-model exhaustion key keeps failover scoped to the exhausted bucket
/// without needing a separate "sub-subscription pool" subsystem.</para>
/// </summary>
public sealed class AntigravityAgentRunner : CliAgentRunnerBase
{
    public override AgentKind Kind => AgentKind.Antigravity;

    /// <summary>Default agy binary name on the sandbox PATH. The in-VM smoke
    /// probe pins to this so the probe and runner can never drift.</summary>
    public const string DefaultBinary = "agy";

    /// <summary>Path to the agy binary inside the sandbox. Override only if
    /// the sandbox image installs it elsewhere.</summary>
    public string Binary { get; init; } = DefaultBinary;

    protected override IReadOnlyList<string> ScratchpadHomeDirectories =>
        // The agy binary stashes session state under ~/.gemini/antigravity-cli
        // (conversations index + per-conversation "brain" transcripts).
        // Capturing both lets a preempt/resume cycle pick the conversation back
        // up via --conversation <id>.
        [".gemini/antigravity-cli/conversations", ".gemini/antigravity-cli/brain"];

    protected override string PreemptProcessPattern => Binary;

    /// <summary>
    /// Materialises the Antigravity OAuth token bundle into the sandbox at
    /// <c>~/.gemini/antigravity-cli/antigravity-oauth-token</c> — the path agy's
    /// <c>fileTokenStorage</c> reads when no system keyring is present (every
    /// headless sandbox). The bundle is written verbatim: it carries the
    /// refresh_token so the in-VM agy can refresh the short-lived access_token
    /// itself (it has no other refresh path). When no bundle is present, the
    /// runner falls back to whatever auth path the credential pipeline plugged in.
    /// </summary>
    protected override async Task<AgentResult?> PrepareSandboxAsync(
        ISandbox sandbox,
        string workingDirectory,
        AgentCredential? credential,
        AgentResumeContext? resume,
        CancellationToken ct = default)
    {
        if (credential is null
            || !credential.EnvironmentVariables.ContainsKey(AntigravityConstants.OAuthCredsEnvVar))
            return null;

        var script =
            "set -eu\n" +
            "umask 077\n" +
            "mkdir -p \"$HOME/.gemini/antigravity-cli\"\n" +
            "if [ -n \"${CODEYBOX_ANTIGRAVITY_OAUTH_CREDS_JSON:-}\" ]; then\n" +
            "  printf '%s' \"$CODEYBOX_ANTIGRAVITY_OAUTH_CREDS_JSON\" > \"$HOME/.gemini/antigravity-cli/antigravity-oauth-token\"\n" +
            "  chmod 600 \"$HOME/.gemini/antigravity-cli/antigravity-oauth-token\"\n" +
            "fi\n";
        var write = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["bash", "-c", script],
        }, ct).ConfigureAwait(false);
        if (!write.Success)
        {
            return new AgentResult(
                Success: false,
                Summary: $"failed to materialise antigravity auth: exit {write.ExitCode}",
                Stdout: write.Stdout,
                Stderr: write.Stderr);
        }
        return null;
    }

    protected override AgentInvocation BuildInvocation(
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        bool captureStructuredStream = false)
        => BuildAgyInvocation(prompt, modelId, reasoningMode, resumeConversationId: null, useContinue: false);

    protected override AgentInvocation BuildResumeInvocation(
        string prompt,
        AgentCredential? credential,
        AgentResumeContext resume,
        string? modelId = null,
        string? reasoningMode = null)
    {
        // CheckpointRef can carry a specific conversation id captured at preempt
        // time (format "agy-conversation:<id>"). If absent, fall back to --continue
        // (most recent conversation) — strictly worse than a pinned id but matches
        // Claude's resume-without-id fallback for parity.
        var id = TryParseConversationId(resume.CheckpointRef);
        return BuildAgyInvocation(
            prompt,
            modelId,
            reasoningMode,
            resumeConversationId: id,
            useContinue: id is null);
    }

    private AgentInvocation BuildAgyInvocation(
        string prompt,
        string? modelId,
        string? reasoningMode,
        string? resumeConversationId,
        bool useContinue)
    {
        // agy --print --dangerously-skip-permissions [...]: one-shot prompt
        // that auto-approves tool calls. The sandbox boundary is the real
        // permission boundary — same shape we use for Claude.
        var argv = new List<string> { Binary, "--print", "--dangerously-skip-permissions" };

        if (!string.IsNullOrWhiteSpace(resumeConversationId))
        {
            argv.Add("--conversation");
            argv.Add(resumeConversationId);
        }
        else if (useContinue)
        {
            argv.Add("--continue");
        }

        if (!string.IsNullOrWhiteSpace(modelId))
        {
            argv.Add("--model");
            argv.Add(modelId);
        }

        // Reasoning level is encoded in the model id for Antigravity (each
        // gateway model carries its thinking level — gemini-3.5-flash-high,
        // claude-opus-4-6-thinking, …), so ReasoningMode is informational
        // only on this runner. Same approach as Gemini.
        _ = reasoningMode;

        // Feed the prompt via stdin rather than as a positional argv element.
        // Linux's MAX_ARG_STRLEN is 128 KiB per single argv element; rework
        // prompts that include many audit findings can exceed that and surface
        // as exit 126 from the sandbox wrapper's exec. Mirrors GeminiAgentRunner.
        return new AgentInvocation(argv, Stdin: prompt);
    }

    internal const string ConversationCheckpointPrefix = "agy-conversation:";

    internal static string? TryParseConversationId(string? checkpointRef)
    {
        if (string.IsNullOrWhiteSpace(checkpointRef)) return null;
        if (!checkpointRef.StartsWith(ConversationCheckpointPrefix, StringComparison.Ordinal))
            return null;
        var id = checkpointRef[ConversationCheckpointPrefix.Length..].Trim();
        return string.IsNullOrEmpty(id) ? null : id;
    }
}
