using System.Collections.Concurrent;
using System.Text.Json.Serialization;

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
    IReadOnlyDictionary<string, string>? Metadata = null)
{
    /// <summary>
    /// Live sandbox object for in-process adapters. This is deliberately not
    /// serialized; restart recovery must reattach from <see cref="Sandbox"/>.
    /// </summary>
    [JsonIgnore]
    public ISandbox? RuntimeSandbox { get; init; }

    /// <summary>
    /// Live credential for in-process adapters. Credentials must be reacquired
    /// after restart rather than serialized into a persisted handle.
    /// </summary>
    [JsonIgnore]
    public AgentCredential? RuntimeCredential { get; init; }
}

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
/// session contract.
/// </summary>
public sealed class StatelessSessionAgentRunner : ISessionAgentRunner
{
    private readonly IAgentRunner _inner;
    private readonly ConcurrentDictionary<string, StatelessSessionState> _sessions = new(StringComparer.Ordinal);

    public StatelessSessionAgentRunner(IAgentRunner inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
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
        var handle = new AgentSessionHandle(
            Kind,
            sessionId,
            new AgentSessionSandboxRef(sandbox.Id),
            workingDirectory,
            modelId,
            reasoningMode,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["mode"] = "stateless",
            })
        {
            RuntimeSandbox = sandbox,
            RuntimeCredential = credential,
        };

        _sessions[sessionId] = new StatelessSessionState(sandbox, credential);
        return Task.FromResult(handle);
    }

    public Task<AgentResult> SendTurnAsync(
        AgentSessionHandle sessionHandle,
        string prompt,
        CancellationToken ct = default,
        Action<string>? stdoutChunkCallback = null,
        bool captureStructuredStream = false)
    {
        ArgumentNullException.ThrowIfNull(sessionHandle);
        ArgumentNullException.ThrowIfNull(prompt);
        ct.ThrowIfCancellationRequested();

        var state = ResolveState(sessionHandle);
        lock (state.Gate)
        {
            if (state.Suspended)
                throw new InvalidOperationException("Cannot send an agent turn while the session is suspended.");
        }

        return _inner.RunAsync(
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

    public Task SuspendSessionAsync(AgentSessionHandle sessionHandle, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var state = ResolveState(sessionHandle);
        lock (state.Gate)
        {
            state.Suspended = true;
        }

        return Task.CompletedTask;
    }

    public Task ResumeSessionAsync(AgentSessionHandle sessionHandle, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var state = ResolveState(sessionHandle);
        lock (state.Gate)
        {
            state.Suspended = false;
        }

        return Task.CompletedTask;
    }

    public async Task CloseSessionAsync(AgentSessionHandle sessionHandle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessionHandle);
        ct.ThrowIfCancellationRequested();

        EnsureKind(sessionHandle);
        if (!_sessions.TryRemove(sessionHandle.SessionId, out var state))
        {
            if (sessionHandle.RuntimeSandbox is null)
                return;
            state = new StatelessSessionState(sessionHandle.RuntimeSandbox, sessionHandle.RuntimeCredential);
        }

        await state.Sandbox.DisposeAsync();
    }

    private StatelessSessionState ResolveState(AgentSessionHandle sessionHandle)
    {
        ArgumentNullException.ThrowIfNull(sessionHandle);
        EnsureKind(sessionHandle);

        if (_sessions.TryGetValue(sessionHandle.SessionId, out var state))
            return state;

        if (sessionHandle.RuntimeSandbox is not null)
        {
            state = new StatelessSessionState(sessionHandle.RuntimeSandbox, sessionHandle.RuntimeCredential);
            _sessions.TryAdd(sessionHandle.SessionId, state);
            return state;
        }

        throw new InvalidOperationException(
            "This stateless session is not active in the current process. Reopen a session or use a runner that can reattach from the persisted session handle.");
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

    private sealed class StatelessSessionState(ISandbox sandbox, AgentCredential? credential)
    {
        public object Gate { get; } = new();
        public ISandbox Sandbox { get; } = sandbox;
        public AgentCredential? Credential { get; } = credential;
        public bool Suspended { get; set; }
    }
}

public static class AgentSessionRunnerExtensions
{
    public static ISessionAgentRunner AsSessionRunner(this IAgentRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        return runner as ISessionAgentRunner ?? new StatelessSessionAgentRunner(runner);
    }
}
