using System.Globalization;
using System.Text.Json;
using System.Threading;
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
public sealed class AntigravityAgentRunner : CliAgentRunnerBase, IStructuredStreamAgentRunner
{
    /// <summary>
    /// Upper bound on how many bytes of agy's glog we read back and merge into
    /// the captured output (256 KiB). agy's glog is cumulative and can grow
    /// large on a long tool-heavy run; the read is bounded so a runaway log
    /// can't be ingested unbounded into Stderr, the stream, and the audit log.
    /// </summary>
    private const int MaxLogTailBytes = 256 * 1024;

    // Threads the per-invocation agy log path into BuildAgyInvocation (whose
    // signature is fixed by the base class) without a new IAgentRunner
    // parameter. Set for the duration of a single run and cleared in finally.
    private readonly AsyncLocal<string?> _currentLogPath = new();

    public override AgentKind Kind => AgentKind.Antigravity;

    /// <summary>Default agy binary name on the sandbox PATH. The in-VM smoke
    /// probe pins to this so the probe and runner can never drift.</summary>
    public const string DefaultBinary = "agy";

    /// <summary>Path to the agy binary inside the sandbox. Override only if
    /// the sandbox image installs it elsewhere.</summary>
    public string Binary { get; init; } = DefaultBinary;

    /// <summary>
    /// Per-model-response wait passed to agy as <c>--print-timeout</c>. agy's
    /// built-in default is 5m: the first time a single gemini turn on a large
    /// CodeyBox work item exceeds it, agy aborts the entire one-shot session
    /// with <c>Error: timed out waiting for response</c> and ZERO committed
    /// changes — which then trips the no-changes circuit breaker. A generous
    /// budget lets a slow large-context turn complete. Configurable via
    /// <c>CodeyBox:Antigravity:PrintTimeoutMinutes</c>; <see cref="TimeSpan.Zero"/>
    /// leaves agy's own default in place.
    /// </summary>
    public TimeSpan PrintTimeout { get; init; } = TimeSpan.FromMinutes(20);

