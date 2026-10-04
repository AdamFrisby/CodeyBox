using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Executes one dispatched phase against a staged bare repo inside an
/// already-provisioned sandbox. The colocated host calls this against its
/// staged copy; a remote executor runs the same logic against its own staged
/// copy, which is what makes a colocated dispatch return an outcome
/// equivalent to a remote one. The caller owns the sandbox
/// lifecycle (provision, track, tear down); the handler owns the phase's
/// agent work: it clones or reads the staged repo at
/// <paramref name="repoPath"/>, works inside <paramref name="sandbox"/>, and
/// returns the phase result. The handler must not dispose
/// <paramref name="sandbox"/>.
/// </summary>
public interface IExecutorPhaseHandler
{
    Task<ExecutorPhaseResult> ExecuteAsync(
        ExecutorPhaseRequest request,
        string repoPath,
        ISandbox sandbox,
        CancellationToken ct);
}

/// <summary>
/// Optional streaming extension to <see cref="IExecutorPhaseHandler"/>: a
/// handler implementing this interface emits live agent-output chunks to
/// <paramref name="onChunk"/> as they are produced, and the executor-side
/// runner forwards them to the orchestrator while the phase runs — over the
/// colocated transport's in-process callback for the <c>"local"</c> host and
/// over the executor's existing outbound channel (poll-driven chunk posts,
/// never an inbound port) for remote hosts. Implementations MUST NOT buffer
/// to phase end: the orchestrator feeds chunks into the live capture and the
/// supervision hub in real time. A null callback behaves exactly like the
/// non-streaming overload. The callback never fails the phase: a throwing
/// callback is swallowed executor-side and only degrades observability.
/// </summary>
public interface IStreamingExecutorPhaseHandler : IExecutorPhaseHandler
{
    Task<ExecutorPhaseResult> ExecuteAsync(
        ExecutorPhaseRequest request,
        string repoPath,
        ISandbox sandbox,
        Func<ExecutorStreamChunk, CancellationToken, Task>? onChunk,
        CancellationToken ct);
}

/// <summary>
/// Phase-execution interface for one work-item phase. Implemented by
/// <see cref="ExecutorHostPhaseRunner"/> (runs the phase on an executor host
/// against its staged copy — including the colocated in-process host) and
/// <see cref="ExecutorPhaseProxy"/> (dispatches to a registered executor
/// host through the executor path; there is no in-process fallback).
/// </summary>
public interface IExecutorPhaseRunner
{
    Task<ExecutorPhaseResult> ExecutePhaseAsync(ExecutorPhaseRequest request, CancellationToken ct);
}
