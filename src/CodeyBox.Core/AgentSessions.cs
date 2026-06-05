using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace CodeyBox.Core;

/// <summary>
/// Persistable reference to the sandbox/VM that owns an agent session. For VM
/// providers this ID is the provider's resumable VM name, not a process ID.
/// </summary>
public sealed record AgentSessionSandboxRef(
    string Id,
    string? Provider = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>
/// Persistable handle for a logical multi-turn agent session.
/// The handle intentionally carries only durable identifiers and non-secret
/// metadata. Live sandbox objects and credentials are owned by the runner
/// process and must be reattached/reacquired from these identifiers after an
/// orchestrator restart.
/// </summary>
/// <param name="RunnerKind">Agent runner kind that owns the handle.</param>
/// <param name="SessionId">Runner/provider session identifier, e.g. a Claude CLI resume ID.</param>
/// <param name="Sandbox">Specific sandbox/VM that must be resumed for later turns.</param>
/// <param name="WorkingDirectory">Working directory used for every turn in this session.</param>
/// <param name="ModelId">Model selected when the session was opened.</param>
/// <param name="ReasoningMode">Reasoning mode selected when the session was opened.</param>
/// <param name="Metadata">Non-secret runner metadata needed to reattach after restart.</param>
public sealed record AgentSessionHandle(
    AgentKind RunnerKind,
    string SessionId,
    AgentSessionSandboxRef Sandbox,
    string WorkingDirectory,
    string? ModelId = null,
    string? ReasoningMode = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>
/// Optional runner capability for logical sessions that span multiple turns.
/// Session-capable runners can keep model conversation context and provider
/// prompt-cache identity across work, audit, and rework phases while the
/// underlying sandbox/VM may be stopped between turns.
/// </summary>
public interface ISessionAgentRunner : IAgentRunner
{
    Task<AgentSessionHandle> OpenSessionAsync(
        ISandbox sandbox,
        string workingDirectory,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        CancellationToken ct = default);

    Task<AgentResult> SendTurnAsync(
        AgentSessionHandle sessionHandle,
        string prompt,
        CancellationToken ct = default,
        Action<string>? stdoutChunkCallback = null,
        bool captureStructuredStream = false);

    Task SuspendSessionAsync(AgentSessionHandle sessionHandle, CancellationToken ct = default);

    Task ResumeSessionAsync(AgentSessionHandle sessionHandle, CancellationToken ct = default);

    Task CloseSessionAsync(AgentSessionHandle sessionHandle, CancellationToken ct = default);
}

/// <summary>
/// Stateless compatibility adapter for runners that only implement
/// <see cref="IAgentRunner"/>. Each session turn is an ordinary one-shot
/// <see cref="IAgentRunner.RunAsync"/> invocation, so this provides no
/// provider-side cache benefit but lets non-session runners participate in the
/// session contract. Handles opened in the current process resolve through
/// adapter-private state. Hosts that need restart recovery must provide a
/// sandbox reattacher and, when needed, a credential provider; the persisted
/// handle itself never stores live objects or secret material.
/// </summary>
public sealed class StatelessSessionAgentRunner : ISessionAgentRunner
{
    private readonly IAgentRunner _inner;
    private readonly Func<ISandbox, AgentSessionSandboxRef> _sandboxRefFactory;
    private readonly Func<AgentSessionSandboxRef, CancellationToken, Task<ISandbox>>? _sandboxReattacher;
    private readonly ICredentialProvider? _credentialProvider;
    private readonly ConcurrentDictionary<string, StatelessSessionState> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _closedSessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _reattachLocks = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates a session adapter over a stateless runner.
    /// </summary>
    /// <param name="inner">The one-shot runner to invoke for each session turn.</param>
    /// <param name="sandboxReattacher">
    /// Optional restart-recovery hook that maps the durable sandbox reference
    /// back to a live sandbox object. Without it, inactive/deserialized handles
    /// are rejected explicitly.
    /// </param>
    /// <param name="credentialProvider">
    /// Optional provider for reacquiring credentials after restart. When omitted,
    /// reattached turns run with a null credential, matching image-baked auth
    /// scenarios.
    /// </param>
    /// <param name="sandboxRefFactory">
    /// Optional hook for producing provider-specific durable sandbox references
    /// from the live sandbox supplied to <see cref="OpenSessionAsync"/>.
    /// </param>
    public StatelessSessionAgentRunner(
        IAgentRunner inner,
        Func<AgentSessionSandboxRef, CancellationToken, Task<ISandbox>>? sandboxReattacher = null,
        ICredentialProvider? credentialProvider = null,
        Func<ISandbox, AgentSessionSandboxRef>? sandboxRefFactory = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _sandboxReattacher = sandboxReattacher;
        _credentialProvider = credentialProvider;
        _sandboxRefFactory = sandboxRefFactory ?? (static sandbox => new AgentSessionSandboxRef(sandbox.Id));
    }

    public AgentKind Kind => _inner.Kind;

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
        => _inner.RunAsync(
            sandbox,
            workingDirectory,
            prompt,
            credential,
            modelId,
            reasoningMode,
            ct,
            stdoutChunkCallback,
            captureStructuredStream);

    public AgentFailureClassification ClassifyFailure(AgentResult result)
        => _inner.ClassifyFailure(result);

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

        var sessionId = $"stateless-{Kind.Value}-{Guid.NewGuid():N}";
        var sandboxRef = _sandboxRefFactory(sandbox);
        if (string.IsNullOrWhiteSpace(sandboxRef.Id))
            throw new InvalidOperationException("Session sandbox references must include a non-blank id.");

        var handle = new AgentSessionHandle(
            Kind,
            sessionId,
            sandboxRef,
            workingDirectory,
            modelId,
            reasoningMode,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["mode"] = "stateless",
            });

        _sessions[sessionId] = new StatelessSessionState(sandbox, credential);
        return Task.FromResult(handle);
    }

    public async Task<AgentResult> SendTurnAsync(
        AgentSessionHandle sessionHandle,
        string prompt,
        CancellationToken ct = default,
        Action<string>? stdoutChunkCallback = null,
        bool captureStructuredStream = false)
    {
        ArgumentNullException.ThrowIfNull(sessionHandle);
        ArgumentNullException.ThrowIfNull(prompt);
        ct.ThrowIfCancellationRequested();

        var state = await ResolveStateAsync(sessionHandle, ct);
        await state.Gate.WaitAsync(ct);
        try
        {
            ThrowIfClosed(state);
            if (state.Suspended)
                throw new InvalidOperationException("Cannot send an agent turn while the session is suspended.");

            return await _inner.RunAsync(
                state.Sandbox,
                sessionHandle.WorkingDirectory,
                prompt,
                state.Credential,
                sessionHandle.ModelId,
                sessionHandle.ReasoningMode,
                ct,
                stdoutChunkCallback,
                captureStructuredStream);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    public async Task SuspendSessionAsync(AgentSessionHandle sessionHandle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessionHandle);
        EnsureKind(sessionHandle);
        ct.ThrowIfCancellationRequested();
        var state = await ResolveStateAsync(sessionHandle, ct);
        await state.Gate.WaitAsync(ct);
        try
        {
            ThrowIfClosed(state);
            state.Suspended = true;
        }
        finally
        {
            state.Gate.Release();
        }
    }

    public async Task ResumeSessionAsync(AgentSessionHandle sessionHandle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessionHandle);
        EnsureKind(sessionHandle);
        ct.ThrowIfCancellationRequested();
        var state = await ResolveStateAsync(sessionHandle, ct);
        await state.Gate.WaitAsync(ct);
        try
        {
            ThrowIfClosed(state);
            state.Suspended = false;
        }
        finally
        {
            state.Gate.Release();
        }
    }

    public async Task CloseSessionAsync(AgentSessionHandle sessionHandle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessionHandle);
        ct.ThrowIfCancellationRequested();

        EnsureKind(sessionHandle);
        var state = await ResolveStateAsync(sessionHandle, ct);
        await state.Gate.WaitAsync(ct);
        try
        {
            ThrowIfClosed(state);
            await state.Sandbox.DisposeAsync();

            state.Closed = true;
            _sessions.TryRemove(sessionHandle.SessionId, out _);
            _closedSessions[sessionHandle.SessionId] = 0;
            _reattachLocks.TryRemove(sessionHandle.SessionId, out _);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private async Task<StatelessSessionState> ResolveStateAsync(
        AgentSessionHandle sessionHandle,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sessionHandle);
        EnsureKind(sessionHandle);
        ThrowIfClosed(sessionHandle);

        if (_sessions.TryGetValue(sessionHandle.SessionId, out var state))
            return state;

        if (_sandboxReattacher is null)
        {
            throw new InvalidOperationException(
                "This stateless session is not active in the current process and no sandbox reattacher was configured. Reopen a session or configure a runner that can reattach from the persisted session handle.");
        }

        var gate = _reattachLocks.GetOrAdd(sessionHandle.SessionId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            ThrowIfClosed(sessionHandle);
            if (_sessions.TryGetValue(sessionHandle.SessionId, out state))
                return state;

            ISandbox? sandbox = null;
            try
            {
                sandbox = await _sandboxReattacher(sessionHandle.Sandbox, ct);
                if (sandbox is null)
                    throw new InvalidOperationException("The configured sandbox reattacher returned null.");

                var credential = _credentialProvider is null
                    ? null
                    : await _credentialProvider.GetAsync(sessionHandle.RunnerKind, ct);
                if (credential is not null && credential.Agent != sessionHandle.RunnerKind)
                    throw new InvalidOperationException(
                        $"Credential provider returned credentials for '{credential.Agent}', not '{sessionHandle.RunnerKind}'.");

                state = new StatelessSessionState(sandbox, credential);
                _sessions[sessionHandle.SessionId] = state;
                sandbox = null;
                return state;
            }
            finally
            {
                if (sandbox is not null)
                    await sandbox.DisposeAsync();
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

    private static void ThrowIfClosed(StatelessSessionState state)
    {
        if (state.Closed)
            throw new InvalidOperationException("This agent session has already been closed.");
    }

    private sealed class StatelessSessionState(ISandbox sandbox, AgentCredential? credential)
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public ISandbox Sandbox { get; } = sandbox;
        public AgentCredential? Credential { get; } = credential;
        public bool Suspended { get; set; }
        public bool Closed { get; set; }
    }
}

public static class AgentSessionRunnerExtensions
{
    private static readonly ConditionalWeakTable<IAgentRunner, ISessionAgentRunner> StatelessAdapters = new();

    public static ISessionAgentRunner AsSessionRunner(this IAgentRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        return runner as ISessionAgentRunner
            ?? StatelessAdapters.GetValue(runner, static inner => new StatelessSessionAgentRunner(inner));
    }
}
