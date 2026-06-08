using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Claude;

/// <summary>
/// Session-capable Claude runner. Unlike the one-shot
/// <see cref="ClaudeAgentRunner"/> (which spawns a fresh <c>claude --print</c>
/// per turn — cold context, no provider cache identity), this worker keeps
/// ONE logical Claude CLI session across turns and uses
/// <c>claude --resume &lt;session-id&gt;</c> to continue it. The underlying
/// sandbox/VM can be STOPPED between turns to free host resources: the prompt
/// cache is server-side at Anthropic (~5min default TTL, 1h extended) so a
/// resume that lands inside the TTL still cache-hits even after a host-side
/// VM stop / start cycle, and the Claude session JSONL transcript persists on
/// the stopped VM's disk so conversation context survives regardless.
///
/// <para>This is an opt-in alternative to the one-shot runner. The default
/// ClaudeAgentRunner stays the registered <see cref="IAgentRunner"/> until
/// config opts an item in (see <see cref="ClaudeSessionWorkerOptions"/>).</para>
///
/// <para>Lifecycle:</para>
/// <list type="bullet">
///   <item><c>OpenSessionAsync</c> just allocates in-process state — the
///   actual Claude CLI session id is captured from the first turn's
///   <c>stream-json</c> <c>system/init</c> event.</item>
///   <item><c>SendTurnAsync</c> runs one turn. The first turn runs fresh with
///   stream-json capture; subsequent turns pass <c>--resume &lt;id&gt;</c>.
///   Each turn invokes the same <see cref="ClaudeSessionSanitizer"/> the
///   one-shot runner uses (preventive before invocation when the CLI is
///   about to replay an existing transcript, reactive after a thinking-block
///   400). Stream-json usage is parsed by <see cref="ClaudeCostExtractor"/>
///   and emitted via <see cref="IClaudeSessionMetricsSink"/> so the
///   <c>cache_read</c> vs fresh-input savings are measurable.</item>
///   <item><c>SuspendSessionAsync</c> stops the sandbox VM via
///   <see cref="IPreemptibleSandbox.StopAndPreserveAsync"/> (multipass stop,
///   NOT dispose) so the VM disk and Claude session JSONL are preserved.</item>
///   <item><c>ResumeSessionAsync</c> brings the VM back via the configured
///   <c>sandboxResumeHook</c> (multipass start). After an orchestrator
///   restart the persisted <see cref="AgentSessionHandle"/> alone is enough
///   to reattach: the <c>sandboxReattacher</c> binds a fresh <see cref="ISandbox"/>
///   to the same VM name and the worker continues. If the VM can't be
///   resumed the next turn falls back to a fresh one-shot run rather than
///   stranding the work item.</item>
///   <item><c>CloseSessionAsync</c> disposes the sandbox and ends the
///   logical session.</item>
/// </list>
///
/// <para>The fleet stays pinned to <c>claude-opus-4-7</c>; the runner does not
/// hot-swap models mid-session. Long resumable sessions are the exact trigger
/// surface for the thinking-block immutability 400 cluster, so the sanitiser
/// runs unconditionally on every resume turn (gated only by the global
/// <see cref="ClaudeThinkingBlockSanitizerConfig.Enabled"/>).</para>
/// </summary>
public sealed class ClaudeSessionWorker : ISessionAgentRunner
{
    /// <summary>
    /// Suggested metadata key on <see cref="AgentSessionHandle.Metadata"/> for
    /// the Claude CLI session id captured on the first turn. Callers that
    /// persist the handle re-call <see cref="SnapshotPersistedHandle"/> after
    /// each turn to get the up-to-date form.
    /// </summary>
    public const string CliSessionIdMetadataKey = "claude.cliSessionId";

    /// <summary>
    /// Metadata flag stamped on the handle when the worker has degraded to
    /// fresh-one-shot mode (resume failed). Persisted so a restart inherits
    /// the degraded state.
    /// </summary>
    public const string FallbackMetadataKey = "claude.fallbackToOneShot";

    private const string SessionIdMarkerPrefix = "claude-session-";

