using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// One phase assignment received over the executor's outbound channel: the
/// dispatch key, the phase request envelope, and the tar root name the
/// executor must reuse when tarring the staged copy back (the
/// orchestrator-side stage-out validator requires the archive root to be the
/// orchestrator repo basename).
/// </summary>
public sealed record ExecutorPendingPhase(
    string DispatchKey,
    ExecutorPhaseRequest Request,
    string RepoRootName);

/// <summary>
/// The executor's outbound-only view of remote phase dispatch. Every
/// operation is executor-initiated — poll for work, download the stage-in
/// tar, post live stream chunks, report infrastructure failure, complete
/// with the result plus the stage-out tar — so an executor behind NAT or a
/// host firewall needs no inbound port. The orchestrator never dials the
/// executor; liveness reuses the existing heartbeat/dead-worker path.
/// </summary>
public interface IExecutorPhaseChannel
{
    /// <summary>
    /// Executor-initiated poll: returns the next pending dispatch for this
    /// host, waiting up to <paramref name="wait"/> for one to arrive.
    /// Returns null on timeout.
    /// </summary>
    Task<ExecutorPendingPhase?> PollAsync(TimeSpan wait, CancellationToken ct);

    /// <summary>
    /// Downloads the orchestrator-written stage-in tar for
    /// <paramref name="dispatchKey"/> to <paramref name="destinationTarPath"/>,
    /// enforcing the archive cap while receiving. Only the dispatch's
    /// assigned host may download it.
    /// </summary>
    Task DownloadStageInAsync(string dispatchKey, string destinationTarPath, CancellationToken ct);

    /// <summary>
    /// Posts one live agent-output chunk for <paramref name="dispatchKey"/>.
    /// The orchestrator relays it incrementally to the capture and the live
    /// hub as it arrives — implementations MUST send each chunk as it is
    /// produced, never buffered to phase end.
    /// </summary>
    Task PostChunkAsync(string dispatchKey, ExecutorStreamChunk chunk, CancellationToken ct);

    /// <summary>
    /// Reports an executor-side infrastructure failure for
    /// <paramref name="dispatchKey"/> (nothing staged, provisioning or
    /// sandbox loss): the orchestrator fails the dispatch as a
    /// host-attributed transport failure — retried elsewhere, never charged
    /// to the work item. An agent failure is a <i>result</i> reported through
    /// <see cref="CompleteAsync"/>, and an executor-detected unacceptable
    /// payload is a phase failure reported through
    /// <see cref="FailPhaseAsync"/>; never this path.
    /// </summary>
    Task FailAsync(string dispatchKey, string message, CancellationToken ct);

    /// <summary>
    /// Reports an executor-detected phase failure for
    /// <paramref name="dispatchKey"/> (for example the stage-out tar exceeded
    /// the archive cap): the orchestrator fails the dispatch as an
    /// <see cref="ExecutorPhaseException"/> — the host was reachable, so no
    /// failover, no repo write, nothing cached — exactly like the
    /// orchestrator-side stage-out validator rejecting a landed archive.
    /// </summary>
    Task FailPhaseAsync(string dispatchKey, string message, CancellationToken ct);

    /// <summary>
    /// Completes <paramref name="dispatchKey"/> with the phase result and the
    /// executor-written stage-out tar at <paramref name="stageOutTarPath"/>.
    /// </summary>
    Task CompleteAsync(string dispatchKey, ExecutorPhaseResult result, string stageOutTarPath, CancellationToken ct);
}
