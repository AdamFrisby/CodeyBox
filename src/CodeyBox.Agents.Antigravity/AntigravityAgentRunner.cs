using System.Collections.Concurrent;
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
    /// Upper bound on how many bytes of agy's glog we read back for capture
    /// (256 KiB). agy's glog is cumulative and can grow large on a long tool-heavy
    /// run; the read is bounded so a runaway log can't be ingested unbounded into
    /// the per-run stream and the audit log. The full tail is archived to the
    /// observability stream; only the terminal error region (see
    /// <see cref="AntigravityQuotaFailureDetector.ExtractTerminalErrorRegion"/>) is
    /// folded into the classifier-facing <c>result.Stderr</c>, and only on failure.
    /// </summary>
    private const int MaxLogTailBytes = 256 * 1024;

    // Threads the per-invocation agy log path into BuildAgyInvocation (whose
    // signature is fixed by the base class) without a new IAgentRunner
    // parameter. Set for the duration of a single run and cleared in finally.
    private readonly AsyncLocal<string?> _currentLogPath = new();

    private static readonly ConcurrentDictionary<string, bool> StructuredStreamSupportByVersion =
        new(StringComparer.Ordinal);

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
    /// Verifies structured-stream support with a real one-shot print-mode
    /// invocation. Some agy builds can mention <c>--output-format stream-json</c>
    /// in help text without accepting the flag in <c>--print</c>; only a
    /// successful NDJSON probe enables structured capture. Ambiguous failures
    /// fall back to plaintext capture.
    /// </summary>
    public async Task<bool> SupportsStructuredStreamAsync(ISandbox sandbox, CancellationToken ct = default)
    {
        try
        {
            var version = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = [Binary, "--version"],
            }, ct).ConfigureAwait(false);

            if (!version.Success)
                return false;

            var versionText = CombinedOutput(version).Trim();
            if (string.IsNullOrWhiteSpace(versionText))
                return false;

            var cacheKey = $"{Binary}\n{versionText}";
            if (StructuredStreamSupportByVersion.TryGetValue(cacheKey, out var cached))
                return cached;

            var help = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = [Binary, "--help"],
            }, ct).ConfigureAwait(false);

            if (!help.Success)
                return false;

            var helpOutput = CombinedOutput(help);
            if (!helpOutput.Contains("--output-format", StringComparison.Ordinal)
                || !helpOutput.Contains("stream-json", StringComparison.Ordinal))
            {
                StructuredStreamSupportByVersion.TryAdd(cacheKey, false);
                return false;
            }

            if (!await TryMaterialiseAuthForProbeAsync(sandbox, ct).ConfigureAwait(false))
                return false;

            var probe = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = BuildStructuredStreamProbeArgv(),
                WorkingDirectory = "/tmp",
                Stdin = "Reply with exactly CODEYBOX_STRUCTURED_STREAM_PROBE. Do not inspect or modify files.",
            }, ct).ConfigureAwait(false);

            if (!probe.Success)
                return false;

            var supported = IsStructuredNdjson(probe.Stdout);
            StructuredStreamSupportByVersion.TryAdd(cacheKey, supported);
            return supported;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    protected override IReadOnlyList<string> ScratchpadHomeDirectories =>
        // The agy binary stashes session state under ~/.gemini/antigravity-cli
        // (conversations index + per-conversation "brain" transcripts).
        // Capturing both lets a preempt/resume cycle pick the conversation back
        // up via --conversation <id>.
        [".gemini/antigravity-cli/conversations", ".gemini/antigravity-cli/brain"];

    protected override IReadOnlyList<string> FileBackedCredentialEnvironmentVariables =>
        [AntigravityConstants.OAuthCredsEnvVar];

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

        var write = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["bash", "-c", AuthMaterialisationScript],
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

        var result = await RunWithLogCaptureAsync(
            sandbox,
            effectiveCaptureStructuredStream,
            stdoutChunkCallback,
            ct,
            () => base.RunAsync(
                sandbox,
                workingDirectory,
                prompt,
                credential,
                modelId,
                reasoningMode,
                ct,
                stdoutChunkCallback,
                effectiveCaptureStructuredStream)).ConfigureAwait(false);

        if (!captureStructuredStream || structuredStreamSupported)
            return result;

        var warning = $"Warning: Antigravity CLI at '{Binary}' does not support --output-format stream-json in --print mode; structured stream capture was disabled.";
        var stderr = string.IsNullOrEmpty(result.Stderr) ? warning : $"{warning}\n{result.Stderr}";
        return result with { Stderr = stderr };
    }

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
    /// then archive the glog to the observability stream and (on failure) fold its
    /// terminal error region into the classifier-facing <c>Stderr</c>. The
    /// setup/teardown lives here once so the log-path convention can't drift
    /// between the two paths.
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
            await ExcludeGlogFromWorkTreeGitAsync(sandbox, ct).ConfigureAwait(false);
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
    private async Task EnsureLogDirectoryAsync(ISandbox sandbox, string logFile, CancellationToken ct)
    {
        var dir = PosixDirName(logFile);
        if (dir.Length == 0)
            return;
        var mkdir = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["mkdir", "-p", dir],
        }, ct).ConfigureAwait(false);

        // A failed mkdir means agy's `--log-file` open will fail and the whole
        // capture silently yields nothing. Surface it via the same audit event the
        // tail-failure path uses so the broken diagnostics path is observable
        // rather than degrading invisibly to zero-capture. Non-fatal: the run still
        // proceeds (agy may still write to a default location or fail loudly on its
        // own), we just record that our capture directory could not be created.
        if (!mkdir.Success)
        {
            AuditLog.AgentLogCaptureFailed(
                Kind,
                "mkdir",
                $"could not create glog directory '{dir}' (exit {mkdir.ExitCode}): {mkdir.Stderr}");
        }
    }

    private static string PosixDirName(string path)
    {
        var idx = path.LastIndexOf('/');
        return idx <= 0 ? string.Empty : path[..idx];
    }

    /// <summary>
    /// Adds the agent-log scratch dir to the work tree's
    /// <c>.git/info/exclude</c> BEFORE agy runs, so that an agy self-commit's
    /// <c>git add -A</c> can never stage the glog.
    ///
    /// Unlike the other agents, agy writes its diagnostics to a real file
    /// (<c>--log-file</c>, see <see cref="ComputeAgyLogPath"/>) that lives under
    /// <see cref="SandboxConventions.AgentLogDir"/> — inside the <c>/work</c> git
    /// tree — and it is UNREDACTED on disk (agy logs applyAuthResult / auth
    /// diagnostics and, per the credential contract, ships the OAuth
    /// refresh_token verbatim). The orchestrator's post-run
    /// <c>StripAgentLogScratchFromIndexAsync</c> unstages that dir before its OWN
    /// commit, but cannot rewrite a commit an agent already made itself — and the
    /// rework prompt explicitly asks agents to make new commits. A local
    /// <c>.git/info/exclude</c> entry closes that gap: it is never committed, and
    /// it makes <c>git add -A</c> skip the (never-tracked) scratch dir regardless
    /// of who runs it.
    ///
    /// Best-effort and idempotent. A non-git working directory (unit tests, a
    /// non-pipeline caller) is a no-op; a failed write leaves the orchestrator's
    /// post-run strip as the remaining guard, so we do not fail the run over it.
    /// </summary>
    private async Task ExcludeGlogFromWorkTreeGitAsync(ISandbox sandbox, CancellationToken ct)
    {
        // Anchor on AgentLogDir's leaf under the reserved .codeybox/ namespace so
        // both ComputeAgyLogPath branches (the "<assigned>.agy.log" production
        // shape and the Guid fallback) sit under the excluded dir. Relative to the
        // work tree root; matches the pattern the orchestrator strip targets.
        const string excludeEntry = ".codeybox/agent-logs/";
        var script =
            "[ -d .git ] || exit 0; mkdir -p .git/info; "
            + $"grep -qxF '{excludeEntry}' .git/info/exclude 2>/dev/null || "
            + $"printf '%s\\n' '{excludeEntry}' >> .git/info/exclude";
        try
        {
            await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["sh", "-c", script],
                WorkingDirectory = SandboxConventions.WorkDir,
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Defence-in-depth: the post-run --cached strip still protects the
            // orchestrator's own commit. Surface the miss so a persistently broken
            // exclude path is visible rather than silently narrowing the guard.
            AuditLog.AgentLogCaptureFailed(Kind, ex.GetType().Name, $"git exclude write failed: {ex.Message}");
        }
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

        // (1) Archive the FULL glog to the per-run stream (observability / audit) on
        // EVERY outcome — this is what surfaces agy's otherwise invisible
        // diagnostics (model resolution, applyAuthResult, tool output) in the
        // agent-stream files, which the pipeline records and audits.
        if (stdoutChunkCallback is not null)
        {
            ForwardLogToStream(redactedLog, stdoutChunkCallback, captureStructuredStream);
        }

        // (2) Lift agy's TERMINAL error region out of the glog. We extract ONLY the
        // terminal region (the slice from the last quota/auth marker in the tail
        // window to end — agy aborts right after its terminal error, so an earlier
        // transient it recovered past sits outside the window and is excluded),
        // never the whole cumulative log, so a recovered-then-cleared 429/401 can't
        // falsely bench/park the member.
        var terminalError = AntigravityQuotaFailureDetector.ExtractTerminalErrorRegion(redactedLog);
        if (!string.IsNullOrEmpty(terminalError))
        {
            // (2a) On FAILURE (non-zero exit), fold the terminal region into the
            // classifier-facing result.Stderr so the existing !Success quota/auth
            // routing sees agy's terminal RESOURCE_EXHAUSTED / API Error: 401 (agy
            // writes these ONLY to its glog; its process stderr is frequently
            // ~0 bytes).
            if (!result.Success)
            {
                result = result with { Stderr = AppendDiagnostic(result.Stderr, terminalError) };
            }

            // (2b) ALWAYS surface the terminal region on TerminalDiagnostic — the
            // critical case is the EXIT-0 give-up: agy exits 0 and makes no changes
            // when a consumer-tier quota block stops it, so the failure branch never
            // fires and the pipeline would terminal-fail the run as "produced no
            // changes" and eventually dead-letter it. TerminalDiagnostic is a
            // side-channel distinct from Stderr (so the success-path auth classifier,
            // which reads Stderr, is NOT re-triggered by a recovered transient), and
            // the pipeline's no-changes branch classifies it to park a real 429 in
            // WaitingForQuotaReset with the gateway's reset hint.
            result = result with { TerminalDiagnostic = terminalError };
        }

        return result;
    }

    /// <summary>
    /// Appends <paramref name="addition"/> to an existing (possibly empty) stderr
    /// buffer on its own line, preserving agy's original process stderr ahead of
    /// the folded glog region.
    /// </summary>
    private static string AppendDiagnostic(string? existing, string addition)
        => string.IsNullOrEmpty(existing) ? addition : existing + "\n" + addition;

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
                // Reuse the base class's serializer so the folded envelope stays
                // byte-identical to StderrEnvelopeForwarder's and to what
                // AgentStreamParser expects.
                stdoutChunkCallback(SerializeStderrEnvelopeLine(line));
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

        // captureStructuredStream is set only after SupportsStructuredStreamAsync
        // has verified that print-mode accepts this flag and emits parseable
        // NDJSON. When false, agy emits its human-readable footer and the
        // plaintext-fallback summariser takes over.
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

    private IReadOnlyList<string> BuildStructuredStreamProbeArgv()
    {
        var argv = new List<string> { Binary, "--print", "--dangerously-skip-permissions" };
        if (PrintTimeout > TimeSpan.Zero)
        {
            var probeTimeoutSeconds = Math.Max(1, Math.Min((long)PrintTimeout.TotalSeconds, 60));
            argv.Add("--print-timeout");
            argv.Add($"{probeTimeoutSeconds}s");
        }

        argv.Add("--output-format");
        argv.Add("stream-json");
        return argv;
    }

    private async Task<bool> TryMaterialiseAuthForProbeAsync(ISandbox sandbox, CancellationToken ct)
    {
        var write = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["bash", "-c", AuthMaterialisationScript],
        }, ct).ConfigureAwait(false);
        return write.Success;
    }

    private const string AuthMaterialisationScript =
        "set -eu\n" +
        "umask 077\n" +
        "mkdir -p \"$HOME/.gemini/antigravity-cli\"\n" +
        "if [ -n \"${CODEYBOX_ANTIGRAVITY_OAUTH_CREDS_JSON:-}\" ]; then\n" +
        "  printf '%s' \"$CODEYBOX_ANTIGRAVITY_OAUTH_CREDS_JSON\" > \"$HOME/.gemini/antigravity-cli/antigravity-oauth-token\"\n" +
        "  chmod 600 \"$HOME/.gemini/antigravity-cli/antigravity-oauth-token\"\n" +
        "fi\n";

    private static string CombinedOutput(SandboxExecResult result) =>
        string.Concat(result.Stdout, "\n", result.Stderr);

    private static bool IsStructuredNdjson(string stdout)
    {
        var sawStructuredEvent = false;
        using var reader = new StringReader(stdout);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    return false;

                sawStructuredEvent |= LooksLikeAgyStructuredEvent(root);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        return sawStructuredEvent;
    }

    private static bool LooksLikeAgyStructuredEvent(JsonElement root)
    {
        if (root.TryGetProperty("type", out var type)
            && type.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(type.GetString()))
            return true;

        return root.TryGetProperty("usageMetadata", out _)
            || root.TryGetProperty("usage_metadata", out _)
            || root.TryGetProperty("candidates", out _)
            || root.TryGetProperty("functionCall", out _)
            || root.TryGetProperty("function_call", out _);
    }

    internal static void ClearStructuredStreamSupportCacheForTests() =>
        StructuredStreamSupportByVersion.Clear();

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
