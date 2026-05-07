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

    /// <summary>
    /// Why the item was cancelled. Only populated when <see cref="State"/> is
    /// <see cref="WorkItemState.Cancelled"/>; null for all other states and for
    /// legacy rows written before this column existed.
    /// </summary>
    public WorkItemCancellationReason? CancellationReason { get; init; }

    /// <summary>
    /// How many times the recovery loop has reset this item from a mid-flight
    /// state back to a recoverable state after successive host shutdowns. When
    /// this reaches <c>OrchestratorOptions.MaxRecoveryAttempts</c> the item is
    /// transitioned to <see cref="WorkItemState.AbandonedAfterRecoveryAttempts"/>
    /// instead of being re-queued.
    /// </summary>
    public int RecoveryAttempts { get; init; }

    /// <summary>Number of attempts that have been made on the upstream-push phase.</summary>
    public int UpstreamPushAttempts { get; init; }

    /// <summary>
    /// Number of times this work item has been automatically re-queued after
    /// stuck-agent detection. Counts only auto-retries triggered by the stuck
    /// probe, not manual retries via the API.
    /// </summary>
    public int StuckRetries { get; init; }

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

    /// <summary>
    /// Runtime-only model override set by the quota router when a class member specifies
    /// a ModelId. Not persisted; resolved fresh at each pickup from the chosen
    /// <see cref="AgentMembership"/>. Passed to the agent CLI as <c>--model &lt;ModelId&gt;</c>.
    /// </summary>
    public string? ModelId { get; init; }

    /// <summary>
    /// Runtime-only reasoning-mode hint set by the quota router from the chosen
    /// <see cref="AgentMembership.ReasoningMode"/>. Not persisted; resolved at
    /// each pickup. The agent runner translates this into the appropriate CLI flag.
    /// </summary>
    public string? ReasoningMode { get; init; }

    /// <summary>
    /// Minimum acceptable <see cref="AgentMembership.QualityScore"/> for this work item.
    /// The router picks any member whose base score is at or above this floor.
    /// Default 95: Gemini-3-Flash-high-reasoning is allowed as a frontier-adjacent fallback.
    /// Set lower (e.g. 70) for low-stakes work that can tolerate a weaker model.
    /// Persisted; existing records without the column default to 95 on read.
    /// </summary>
    public int MinModelScore { get; init; } = 95;

    /// <summary>
    /// Display and pickup ordering for Queued items. Set to <c>CreatedAt.Ticks</c> on
    /// first persist so items sort in creation order by default. <see cref="IWorkItemStore.ReorderAsync"/>
    /// overwrites this with small integers (1, 2, 3 …) so explicitly prioritised items
    /// sort before timestamp-ordered items. Value 0 is treated as "sort last" by the store.
    /// </summary>
    public long QueuePosition { get; init; } = 0;

    /// <summary>
    /// UTC timestamp when this work item was first picked up by a worker
    /// (transitioned out of Queued state). Null until the worker commits to
    /// running it. Used for per-project budget window calculations.
    /// </summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>
    /// UTC timestamp when a graceful host shutdown preempted this item while an
    /// agent was running. Null for normal and crash-recovered work.
    /// </summary>
    public DateTimeOffset? PreemptedAt { get; init; }

    /// <summary>
    /// Host-side git ref containing the best-effort checkpoint captured during
    /// graceful shutdown. Null means there is no clean preemption checkpoint.
    /// </summary>
    public string? PreemptCheckpoint { get; init; }

    /// <summary>
    /// Caller-supplied identifier unique within the project (e.g. "JIRA-1234", "GH-#456").
    /// Null when not provided. Allows API callers to reference work items by a familiar
    /// external ID and to batch-queue dependent work items without a round-trip for UUIDs.
    /// </summary>
    public string? ExternalId { get; init; }

    /// <summary>
    /// When set, identifies the source work item this item was created as a replay of.
    /// Immutable after creation. Null for items not created via the replay API.
    /// When the source is cancelled the link is cleared (orphaned) but the replay
    /// continues running.
    /// </summary>
    public WorkItemId? ReplayOfWorkItemId { get; init; }

    /// <summary>
    /// SHA of the merge commit produced during the merge phase. Populated by the
    /// pipeline runner when the merge completes; null until then.
    /// </summary>
    public string? MergeSha { get; init; }

    /// <summary>
    /// The release this work item belongs to. When set, the orchestrator targets the
    /// release branch instead of the project's default base branch, and the release state
    /// machine tracks this item's terminal state for the closed→in_review auto-transition.
    /// Null = merge directly to main (legacy/default behaviour).
    /// </summary>
    public ReleaseId? ReleaseId { get; init; }

    public WorkItem With(
        WorkItemState state,
        string? error = null,
        WorkItemCancellationReason? cancellationReason = null) => this with
        {
            State = state,
            LastError = error,
            // CancellationReason is only meaningful when transitioning to Cancelled.
            CancellationReason = state == WorkItemState.Cancelled ? cancellationReason : null,
            UpdatedAt = DateTimeOffset.UtcNow,
            // Clear StartedAt when re-queuing: retried items must not appear in-flight
            // to CountInFlightAsync, which uses started_at IS NOT NULL as its proxy.
            StartedAt = state == WorkItemState.Queued ? null : StartedAt,
            // Clear WorkBranch when re-queuing from Working: the in-flight branch is
            // gone; the next pickup generates a fresh one.
            WorkBranch = state == WorkItemState.Queued ? null : WorkBranch,
            PreemptedAt = state is WorkItemState.Working or WorkItemState.Reworking ? PreemptedAt : null,
            PreemptCheckpoint = state is WorkItemState.Working or WorkItemState.Reworking ? PreemptCheckpoint : null,
        };
}
