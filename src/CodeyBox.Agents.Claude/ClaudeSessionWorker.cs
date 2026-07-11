using System.Collections.Concurrent;
using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Claude;

/// <summary>
/// Session-capable Claude runner. Keeps ONE logical Claude session across
/// turns and continues it across the work → audit → rework cycle so the
/// provider-side prompt cache and the conversation transcript both carry
/// over. The underlying sandbox/VM can be STOPPED between turns; the prompt
/// cache is server-side at Anthropic (~5min TTL by default, 1h extended) so
/// a resume that lands inside the TTL still cache-hits, and the on-VM
/// transcript persists across stop/start.
///
/// <para>Two layers, only one of them varies per the operator's
/// <see cref="ClaudeSessionWorkerOptions.Transport"/>:</para>
/// <list type="bullet">
///   <item>SESSION layer — owned by this class. Open / Send / Suspend /
///   Resume / Close lifecycle, captured session-id continuation, persisted
///   handle, sandbox lifecycle, sanitiser hookup, restart recovery,
///   fallback-to-fresh degradation.</item>
///   <item>TRANSPORT layer — implemented by <see cref="IClaudeTransport"/>.
///   <see cref="ClaudeSessionTransport.Print"/> = today's
///   <c>claude --print --resume</c> path;
///   <see cref="ClaudeSessionTransport.Acp"/> = an Agent Client Protocol
///   transport that drives <c>claude --ide</c> off the metered <c>-p</c>
///   pool. The worker resolves a transport at OPEN time; runtime
///   <see cref="AcpTransportUnavailableException"/> from the configured ACP
///   transport degrades that handle to the print fallback so the work item
///   continues rather than stranding.</item>
/// </list>
///
/// <para>Auditor isolation. Auditor and worker must run on SEPARATE ACP
/// sessions so a self-reviewing session does not rubber-stamp itself. The
/// contract already requires each caller to invoke
/// <see cref="OpenSessionAsync"/>; the worker assigns a unique
/// <c>local-session-id</c> per call and the transport derives its own
/// per-call session, so simply ensuring auditor and worker each open their
/// own handle keeps them isolated.</para>
///
/// <para>Model + sanitiser stay pinned: the fleet runs <c>claude-opus-4-7</c>;
/// the sanitiser runs unconditionally on every resume turn (gated by the
/// global <see cref="ClaudeThinkingBlockSanitizerConfig.Enabled"/>) regardless
/// of which transport is in use.</para>
/// </summary>
public sealed class ClaudeSessionWorker : IScopedSessionAgentRunner, ICredentialRefreshableSessionAgentRunner
{
    /// <summary>
    /// Metadata key under <see cref="AgentSessionHandle.Metadata"/> carrying
    /// the runner-assigned session id (Claude CLI id for the print transport;
    /// ACP session id for the ACP transport). Callers that persist the handle
    /// re-call <see cref="SnapshotPersistedHandle"/> after each turn to refresh
    /// it.
    /// </summary>
    public const string CliSessionIdMetadataKey = "claude.cliSessionId";

    /// <summary>
    /// Metadata flag stamped on the handle when the worker has degraded to
    /// fresh-one-shot mode (resume failed). Persisted so a restart inherits
    /// the degraded state.
    /// </summary>
    public const string FallbackMetadataKey = "claude.fallbackToOneShot";

    /// <summary>
    /// Metadata flag stamped on the handle when the worker has degraded from
    /// <see cref="ClaudeSessionTransport.Acp"/> to
    /// <see cref="ClaudeSessionTransport.Print"/> at runtime. Persisted so a
    /// restart inherits the degraded transport rather than retrying ACP on
    /// every turn.
    /// </summary>
    public const string AcpFallbackToPrintMetadataKey = "claude.acpFallbackToPrint";

    /// <summary>
    /// Optional metadata key callers may stamp on the handle to scope the
    /// configured transport overrides. When present, the value is matched
    /// against <see cref="ClaudeSessionWorkerOptions.TransportOverridesByAgentClassMember"/>
    /// (case-insensitive) to pick a per-member transport before falling
    /// through to <see cref="ProjectIdMetadataKey"/> and the global default.
    /// </summary>
    public const string AgentClassMemberMetadataKey = "codeybox.agentClassMember";

