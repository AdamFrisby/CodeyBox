namespace CodeyBox.Core;

/// <summary>
/// A unit of work to be performed by an agent inside a sandbox. Bound to a
/// <see cref="Project"/> by <see cref="ProjectId"/>; per-project config
/// (repository URL, upstream, auditors, default agent, default branch) is
/// resolved at pipeline time.
///
/// Per-item fields (Title, Prompt) describe the task; per-item overrides
/// (Agent, BaseBranch, WorkBranch, PushUpstream) win over project defaults
/// when set, otherwise inherit from the project.
///
/// Immutable; state transitions produce new instances via <see cref="With"/>.
/// </summary>
public sealed record WorkItem
{
    public required WorkItemId Id { get; init; }

    /// <summary>The project this work item belongs to.</summary>
    public required ProjectId ProjectId { get; init; }

    /// <summary>Human-readable label for logs and the API.</summary>
    public required string Title { get; init; }

    /// <summary>The natural-language task to give to the agent.</summary>
    public required string Prompt { get; init; }

    /// <summary>If set, overrides the project's default base branch.</summary>
    public string? BaseBranch { get; init; }

    /// <summary>Branch the agent pushes its work to. Generated if null.</summary>
    public string? WorkBranch { get; init; }

    /// <summary>If set, overrides the project's default agent.</summary>
    public AgentKind? Agent { get; init; }

    /// <summary>Wall-clock budget for the work phase.</summary>
    public TimeSpan WorkTimeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Wall-clock budget for the merge phase.</summary>
    public TimeSpan MergeTimeout { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>If true and the project has an upstream, push to it after merge.</summary>
    public bool PushUpstream { get; init; } = true;

    public WorkItemState State { get; init; } = WorkItemState.Queued;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Last error message if state is Failed.</summary>
    public string? LastError { get; init; }

    /// <summary>Number of attempts that have been made on the upstream-push phase.</summary>
    public int UpstreamPushAttempts { get; init; }

    /// <summary>
    /// IDs of work items this item depends on. The orchestrator will not pick
    /// this item up until every dependency has reached a terminal state
    /// (Done, Failed, AuditFailed, or Cancelled). Immutable after creation.
    /// </summary>
    public IReadOnlyList<WorkItemId> DependsOn { get; init; } = [];

    /// <summary>
    /// If set, the orchestrator routes this item via the named <see cref="AgentClass"/>
    /// instead of using <see cref="Agent"/> directly. Quota is probed across class
    /// members in preference order; exhausted subscription members fall back to peers.
    /// When null, falls back to <see cref="Project.DefaultAgentClass"/> and then to
    /// direct <see cref="Agent"/> pick (no quota probe, identical to legacy behaviour).
    /// </summary>
    public string? AgentClassId { get; init; }

    public WorkItem With(WorkItemState state, string? error = null) => this with
    {
        State = state,
        LastError = error,
        UpdatedAt = DateTimeOffset.UtcNow,
    };
}
