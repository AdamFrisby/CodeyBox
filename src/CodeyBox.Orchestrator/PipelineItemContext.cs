using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Projects;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Per-item pipeline state carried across phase helpers.
///
/// Introduced as Phase 0 prep for the pipeline split: several cores (notably
/// the pickup/incremental rebase path) take near-identical long parameter
/// lists — <c>(WorkItem, Project, runner, repoId, baseBranch, workBranch,
/// CancellationToken)</c> — that grow with every extracted collaborator.
/// Threading one immutable context instead keeps new signatures stable while
/// the split proceeds. The type is deliberately narrow: identity + routing +
/// skip/preempt flags + accumulated phase outputs. It never carries
/// infrastructure clients (sandboxes, stores, loggers); those stay on the
/// runner/collaborator that owns them.
/// </summary>
internal sealed record PipelineItemContext
{
    public required WorkItem Item { get; init; }
    public required Project Project { get; init; }
    public AgentKind AgentKind { get; init; }
    public IAgentRunner? Runner { get; init; }
    public string? RepoId { get; init; }
    public string? BaseBranch { get; init; }
    public string? WorkBranch { get; init; }
    public bool SkipWork { get; init; }
    public bool SkipAudit { get; init; }
    public bool SkipMerge { get; init; }
    public bool PreemptRequested { get; init; }
    public string? ResumeCheckpoint { get; init; }
    public string? MergeSha { get; init; }
    public string? AgentStdout { get; init; }

    /// <summary>
    /// Creates the base context every phase starts from: item + project +
    /// resolved agent kind. Callers extend it with <c>with</c> expressions as
    /// phases resolve more state (runner, branches, outputs).
    /// </summary>
    public static PipelineItemContext Create(WorkItem item, Project project)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(project);
        return new PipelineItemContext
        {
            Item = item,
            Project = project,
            AgentKind = item.Agent ?? project.DefaultAgent,
        };
    }

    public PipelineItemContext WithRunner(IAgentRunner runner) =>
        this with { Runner = runner };

    public PipelineItemContext WithBranches(string repoId, string baseBranch, string workBranch) =>
        this with
        {
            RepoId = repoId,
            BaseBranch = baseBranch,
            WorkBranch = workBranch,
        };
}