    /// <summary>
    /// Optional metadata key callers may stamp on the handle to scope the
    /// configured per-project transport overrides
    /// (<see cref="ClaudeSessionWorkerOptions.TransportOverridesByProject"/>).
    /// </summary>
    public const string ProjectIdMetadataKey = "codeybox.projectId";

    /// <summary>
    /// Metadata key recording the transport actually in use for this session
    /// (after override and fallback resolution). Surfaced on the persisted
    /// handle and on per-turn metrics so dashboards can confirm Claude work is
    /// off the <c>--print</c> metered pool.
    /// </summary>
    public const string TransportMetadataKey = "claude.transport";

    private const string SessionIdMarkerPrefix = "claude-session-";

    private readonly IClaudeTransport _printTransport;
    private readonly IClaudeTransport? _acpTransport;
    private readonly ClaudeAgentRunner _runner;
    private readonly Func<ISandbox, AgentSessionSandboxRef> _sandboxRefFactory;
    private readonly Func<AgentSessionSandboxRef, CancellationToken, Task<ISandbox>>? _sandboxReattacher;
    private readonly Func<AgentSessionSandboxRef, CancellationToken, Task>? _sandboxResumeHook;
    private readonly ICredentialProvider? _credentialProvider;
    private readonly IClaudeSessionMetricsSink _metricsSink;
    private readonly ClaudeSessionWorkerOptions _options;
    private readonly Action<string, string>? _onTransportDegraded;
    private readonly ConcurrentDictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _closedSessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _reattachLocks = new(StringComparer.Ordinal);

