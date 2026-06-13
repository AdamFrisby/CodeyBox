using CodeyBox.Core;

namespace CodeyBox.Agents.Claude;

/// <summary>
/// Strategy abstraction for the per-turn command-delivery + billing channel.
/// <see cref="ClaudeSessionWorker"/> selects an implementation per session
/// based on <see cref="ClaudeSessionWorkerOptions.Transport"/> (with optional
/// per-project / per-agent-class-member overrides), and falls back to the
/// <see cref="ClaudeSessionTransport.Print"/> implementation if the configured
/// transport throws an <see cref="AcpTransportUnavailableException"/> at
/// runtime.
///
/// <para>
/// The SESSION layer (one logical, cache-warm session continued across the
/// work/rework cycle) is owned by the worker — the worker decides when to
/// open, suspend, resume, and close a logical session. The transport's job is
/// the per-turn round-trip plus any continuation handle the worker should pass
/// back on the next turn.
/// </para>
/// </summary>
public interface IClaudeTransport
{
    /// <summary>
    /// Stable identifier surfaced in <see cref="ClaudeSessionTurnMetrics.Transport"/>
    /// so per-turn metrics distinguish the two channels and downstream
    /// dashboards can confirm whether traffic actually moved off
    /// <c>claude --print</c>.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// The transport-tag value this implementation reports — exposed for
    /// preset metric assertions in tests.
    /// </summary>
    ClaudeSessionTransport Transport { get; }

    /// <summary>
    /// Open a transport-scoped session paired with the worker's logical
    /// session. Implementations that have no per-session setup may return a
    /// no-op handle. Called once per <see cref="ClaudeSessionWorker.OpenSessionAsync"/>.
    /// </summary>
    Task<IClaudeTransportSession> OpenAsync(
        ClaudeTransportOpenRequest request,
        CancellationToken ct);
}

/// <summary>
/// Per-session transport state. Disposed when the worker closes the logical
/// session. Implementations MUST be safe to dispose more than once.
/// </summary>
public interface IClaudeTransportSession : IAsyncDisposable
{
    /// <summary>
    /// Run one turn over the transport and return the resulting
    /// <see cref="AgentResult"/>. Throws
    /// <see cref="AcpTransportUnavailableException"/> when the transport
    /// cannot deliver this turn (e.g. ACP endpoint failed to start); the
    /// worker catches that and falls back to the
    /// <see cref="ClaudeSessionTransport.Print"/> transport without stranding
    /// the work item.
    /// </summary>
    Task<ClaudeTransportTurnResult> SendTurnAsync(
        ClaudeTransportTurnRequest request,
        CancellationToken ct);

    /// <summary>
    /// Optional pre-suspend hook for transport state outside the worker's
    /// sandbox lifecycle (e.g. ACP server sockets bound to the host). Default
    /// is a no-op.
    /// </summary>
    Task SuspendAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Optional post-resume hook paired with <see cref="SuspendAsync"/>.
    /// Default is a no-op.
    /// </summary>
    Task ResumeAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Optional transport-session capability used when a long-lived logical
/// session receives a freshly resolved credential before a later turn.
/// </summary>
public interface ICredentialRefreshableClaudeTransportSession : IClaudeTransportSession
{
    void RefreshCredential(AgentCredential? credential);
}

/// <summary>
/// Inputs the worker hands a transport when opening a session. Carries only
/// what the transport actually needs.
/// </summary>
public sealed record ClaudeTransportOpenRequest(
    ISandbox Sandbox,
    string WorkingDirectory,
    AgentCredential? Credential,
    string? ModelId,
    string? ReasoningMode,
    string LocalSessionId);

/// <summary>
/// Per-turn inputs.
/// </summary>
/// <param name="Prompt">User prompt for this turn.</param>
/// <param name="CliResumeSessionId">
/// Captured CLI session id from a prior turn (null on the first turn or after
/// a fallback). The transport uses this to continue the same logical session.
/// </param>
/// <param name="StdoutChunkCallback">Optional streaming hook.</param>
public sealed record ClaudeTransportTurnRequest(
    string Prompt,
    string? CliResumeSessionId,
    Action<string>? StdoutChunkCallback);

/// <summary>
/// Per-turn outputs. <see cref="CapturedCliSessionId"/> is populated by
/// transports that learn the CLI / ACP session id from the response stream;
/// the worker stamps it on the persisted handle for next-turn continuation.
/// <see cref="CombinedStdout"/> is the aggregated stream-json (or transport
/// equivalent) the metrics extractor consumes.
/// </summary>
public sealed record ClaudeTransportTurnResult(
    AgentResult Result,
    string CombinedStdout,
    string? CapturedCliSessionId);

/// <summary>
/// Raised by an <see cref="IClaudeTransport"/> when it cannot deliver a turn
/// because of transport-layer unavailability (lockfile write failed,
/// WebSocket handshake failed, peer never connected). The worker catches this
/// to degrade to <see cref="ClaudeSessionTransport.Print"/> rather than
/// stranding the work item.
/// </summary>
public sealed class AcpTransportUnavailableException : Exception
{
    public AcpTransportUnavailableException(string message) : base(message) { }
    public AcpTransportUnavailableException(string message, Exception inner) : base(message, inner) { }
}
