using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Drives the resumable session worker's lifecycle across one work item's
/// work→audit→rework cycle. The session is OPENED once at the start of the
/// work phase, used for every worker turn (work + each rework iteration),
/// SUSPENDED before each audit, RESUMED before the next rework, and CLOSED
/// when the work item completes (AuditPassed) or fails terminally.
///
/// <para>The auditor is intentionally NEVER given this lifecycle — auditors
/// run in their own fresh sandboxes (see
/// <c>PipelineRunner.CollectFindingsAsync</c>) so a self-reviewing session
/// can't rubber-stamp its own work. Item 3 of the rollout brief makes this
/// non-negotiable.</para>
///
/// <para>The lifecycle owns the worker VM. PipelineRunner asks for the
/// sandbox via <see cref="GetSandboxAsync(CancellationToken)"/>, which
/// resumes the VM if it was suspended between turns and returns the live
/// <see cref="ISandbox"/>. Each turn calls <see cref="SendTurnAsync"/> to
/// run a worker turn (the first turn establishes the CLI session id; later
/// turns pass <c>--resume</c> against it), and
/// <see cref="SuspendAsync(CancellationToken)"/> at the end of the work or
/// rework phase to stop the VM during the (long) audit.</para>
///
/// <para>On <see cref="DisposeAsync"/>, the underlying session is closed
/// regardless of state, which disposes the worker VM. Calling
/// <see cref="DisposeAsync"/> twice is safe.</para>
/// </summary>
internal sealed class ClaudeSessionLifecycle : IAsyncDisposable
{
    private readonly ISessionAgentRunner _worker;
    private readonly Func<AgentSessionHandle, AgentSessionHandle>? _handleSnapshot;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _suspended;
    private bool _closed;
    private bool _firstTurnComplete;
    private readonly string _openedAgentRouteKey;
    private readonly string? _openedModelId;
    private readonly string? _openedReasoningMode;

    private ClaudeSessionLifecycle(
        ISessionAgentRunner worker,
        Func<AgentSessionHandle, AgentSessionHandle>? handleSnapshot,
        ISandbox sandbox,
        AgentSessionHandle handle,
        string openedAgentRouteKey,
        string? openedModelId,
        string? openedReasoningMode)
    {
        _worker = worker;
        _handleSnapshot = handleSnapshot;
        Sandbox = sandbox;
        Handle = handle;
        _openedAgentRouteKey = openedAgentRouteKey;
        _openedModelId = openedModelId;
        _openedReasoningMode = openedReasoningMode;
    }

    /// <summary>
    /// The live worker sandbox. The same instance is returned for every turn
    /// of the same lifecycle, even after a suspend/resume cycle — the
    /// underlying session runner performs the resume via its configured
    /// provider hook (<c>multipass start &lt;vm&gt;</c> in production).
    /// </summary>
    public ISandbox Sandbox { get; }

    /// <summary>
    /// The session handle returned by
    /// <see cref="ISessionAgentRunner.OpenSessionAsync"/>. Carries the
    /// runner kind, a durable sandbox reference, and provider-specific
    /// metadata captured by the concrete session runner.
    /// </summary>
    public AgentSessionHandle Handle { get; private set; }

    /// <summary>
    /// True once a worker turn has actually executed. PipelineRunner uses
    /// this to skip the per-turn <c>git clone</c> step on subsequent turns:
    /// the working tree from the previous turn (work or earlier rework) is
    /// still on disk, and re-cloning would erase any non-pushed agent
    /// scratch state. Reset to <c>false</c> when the session has not yet
    /// produced its first successful turn.
    /// </summary>
    public bool FirstTurnComplete => _firstTurnComplete;

    /// <summary>
    /// Counter of worker turns sent through this session. Tests use this to
    /// assert that work + every rework iteration ran on ONE session (the
    /// CLI session id stays the same — captured on turn 1, reused via
    /// <c>--resume</c> on every subsequent turn).
    /// </summary>
    public int TurnsCompleted { get; private set; }