    /// <param name="runner">Underlying one-shot runner whose argv/credential machinery the print transport reuses.</param>
    /// <param name="sandboxReattacher">
    /// Reattaches a fresh <see cref="ISandbox"/> to the same VM after an
    /// orchestrator restart. Left null until the restart-recovery dispatch
    /// wiring lands.
    /// </param>
    /// <param name="sandboxResumeHook">
    /// Starts the underlying VM back up. Wired in production to
    /// <c>ISuspendingSandboxProvider.ResumeSandboxAsync</c> when the registered
    /// provider implements the suspend contract; null otherwise. On any
    /// failure the worker degrades to fresh-one-shot mode.
    /// </param>
    /// <param name="credentialProvider">Restart-time credential provider. The persisted handle never stores secret material.</param>
    /// <param name="sandboxRefFactory">Maps a live sandbox to its durable provider-specific reference.</param>
    /// <param name="metricsSink">Receives per-turn cache-hit metrics. Null defaults to the no-op sink.</param>
    /// <param name="options">
    /// Bound configuration. When supplied with <c>EmitTurnMetrics=false</c>,
    /// <see cref="SendTurnAsync"/> skips the
    /// <see cref="IClaudeSessionMetricsSink"/> emission entirely. The
    /// <see cref="ClaudeSessionWorkerOptions.Transport"/> field selects the
    /// command-delivery channel; default <see cref="ClaudeSessionTransport.Print"/>.
    /// </param>
    /// <param name="acpTransport">
    /// ACP transport implementation. Optional — when null and the operator
    /// selects <see cref="ClaudeSessionTransport.Acp"/>, the worker logs the
    /// missing transport and uses <see cref="ClaudeSessionTransport.Print"/>.
    /// Production wires <see cref="AcpClaudeTransport"/> here.
    /// </param>
    /// <param name="printTransport">
    /// Print transport implementation. Optional — defaults to a
    /// <see cref="PrintClaudeTransport"/> wrapping <paramref name="runner"/>
    /// so existing callers don't have to change their construction.
    /// </param>
    /// <param name="onTransportDegraded">
    /// Optional hook invoked with (handleSessionId, reason) when the worker
    /// flips the active transport from ACP to print at runtime. The host wires
    /// this to <see cref="AuditLog"/> so operators see degraded sessions.
    /// </param>
    public ClaudeSessionWorker(
        ClaudeAgentRunner runner,
        Func<AgentSessionSandboxRef, CancellationToken, Task<ISandbox>>? sandboxReattacher = null,
        Func<AgentSessionSandboxRef, CancellationToken, Task>? sandboxResumeHook = null,
        ICredentialProvider? credentialProvider = null,
        Func<ISandbox, AgentSessionSandboxRef>? sandboxRefFactory = null,
        IClaudeSessionMetricsSink? metricsSink = null,
        ClaudeSessionWorkerOptions? options = null,
        IClaudeTransport? acpTransport = null,
        IClaudeTransport? printTransport = null,
        Action<string, string>? onTransportDegraded = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _sandboxReattacher = sandboxReattacher;
        _sandboxResumeHook = sandboxResumeHook;
        _credentialProvider = credentialProvider;
        _sandboxRefFactory = sandboxRefFactory ?? (static sandbox => new AgentSessionSandboxRef(sandbox.Id));
        _metricsSink = metricsSink ?? NullClaudeSessionMetricsSink.Instance;
        _options = options ?? new ClaudeSessionWorkerOptions();
        _printTransport = printTransport ?? new PrintClaudeTransport(_runner);
        _acpTransport = acpTransport;
        _onTransportDegraded = onTransportDegraded;
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
        => OpenSessionAsync(sandbox, workingDirectory, credential, modelId, reasoningMode,
            projectId: null, agentClassMember: null, ct: ct);

    /// <inheritdoc/>
    public Task<AgentSessionHandle> OpenSessionAsync(
        AgentSessionOpenRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return OpenSessionAsync(
            request.Sandbox,
            request.WorkingDirectory,
            request.Credential,
            request.ModelId,
            request.ReasoningMode,
            request.ProjectId,
            request.AgentClassMember,
            ct);
    }

    /// <summary>
    /// Override for callers that know the dispatch context. <paramref name="projectId"/> and
    /// <paramref name="agentClassMember"/> are matched against
    /// <see cref="ClaudeSessionWorkerOptions.TransportOverridesByProject"/> and
    /// <see cref="ClaudeSessionWorkerOptions.TransportOverridesByAgentClassMember"/> respectively;
    /// the resolved transport is the per-member override (if any), then per-project, then the
    /// global default. Both values are stamped into the returned handle's metadata so a
    /// post-restart reattach replays the same scoped resolution rather than falling back to the
    /// global default.
    /// </summary>
    public async Task<AgentSessionHandle> OpenSessionAsync(
        ISandbox sandbox,
        string workingDirectory,
        AgentCredential? credential,
        string? modelId,
        string? reasoningMode,
        string? projectId,
        string? agentClassMember,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ct.ThrowIfCancellationRequested();

        var localSessionId = SessionIdMarkerPrefix + Guid.NewGuid().ToString("N");
        var sandboxRef = _sandboxRefFactory(sandbox);
        if (string.IsNullOrWhiteSpace(sandboxRef.Id))
            throw new InvalidOperationException("Session sandbox references must include a non-blank id.");

        var scopeMetadata = BuildScopeMetadata(projectId, agentClassMember);
        var requestedTransport = _options.ResolveTransport(scopeMetadata);
        var effectiveTransport = await OpenTransportAsync(
            requestedTransport,
            sandbox,
            workingDirectory,
            credential,
            modelId,
            reasoningMode,
            localSessionId,
            ct).ConfigureAwait(false);

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mode"] = "claude-session",
            [TransportMetadataKey] = effectiveTransport.Transport.Name,
        };
        if (!string.IsNullOrWhiteSpace(projectId))
            metadata[ProjectIdMetadataKey] = projectId!;
        if (!string.IsNullOrWhiteSpace(agentClassMember))
            metadata[AgentClassMemberMetadataKey] = agentClassMember!;
        if (effectiveTransport.Degraded)
            metadata[AcpFallbackToPrintMetadataKey] = "true";
        var handle = new AgentSessionHandle(
            Kind,
            localSessionId,
            sandboxRef,
            workingDirectory,
            modelId,
            reasoningMode,
            metadata);

        _sessions[localSessionId] = new SessionState(
            sandbox, credential, effectiveTransport.Transport, effectiveTransport.Session, effectiveTransport.Degraded);
        return handle;
    }

    private static IReadOnlyDictionary<string, string>? BuildScopeMetadata(
        string? projectId, string? agentClassMember)
    {
        if (string.IsNullOrWhiteSpace(projectId) && string.IsNullOrWhiteSpace(agentClassMember))
            return null;
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(projectId))
            d[ProjectIdMetadataKey] = projectId!;
        if (!string.IsNullOrWhiteSpace(agentClassMember))
            d[AgentClassMemberMetadataKey] = agentClassMember!;
        return d;
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

            _ = captureStructuredStream;

            var resumeId = state.FallbackToFresh ? null : state.CapturedSessionId;
            var turnRequest = new ClaudeTransportTurnRequest(prompt, resumeId, stdoutChunkCallback);