    /// <summary>
    /// Probes <c>agy --help</c> for structured-stream support. The agy CLI is
    /// shape-compatible with Claude Code (see <see cref="AntigravityCostExtractor"/>'s
    /// comment about the terminal <c>type:result</c> NDJSON envelope), so when
    /// the help text advertises <c>--output-format</c> with <c>stream-json</c>,
    /// the runner asks for it on the next dispatch. If the flag is absent
    /// (older agy build, or a release that pivots to a different schema), the
    /// orchestrator falls back to plaintext capture via the runner's normal
    /// stdout/stderr stream — captured all the same by AgentStreamStore — and
    /// <see cref="AntigravityStreamParser"/> defers to the plaintext-fallback
    /// summary path.
    /// </summary>
    public async Task<bool> SupportsStructuredStreamAsync(ISandbox sandbox, CancellationToken ct = default)
    {
        var help = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = [Binary, "--help"],
        }, ct).ConfigureAwait(false);

        if (!help.Success)
            return false;

        var output = string.Concat(help.Stdout, "\n", help.Stderr);
        return output.Contains("--output-format", StringComparison.Ordinal)
            && output.Contains("stream-json", StringComparison.Ordinal);
    }

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

    public override Task<AgentResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        CancellationToken ct = default,
        Action<string>? stdoutChunkCallback = null,
        bool captureStructuredStream = false)
        => RunWithLogCaptureAsync(
            sandbox,
            captureStructuredStream,
            stdoutChunkCallback,
            ct,
            () => base.RunAsync(sandbox, workingDirectory, prompt, credential, modelId, reasoningMode, ct, stdoutChunkCallback, captureStructuredStream));

    public override Task<AgentResult> RunResumedAsync(
        ISandbox sandbox,
        string workingDirectory,
        string prompt,
        AgentCredential? credential,
        AgentResumeContext resume,
        string? modelId = null,
        string? reasoningMode = null,
        CancellationToken ct = default,
        Action<string>? stdoutChunkCallback = null)
        => RunWithLogCaptureAsync(
            sandbox,
            // Resume turns deliberately do not request the structured stream
            // (see CliAgentRunnerBase.RunResumedAsync), so the folded glog uses
            // the same plaintext line shape.
            captureStructuredStream: false,
            stdoutChunkCallback,
            ct,
            () => base.RunResumedAsync(sandbox, workingDirectory, prompt, credential, resume, modelId, reasoningMode, ct, stdoutChunkCallback));

    /// <summary>
    /// Shared lifecycle for both run overrides: pick the per-run agy glog path,
    /// publish it for <see cref="BuildAgyInvocation"/> to emit as
    /// <c>--log-file</c>, ensure the directory exists, run the base invocation,
    /// then fold the glog into the captured output. The setup/teardown lives
    /// here once so the log-path convention can't drift between the two paths.
    /// </summary>
    private async Task<AgentResult> RunWithLogCaptureAsync(
        ISandbox sandbox,
        bool captureStructuredStream,
        Action<string>? stdoutChunkCallback,
        CancellationToken ct,
        Func<Task<AgentResult>> runBase)
    {
        var logFile = ComputeAgyLogPath();
        _currentLogPath.Value = logFile;
        try
        {
            await EnsureLogDirectoryAsync(sandbox, logFile, ct).ConfigureAwait(false);
            var result = await runBase().ConfigureAwait(false);
            return await ProcessResultAsync(sandbox, result, logFile, stdoutChunkCallback, captureStructuredStream, ct).ConfigureAwait(false);
        }
        finally
        {
            _currentLogPath.Value = null;
        }
    }

    /// <summary>
    /// Deterministic per-run path agy is told to write its glog to. Co-located
    /// with the orchestrator-assigned per-invocation log path
    /// (<see cref="AgentInvocationLogContext.CurrentLogPath"/>) so it correlates
    /// with the rest of the run's capture and lives under
    /// <see cref="SandboxConventions.AgentLogDir"/> — which is on the <c>/work</c>
    /// mount (survives a multipass suspend) and created by the exec wrapper. The
    /// fallback (test / non-pipeline callers with no assigned path) still lands
    /// under <c>AgentLogDir</c> rather than a provider-specific <c>$HOME</c> the
    /// process/bubblewrap sandboxes do not necessarily share.
    /// </summary>
    private static string ComputeAgyLogPath()
    {
        var assigned = AgentInvocationLogContext.CurrentLogPath;
        return string.IsNullOrEmpty(assigned)
            ? $"{SandboxConventions.AgentLogDir}/agy-run-{Guid.NewGuid():N}.log"
            : assigned + ".agy.log";
    }

    /// <summary>
    /// agy's <c>--log-file</c> open fails if the parent directory is missing.
    /// The exec wrapper only creates the log dir when <c>CODEYBOX_AGENT_LOG_FILE</c>
    /// is set, and <see cref="PrepareSandboxAsync"/> only creates
    /// <c>~/.gemini/…</c> on the OAuth-creds branch — so create the directory
    /// unconditionally here, before agy runs, independent of the credential path.
    /// </summary>
    private static async Task EnsureLogDirectoryAsync(ISandbox sandbox, string logFile, CancellationToken ct)
    {
        var dir = PosixDirName(logFile);
        if (dir.Length == 0)
            return;
        await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["mkdir", "-p", dir],
        }, ct).ConfigureAwait(false);
    }

    private static string PosixDirName(string path)
    {
        var idx = path.LastIndexOf('/');
        return idx <= 0 ? string.Empty : path[..idx];
    }

    private async Task<AgentResult> ProcessResultAsync(
        ISandbox sandbox,
        AgentResult result,
        string logFile,
        Action<string>? stdoutChunkCallback,
        bool captureStructuredStream,
        CancellationToken ct)
    {
        SandboxExecResult tailCmd;
        try
        {
            tailCmd = await sandbox.ExecAsync(new SandboxExec
            {
                // tail keeps the last MaxLogTailBytes; for the motivating case
                // (near-0-byte stdout, small glog) the whole file fits.
                Argv = ["tail", "-c", MaxLogTailBytes.ToString(CultureInfo.InvariantCulture), logFile],
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A requested cancellation must not be masked as a normal
            // completion — let it propagate like the base class's own probes do.
            throw;
        }
        catch (Exception ex)
        {
            // A broken capture path (sandbox/provider fault) must be observable
            // rather than silently degrading back to zero diagnostics.
            AuditLog.AgentLogCaptureFailed(Kind, ex.GetType().Name, ex.Message);
            return result;
        }

        if (!tailCmd.Success || string.IsNullOrEmpty(tailCmd.Stdout))
        {
            return result;
        }

        // Redact with the same routine the normal stream-capture path uses
        // (SensitiveDataRedactionEnricher.RedactText — see AgentStreamParser)
        // so token/auth lines in agy's glog are scrubbed identically before they
        // reach the stream or audit.
        var redactedLog = SensitiveDataRedactionEnricher.RedactText(tailCmd.Stdout);

        // Archive the glog to the per-run stream (observability / audit) on EVERY
        // outcome — this is what surfaces agy's otherwise invisible diagnostics in
        // the agent-stream files, which the pipeline records and audits.
        //
        // The glog is deliberately NOT merged into result.Stderr on any outcome.
        // agy's glog is CUMULATIVE within a single --print process: it records
        // transient errors agy later recovered from (an internal 429 a retry
        // cleared, an auth blip a refresh fixed). The orchestrator substring-scans
        // result.Stderr with the quota/auth classifiers, which cannot tell a
        // recovered-then-cleared "RESOURCE_EXHAUSTED"/"API Error: 401" from a
        // terminal one; folding the cumulative log into Stderr would let a stale
        // recovered line falsely bench the member or park it in
        // WaitingForQuotaReset (up to a 7-day lockout) even when the run failed
        // for an unrelated reason (timeout, tool error, no-changes). The
        // classifiers therefore stay on agy's own authoritative process
        // stdout/stderr; whether cumulative-log content is classifier-worthy is
        // orchestrator policy, not a decision this runner should encode. The
        // stream archive above still gives operators (and the audit) the full
        // diagnostics either way.
        if (stdoutChunkCallback is not null)
        {
            ForwardLogToStream(redactedLog, stdoutChunkCallback, captureStructuredStream);
        }

        return result;
    }

    private static void ForwardLogToStream(
        string redactedLog,
        Action<string> stdoutChunkCallback,
        bool captureStructuredStream)
    {
        var lines = redactedLog.Replace("\r", "", StringComparison.Ordinal).Split('\n');
        var count = lines.Length;
        if (count > 0 && string.IsNullOrEmpty(lines[count - 1]))
        {
            count--;
        }
        for (int i = 0; i < count; i++)
        {
            var line = lines[i];
            if (captureStructuredStream)
            {
                var envelope = JsonSerializer.Serialize(new { type = StderrEnvelopeType, text = line }) + "\n";
                stdoutChunkCallback(envelope);
            }
            else
            {
                stdoutChunkCallback(line + "\n");
            }
        }
    }

    protected override AgentInvocation BuildInvocation(
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        bool captureStructuredStream = false)
        => BuildAgyInvocation(prompt, modelId, reasoningMode, resumeConversationId: null, useContinue: false, captureStructuredStream);

    protected override AgentInvocation BuildResumeInvocation(
        string prompt,
        AgentCredential? credential,
        AgentResumeContext resume,
        string? modelId = null,
        string? reasoningMode = null,
        bool captureStructuredStream = false)
    {
        // CheckpointRef can carry a specific conversation id captured at preempt
        // time (format "agy-conversation:<id>"). If absent, fall back to --continue
        // (most recent conversation) — strictly worse than a pinned id but matches
        // Claude's resume-without-id fallback for parity.
        _ = captureStructuredStream;
        var id = TryParseConversationId(resume.CheckpointRef);
        return BuildAgyInvocation(
            prompt,
            modelId,
            reasoningMode,
            resumeConversationId: id,
            useContinue: id is null,
            captureStructuredStream: false);
    }

    private AgentInvocation BuildAgyInvocation(
        string prompt,
        string? modelId,
        string? reasoningMode,
        string? resumeConversationId,
        bool useContinue,
        bool captureStructuredStream)
    {
        // agy --print --dangerously-skip-permissions [...]: one-shot prompt
        // that auto-approves tool calls. The sandbox boundary is the real
        // permission boundary — same shape we use for Claude.
        var argv = new List<string> { Binary, "--print", "--dangerously-skip-permissions" };

        if (_currentLogPath.Value is { } logPath)
        {
            argv.Add("--log-file");
            argv.Add(logPath);
        }

        // Override agy's 5m default --print-timeout (the per-response wait). On a
        // large work item a single gemini turn can exceed 5m; agy then aborts the
        // whole session with "timed out waiting for response" and no committed
        // changes, tripping the no-changes circuit breaker. A generous budget
        // (Go duration syntax, e.g. "1200s") gives slow turns room to complete.
        if (PrintTimeout > TimeSpan.Zero)
        {
            argv.Add("--print-timeout");
            argv.Add($"{(long)PrintTimeout.TotalSeconds}s");
        }

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

        // captureStructuredStream is set by PipelineRunner when
        // CanCaptureStructuredStreamAsync returned true — i.e.
        // SupportsStructuredStreamAsync confirmed `agy --help` advertises
        // the flag. Pass it through so the captured stream file is NDJSON
        // (AntigravityCostExtractor / AntigravityStreamParser then extract
        // the structured token usage). When false, agy emits its human-
        // readable footer and the plaintext-fallback summariser takes over.
        if (captureStructuredStream)
        {
            argv.Add("--output-format");
            argv.Add("stream-json");
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
