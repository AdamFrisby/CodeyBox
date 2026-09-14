namespace CodeyBox.Core;

/// <summary>
/// Transport failure moving a phase to or from an executor host: the host was
/// unreachable, authentication failed, the connection dropped mid-transfer,
/// or no transport is configured for the selected host. The phase itself did
/// not fail — the caller must retry elsewhere (another host or the existing
/// pipeline recovery path) without charging the work item with an agent
/// failure or consuming a rework iteration. Carries host and operation only.
/// </summary>
public sealed class ExecutorPhaseTransportException : Exception
{
    public ExecutorPhaseTransportException(string hostId, string operation, string message)
        : base($"Executor phase {operation} failed on host '{hostId}': {message}")
    {
        HostId = hostId;
        Operation = operation;
    }

    public ExecutorPhaseTransportException(string hostId, string operation, string message, Exception inner)
        : base($"Executor phase {operation} failed on host '{hostId}': {message}", inner)
    {
        HostId = hostId;
        Operation = operation;
    }

    public string HostId { get; }
    public string Operation { get; }
}

/// <summary>
/// The phase ran (or was answered) but cannot be accepted: the staged-back
/// payload exceeded its bounds, failed validation, or the executor's result
/// was malformed. Distinct from <see cref="ExecutorPhaseTransportException"/>:
/// the host was reachable, so retrying on another host with the same payload
/// shape may legitimately fail the same way. The orchestrator's bare repo is
/// never written when this is thrown, and no idempotency record is stored so
/// a corrected redelivery can still execute.
/// </summary>
public sealed class ExecutorPhaseException : Exception
{
    public ExecutorPhaseException(string message)
        : base(message)
    {
    }

    public ExecutorPhaseException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Idempotency conflict: the dispatch key (work item + phase + attempt)
/// already delivered a result for a DIFFERENT request body. The redelivery
/// must not execute — something is dispatching inconsistent inputs under one
/// attempt. Carries the key only, never request bodies.
/// </summary>
public sealed class ExecutorPhaseConflictException : Exception
{
    public ExecutorPhaseConflictException(string dispatchKey)
        : base($"Executor phase dispatch key '{dispatchKey}' was already used with a different request body; refusing to execute.")
    {
        DispatchKey = dispatchKey;
    }

    public string DispatchKey { get; }
}

/// <summary>
/// Dispatch-only channel to one executor host. Carries the phase request and
/// the per-item bare repo to the host and back; it persists no queue state.
/// The work item table remains the queue of record — re-dispatch after
/// failure is driven by the existing pipeline state machine and recovery
/// paths, which call back into the dispatch proxy with a new attempt.
///
/// <para>All methods throw <see cref="ExecutorPhaseTransportException"/> on
/// transport failure. Implementations must stage exactly the repo path they
/// are given — never the whole repos root — so an executor receives only the
/// repo for the item it is running. Exception messages must carry host and
/// operation only: never key material, request bodies, or raw remote output.</para>
/// </summary>
public interface IExecutorPhaseTransport
{
    /// <summary>Stable executor host id this transport talks to.</summary>
    string HostId { get; }

    /// <summary>
    /// Copies the host-local bare repo at <paramref name="hostRepoPath"/> to
    /// the executor. Called with exactly one per-item repo path per dispatch.
    /// </summary>
    Task StageInAsync(string hostRepoPath, CancellationToken ct);

    /// <summary>
    /// Runs the phase on the executor against its staged repo copy and
    /// returns the phase result. An agent failure on the executor is returned
    /// as a result with <see cref="ExecutorPhaseOutcome.AgentFailed"/>, not
    /// thrown — only transport failures throw.
    /// </summary>
    Task<ExecutorPhaseResult> RunPhaseAsync(ExecutorPhaseRequest request, CancellationToken ct);

    /// <summary>
    /// Writes the executor-side repo back to a host-local tar archive at
    /// <paramref name="hostArchivePath"/>. The caller validates the archive
    /// (size, entry count, expansion ratio, path containment) before anything
    /// is extracted over the orchestrator's bare repo.
    ///
    /// <para>The transport MUST enforce <paramref name="maxArchiveBytes"/>
    /// while receiving — aborting the transfer as soon as the cap is
    /// exceeded — rather than buffering an unbounded payload and reporting
    /// its size afterwards. The executor is untrusted, so a post-write size
    /// check alone lets a compromised executor fill the orchestrator disk
    /// before validation rejects the payload. Exceeding the cap throws
    /// <see cref="ExecutorPhaseException"/> (a phase failure: the host was
    /// reachable, the payload was hostile), never a transport exception, and
    /// must leave no usable archive behind at
    /// <paramref name="hostArchivePath"/>.</para>
    /// </summary>
    Task StageOutToArchiveAsync(string hostArchivePath, long maxArchiveBytes, CancellationToken ct);
}

/// <summary>
/// Resolves the dispatch transport for a registered executor host. Returns
/// null when the host has no transport configured (for example no SSH target
/// is mapped for it); the proxy treats that as a transport failure rather
/// than silently running the phase elsewhere.
/// </summary>
public interface IExecutorPhaseTransportFactory
{
    Task<IExecutorPhaseTransport?> ResolveAsync(string hostId, CancellationToken ct);
}
