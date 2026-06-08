using CodeyBox.Agents;
using CodeyBox.Agents.Claude;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Drives the resumable-Claude worker's lifecycle across one work item's
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

    private ClaudeSessionLifecycle(
        ISessionAgentRunner worker,
        Func<AgentSessionHandle, AgentSessionHandle>? handleSnapshot,
        ISandbox sandbox,
        AgentSessionHandle handle)
    {
        _worker = worker;
        _handleSnapshot = handleSnapshot;
        Sandbox = sandbox;
        Handle = handle;
    }

    /// <summary>
    /// The live worker sandbox. The same instance is returned for every turn
    /// of the same lifecycle, even after a suspend/resume cycle — the
    /// underlying <see cref="ClaudeSessionWorker"/> performs the resume via
    /// its configured <c>sandboxResumeHook</c> (<c>multipass start &lt;vm&gt;</c>
    /// in production).
    /// </summary>
    public ISandbox Sandbox { get; }

    /// <summary>
    /// The session handle returned by
    /// <see cref="ClaudeSessionWorker.OpenSessionAsync"/>. Carries the
    /// runner kind, a durable sandbox reference, and (after the first turn)
    /// the captured CLI session id under
    /// <see cref="ClaudeSessionWorker.CliSessionIdMetadataKey"/>.
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
    /// CLI session id captured from the first turn's stream-json
    /// <c>system/init</c> event, or null when the worker has fallen back to
    /// fresh-one-shot mode. Exposed for tests that assert session-id
    /// continuity across work/rework turns.
    /// </summary>
    public string? CliSessionId
    {
        get
        {
            if (Handle.Metadata is null)
                return null;
            return Handle.Metadata.TryGetValue(ClaudeSessionWorker.CliSessionIdMetadataKey, out var v) ? v : null;
        }
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
    /// disposes it via <see cref="ClaudeSessionWorker.CloseSessionAsync"/>.
    /// </summary>
    public static Task<ClaudeSessionLifecycle> OpenAsync(
        ClaudeSessionWorker worker,
        ISandbox sandbox,
        string workingDirectory,
        AgentCredential? credential,
        string? modelId,
        string? reasoningMode,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(worker);
        return OpenAsync(
            (ISessionAgentRunner)worker,
            worker.SnapshotPersistedHandle,
            sandbox,
            workingDirectory,
            credential,
            modelId,
            reasoningMode,
            ct);
    }

    /// <summary>
    /// Test-facing overload taking the worker as <see cref="ISessionAgentRunner"/>
    /// so unit tests can supply a fake without spinning up the real Claude CLI
    /// machinery. <paramref name="handleSnapshot"/> is the production
    /// <c>SnapshotPersistedHandle</c> hook (null in tests that don't exercise
    /// the persistence shape).
    /// </summary>
    internal static async Task<ClaudeSessionLifecycle> OpenAsync(
        ISessionAgentRunner worker,
        Func<AgentSessionHandle, AgentSessionHandle>? handleSnapshot,
        ISandbox sandbox,
        string workingDirectory,
        AgentCredential? credential,
        string? modelId,
        string? reasoningMode,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(worker);
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        AgentSessionHandle handle;
        try
        {
            handle = await worker.OpenSessionAsync(
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

        return new ClaudeSessionLifecycle(worker, handleSnapshot, sandbox, handle);
    }

    /// <summary>
    /// Returns the sandbox to use for the next worker turn, resuming the
    /// VM via <see cref="ClaudeSessionWorker.ResumeSessionAsync"/> when the
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
    /// <see cref="ClaudeSessionWorker.SuspendSessionAsync"/>. Called by
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
            if (_closed)
                return;
            _closed = true;
            try
            {
                await _worker.CloseSessionAsync(Handle, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Disposal must never throw — the work item's terminal-
                // transition path is the surface that surfaces the real
                // error and would re-throw a more useful summary anyway.
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ThrowIfClosed()
    {
        if (_closed)
            throw new InvalidOperationException("Claude session lifecycle has been closed.");
    }
}
