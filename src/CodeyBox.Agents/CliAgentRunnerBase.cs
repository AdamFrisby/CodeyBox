using System.Text;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Agents;

/// <summary>
/// Shared scaffolding for agent runners that drive a one-shot CLI binary
/// inside the sandbox. Subclasses describe how to invoke their CLI; this base
/// handles credential staging and result wrapping uniformly.
/// </summary>
public abstract class CliAgentRunnerBase : IPreemptibleAgentRunner, IResumableAgentRunner, IAgentCredentialEnvironmentPolicy
{
    private const string AgentRunIdEnvironmentVariable = "CODEYBOX_AGENT_RUN_ID";
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> ActiveAgentRunIds = new();

    public abstract AgentKind Kind { get; }

    /// <summary>
    /// Wire <c>type</c> of the JSON envelope used to fold agent stderr
    /// diagnostics into a structured (NDJSON) capture stream without
    /// interleaving non-JSON noise. Exposed so runners in sibling assemblies
    /// that hand-build the same envelope (e.g. antigravity folding its glog
    /// into the stream) reuse this literal instead of re-hardcoding it and
    /// silently desynchronising from <see cref="AgentStreamParser"/>'s parser.
    /// </summary>
    public const string StderrEnvelopeType = "codeybox.stderr";

    /// <summary>
    /// Serialises a single stderr-diagnostic line as the codeybox-internal NDJSON
    /// envelope (<c>{"type":"codeybox.stderr","text":...}</c>) that
    /// <see cref="AgentStreamParser"/> recognises, terminated with the newline that
    /// frames one envelope per line. Exposed so sibling-assembly runners that fold a
    /// CLI log into a structured stream (e.g. antigravity's glog) emit a
    /// byte-identical envelope via the SAME serializer <see cref="StderrEnvelopeForwarder"/>
    /// uses, instead of re-implementing the shape and drifting from the parser.
    /// </summary>
    public static string SerializeStderrEnvelopeLine(string text)
        => JsonSerializer.Serialize(new { type = StderrEnvelopeType, text }) + "\n";

    /// <summary>
    /// Sandbox CLI invocation built by concrete agent runners. This stays
    /// protected so argv/environment/stdin details do not leak into Core's
    /// domain/plugin-facing API.
    /// </summary>
    protected sealed record AgentInvocation(
        IReadOnlyList<string> Argv,
        IReadOnlyDictionary<string, string>? ExtraEnvironment = null,
        string? Stdin = null);

    /// <summary>
    /// Build the argv to execute inside the sandbox for a given prompt. The
    /// prompt may be passed via argv, stdin, or a file; subclasses choose.
    /// </summary>
    protected abstract AgentInvocation BuildInvocation(
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        bool captureStructuredStream = false);

    /// <summary>
    /// CLI state paths under HOME whose contents are useful for graceful
    /// preemption. The default preempt hook captures only these allowlisted
    /// relative paths, with size/type/path validation, into the checkpointed
    /// scratchpad archive.
    /// </summary>
    protected virtual IReadOnlyList<string> ScratchpadHomeDirectories => [];

    /// <summary>
    /// A credential payload carried in <see cref="AgentCredential.EnvironmentVariables"/>
    /// that must be materialised to a regular file under <c>$HOME</c> before
    /// invoking the CLI.
    /// </summary>
    protected sealed record EnvBackedCredentialFile(
        string EnvironmentVariable,
        string HomeRelativePath,
        string FailureDescription,
        string? DestinationEnvironmentVariable = null,
        bool MaterialiseFromSandboxEnvironmentWhenCredentialMissing = false);

    /// <summary>
    /// Env-var-backed credential files this runner writes before executing the
    /// agent CLI. The shared writer requires Python 3 in the sandbox image.
    /// Values supplied by a concrete <see cref="AgentCredential"/> are passed
    /// via stdin, not per-exec environment, so fallback candidates can
    /// authenticate without exposing file payloads to argv or the CLI process.
    /// </summary>
    protected virtual IReadOnlyList<EnvBackedCredentialFile> EnvBackedCredentialFiles => [];

    /// <summary>
    /// Credential environment variables the CLI reads directly from its
    /// process environment. Resolver candidate scoping uses this exact list as
    /// the allowlist at the process-environment sink.
    /// </summary>
    protected virtual IReadOnlyList<string> DirectCredentialEnvironmentVariables => [];

    /// <summary>
    /// Credential payload and destination-metadata variables consumed by the
    /// staging lifecycle rather than exposed to the agent CLI process.
    /// </summary>
    protected virtual IReadOnlyList<string> FileBackedCredentialEnvironmentVariables =>
        EnvBackedCredentialFiles
            .SelectMany(static file => file.DestinationEnvironmentVariable is null
                ? [file.EnvironmentVariable]
                : new[] { file.EnvironmentVariable, file.DestinationEnvironmentVariable })
            .ToArray();

    IReadOnlySet<string> IAgentCredentialEnvironmentPolicy.DirectCredentialEnvironmentVariables =>
        DirectCredentialEnvironmentVariables.ToHashSet(StringComparer.Ordinal);

    IReadOnlySet<string> IAgentCredentialEnvironmentPolicy.FileBackedCredentialEnvironmentVariables =>
        FileBackedCredentialEnvironmentVariables.ToHashSet(StringComparer.Ordinal);

    IReadOnlyList<AgentCredentialFileDestination> IAgentCredentialEnvironmentPolicy.CredentialFileDestinations =>
        EnvBackedCredentialFiles
            .Select(static file => new AgentCredentialFileDestination(
                file.EnvironmentVariable,
                file.HomeRelativePath,
                file.DestinationEnvironmentVariable))
            .ToArray();

    /// <summary>
    /// Pattern used to ask the running CLI to stop before scratchpad capture.
    /// </summary>
    protected virtual string PreemptProcessPattern => Kind.Value;

    /// <summary>
    /// Build the invocation used after a checkpoint restore. The default restores
    /// CLI state and reruns the normal one-shot command; CLIs with a native resume
    /// mode can override this to add the relevant flag.
    /// </summary>
    protected virtual AgentInvocation BuildResumeInvocation(
        string prompt,
        AgentCredential? credential,
        AgentResumeContext resume,
        string? modelId = null,
        string? reasoningMode = null,
        bool captureStructuredStream = false)
        => BuildInvocation(prompt, credential, modelId, reasoningMode, captureStructuredStream);

    /// <summary>
    /// Build the invocation used to continue a crashed native CLI session in
    /// the same sandbox. Only subclasses that implement
    /// <see cref="ICliSessionResumableAgentRunner"/> should override this hook.
    /// </summary>
    protected virtual AgentInvocation BuildSessionResumeInvocation(
        string sessionId,
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        bool captureStructuredStream = false)
        => throw new NotSupportedException(
            $"{Kind} declares CLI session resume but does not implement {nameof(BuildSessionResumeInvocation)}.");