    private readonly ClaudeAgentRunner _runner;
    private readonly Func<ISandbox, AgentSessionSandboxRef> _sandboxRefFactory;
    private readonly Func<AgentSessionSandboxRef, CancellationToken, Task<ISandbox>>? _sandboxReattacher;
    private readonly Func<AgentSessionSandboxRef, CancellationToken, Task>? _sandboxResumeHook;
    private readonly ICredentialProvider? _credentialProvider;
    private readonly IClaudeSessionMetricsSink _metricsSink;
    private readonly ConcurrentDictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _closedSessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _reattachLocks = new(StringComparer.Ordinal);

    /// <param name="runner">Underlying one-shot runner whose argv/credential machinery this worker reuses for every turn.</param>
    /// <param name="sandboxReattacher">
    /// Reattaches a fresh <see cref="ISandbox"/> to the same VM after an
    /// orchestrator restart. Without it, persisted handles cannot be revived.
    /// </param>
    /// <param name="sandboxResumeHook">
    /// Starts the underlying VM back up. Wired to
    /// <c>ISuspendingSandboxProvider.ResumeSandboxAsync</c> in production; tests
    /// supply a no-op. Without it, a restart that finds the VM stopped will
    /// fall back cleanly to fresh-one-shot mode.
    /// </param>
    /// <param name="credentialProvider">
    /// Optional restart-time credential provider. The persisted handle never
    /// stores secret material, so reattach must reacquire from here.
    /// </param>
    /// <param name="sandboxRefFactory">
    /// Maps a live sandbox to its durable provider-specific reference.
    /// Defaults to <c>new AgentSessionSandboxRef(sandbox.Id)</c>.
    /// </param>
    /// <param name="metricsSink">
    /// Receives per-turn cache-hit metrics. Null defaults to the no-op sink.
    /// </param>
    public ClaudeSessionWorker(
        ClaudeAgentRunner runner,
        Func<AgentSessionSandboxRef, CancellationToken, Task<ISandbox>>? sandboxReattacher = null,
        Func<AgentSessionSandboxRef, CancellationToken, Task>? sandboxResumeHook = null,
        ICredentialProvider? credentialProvider = null,
        Func<ISandbox, AgentSessionSandboxRef>? sandboxRefFactory = null,
        IClaudeSessionMetricsSink? metricsSink = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _sandboxReattacher = sandboxReattacher;
        _sandboxResumeHook = sandboxResumeHook;
        _credentialProvider = credentialProvider;
        _sandboxRefFactory = sandboxRefFactory ?? (static sandbox => new AgentSessionSandboxRef(sandbox.Id));
        _metricsSink = metricsSink ?? NullClaudeSessionMetricsSink.Instance;
    }

    public AgentKind Kind => AgentKind.Claude;