            ClaudeTransportTurnResult turn;
            try
            {
                turn = await state.TransportSession.SendTurnAsync(turnRequest, ct).ConfigureAwait(false);
            }
            catch (AcpTransportUnavailableException unavailable)
            {
                await DegradeToPrintAsync(sessionHandle, state, unavailable.Message, ct).ConfigureAwait(false);
                // DegradeToPrintAsync cleared CapturedSessionId because the
                // prior ACP id is meaningless to the print transport. The
                // pre-degrade turnRequest still carries that ACP UUID as the
                // resume id, so rebuild it from the post-degrade state before
                // the print transport sees it.
                var fallbackResumeId = state.FallbackToFresh ? null : state.CapturedSessionId;
                turnRequest = new ClaudeTransportTurnRequest(prompt, fallbackResumeId, stdoutChunkCallback);
                resumeId = fallbackResumeId;
                turn = await state.TransportSession.SendTurnAsync(turnRequest, ct).ConfigureAwait(false);
            }

            if (state.CapturedSessionId is null && !state.FallbackToFresh)
            {
                if (!string.IsNullOrEmpty(turn.CapturedCliSessionId))
                    state.CapturedSessionId = turn.CapturedCliSessionId;
            }

            EmitMetrics(state, turn, usedResume: resumeId is not null);