    /// <summary>
    /// Gives subclasses a chance to prepare agent-specific, non-credential
    /// prerequisites immediately before credential staging and CLI invocation.
    /// Returning a result short-circuits the run with that failure. Credential
    /// staging itself is a non-overridable lifecycle step so subclasses cannot
    /// accidentally bypass fresh-run or resume preservation semantics.
    /// </summary>
    protected virtual Task<AgentResult?> PrepareAgentSandboxAsync(
        ISandbox sandbox,
        string workingDirectory,
        AgentCredential? credential,
        AgentResumeContext? resume,
        CancellationToken ct)
        => Task.FromResult<AgentResult?>(null);

    protected async Task<AgentResult?> PrepareSandboxForRunAsync(
        ISandbox sandbox,
        string workingDirectory,
        AgentCredential? credential,
        AgentResumeContext? resume,
        CancellationToken ct)
    {
        var agentPreparation = await PrepareAgentSandboxAsync(
            sandbox, workingDirectory, credential, resume, ct).ConfigureAwait(false);
        if (agentPreparation is not null)
            return agentPreparation;

        return await MaterialiseEnvBackedCredentialFilesAsync(
            sandbox, credential, preserveExistingCredentialFile: resume is not null, ct).ConfigureAwait(false);
    }

    private async Task<AgentResult?> MaterialiseEnvBackedCredentialFilesAsync(
        ISandbox sandbox,
        AgentCredential? credential,
        bool preserveExistingCredentialFile,
        CancellationToken ct)
    {
        if (EnvBackedCredentialFiles.Count == 0)
            return null;

        var overwritePolicy = preserveExistingCredentialFile
            ? SandboxCredentialOverwritePolicy.PreserveNonEmpty
            : SandboxCredentialOverwritePolicy.Overwrite;

        foreach (var file in EnvBackedCredentialFiles)
        {
            ValidateEnvBackedCredentialFile(file);

            if (credential?.EnvironmentVariables.TryGetValue(file.EnvironmentVariable, out var contents) == true
                && !string.IsNullOrEmpty(contents))
            {
                try
                {
                    await SandboxCredentialFileWriter.WriteAsync(
                        sandbox,
                        new SandboxCredentialFileTarget(
                            SandboxCredentialFileRoot.Home,
                            file.HomeRelativePath,
                            ResolveCredentialDestinationOverride(file, credential.EnvironmentVariables)),
                        contents,
                        overwritePolicy,
                        ct).ConfigureAwait(false);
                }
                catch (SandboxCredentialFileWriteException ex)
                {
                    return new AgentResult(
                        Success: false,
                        Summary: $"failed to materialise {file.FailureDescription}: exit {ex.ExitCode}",
                        Stdout: ex.Stdout,
                        Stderr: ex.Stderr);
                }
            }
            else if (credential is null && file.MaterialiseFromSandboxEnvironmentWhenCredentialMissing)
            {
                var write = await sandbox.ExecAsync(new SandboxExec
                {
                    Argv = ["bash", "-c", BuildEnvBackedCredentialScript(file)],
                }, ct).ConfigureAwait(false);
                if (!write.Success)
                {
                    return new AgentResult(
                        Success: false,
                        Summary: $"failed to materialise {file.FailureDescription}: exit {write.ExitCode}",
                        Stdout: write.Stdout,
                        Stderr: write.Stderr);
                }
            }
        }

        return null;
    }

    protected static string BuildEnvBackedCredentialScript(EnvBackedCredentialFile file)
    {
        ValidateEnvBackedCredentialFile(file);
        return SandboxCredentialFileWriter.BuildEnvironmentMaterialisationScript(
            file.EnvironmentVariable,
            file.HomeRelativePath,
            file.DestinationEnvironmentVariable,
            SandboxCredentialOverwritePolicy.PreserveNonEmpty);
    }

    private static string ResolveCredentialDestinationOverride(
        EnvBackedCredentialFile file,
        IReadOnlyDictionary<string, string> environment)
    {
        if (file.DestinationEnvironmentVariable is null)
            return string.Empty;

        return environment.TryGetValue(file.DestinationEnvironmentVariable, out var destination)
            ? destination
            : string.Empty;
    }

    private static void ValidateEnvBackedCredentialFile(EnvBackedCredentialFile file)
    {
        ValidateEnvironmentVariableName(file.EnvironmentVariable, nameof(file.EnvironmentVariable));
        if (file.DestinationEnvironmentVariable is not null)
            ValidateEnvironmentVariableName(file.DestinationEnvironmentVariable, nameof(file.DestinationEnvironmentVariable));
        ValidateHomeRelativeCredentialPath(file.HomeRelativePath);
        if (string.IsNullOrWhiteSpace(file.FailureDescription))
            throw new ArgumentException("Credential file failure description must be non-empty.", nameof(file));
    }

    private static void ValidateEnvironmentVariableName(string value, string fieldName)
        => SandboxCredentialFileWriter.ValidateEnvironmentVariableName(value, fieldName);

    private static void ValidateHomeRelativeCredentialPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Credential home-relative path must be non-empty.", nameof(value));
        if (value.StartsWith('/'))
            throw new ArgumentException($"Credential path must be relative to HOME: {value}", nameof(value));