    /// <summary>
    /// True when the requested turn still matches the agent instance, model,
    /// and reasoning mode used to open this lifecycle. Class-level fallback can
    /// retry another Claude member; that must not reuse the original member's
    /// session, credential, or model.
    /// </summary>
    public bool CanRunTurn(IAgentRunner runner, WorkItem item)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(item);
        return runner.Kind == Handle.RunnerKind
            && string.Equals(_openedAgentRouteKey, RouteKeyFor(runner.Kind, item.AgentInstanceId), StringComparison.OrdinalIgnoreCase)
            && string.Equals(_openedModelId ?? string.Empty, item.ModelId ?? string.Empty, StringComparison.Ordinal)
            && string.Equals(_openedReasoningMode ?? string.Empty, item.ReasoningMode ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>
    /// Provider-specific session identifier captured after the first turn,
    /// looked up from <see cref="AgentSessionHandle.Metadata"/> using the
    /// well-known metadata key the provider stamps under (e.g. Claude's
    /// CLI <c>--resume</c> id). Returns null when the runner has not yet
    /// (or has stopped) populating the key. The metadata key string is a
    /// provider contract: the orchestration boundary does not own which
    /// key a given runner uses, so callers pass the key they expect their
    /// runner to stamp under. Tests use this to assert session-id
    /// continuity across work/rework turns without the lifecycle taking
    /// a hard reference to any provider-specific metadata-key constant.
    /// </summary>
    public string? GetSessionIdFromMetadata(string metadataKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metadataKey);
        if (Handle.Metadata is null)
            return null;
        return Handle.Metadata.TryGetValue(metadataKey, out var v) ? v : null;
    }

    /// <summary>
    /// True once the lifecycle has been closed (the underlying session
    /// disposed and the worker VM torn down). Idempotent — further calls
    /// to <see cref="DisposeAsync"/> are no-ops.
    /// </summary>
    public bool IsClosed => _closed;