            state.TurnsCompleted++;
            return turn.Result;
        }
        finally
        {
            state.Gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task RefreshSessionCredentialAsync(
        AgentSessionHandle sessionHandle,
        AgentCredential? credential,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessionHandle);
        EnsureKind(sessionHandle);
        ct.ThrowIfCancellationRequested();

        if (credential is not null && credential.Agent != Kind)
        {
            throw new InvalidOperationException(
                $"Credential refresh supplied credentials for '{credential.Agent}', not '{Kind}'.");
        }

        var state = await ResolveStateAsync(sessionHandle, ct).ConfigureAwait(false);
        await state.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfClosed(state);
            state.Credential = credential;
            if (state.TransportSession is ICredentialRefreshableClaudeTransportSession refreshable)
                refreshable.RefreshCredential(credential);
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
            var preemptible = SandboxCapability.Find<IPreemptibleSandbox>(state.Sandbox);
            if (preemptible is not null
                && SandboxCapability.Find<ISuspendableSandbox>(state.Sandbox) is null)
            {
                throw new NotSupportedException(
                    "The sandbox does not support stopped-session resume.");
            }
            try
            {
                await state.TransportSession.SuspendAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // Transport-side suspend failures must not block the VM stop.
            }
            if (preemptible is not null)
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
            if (SandboxCapability.Find<IPreemptibleSandbox>(state.Sandbox) is not null
                && SandboxCapability.Find<ISuspendableSandbox>(state.Sandbox) is null)
            {
                throw new NotSupportedException(
                    "The sandbox does not support stopped-session resume.");
            }

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
                    state.CapturedSessionId = null;
                }
            }

            try
            {
                await state.TransportSession.ResumeAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // Same degradation rule as the suspend path: don't strand.
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
            try { await state.TransportSession.DisposeAsync().ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { /* Transport teardown must not strand */ }
            if (state.Sandbox is IPreserveOnDisposeSandbox preserveOnDispose)
                preserveOnDispose.DisablePreserveOnDispose();
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
    /// Returns a fresh <see cref="AgentSessionHandle"/> reflecting state
    /// captured during turns (CLI/ACP session id, fallback flags, active
    /// transport). Callers persist this after each turn so orchestrator
    /// restart picks the session up where the turn left it. Returns the
    /// supplied handle unchanged when no state has been captured yet.
    /// </summary>
    public AgentSessionHandle SnapshotPersistedHandle(AgentSessionHandle sessionHandle)
    {
        ArgumentNullException.ThrowIfNull(sessionHandle);
        if (!_sessions.TryGetValue(sessionHandle.SessionId, out var state))
            return sessionHandle;

        var metadata = sessionHandle.Metadata is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(sessionHandle.Metadata, StringComparer.Ordinal);

        if (!string.IsNullOrEmpty(state.CapturedSessionId))
            metadata[CliSessionIdMetadataKey] = state.CapturedSessionId;
        else
            metadata.Remove(CliSessionIdMetadataKey);

        if (state.FallbackToFresh)
        {
            metadata[FallbackMetadataKey] = "true";
            metadata[AgentSessionMetadataKeys.FallbackToOneShot] = "true";
        }
        else
        {
            metadata.Remove(FallbackMetadataKey);
            metadata.Remove(AgentSessionMetadataKeys.FallbackToOneShot);
        }

        if (state.DegradedFromAcp)
            metadata[AcpFallbackToPrintMetadataKey] = "true";
        else
            metadata.Remove(AcpFallbackToPrintMetadataKey);

        metadata[TransportMetadataKey] = state.ActiveTransport.Name;

        return sessionHandle with { Metadata = metadata };
    }

    private async Task<OpenTransportResult> OpenTransportAsync(
        ClaudeSessionTransport requested,
        ISandbox sandbox,
        string workingDirectory,
        AgentCredential? credential,
        string? modelId,
        string? reasoningMode,
        string localSessionId,
        CancellationToken ct)
    {
        var openRequest = new ClaudeTransportOpenRequest(
            sandbox, workingDirectory, credential, modelId, reasoningMode, localSessionId);

        if (requested == ClaudeSessionTransport.Acp && _acpTransport is not null)
        {
            try
            {
                var session = await _acpTransport.OpenAsync(openRequest, ct).ConfigureAwait(false);
                return new OpenTransportResult(_acpTransport, session, Degraded: false);
            }
            catch (OperationCanceledException) { throw; }
            catch (AcpTransportUnavailableException unavailable)
            {
                _onTransportDegraded?.Invoke(localSessionId,
                    $"acp transport open failed: {unavailable.Message}");
            }
            catch (Exception ex)
            {
                _onTransportDegraded?.Invoke(localSessionId,
                    $"acp transport open faulted: {ex.GetType().Name}: {ex.Message}");
            }
        }
        else if (requested == ClaudeSessionTransport.Acp)
        {
            _onTransportDegraded?.Invoke(localSessionId,
                "acp transport requested but no implementation is registered; using print");
        }

        var printSession = await _printTransport.OpenAsync(openRequest, ct).ConfigureAwait(false);
        return new OpenTransportResult(_printTransport, printSession, Degraded: requested == ClaudeSessionTransport.Acp);
    }

    private async Task DegradeToPrintAsync(
        AgentSessionHandle sessionHandle,
        SessionState state,
        string reason,
        CancellationToken ct)
    {
        if (state.ActiveTransport.Transport == ClaudeSessionTransport.Print)
            return;
        _onTransportDegraded?.Invoke(sessionHandle.SessionId, reason);
        try { await state.TransportSession.DisposeAsync().ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { /* swallow */ }

        var open = new ClaudeTransportOpenRequest(
            state.Sandbox,
            sessionHandle.WorkingDirectory,
            state.Credential,
            sessionHandle.ModelId,
            sessionHandle.ReasoningMode,
            sessionHandle.SessionId);
        var fallback = await _printTransport.OpenAsync(open, ct).ConfigureAwait(false);
        state.ActiveTransport = _printTransport;
        state.TransportSession = fallback;
        state.DegradedFromAcp = true;
        // The ACP session id is meaningless to the print transport; clear it so
        // the next turn starts fresh rather than passing an unknown id to --resume.
        state.CapturedSessionId = null;
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

                // Reattach honours the persisted transport choice: if the prior
                // run degraded ACP → print, the restart inherits the print
                // transport rather than retrying ACP on every turn.
                var requestedTransport = sessionHandle.Metadata is not null
                    && sessionHandle.Metadata.TryGetValue(AcpFallbackToPrintMetadataKey, out var degraded)
                    && string.Equals(degraded, "true", StringComparison.OrdinalIgnoreCase)
                    ? ClaudeSessionTransport.Print
                    : _options.ResolveTransport(sessionHandle.Metadata);
                var openResult = await OpenTransportAsync(
                    requestedTransport,
                    sandbox,
                    sessionHandle.WorkingDirectory,
                    credential,
                    sessionHandle.ModelId,
                    sessionHandle.ReasoningMode,
                    sessionHandle.SessionId,
                    ct).ConfigureAwait(false);

                state = new SessionState(sandbox, credential, openResult.Transport, openResult.Session,
                    degradedFromAcp: openResult.Degraded
                        || (sessionHandle.Metadata?.ContainsKey(AcpFallbackToPrintMetadataKey) ?? false));
                if (sessionHandle.Metadata is not null)
                {
                    if (sessionHandle.Metadata.TryGetValue(CliSessionIdMetadataKey, out var persistedCliId)
                        && IsValidCliSessionId(persistedCliId))
                        state.CapturedSessionId = persistedCliId;
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

    private void EmitMetrics(SessionState state, ClaudeTransportTurnResult turn, bool usedResume)
    {
        if (!_options.EmitTurnMetrics)
            return;
        var cliSessionId = state.CapturedSessionId ?? "(unassigned)";
        try
        {
            var extractor = new ClaudeCostExtractor();
            var snapshot = extractor.TryExtract(turn.Result.Stdout ?? turn.CombinedStdout, turn.Result.Stderr);
            if (snapshot is null)
                return;
            // AgentCostSnapshot.InputTokens is the non-cached billable bucket
            // (fresh + cache_creation for Claude); CachedInputTokens is cache_read.
            // The metric exposes the operator-facing TOTAL prompt-input bucket so
            // dashboards can chart cache_read share of total turn-over-turn.
            var fresh = Math.Max(0, snapshot.InputTokens);
            var total = (int)Math.Min(int.MaxValue,
                (long)Math.Max(0, snapshot.InputTokens) + Math.Max(0, snapshot.CachedInputTokens));
            // The extractor folds cache_creation into the billable InputTokens
            // bucket so cost rows charge correctly. The metric exposes
            // cache_creation separately so dashboards can chart cache_read vs
            // cache_creation over consecutive turns — the signal the ACP
            // cache-warmth verification reads to decide whether session/load is
            // reattaching to a warm cache or rebuilding it every turn.
            var cacheCreation = Math.Max(0,
                ClaudeCostExtractor.ExtractCacheCreationTokens(turn.Result.Stdout ?? turn.CombinedStdout));
            var metrics = new ClaudeSessionTurnMetrics(
                CliSessionId: cliSessionId,
                TurnIndex: state.TurnsCompleted,
                InputTokens: total,
                CachedInputTokens: snapshot.CachedInputTokens,
                FreshInputTokens: fresh,
                OutputTokens: snapshot.OutputTokens,
                ModelId: snapshot.ModelId,
                UsedResume: usedResume,
                Transport: state.ActiveTransport.Name)
            {
                CacheCreationInputTokens = cacheCreation,
            };
            _metricsSink.Record(metrics);
        }
        catch
        {
            // Observability must never break a turn — swallow everything.
        }
    }

    /// <summary>
    /// Pulls the Claude CLI session id out of a stream-json stdout payload.
    /// </summary>
    public static string? TryExtractCliSessionId(string? stdout)
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

    internal static bool IsValidCliSessionId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;
        if (id.Length > 128)
            return false;
        foreach (var c in id)
        {
            if (c == '-' || c == '_' || (c >= '0' && c <= '9')
                || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'))
                continue;
            return false;
        }
        return true;
    }

    /// <summary>
    /// Validates an ACP session id observed in the transport's response stream
    /// before adopting it for next-turn continuation. Same conservative
    /// character set as <see cref="IsValidCliSessionId"/>.
    /// </summary>
    internal static bool IsValidAcpSessionId(string? id) => IsValidCliSessionId(id);

    private sealed record OpenTransportResult(
        IClaudeTransport Transport,
        IClaudeTransportSession Session,
        bool Degraded);

    private sealed class SessionState
    {
        public SessionState(
            ISandbox sandbox,
            AgentCredential? credential,
            IClaudeTransport activeTransport,
            IClaudeTransportSession transportSession,
            bool degradedFromAcp)
        {
            Sandbox = sandbox;
            Credential = credential;
            ActiveTransport = activeTransport;
            TransportSession = transportSession;
            DegradedFromAcp = degradedFromAcp;
        }

        public SemaphoreSlim Gate { get; } = new(1, 1);
        public ISandbox Sandbox { get; }
        public AgentCredential? Credential { get; set; }
        public IClaudeTransport ActiveTransport { get; set; }
        public IClaudeTransportSession TransportSession { get; set; }
        public string? CapturedSessionId { get; set; }
        public int TurnsCompleted { get; set; }
        public bool Suspended { get; set; }
        public bool Closed { get; set; }
        public bool FallbackToFresh { get; set; }
        public bool DegradedFromAcp { get; set; }
    }
}