        foreach (var segment in value.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or "..")
                throw new ArgumentException($"Credential path must not contain traversal segments: {value}", nameof(value));
        }
    }

    public virtual async Task<AgentResult> RunAsync(
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
        // Direct CLI env credentials are provisioned by the sandbox owner in
        // SandboxSpec, including resolver sandboxes whose candidates are known
        // before creation. Env-backed credential files are materialised below
        // via stdin. This runner deliberately does NOT merge
        // credential.EnvironmentVariables into per-exec ExtraEnvironment.
        if (RejectUnsupportedFileBackedCredentials(sandbox, credential) is { } unsupported)
            return unsupported;

        var preparation = await PrepareSandboxForRunAsync(sandbox, workingDirectory, credential, resume: null, ct);
        if (preparation is not null)
            return preparation;

        var invocation = BuildInvocation(prompt, credential, modelId, reasoningMode, captureStructuredStream);
        return await ExecuteWithSuspendResilienceAsync(
            sandbox,
            workingDirectory,
            invocation,
            stdoutChunkCallback,
            captureStructuredStream,
            ct,
            sessionResumeContext: CreateSessionResumeContext(
                prompt,
                credential,
                modelId,
                reasoningMode,
                captureStructuredStream));
    }

    public virtual async Task<AgentResult> RunResumedAsync(
        ISandbox sandbox,
        string workingDirectory,
        string prompt,
        AgentCredential? credential,
        AgentResumeContext resume,
        string? modelId = null,
        string? reasoningMode = null,
        CancellationToken ct = default,
        Action<string>? stdoutChunkCallback = null)
        => await RunResumedCoreAsync(
            sandbox,
            workingDirectory,
            prompt,
            credential,
            resume,
            modelId,
            reasoningMode,
            ct,
            stdoutChunkCallback,
            captureStructuredStream: this is ICliSessionResumableAgentRunner
            {
                RequiresStructuredStreamForSessionId: true,
            }).ConfigureAwait(false);

    protected async Task<AgentResult> RunResumedCoreAsync(
        ISandbox sandbox,
        string workingDirectory,
        string prompt,
        AgentCredential? credential,
        AgentResumeContext resume,
        string? modelId,
        string? reasoningMode,
        CancellationToken ct,
        Action<string>? stdoutChunkCallback,
        bool captureStructuredStream)
    {
        if (RejectUnsupportedFileBackedCredentials(sandbox, credential) is { } unsupported)
            return unsupported;

        await RestoreScratchpadAsync(sandbox, workingDirectory, resume, ct);

        var preparation = await PrepareSandboxForRunAsync(sandbox, workingDirectory, credential, resume, ct);
        if (preparation is not null)
            return preparation;

        var invocation = BuildResumeInvocation(
            prompt,
            credential,
            resume,
            modelId,
            reasoningMode,
            captureStructuredStream);
        return await ExecuteWithSuspendResilienceAsync(
            sandbox,
            workingDirectory,
            invocation,
            stdoutChunkCallback,
            captureStructuredStream,
            ct,
            sessionResumeContext: CreateSessionResumeContext(
                prompt,
                credential,
                modelId,
                reasoningMode,
                captureStructuredStream));
    }

    private async Task<AgentResult> ExecuteWithSuspendResilienceAsync(
        ISandbox sandbox,
        string workingDirectory,
        AgentInvocation invocation,
        Action<string>? stdoutChunkCallback,
        bool captureStructuredStream,
        CancellationToken ct,
        SessionResumeRebuildContext? sessionResumeContext = null)
    {
        var attempt = 0;
        var resumeAttempts = 0;
        var current = invocation;
        AgentResult? last = null;
        // Tracked across the retry loop so a session id captured on attempt N
        // can drive the rebuild on attempt N+1 even if attempt N+1 itself
        // crashes before re-emitting the init event.
        string? capturedSessionId = null;
        while (true)
        {
            last = await ExecuteInvocationOnceAsync(
                sandbox,
                workingDirectory,
                current,
                stdoutChunkCallback,
                captureStructuredStream,
                ct);
            if (last.Success)
                return last;

            // Session id extraction requires the CLI's structured (id-bearing)
            // output mode: plain stdout on the model-controlled call paths
            // could be spoofed with a fake init line, redirecting --resume
            // to an attacker-chosen session. The orchestrator now enables
            // CaptureStructuredStream for resumable runners independently of
            // optional persistent stream logging (see ICliSessionResumableAgentRunner),
            // so a transient crash is recoverable on the production work/
            // audit/merge paths regardless of AgentStreams. Plain-stdout call
            // sites (verdict-parser shortcuts) intentionally forgo resume to
            // keep their stdout contract intact.
            if (sessionResumeContext is not null
                && (!sessionResumeContext.Capability.RequiresStructuredStreamForSessionId
                    || sessionResumeContext.CaptureStructuredStream)
                && sessionResumeContext.Capability.TryExtractSessionId(last.Stdout) is { Length: > 0 } freshId)
            {
                capturedSessionId = freshId;
            }

            var classification = ((IAgentRunner)this).ClassifyFailure(last);
            var exitCode = ParseExitCodeFromSummary(last.Summary);

            // Classify FIRST: a captured session id is not by itself a license
            // to relaunch. Deterministic auth failures and terminal API crashes
            // are excluded here / by the quota gate, while otherwise-unmatched
            // non-zero exits are treated as resumable CLI crashes within the
            // bounded budget.
            if (sessionResumeContext is not null
                && capturedSessionId is not null
                && IsResumeEligibleFailure(classification, exitCode)
                && SessionResumeQuotaGate.AllowsResume(
                    sessionResumeContext.Capability.SessionResumeQuotaClassifier,
                    Kind,
                    last.Stderr,
                    last.Stdout))
            {
                var maxResumeAttempts = SessionResumeOptions.MaxResumeAttempts;
                if (resumeAttempts < maxResumeAttempts)
                {
                    var livenessProbe = await TryProbeResumeLivenessAsync(
                        sandbox, workingDirectory, ct).ConfigureAwait(false);
                    if (!livenessProbe.IsAlive)
                        return WithLivenessProbeNote(last, livenessProbe);

                    resumeAttempts++;
                    current = BuildSessionResumeInvocation(
                        capturedSessionId,
                        sessionResumeContext.Prompt,
                        sessionResumeContext.Credential,
                        sessionResumeContext.ModelId,
                        sessionResumeContext.ReasoningMode,
                        sessionResumeContext.CaptureStructuredStream);
                    continue;
                }

                if (maxResumeAttempts > 0)
                    throw new AgentSessionResumeExhaustedException(Kind, maxResumeAttempts, last);
            }

            if (attempt >= AgentSuspendResilience.MaxRetries)
                return last;
            if (!AgentSuspendResilience.ShouldRetry(Kind, classification, exitCode))
                return last;

            attempt++;
            // Fall through to the pre-resume single-shot retry shape only when
            // native session resume was unavailable or disabled (no captured id,
            // non-eligible failure, or MaxResumeAttempts=0). A positive resume
            // budget that has been exhausted returns above so the same agent is
            // not restarted from scratch in the same sandbox.
            current = invocation;
        }
    }

    /// <summary>
    /// Resume in-place is the recovery for the transient agent-process crash
    /// shapes the task spec calls out: OOM/SIGKILL/SIGPIPE/network blip and
    /// CLI bugs that exit non-zero with no recognised failure pattern.
    ///
    /// <para>
    /// Explicitly EXCLUDED:
    /// <list type="bullet">
    /// <item><see cref="AgentFailureKind.AuthError"/> — revoked/expired credentials
    /// would re-fail the same way on resume; the orchestrator's auth recovery
    /// path is the right escalation, not a same-session relaunch.</item>
    /// <item>Hard quota exhaustion (account caps, RESOURCE_EXHAUSTED), and
    /// rate limits with parsed reset windows — handled upstream by
    /// <see cref="SessionResumeQuotaGate"/> which blocks these even when this
    /// method allows the QuotaExhausted classification through.</item>
    /// <item>Terminal non-quota API crashes (e.g. Claude 400 thinking-block) —
    /// also blocked by the gate via the provider detector.</item>
    /// </list>
    /// </para>
    /// <para>
    /// Normal classification + non-zero exit code is included because
    /// the shared <see cref="AgentFailureClassifier"/> defaults to
    /// <see cref="AgentFailureKind.Normal"/> for any non-matched pattern,
    /// which conflates "agent reported a work refusal" with "process was
    /// killed and emitted no recognised diagnostic." The task requires native
    /// resume for generic CLI crashes, OOM/SIGKILL (usually exit 137), and
    /// non-zero CLI bugs when a session id was captured; the bounded resume
    /// budget keeps the cost of a misclassified refusal to a couple of retries
    /// before the orchestrator fails over.
    /// </para>
    /// </summary>
    private static bool IsResumeEligibleFailure(AgentFailureClassification classification, int exitCode)
        => classification.Kind switch
        {
            AgentFailureKind.TransientNetwork => true,
            AgentFailureKind.Unknown => AgentSuspendResilience.IsSuspendRelatedExitCode(exitCode),
            AgentFailureKind.Normal => exitCode != 0,
            // Soft rate-limit/overload comes through as QuotaExhausted; the
            // session-resume quota gate is the authoritative decision for
            // that shape (it inspects the provider detector). Returning true
            // here defers to that gate.
            AgentFailureKind.QuotaExhausted => true,
            AgentFailureKind.AuthError => false,
            _ => false,
        };

    private async Task<ResumeLivenessProbeResult> TryProbeResumeLivenessAsync(
        ISandbox sandbox,
        string workingDirectory,
        CancellationToken ct)
    {
        SandboxExecResult result;
        try
        {
            result = await sandbox.ExecAsync(new SandboxExec
            {
                Argv =
                [
                    "sh",
                    "-c",
                    """
                    target=$1
                    repo=$2
                    [ -n "$target" ] || exit 1
                    [ -n "$repo" ] || exit 1
                    [ -d "$target" ] || exit 1
                    [ -x "$target" ] || exit 1
                    [ -w "$target" ] || exit 1
                    [ -e "$target/.git" ] || exit 1
                    git -C "$target" rev-parse --git-dir >/dev/null 2>&1 || exit 1
                    git -C "$target" rev-parse --is-inside-work-tree >/dev/null 2>&1 || exit 1
                    [ -d "$repo" ] || exit 1
                    [ -x "$repo" ] || exit 1
                    [ -w "$repo" ] || exit 1
                    [ "$(git -C "$repo" rev-parse --is-bare-repository 2>/dev/null)" = "true" ] || exit 1
                    origin=$(git -C "$target" remote get-url origin 2>/dev/null) || exit 1
                    case "$origin" in
                        "$repo"|"$repo/"|file://"$repo"|file://"$repo/") ;;
                        *) exit 1 ;;
                    esac
                    git -C "$target" ls-remote --exit-code origin HEAD >/dev/null 2>&1 || exit 1
                    """,
                    "codeybox-resume-liveness",
                    workingDirectory,
                    SandboxConventions.RepoDir,
                ],
                WorkingDirectory = "/",
            }, ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Sandbox already torn down between the crashed run and the
            // liveness check — VM is gone, resume cannot proceed in this
            // sandbox, and re-drive in a fresh sandbox is the correct path.
            return new ResumeLivenessProbeResult(IsAlive: false, FailureKind: "sandbox-disposed", FailureDetail: null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Sandbox/provider bug surfaced during the liveness exec.
            // Surface the real failure rather than masking it as a generic
            // non-resumable exit so an infrastructure problem doesn't look
            // like an ordinary agent failure.
            AuditLog.SessionResumeLivenessProbeFailed(Kind, ex.GetType().Name, ex.Message);
            throw;
        }

        return result.Success
            ? new ResumeLivenessProbeResult(IsAlive: true, FailureKind: null, FailureDetail: null)
            : new ResumeLivenessProbeResult(
                IsAlive: false,
                FailureKind: "probe-exit-nonzero",
                FailureDetail: $"exit {result.ExitCode}: {Tail(result.Stderr)}");
    }

    private static AgentResult WithLivenessProbeNote(AgentResult original, ResumeLivenessProbeResult probe)
    {
        if (probe.FailureKind is null)
            return original;

        var note = probe.FailureDetail is null
            ? $"resume liveness probe rejected ({probe.FailureKind})"
            : $"resume liveness probe rejected ({probe.FailureKind}): {probe.FailureDetail}";
        var stderr = string.IsNullOrEmpty(original.Stderr) ? note : $"{note}\n{original.Stderr}";
        return original with { Stderr = stderr };
    }

    private static string Tail(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        const int max = 200;
        return text.Length <= max ? text : text[^max..];
    }

    private readonly record struct ResumeLivenessProbeResult(bool IsAlive, string? FailureKind, string? FailureDetail);

    private SessionResumeRebuildContext? CreateSessionResumeContext(
        string prompt,
        AgentCredential? credential,
        string? modelId,
        string? reasoningMode,
        bool captureStructuredStream)
        => this is ICliSessionResumableAgentRunner capability
            ? new SessionResumeRebuildContext(
                prompt,
                credential,
                modelId,
                reasoningMode,
                captureStructuredStream,
                capability)
            : null;

    /// <summary>
    /// Inputs the suspend-resilience loop needs to rebuild a failed invocation
    /// as a CLI-native session resume. The credential/model/reasoning are the
    /// same the caller supplied to <see cref="RunAsync"/> / <see cref="RunResumedAsync"/>;
    /// re-using them keeps the resumed call's auth / model identical to the
    /// crashed run.
    /// </summary>
    private sealed record SessionResumeRebuildContext(
        string Prompt,
        AgentCredential? Credential,
        string? ModelId,
        string? ReasoningMode,
        bool CaptureStructuredStream,
        ICliSessionResumableAgentRunner Capability);

    private async Task<AgentResult> ExecuteInvocationOnceAsync(
        ISandbox sandbox,
        string workingDirectory,
        AgentInvocation invocation,
        Action<string>? stdoutChunkCallback,
        bool captureStructuredStream,
        CancellationToken ct)
    {
        var runKey = AgentRunKey(sandbox, workingDirectory);
        var runId = Guid.NewGuid().ToString("N");
        ActiveAgentRunIds[runKey] = runId;
        // Plaintext-fallback runs tee stderr into the stdout chunk channel
        // directly: the captured .jsonl has no JSON framing to corrupt and
        // agy / opencode emit useful diagnostics there. Structured runs
        // (stream-json) cannot interleave raw stderr — chunks arrive split
        // at non-newline boundaries from arbitrary sandbox threads
        // (see SandboxExec docs) and would break per-line JSON framing.
        // Instead the runner wraps each complete stderr line in a single-
        // line JSON envelope and forwards it through the same callback, so
        // the .jsonl carries a recoverable record of stderr (auth/usage
        // diagnostics that fire before any structured event is emitted)
        // without any framing risk.
        StderrEnvelopeForwarder? envelopeForwarder = null;
        Action<string>? stderrChunkCallback;
        if (captureStructuredStream)
        {
            envelopeForwarder = stdoutChunkCallback is null
                ? null
                : new StderrEnvelopeForwarder(stdoutChunkCallback);
            stderrChunkCallback = envelopeForwarder is null ? null : envelopeForwarder.Append;
        }
        else
        {
            stderrChunkCallback = stdoutChunkCallback;
        }

        var exec = new SandboxExec
        {
            Argv = invocation.Argv,
            WorkingDirectory = workingDirectory,
            ExtraEnvironment = WithAgentRunId(invocation.ExtraEnvironment, runId),
            Stdin = invocation.Stdin,
            StdoutChunkCallback = stdoutChunkCallback,
            StderrChunkCallback = stderrChunkCallback,
            AgentOutputTransport = SelectBatchAgentOutputTransport(sandbox),
            LaunchMode = SelectBatchLaunchMode(sandbox),
        };

        SandboxExecResult result;
        try
        {
            result = await sandbox.ExecAsync(exec, ct);
        }
        finally
        {
            envelopeForwarder?.FlushTrailing();
            RemoveActiveAgentRunId(runKey, runId);
        }

        return new AgentResult(
            Success: result.Success,
            Summary: result.Success ? "ok" : $"agent exited {result.ExitCode}",
            Stdout: result.Stdout,
            Stderr: result.Stderr);
    }

    // Centralised batch-runner policy. Transport chooses the output data plane;
    // launch mode independently asks capable sandboxes to detach one-shot batch
    // runs so a long-lived attached exec client is not kept alive for stdout.
    protected internal static SandboxAgentOutputTransportPreference SelectBatchAgentOutputTransport(ISandbox sandbox)
        => sandbox.AgentOutputTransportKind == SandboxAgentOutputTransportKind.HttpIngest
            ? SandboxAgentOutputTransportPreference.PreferHttpIngest
            : SandboxAgentOutputTransportPreference.ExecPipe;

    protected internal static SandboxExecLaunchMode SelectBatchLaunchMode(ISandbox sandbox)
        => sandbox.BatchLaunchMode == SandboxBatchLaunchMode.Detached
            ? SandboxExecLaunchMode.DetachedBatch
            : SandboxExecLaunchMode.Attached;

    /// <summary>
    /// Buffers stderr chunks up to the next newline and forwards each complete
    /// line as a single-line JSON envelope through the supplied callback. Used
    /// by structured-stream runs so stderr diagnostics still land in the
    /// captured .jsonl without interleaving non-JSON noise into the file.
    /// </summary>
    internal sealed class StderrEnvelopeForwarder
    {
        public const string EnvelopeType = StderrEnvelopeType;

        // A misbehaving CLI / tool can emit a single very long stderr line
        // without a newline (terminal control sequences, JSON dumps, stack
        // traces concatenated by a broken logger). The forwarder buffers
        // stderr until newline so each emitted JSON envelope is one
        // recoverable line — but without a cap that buffer grows unbounded
        // in host process memory before the per-stream-file size cap can
        // engage. Cap the buffered line; once exceeded we emit the buffered
        // prefix with a truncation marker and discard the overflow until
        // the next newline.
        internal const int MaxBufferedLineChars = 64 * 1024;
        internal const string LineTruncationMarker = "[...stderr line truncated]";

        private readonly Action<string> _downstream;
        private readonly StringBuilder _buffer = new();
        private readonly object _gate = new();
        private bool _lineOverflowed;

        public StderrEnvelopeForwarder(Action<string> downstream)
        {
            _downstream = downstream;
        }

        public void Append(string chunk)
        {
            if (string.IsNullOrEmpty(chunk))
                return;

            lock (_gate)
            {
                foreach (var ch in chunk)
                {
                    if (ch == '\n')
                    {
                        FlushLocked();
                        continue;
                    }

                    if (ch == '\r')
                        continue;

                    if (_lineOverflowed)
                        continue;

                    if (_buffer.Length >= MaxBufferedLineChars)
                    {
                        _buffer.Append(LineTruncationMarker);
                        _lineOverflowed = true;
                        continue;
                    }

                    _buffer.Append(ch);
                }
            }
        }

        public void FlushTrailing()
        {
            lock (_gate)
            {
                if (_buffer.Length > 0)
                    FlushLocked();
            }
        }

        private void FlushLocked()
        {
            var text = _buffer.ToString();
            _buffer.Clear();
            _lineOverflowed = false;
            string envelope;
            try
            {
                envelope = SerializeStderrEnvelopeLine(text);
            }
            catch (NotSupportedException)
            {
                return;
            }

            try
            {
                _downstream(envelope);
            }
            catch
            {
                // Downstream sink failures are observability-only; never block the agent run.
            }
        }
    }

    /// <summary>
    /// Build argv for text-only sandbox calls. Must not include tool-auto-approve
    /// flags (<c>--trust</c>, <c>--force</c>, <c>--dangerously-skip-permissions</c>).
    /// Returns <c>null</c> when this runner has no sandbox text-only CLI path.
    /// </summary>
    protected virtual AgentInvocation? BuildTextOnlyInvocation(
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null)
        => null;

    /// <summary>
    /// Viability probe for subscription CLIs whose sandbox auth materialisation
    /// no-ops when the auth-json env var is absent (image-baked CLI auth).
    /// Returns null when text-only may proceed (including with no host credential).
    /// </summary>
    protected static string? GetSandboxSubscriptionTextOnlyUnavailabilityReason(
        AgentCredential? credential,
        string authJsonEnvVarName)
    {
        if (credential is null)
            return null;

        if (credential.EnvironmentVariables is not { Count: > 0 })
            return $"{authJsonEnvVarName} is required when a credential bundle is supplied";

        if (credential.EnvironmentVariables.TryGetValue(authJsonEnvVarName, out var json)
            && !string.IsNullOrWhiteSpace(json))
            return null;

        // Bundle present but auth JSON absent — credential staging no-ops; image auth may suffice.
        return null;
    }

    /// <summary>
    /// Runs a one-shot print-mode CLI invocation inside the sandbox for
    /// text-only resolver/review calls using <see cref="BuildTextOnlyInvocation"/>.
    /// </summary>
    protected Task<TextOnlyAgentResult> RunTextOnlyRequiresSandboxAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new TextOnlyAgentResult(
            false,
            $"{Kind.Value} text-only must run inside the work-item sandbox",
            null,
            null));
    }

    protected async Task<TextOnlyAgentResult> ExecuteTextOnlyInSandboxAsync(
        ISandbox sandbox,
        string workingDirectory,
        string prompt,
        AgentCredential? credential,
        string? modelId,
        string? reasoningMode,
        CancellationToken ct)
    {
        if (RejectUnsupportedFileBackedCredentials(sandbox, credential) is { } unsupported)
            return new TextOnlyAgentResult(false, unsupported.Summary, unsupported.Stdout, unsupported.Stderr);

        var preparation = await PrepareSandboxForRunAsync(sandbox, workingDirectory, credential, resume: null, ct);
        if (preparation is not null)
            return new TextOnlyAgentResult(false, preparation.Summary, preparation.Stdout, preparation.Stderr);

        var invocation = BuildTextOnlyInvocation(prompt, credential, modelId, reasoningMode);
        if (invocation is null)
        {
            return new TextOnlyAgentResult(
                false,
                $"{Kind.Value} text-only is not supported inside the sandbox",
                null,
                null);
        }

        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = invocation.Argv,
            WorkingDirectory = workingDirectory,
            Stdin = invocation.Stdin,
            // Text-only CLI calls are also one-shot stdin-driven agent CLI
            // runs (cursor/opencode text resolvers, review hooks) — give them
            // the same detached HTTP transport as the main batch path so the
            // host-side multipass exec returns immediately rather than
            // busy-looping its SSH/gRPC pump for the call's full lifetime.
            AgentOutputTransport = SelectBatchAgentOutputTransport(sandbox),
            LaunchMode = SelectBatchLaunchMode(sandbox),
        }, ct);

        if (!result.Success)
        {
            var detail = string.IsNullOrWhiteSpace(result.Stderr) ? result.Stdout : result.Stderr;
            return new TextOnlyAgentResult(
                false,
                $"{Kind.Value} text-only call failed: exit {result.ExitCode}",
                result.Stdout,
                detail.Trim());
        }

        var output = string.IsNullOrWhiteSpace(result.Stdout) ? result.Stderr : result.Stdout;
        return new TextOnlyAgentResult(true, "ok", output.Trim(), null);
    }

    private static int ParseExitCodeFromSummary(string summary)
    {
        const string prefix = "agent exited ";
        if (!summary.StartsWith(prefix, StringComparison.Ordinal))
            return -1;
        var tail = summary[prefix.Length..];
        return int.TryParse(tail, out var code) ? code : -1;
    }

    private AgentResult? RejectUnsupportedFileBackedCredentials(ISandbox sandbox, AgentCredential? credential)
    {
        // In production the sandbox is wrapped by admission-control / reusable decorators that cannot
        // conditionally re-implement the IRejectsFileBackedAgentCredentials marker, so probe the whole
        // decorator chain rather than only the outermost wrapper.
        if (ResolveFileBackedCredentialPolicy(sandbox) is not { } policy)
            return null;
        if (credential?.EnvironmentVariables is not { Count: > 0 } env)
            return null;
        if (FileBackedCredentialEnvironmentVariables.Count == 0)
            return null;

        foreach (var key in FileBackedCredentialEnvironmentVariables)
        {
            if (!env.TryGetValue(key, out var value) || string.IsNullOrEmpty(value))
                continue;

            var summary =
                $"{Kind.Value} file-backed credentials are not supported by sandbox {sandbox.Id}: " +
                policy.FileBackedAgentCredentialsUnsupportedReason;
            return new AgentResult(
                Success: false,
                Summary: summary,
                Stdout: null,
                Stderr: summary);
        }

        return null;
    }

    private static IRejectsFileBackedAgentCredentials? ResolveFileBackedCredentialPolicy(ISandbox sandbox)
    {
        for (ISandbox? current = sandbox; current is not null; current = (current as ISandboxDecorator)?.InnerSandbox)
        {
            if (current is IRejectsFileBackedAgentCredentials policy)
                return policy;
        }

        return null;
    }

    public virtual async Task RequestPreemptAsync(ISandbox sandbox, string workingDirectory, CancellationToken ct = default)
    {
        var dirs = string.Join(" ", ScratchpadHomeDirectories.Select(ShellQuote));
        var pattern = PreemptProcessPattern;
        ActiveAgentRunIds.TryGetValue(AgentRunKey(sandbox, workingDirectory), out var activeRunId);
        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv =
            [
                "bash", "-c",
                $$"""
                set -euo pipefail
                mkdir -p .codeybox
                active_run_id="$2"
                pids=""
                if [ -d /proc ]; then
                  for env_file in /proc/[0-9]*/environ; do
                    [ -r "$env_file" ] || continue
                    pid="${env_file#/proc/}"
                    pid="${pid%/environ}"
                    [ "$pid" = "$$" ] && continue
                    if [ -n "$active_run_id" ]; then
                      if tr '\0' '\n' < "$env_file" 2>/dev/null | grep -Fx -- "{{AgentRunIdEnvironmentVariable}}=$active_run_id" >/dev/null; then
                        pids="$pids $pid"
                      fi
                    elif [ -r "/proc/$pid/cmdline" ] \
                         && tr '\0' ' ' < "/proc/$pid/cmdline" 2>/dev/null | grep -F -- "$1" >/dev/null \
                         && tr '\0' '\n' < "$env_file" 2>/dev/null | grep -Fx -- "HOME=$HOME" >/dev/null; then
                      pids="$pids $pid"
                    fi
                  done
                fi
                for pid in $pids; do
                  kill -TERM "$pid" 2>/dev/null || true
                done
                if [ -n "$pids" ]; then
                  for _ in $(seq 1 20); do
                    still_running=0
                    for pid in $pids; do
                      if kill -0 "$pid" 2>/dev/null; then
                        still_running=1
                        break
                      fi
                    done
                    [ "$still_running" -eq 1 ] || break
                    sleep 0.1
                  done
                fi

                scratch_tmp="$(mktemp -d .codeybox/preempt-scratchpad.XXXXXX)"
                manifest="$scratch_tmp/manifest.txt"
                manifest_tsv="$scratch_tmp/manifest.tsv"
                printf '%s\n' "Preempt requested at $(date -u +%FT%TZ)." > "$manifest"
                : > "$manifest_tsv"
                max_file_bytes=2097152
                max_total_bytes=26214400
                max_entries=2000
                max_depth=16
                captured=0
                total_bytes=0
                entries=0

                valid_rel() {
                  local rel="$1"
                  [ -n "$rel" ] || return 1
                  [[ "$rel" != /* ]] || return 1
                  [[ "$rel" != *$'\t'* && "$rel" != *$'\n'* ]] || return 1
                  IFS=/ read -r -a parts <<< "$rel"
                  [ "${#parts[@]}" -le "$max_depth" ] || return 1
                  for part in "${parts[@]}"; do
                    [ -n "$part" ] || return 1
                    [ "$part" != "." ] || return 1
                    [ "$part" != ".." ] || return 1
                    [ "$part" != ".git" ] || return 1
                  done
                }

                record_entry() {
                  local kind="$1" scope="$2" rel="$3"
                  printf '%s\t%s\t%s\n' "$kind" "$scope" "$rel" >> "$manifest_tsv"
                }

                record_parent_dirs() {
                  local scope="$1" rel="$2"
                  local dir
                  dir="$(dirname "$rel")"
                  while [ "$dir" != "." ] && [ "$dir" != "/" ]; do
                    valid_rel "$dir" || return 0
                    mkdir -p "$scratch_tmp/$scope/$dir"
                    record_entry dir "$scope" "$dir"
                    dir="$(dirname "$dir")"
                  done
                }

                capture_path() {
                  local scope="$1" base="$2" rel="$3"
                  rel="${rel#/}"
                  valid_rel "$rel" || {
                    printf '%s\n' "skipped $scope/$rel: invalid scratchpad path" >> "$manifest"
                    return 0
                  }

                  local src="$base/$rel"
                  [ -e "$src" ] || return 0
                  if [ -L "$src" ]; then
                    printf '%s\n' "skipped $scope/$rel: scratchpad root is a symlink" >> "$manifest"
                    return 0
                  fi
                  if [ ! -d "$src" ] && [ ! -f "$src" ]; then
                    printf '%s\n' "skipped $scope/$rel: scratchpad root is not a regular file or directory" >> "$manifest"
                    return 0
                  fi

                  printf '%s\n' "capturing $scope/$rel" >> "$manifest"
                  while IFS= read -r -d '' path; do
                    local sub=""
                    if [ "$path" != "$src" ]; then
                      sub="${path#"$src"/}"
                    fi
                    local dest_rel="$rel"
                    [ -z "$sub" ] || dest_rel="$rel/$sub"
                    valid_rel "$dest_rel" || {
                      printf '%s\n' "skipped $scope/$dest_rel: invalid nested scratchpad path" >> "$manifest"
                      continue
                    }
                    entries=$((entries + 1))
                    if [ "$entries" -gt "$max_entries" ]; then
                      printf '%s\n' "stopped capturing $scope/$rel: entry limit exceeded" >> "$manifest"
                      break
                    fi

                    local dest="$scratch_tmp/$scope/$dest_rel"
                    if [ -d "$path" ]; then
                      mkdir -p "$dest"
                      record_entry dir "$scope" "$dest_rel"
                      captured=1
                      continue
                    fi

                    if [ ! -f "$path" ]; then
                      printf '%s\n' "skipped $scope/$dest_rel: unsupported file type" >> "$manifest"
                      continue
                    fi

                    local size
                    size="$(wc -c < "$path")"
                    if [ "$size" -gt "$max_file_bytes" ]; then
                      printf '%s\n' "skipped $scope/$dest_rel: file exceeds per-file limit" >> "$manifest"
                      continue
                    fi
                    if [ $((total_bytes + size)) -gt "$max_total_bytes" ]; then
                      printf '%s\n' "skipped $scope/$dest_rel: archive byte limit reached" >> "$manifest"
                      continue
                    fi
                    mkdir -p "$(dirname "$dest")"
                    record_parent_dirs "$scope" "$dest_rel"
                    cp -p "$path" "$dest"
                    total_bytes=$((total_bytes + size))
                    record_entry file "$scope" "$dest_rel"
                    captured=1
                  done < <(find -P "$src" -xdev \( -type f -o -type d \) -print0)
                }

                for rel in {{dirs}} ""; do
                  [ -n "$rel" ] || continue
                  capture_path home "$HOME" "$rel"
                  if [ -e "$PWD/$rel" ] \
                     && { [ ! -e "$HOME/$rel" ] || [ "$(readlink -f "$PWD/$rel")" != "$(readlink -f "$HOME/$rel")" ]; }; then
                    capture_path work "$PWD" "$rel"
                  fi
                done
                if [ "$captured" -eq 0 ]; then
                  printf '%s\n' "No known CLI scratchpad directory existed at preempt time." >> "$manifest"
                fi
                tar -czf .codeybox/preempt-scratchpad.tgz -C "$scratch_tmp" .
                cp "$manifest" .codeybox/preempt-scratchpad.md
                rm -rf "$scratch_tmp"
                """,
                "codeybox-preempt",
                pattern,
                activeRunId ?? string.Empty,
            ],
            WorkingDirectory = workingDirectory,
        }, ct);
        if (!result.Success)
            throw new InvalidOperationException($"agent preempt signal failed (exit {result.ExitCode}): {result.Stderr}");
    }

    private async Task RestoreScratchpadAsync(
        ISandbox sandbox,
        string workingDirectory,
        AgentResumeContext resume,
        CancellationToken ct)
    {
        var argv = new List<string>
        {
            "bash", "-c",
            """
            set -euo pipefail
            archive="$1"
            shift
            [ -f "$archive" ] || exit 0
            max_archive_bytes=33554432
            archive_bytes="$(wc -c < "$archive")"
            [ "$archive_bytes" -le "$max_archive_bytes" ] || {
              echo "scratchpad archive exceeds restore limit" >&2
              exit 10
            }

            scratch_tmp="$(mktemp -d .codeybox/resume-scratchpad.XXXXXX)"
            cleanup() { rm -rf "$scratch_tmp"; }
            trap cleanup EXIT

            manifest="$scratch_tmp/manifest.tsv"
            max_manifest_bytes=262144
            max_entries=2000
            extract_bounded() {
              local member="$1"
              local dest="$2"
              local limit="$3"
              local tmp="$dest.tmp"
              rm -f "$tmp"
              set +e
              tar -xOzf "$archive" "$member" 2>/dev/null | head -c "$limit" > "$tmp"
              local tar_status="${PIPESTATUS[0]}"
              set -e
              local bytes
              bytes="$(wc -c < "$tmp")"
              if [ "$bytes" -ge "$limit" ]; then
                rm -f "$tmp"
                return 20
              fi
              if [ "$tar_status" -ne 0 ]; then
                rm -f "$tmp"
                return 21
              fi
              mv "$tmp" "$dest"
              printf '%s\n' "$bytes"
              return 0
            }

            members="$scratch_tmp/members.txt"
            set +e
            tar -tzf "$archive" 2>/dev/null | head -n $((max_entries + 1)) > "$members"
            list_status="${PIPESTATUS[0]}"
            set -e
            member_count="$(wc -l < "$members")"
            if [ "$member_count" -gt "$max_entries" ]; then
              echo "scratchpad archive entry limit exceeded" >&2
              exit 12
            fi
            if [ "$list_status" -ne 0 ]; then
              echo "scratchpad archive cannot be listed" >&2
              exit 12
            fi
            if grep -Fx -- "./manifest.tsv" "$members" >/dev/null; then
              extract_bounded "./manifest.tsv" "$manifest" $((max_manifest_bytes + 1)) >/dev/null || {
                echo "scratchpad manifest exceeds restore limit or cannot be read" >&2
                exit 10
              }
            elif grep -Fx -- "manifest.tsv" "$members" >/dev/null; then
              extract_bounded "manifest.tsv" "$manifest" $((max_manifest_bytes + 1)) >/dev/null || {
                echo "scratchpad manifest exceeds restore limit or cannot be read" >&2
                exit 10
              }
            else
              # Legacy checkpoints did not carry restorable scratchpad state.
              exit 0
            fi

            max_file_bytes=2097152
            max_total_bytes=26214400
            max_depth=16
            valid_rel() {
              local rel="$1"
              [ -n "$rel" ] || return 1
              [[ "$rel" != /* ]] || return 1
              [[ "$rel" != *$'\t'* && "$rel" != *$'\n'* ]] || return 1
              IFS=/ read -r -a parts <<< "$rel"
              [ "${#parts[@]}" -le "$max_depth" ] || return 1
              for part in "${parts[@]}"; do
                [ -n "$part" ] || return 1
                [ "$part" != "." ] || return 1
                [ "$part" != ".." ] || return 1
                [ "$part" != ".git" ] || return 1
              done
            }

            allowed_roots=()
            for root in "$@"; do
              root="${root#/}"
              valid_rel "$root" || continue
              allowed_roots+=("$root")
            done

            ensure_destination() {
              local dest_base="$1" rel="$2" kind="$3"
              local dest_base_real dest parent rel_parent current
              dest_base_real="$(realpath -e "$dest_base")"
              dest="$dest_base/$rel"
              if [ "$kind" = "file" ]; then
                parent="$(dirname "$dest")"
                rel_parent="$(dirname "$rel")"
              else
                parent="$dest"
                rel_parent="$rel"
              fi

              current="$dest_base"
              if [ "$rel_parent" != "." ]; then
                IFS=/ read -r -a parts <<< "$rel_parent"
                for part in "${parts[@]}"; do
                  [ -n "$part" ] || continue
                  current="$current/$part"
                  if [ -L "$current" ]; then
                    echo "scratchpad restore destination uses symlinked path: $scope/$rel" >&2
                    exit 15
                  fi
                done
              fi

              mkdir -p "$parent"
              parent_real="$(realpath -e "$parent")"
              case "$parent_real/" in
                "$dest_base_real"/*) ;;
                *)
                  echo "scratchpad restore destination escapes base: $scope/$rel" >&2
                  exit 15
                  ;;
              esac

              if [ -L "$dest" ]; then
                echo "scratchpad restore destination is a symlink: $scope/$rel" >&2
                exit 15
              fi
              if [ "$kind" = "file" ] && [ -e "$dest" ] && [ ! -f "$dest" ]; then
                echo "scratchpad restore destination is not a regular file: $scope/$rel" >&2
                exit 15
              fi
            }

            is_allowed_entry() {
              local kind="$1"
              local rel="$2"
              local root
              for root in "${allowed_roots[@]}"; do
                if [ "$rel" = "$root" ] || [[ "$rel" == "$root/"* ]]; then
                  return 0
                fi
                if [ "$kind" = "dir" ] && [[ "$root" == "$rel/"* ]]; then
                  return 0
                fi
              done
              return 1
            }

            allowed="$scratch_tmp/allowed.txt"
            {
              printf '%s\n' "." "./" "manifest.tsv" "./manifest.tsv" "manifest.txt" "./manifest.txt" "home" "./home" "home/" "./home/" "work" "./work" "work/" "./work/"
              while IFS=$'\t' read -r kind scope rel; do
                [ "$kind" = "file" ] || [ "$kind" = "dir" ] || exit 11
                [ "$scope" = "home" ] || [ "$scope" = "work" ] || exit 11
                valid_rel "$rel" || exit 11
                is_allowed_entry "$kind" "$rel" || exit 11
                printf '%s/%s\n' "$scope" "$rel"
                printf './%s/%s\n' "$scope" "$rel"
                if [ "$kind" = "dir" ]; then
                  printf '%s/%s/\n' "$scope" "$rel"
                  printf './%s/%s/\n' "$scope" "$rel"
                fi
              done < "$manifest"
            } > "$allowed"

            normalized_members="$scratch_tmp/normalized-members.txt"
            : > "$normalized_members"
            entry_count=0
            while IFS= read -r member; do
              [ -n "$member" ] || continue
              entry_count=$((entry_count + 1))
              [ "$entry_count" -le "$max_entries" ] || {
                echo "scratchpad archive entry limit exceeded" >&2
                exit 12
              }
              normalized="${member#./}"
              [[ "$normalized" != /* && "$normalized" != *"/../"* && "$normalized" != "../"* ]] || {
                echo "scratchpad archive contains unsafe path: $member" >&2
                exit 12
              }
              printf '%s\n' "$normalized" >> "$normalized_members"
              if ! grep -Fx -- "$member" "$allowed" >/dev/null \
                 && ! grep -Fx -- "$normalized" "$allowed" >/dev/null; then
                echo "scratchpad archive contains unmanifested path: $member" >&2
                exit 12
              fi
            done < "$members"

            if sort "$normalized_members" | uniq -d | grep -q .; then
              echo "scratchpad archive contains duplicate paths" >&2
              exit 13
            fi

            restored_bytes=0
            while IFS=$'\t' read -r kind scope rel; do
              valid_rel "$rel" || exit 11
              is_allowed_entry "$kind" "$rel" || exit 11
              src="$scratch_tmp/$scope/$rel"
              case "$scope" in
                home) dest_base="$HOME" ;;
                work) dest_base="." ;;
                *) exit 11 ;;
              esac
              dest="$dest_base/$rel"
              if [ "$kind" = "dir" ]; then
                mkdir -p "$src"
                ensure_destination "$dest_base" "$rel" dir
              elif [ "$kind" = "file" ]; then
                member="$scope/$rel"
                if ! grep -Fx -- "$member" "$members" >/dev/null; then
                  member="./$scope/$rel"
                fi
                grep -Fx -- "$member" "$members" >/dev/null || continue
                mkdir -p "$(dirname "$src")"
                remaining=$((max_total_bytes - restored_bytes))
                [ "$remaining" -gt 0 ] || {
                  echo "scratchpad restore total byte limit exceeded" >&2
                  exit 14
                }
                limit=$((max_file_bytes + 1))
                if [ "$remaining" -lt "$max_file_bytes" ]; then
                  limit=$((remaining + 1))
                fi
                bytes="$(extract_bounded "$member" "$src" "$limit")" || {
                  echo "scratchpad file exceeds restore limit or cannot be read: $member" >&2
                  exit 14
                }
                [ "$bytes" -le "$max_file_bytes" ] || {
                  echo "scratchpad file exceeds per-file restore limit: $member" >&2
                  exit 14
                }
                restored_bytes=$((restored_bytes + bytes))
                [ "$restored_bytes" -le "$max_total_bytes" ] || {
                  echo "scratchpad restore total byte limit exceeded" >&2
                  exit 14
                }
                ensure_destination "$dest_base" "$rel" file
                cp -p "$src" "$dest"
              else
                exit 11
              fi
            done < "$manifest"
            """,
            "codeybox-resume",
            resume.ScratchpadArchivePath,
        };
        argv.AddRange(ScratchpadHomeDirectories);

        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = argv,
            WorkingDirectory = workingDirectory,
        }, ct);
        if (!result.Success)
            throw new InvalidOperationException($"agent scratchpad restore failed (exit {result.ExitCode}): {result.Stderr}");
    }

    private static string ShellQuote(string value) =>
        "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private string AgentRunKey(ISandbox sandbox, string workingDirectory) =>
        $"{Kind.Value}\n{sandbox.Id}\n{workingDirectory}";

    private static IReadOnlyDictionary<string, string> WithAgentRunId(
        IReadOnlyDictionary<string, string>? environment,
        string runId)
    {
        var merged = environment is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(environment, StringComparer.Ordinal);
        merged[AgentRunIdEnvironmentVariable] = runId;
        // R8-core: the orchestrator-controlled invocation context optionally
        // requests tee'd capture of the agent CLI's stdout/stderr into an in-VM
        // log file. The codeybox-exec wrapper honours this env var; without it
        // (test/non-pipeline callers) the wrapper preserves its
        // existing behaviour of streaming output to the host only.
        var logPath = AgentInvocationLogContext.CurrentLogPath;
        if (!string.IsNullOrEmpty(logPath))
            merged[SandboxConventions.AgentLogFileEnv] = logPath;
        return merged;
    }

    private static void RemoveActiveAgentRunId(string runKey, string runId)
    {
        ((ICollection<KeyValuePair<string, string>>)ActiveAgentRunIds)
            .Remove(new KeyValuePair<string, string>(runKey, runId));
    }

}
