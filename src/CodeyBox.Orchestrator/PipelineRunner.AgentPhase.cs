using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

public sealed partial class PipelineRunner
{
    private readonly IAgentPhaseExecutor _phaseExecutor;

    /// <summary>
    /// The phase-execution seam. Production runs phases in-process through
    /// <see cref="AgentPhaseExecutor"/>; tests substitute a double to drive
    /// orchestration without a sandbox provider.
    /// </summary>
    internal IAgentPhaseExecutor PhaseExecutor => _phaseExecutor;

    private static RequiredBuildPolicy ToRequiredBuildPolicy(AgentPhaseBuildPolicy policy) =>
        policy switch
        {
            AgentPhaseBuildPolicy.Terminal => RequiredBuildPolicy.Terminal,
            AgentPhaseBuildPolicy.DeferToAuditLoop => RequiredBuildPolicy.DeferToAuditLoop,
            _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, "Unknown agent-phase build policy."),
        };

    private static ReworkNoDiffHandling ToReworkNoDiffHandling(AgentPhaseReworkNoDiffHandling handling) =>
        handling switch
        {
            AgentPhaseReworkNoDiffHandling.TerminalError => ReworkNoDiffHandling.TerminalError,
            AgentPhaseReworkNoDiffHandling.AuditEmptyRework => ReworkNoDiffHandling.AuditEmptyRework,
            _ => throw new ArgumentOutOfRangeException(nameof(handling), handling, "Unknown agent-phase rework no-diff handling."),
        };

    private async Task<AgentPhaseResult> ExecuteWorkReworkPhaseAsync(
        AgentPhaseRequest request,
        bool isInitial,
        CancellationToken ct,
        CancellationToken hostShutdownToken)
    {
        if (request.Phase is not (AgentPhaseKind.Work or AgentPhaseKind.Rework))
            throw new ArgumentException(
                $"Work/rework execution requires phase Work or Rework, got {request.Phase}.",
                nameof(request));
        var prompt = request.Prompt
            ?? throw new ArgumentException("Prompt is required for work/rework phases.", nameof(request));
        var buildPolicy = request.BuildPolicy
            ?? throw new ArgumentException("BuildPolicy is required for work/rework phases.", nameof(request));
        Validation.ValidateBranchName(request.BaseBranch, nameof(request));
        Validation.ValidateBranchName(request.Branch, nameof(request));

        var (stdout, streamFileName) = await RunAgentPhaseAsync(
            request.Item,
            request.Runner,
            request.RepositoryId,
            request.BaseBranch,
            request.Branch,
            prompt,
            isInitial,
            request.NetworkProfile,
            request.SandboxFlavor,
            request.Project,
            ct,
            hostShutdownToken,
            ToRequiredBuildPolicy(buildPolicy),
            request.Iteration,
            request.AuditorsForPreemptiveSelfReview,
            ToReworkNoDiffHandling(request.ReworkNoDiffHandling),
            request.ResumePreTurnCommitSha,
            request.SuppressNoChangesBreaker);

        var resultingSha = await _gitHost.ResolveCommitAsync(request.RepositoryId, request.Branch, ct);
        return new AgentPhaseResult
        {
            Phase = request.Phase,
            Outcome = AgentPhaseOutcome.Completed,
            ResultingCommitSha = resultingSha,
            AgentStdout = stdout,
            AgentStreamFileName = streamFileName,
        };
    }

    private async Task<AgentPhaseResult> ExecuteMergePhaseAsync(
        AgentPhaseRequest request,
        CancellationToken ct,
        CancellationToken hostShutdownToken)
    {
        if (request.Phase != AgentPhaseKind.Merge)
            throw new ArgumentException(
                $"Merge execution requires phase Merge, got {request.Phase}.",
                nameof(request));
        Validation.ValidateBranchName(request.BaseBranch, nameof(request));
        Validation.ValidateBranchName(request.Branch, nameof(request));

        var (mergeSha, stdout) = await RunAgentMergePhaseAsync(
            request.Item,
            request.Runner,
            request.RepositoryId,
            request.BaseBranch,
            request.Branch,
            request.NetworkProfile,
            request.Project,
            ct,
            hostShutdownToken);
        return new AgentPhaseResult
        {
            Phase = request.Phase,
            Outcome = AgentPhaseOutcome.Completed,
            ResultingCommitSha = mergeSha,
            AgentStdout = stdout,
        };
    }

    /// <summary>
    /// In-process <see cref="IAgentPhaseExecutor"/>: exactly the current
    /// behaviour, invoked behind the seam. Work maps to the initial
    /// phase, rework to the non-initial phase; merge keeps its own path.
    /// </summary>
    private sealed class AgentPhaseExecutor(PipelineRunner runner) : IAgentPhaseExecutor
    {
        public Task<AgentPhaseResult> ExecuteAsync(
            AgentPhaseRequest request,
            CancellationToken ct,
            CancellationToken hostShutdownToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            return request.Phase switch
            {
                AgentPhaseKind.Work => runner.ExecuteWorkReworkPhaseAsync(
                    request, isInitial: true, ct, hostShutdownToken),
                AgentPhaseKind.Rework => runner.ExecuteWorkReworkPhaseAsync(
                    request, isInitial: false, ct, hostShutdownToken),
                AgentPhaseKind.Merge => runner.ExecuteMergePhaseAsync(
                    request, ct, hostShutdownToken),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(request), request.Phase, "Unknown agent phase."),
            };
        }
    }
}