    /// <summary>
    /// Opens the resumable session against a freshly-provisioned worker
    /// sandbox. The sandbox is owned by the returned lifecycle: callers
    /// must NOT separately dispose the sandbox — <see cref="DisposeAsync"/>
    /// disposes it via the runner's <c>CloseSessionAsync</c>.
    /// <paramref name="handleSnapshot"/> is an optional persistence hook
    /// the production composition root wires to the concrete runner's
    /// snapshot method; tests pass null when they don't exercise the
    /// persistence shape.
    /// </summary>
    public static async Task<ClaudeSessionLifecycle> OpenAsync(
        ISessionAgentRunner worker,
        Func<AgentSessionHandle, AgentSessionHandle>? handleSnapshot,
        ISandbox sandbox,
        string workingDirectory,
        AgentCredential? credential,
        string? modelId,
        string? reasoningMode,
        string openedAgentRouteKey,
        string? projectId,
        string? agentClassMember,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(worker);
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        AgentSessionHandle handle;
        try
        {
            handle = worker is IScopedSessionAgentRunner scoped
                ? await scoped.OpenSessionAsync(
                    new AgentSessionOpenRequest(
                        sandbox,
                        workingDirectory,
                        credential,
                        modelId,
                        reasoningMode,
                        projectId,
                        agentClassMember),
                    ct).ConfigureAwait(false)
                : await worker.OpenSessionAsync(
                    sandbox, workingDirectory, credential, modelId, reasoningMode, ct).ConfigureAwait(false);
        }
        catch
        {
            // OpenSessionAsync failed before adopting the sandbox; the
            // caller hasn't transferred ownership yet, so disposing here
            // protects against a leaked VM.
            await sandbox.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new ClaudeSessionLifecycle(
            worker,
            handleSnapshot,
            sandbox,
            handle,
            openedAgentRouteKey,
            modelId,
            reasoningMode);
    }

    /// <summary>
    /// Returns the sandbox to use for the next worker turn, resuming the
    /// VM via <see cref="ISessionAgentRunner.ResumeSessionAsync"/> when the
    /// lifecycle is currently suspended.
    /// </summary>
    public async Task<ISandbox> GetSandboxAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfClosed();
            if (_suspended)
            {
                await _worker.ResumeSessionAsync(Handle, ct).ConfigureAwait(false);
                if (_handleSnapshot is not null)
                    Handle = _handleSnapshot(Handle);
                if (AgentSessionMetadataKeys.IsFallbackToOneShot(Handle.Metadata))
                {
                    await CloseLockedAsync().ConfigureAwait(false);
                    throw new AgentSessionDegradedException(
                        "Session runner reported fallback-to-one-shot after resume; use a fresh sandbox for the next turn.");
                }
                _suspended = false;
            }
            return Sandbox;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Runs one worker turn against the session. The first call establishes
    /// the CLI session id; subsequent calls pass <c>--resume</c> through to
    /// the worker. Refreshes <see cref="Handle"/> with the latest snapshot
    /// (containing the captured CLI session id) so callers that persist the
    /// handle for restart recovery see the up-to-date form.
    /// </summary>
    public async Task<AgentResult> SendTurnAsync(
        string prompt,
        CancellationToken ct,
        Action<string>? stdoutChunkCallback)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfClosed();
            if (_suspended)
                throw new InvalidOperationException(
                    "ClaudeSessionLifecycle.SendTurnAsync called while session is suspended. " +
                    "Call GetSandboxAsync first to resume the worker VM.");

            var result = await _worker.SendTurnAsync(
                Handle, prompt, ct, stdoutChunkCallback, captureStructuredStream: true)
                .ConfigureAwait(false);

            // Snapshot the handle after every turn so persisted state (used
            // by restart recovery) always carries the latest captured CLI
            // session id and the fallback flag. The hook is null in tests
            // that don't exercise the snapshot shape — the handle then stays
            // as opened (no captured CLI session id surfaces in metadata).
            if (_handleSnapshot is not null)
                Handle = _handleSnapshot(Handle);
            TurnsCompleted++;
            _firstTurnComplete = true;
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Stops the worker VM via
    /// <see cref="ISessionAgentRunner.SuspendSessionAsync"/>. Called by
    /// PipelineRunner after each worker turn completes its post-agent git
    /// work (commit + push to the bare host repo) so the VM is OFF while
    /// the (long) audit runs in its own isolated sandbox.
    /// </summary>
    public async Task SuspendAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_closed || _suspended)
                return;
            await _worker.SuspendSessionAsync(Handle, ct).ConfigureAwait(false);
            _suspended = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Closes the underlying session, which disposes the worker VM and
    /// renders the lifecycle unusable. Idempotent. Uses
    /// <see cref="CancellationToken.None"/> internally so a host-shutdown
    /// cancellation cannot leak the VM.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await CloseLockedAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task CloseLockedAsync()
    {
        if (_closed)
            return;
        await _worker.CloseSessionAsync(Handle, CancellationToken.None).ConfigureAwait(false);
        _closed = true;
    }

    private void ThrowIfClosed()
    {
        if (_closed)
            throw new InvalidOperationException("Claude session lifecycle has been closed.");
    }

    private static string RouteKeyFor(AgentKind kind, string? agentInstanceId)
    {
        if (string.IsNullOrWhiteSpace(agentInstanceId))
            return kind.Value;

        var id = agentInstanceId.Trim();
        if (id.Contains('/', StringComparison.Ordinal)
            || string.Equals(id, kind.Value, StringComparison.OrdinalIgnoreCase))
            return id;

        return AgentInstanceIds.RouteKey(kind, id);
    }
}

internal sealed class AgentSessionDegradedException(string message) : InvalidOperationException(message);
