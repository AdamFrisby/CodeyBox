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