    /// <inheritdoc/>
    public Task<AgentResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        CancellationToken ct = default,
        Action<string>? stdoutChunkCallback = null,
        bool captureStructuredStream = false)
        => _runner.RunAsync(sandbox, workingDirectory, prompt, credential, modelId, reasoningMode, ct, stdoutChunkCallback, captureStructuredStream);

    /// <inheritdoc/>
    public AgentFailureClassification ClassifyFailure(AgentResult result)
        => ((IAgentRunner)_runner).ClassifyFailure(result);

    /// <inheritdoc/>
    public Task<AgentSessionHandle> OpenSessionAsync(
        ISandbox sandbox,
        string workingDirectory,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ct.ThrowIfCancellationRequested();

        var localSessionId = SessionIdMarkerPrefix + Guid.NewGuid().ToString("N");
        var sandboxRef = _sandboxRefFactory(sandbox);
        if (string.IsNullOrWhiteSpace(sandboxRef.Id))
            throw new InvalidOperationException("Session sandbox references must include a non-blank id.");

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mode"] = "claude-session",
        };
        var handle = new AgentSessionHandle(
            Kind,
            localSessionId,
            sandboxRef,
            workingDirectory,
            modelId,
            reasoningMode,
            metadata);

        _sessions[localSessionId] = new SessionState(sandbox, credential);
        return Task.FromResult(handle);
    }

    /// <inheritdoc/>
    public async Task<AgentResult> SendTurnAsync(
        AgentSessionHandle sessionHandle,
        string prompt,
        CancellationToken ct = default,
        Action<string>? stdoutChunkCallback = null,
        bool captureStructuredStream = false)
    {
        ArgumentNullException.ThrowIfNull(sessionHandle);
        ArgumentNullException.ThrowIfNull(prompt);
        EnsureKind(sessionHandle);
        ct.ThrowIfCancellationRequested();

        var state = await ResolveStateAsync(sessionHandle, ct).ConfigureAwait(false);
        await state.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfClosed(state);
            if (state.Suspended)
                throw new InvalidOperationException("Cannot send an agent turn while the session is suspended.");

            // Stream-json is the only path that lets us observe cache_read vs
            // fresh-input AND capture the CLI's session id from system/init,
            // so the worker forces it on every turn. The caller's value is
            // ignored deliberately; a session worker without stream-json
            // would have no cache observability and no way to learn the CLI
            // session id, which defeats the whole point of the worker.
            _ = captureStructuredStream;
            const bool forceStreamJson = true;

            // The worker degrades to fresh one-shot mode after an unrecoverable
            // resume. In that mode every turn runs without --resume; the
            // session JSONL is gone but the work item still makes progress.
            var resumeId = state.FallbackToFresh ? null : state.CliSessionId;

            var stdoutCapture = new StringBuilder(1024);
            Action<string> aggregator = chunk =>
            {
                lock (stdoutCapture)
                {
                    stdoutCapture.Append(chunk);
                }
                stdoutChunkCallback?.Invoke(chunk);
            };

            var result = await _runner.RunSessionTurnAsync(
                state.Sandbox,
                sessionHandle.WorkingDirectory,
                prompt,
                state.Credential,
                resumeId,
                sessionHandle.ModelId,
                sessionHandle.ReasoningMode,
                captureStructuredStream: forceStreamJson,
                ct,
                aggregator).ConfigureAwait(false);

            var combinedStdout = stdoutCapture.Length > 0
                ? stdoutCapture.ToString()
                : result.Stdout ?? string.Empty;

            // Capture the assigned CLI session id from the first stream-json
            // system/init event. Subsequent turns reuse it via --resume; if it
            // never appears (older CLI, malformed output) the worker still
            // succeeds but every turn runs fresh.
            if (state.CliSessionId is null && !state.FallbackToFresh)
            {
                var captured = TryExtractCliSessionId(combinedStdout);
                if (!string.IsNullOrEmpty(captured))
                    state.CliSessionId = captured;
            }

            EmitMetrics(state, combinedStdout, result.Stderr, usedResume: resumeId is not null);

            state.TurnsCompleted++;
            return result;
        }
        finally
        {
            state.Gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task SuspendSessionAsync(AgentSessionHandle sessionHandle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessionHandle);
        EnsureKind(sessionHandle);
        ct.ThrowIfCancellationRequested();

        var state = await ResolveStateAsync(sessionHandle, ct).ConfigureAwait(false);
        await state.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfClosed(state);
            // Stop the VM but PRESERVE its disk so the Claude session JSONL
            // and any in-flight working tree state survive. multipass stop,
            // not delete --purge.
            if (state.Sandbox is IPreemptibleSandbox preemptible)
                await preemptible.StopAndPreserveAsync(ct).ConfigureAwait(false);
            state.Suspended = true;
        }
        finally
        {
            state.Gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task ResumeSessionAsync(AgentSessionHandle sessionHandle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessionHandle);
        EnsureKind(sessionHandle);
        ct.ThrowIfCancellationRequested();

        var state = await ResolveStateAsync(sessionHandle, ct).ConfigureAwait(false);
        await state.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfClosed(state);

            // Bring the VM back up via the provider hook. Any failure here is
            // recoverable: we mark the worker fallback-to-fresh so the next
            // turn runs as a brand-new claude --print (no --resume, no stored
            // context) rather than stranding the work item.
            if (_sandboxResumeHook is not null)
            {
                try
                {
                    await _sandboxResumeHook(sessionHandle.Sandbox, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    state.FallbackToFresh = true;
                    state.CliSessionId = null;
                }
            }

            state.Suspended = false;
        }
        finally
        {
            state.Gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task CloseSessionAsync(AgentSessionHandle sessionHandle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessionHandle);
        EnsureKind(sessionHandle);
        ct.ThrowIfCancellationRequested();

        var state = await ResolveStateAsync(sessionHandle, ct).ConfigureAwait(false);
        await state.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfClosed(state);
            await state.Sandbox.DisposeAsync().ConfigureAwait(false);
            state.Closed = true;
            _closedSessions[sessionHandle.SessionId] = 0;
            _sessions.TryRemove(sessionHandle.SessionId, out _);
            _reattachLocks.TryRemove(sessionHandle.SessionId, out _);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    /// <summary>
    /// Returns a fresh <see cref="AgentSessionHandle"/> that reflects state
    /// captured during turns (e.g. the Claude CLI session id discovered on the
    /// first turn, fallback flag). Callers persist this after each turn so
    /// orchestrator restart can pick the session up where the work turn left
    /// it. Returns the supplied handle unchanged when no state has been
    /// captured yet.
    /// </summary>
    public AgentSessionHandle SnapshotPersistedHandle(AgentSessionHandle sessionHandle)
    {
        ArgumentNullException.ThrowIfNull(sessionHandle);
        if (!_sessions.TryGetValue(sessionHandle.SessionId, out var state))
            return sessionHandle;

        var metadata = sessionHandle.Metadata is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(sessionHandle.Metadata, StringComparer.Ordinal);

        if (!string.IsNullOrEmpty(state.CliSessionId))
            metadata[CliSessionIdMetadataKey] = state.CliSessionId;
        else
            metadata.Remove(CliSessionIdMetadataKey);

        if (state.FallbackToFresh)
            metadata[FallbackMetadataKey] = "true";
        else
            metadata.Remove(FallbackMetadataKey);

        return sessionHandle with { Metadata = metadata };
    }

    private async Task<SessionState> ResolveStateAsync(
        AgentSessionHandle sessionHandle,
        CancellationToken ct)
    {
        ThrowIfClosed(sessionHandle);

        if (_sessions.TryGetValue(sessionHandle.SessionId, out var state))
            return state;

        if (_sandboxReattacher is null)
        {
            throw new InvalidOperationException(
                "This Claude session is not active in the current process and no sandbox reattacher was configured. Configure a reattacher to support orchestrator restart recovery.");
        }

        var gate = _reattachLocks.GetOrAdd(sessionHandle.SessionId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfClosed(sessionHandle);
            if (_sessions.TryGetValue(sessionHandle.SessionId, out state))
                return state;

            ISandbox? sandbox = null;
            try
            {
                sandbox = await _sandboxReattacher(sessionHandle.Sandbox, ct).ConfigureAwait(false);
                if (sandbox is null)
                    throw new InvalidOperationException("The configured sandbox reattacher returned null.");

                var credential = _credentialProvider is null
                    ? null
                    : await _credentialProvider.GetAsync(sessionHandle.RunnerKind, ct).ConfigureAwait(false);
                if (credential is not null && credential.Agent != sessionHandle.RunnerKind)
                    throw new InvalidOperationException(
                        $"Credential provider returned credentials for '{credential.Agent}', not '{sessionHandle.RunnerKind}'.");

                state = new SessionState(sandbox, credential);
                // Restore the Claude CLI session id from the persisted handle
                // so the FIRST turn after restart still benefits from --resume
                // (and the server-side prompt cache when inside its TTL).
                // Same validity rules as when we capture it live — a tampered
                // persisted handle should NOT make it into argv.
                if (sessionHandle.Metadata is not null)
                {
                    if (sessionHandle.Metadata.TryGetValue(CliSessionIdMetadataKey, out var persistedCliId)
                        && IsValidCliSessionId(persistedCliId))
                        state.CliSessionId = persistedCliId;
                    if (sessionHandle.Metadata.TryGetValue(FallbackMetadataKey, out var fallback)
                        && string.Equals(fallback, "true", StringComparison.OrdinalIgnoreCase))
                        state.FallbackToFresh = true;
                }
                _sessions[sessionHandle.SessionId] = state;
                sandbox = null;
                return state;
            }
            finally
            {
                if (sandbox is not null)
                    await sandbox.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private void EnsureKind(AgentSessionHandle sessionHandle)
    {
        if (sessionHandle.RunnerKind != Kind)
        {
            throw new ArgumentException(
                $"Session handle belongs to runner '{sessionHandle.RunnerKind}', not '{Kind}'.",
                nameof(sessionHandle));
        }
    }

    private void ThrowIfClosed(AgentSessionHandle sessionHandle)
    {
        if (_closedSessions.ContainsKey(sessionHandle.SessionId))
            throw new InvalidOperationException("This agent session has already been closed.");
    }

    private static void ThrowIfClosed(SessionState state)
    {
        if (state.Closed)
            throw new InvalidOperationException("This agent session has already been closed.");
    }

    private void EmitMetrics(SessionState state, string? stdout, string? stderr, bool usedResume)
    {
        var cliSessionId = state.CliSessionId ?? "(unassigned)";
        try
        {
            var extractor = new ClaudeCostExtractor();
            var snapshot = extractor.TryExtract(stdout, stderr);
            if (snapshot is null)
                return;
            var fresh = Math.Max(0, snapshot.InputTokens - snapshot.CachedInputTokens);
            var metrics = new ClaudeSessionTurnMetrics(
                CliSessionId: cliSessionId,
                TurnIndex: state.TurnsCompleted,
                InputTokens: snapshot.InputTokens,
                CachedInputTokens: snapshot.CachedInputTokens,
                FreshInputTokens: fresh,
                OutputTokens: snapshot.OutputTokens,
                ModelId: snapshot.ModelId,
                UsedResume: usedResume);
            _metricsSink.Record(metrics);
        }
        catch
        {
            // Observability must never break a turn — swallow everything.
        }
    }

    /// <summary>
    /// Pulls the Claude CLI session id out of a stream-json stdout payload.
    /// The CLI's <c>system/init</c> event carries <c>"session_id":"..."</c>;
    /// the worker captures it once per session.
    ///
    /// <para>Captured ids are clamped to a conservative character set
    /// (UUID-shape: alphanumerics, hyphens, underscores; up to 128 chars).
    /// Even though the id flows through <see cref="IReadOnlyList{T}"/> argv
    /// (no shell, no injection), refusing pathological values stops a
    /// malformed/attacker-influenced stream from silently corrupting
    /// the resume target across persisted handles.</para>
    /// </summary>
    internal static string? TryExtractCliSessionId(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return null;
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.Length == 0 || line[0] != '{')
                continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (!doc.RootElement.TryGetProperty("session_id", out var idProp))
                    continue;
                if (idProp.ValueKind != JsonValueKind.String)
                    continue;
                var id = idProp.GetString();
                if (IsValidCliSessionId(id))
                    return id;
            }
            catch (JsonException)
            {
                // Non-JSON or partial line — keep scanning.
            }
        }
        return null;
    }

    private static bool IsValidCliSessionId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;
        if (id.Length > 128)
            return false;
        foreach (var c in id)
        {
            // Hyphen, underscore, alphanumerics — covers UUID, ULID, and any
            // future format Claude might switch to without re-introducing
            // path/shell metacharacters.
            if (c == '-' || c == '_' || (c >= '0' && c <= '9')
                || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'))
                continue;
            return false;
        }
        return true;
    }

    private sealed class SessionState(ISandbox sandbox, AgentCredential? credential)
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public ISandbox Sandbox { get; } = sandbox;
        public AgentCredential? Credential { get; } = credential;
        public string? CliSessionId { get; set; }
        public int TurnsCompleted { get; set; }
        public bool Suspended { get; set; }
        public bool Closed { get; set; }
        public bool FallbackToFresh { get; set; }
    }
}
