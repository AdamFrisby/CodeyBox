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

    /// <summary>
    /// Monotonic generation counter for <see cref="Prompt"/>. Starts at 1 on
    /// creation; the operator-facing prompt-update endpoint increments it on
    /// every successful write. Dispatched iterations capture this value so the
    /// orchestrator can detect "agent finished against an older prompt".
    /// </summary>
    public int PromptRevision { get; init; } = 1;

    /// <summary>If set, overrides the project's default base branch.</summary>
    public string? BaseBranch { get; init; }

    /// <summary>Branch the agent pushes its work to. Generated if null.</summary>
    public string? WorkBranch { get; init; }

    /// <summary>
    /// Agent preference for this work item. When no <see cref="AgentClassId"/> is set,
    /// overrides the project's default agent. When <see cref="AgentClassId"/> is set,
    /// acts as the <b>initial choice</b> only: the router may override it when the named
    /// agent fails the smoke gate, hits its per-agent concurrency cap, or is outscored
    /// by another member of the class (by <see cref="AgentMembership.QualityScore"/>,
    /// quota availability, and related routing rules). After pickup routing settles,
    /// this field is <b>rewritten</b> to whichever class member the router actually chose.
    /// There is no mechanism today to hard-pin a work item to a specific agent inside a
    /// class.
    /// </summary>
    public AgentKind? Agent { get; init; }

    /// <summary>
    /// Optional audit profile override for this work item. Null means use the
    /// project's configured default audit profile.
    /// </summary>
    public string? AuditorProfile { get; init; }

    /// <summary>Wall-clock budget for the work phase (also applied per rework iteration).</summary>
    public TimeSpan WorkTimeout { get; init; } = TimeSpan.FromMinutes(240);

    /// <summary>Wall-clock budget for the merge phase.</summary>
    public TimeSpan MergeTimeout { get; init; } = TimeSpan.FromMinutes(60);

    /// <summary>If true and the project has an upstream, push to it after merge.</summary>
    public bool PushUpstream { get; init; } = true;

    public WorkItemState State { get; init; } = WorkItemState.Queued;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Last error message if state is Failed.</summary>
    public string? LastError { get; init; }

    /// <summary>
    /// Informational category of the failure. Set when transitioning to Failed.
    /// Values: "quota", "timeout", "agent", "infrastructure", "other".
    /// </summary>
    public string? FailureKind { get; init; }

    /// <summary>
    /// When the quota window that caused a "quota" failure is expected to
    /// reset. Prefer parsed agent-output reset hints; quota failures may also
    /// use probe-derived reset times or the orchestrator's default pause.
    /// </summary>
    public DateTimeOffset? QuotaResetAt { get; init; }

    /// <summary>
    /// UTC timestamp for the next scheduled auto-retry attempt after a quota
    /// failure. Used by QuotaRetryScheduler to re-arm timers after restart.
    /// </summary>
    public DateTimeOffset? NextQuotaRetryAt { get; init; }

    /// <summary>
    /// Number of times this work item has been automatically retried after
    /// a quota failure.
    /// </summary>
    public int QuotaRetryAttempts { get; init; }

    /// <summary>
    /// Pipeline entry point the quota retry scheduler should use when the quota
    /// window opens. Values match the manual retry API: "work", "audit",
    /// "merge", or "upstream".
    /// </summary>
    public string? QuotaRetryFrom { get; init; }

    /// <summary>
    /// Why the item was cancelled. Only populated when <see cref="State"/> is
    /// <see cref="WorkItemState.Cancelled"/>; null for all other states and for
    /// legacy rows written before this column existed.
    /// </summary>
    public WorkItemCancellationReason? CancellationReason { get; init; }

    /// <summary>
    /// Stable label for which contributor first cancelled the most recent
    /// pipeline phase — see <see cref="CancellationSources"/>. Populated when
    /// <see cref="State"/> is <see cref="WorkItemState.Failed"/> with
    /// <see cref="FailureKind"/> = "timeout" or "cancelled", and preserved
    /// across an auto-retry so operators can still see the trigger after a
    /// successful re-run. Null for items that never hit cancellation.
    /// </summary>
    public string? CancellationSource { get; init; }

    /// <summary>
    /// Number of times this work item has been automatically re-queued from a
    /// transient host-side cancellation (i.e. an OCE whose contributor we
    /// couldn't attribute to an operator cancel, configured timeout, host
    /// shutdown, or stuck probe). Capped by
    /// <see cref="OrchestratorOptions.MaxTransientCancelRetries"/>; further
    /// transient cancellations after the cap are surfaced as Failed with a
    /// pointed error message instead of being retried silently.
    /// </summary>
    public int TransientCancelRetries { get; init; }

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
    /// Number of focused conflict-rework iterations the pipeline has executed
    /// for this work item. Capped at <c>1</c> per merge attempt; the original
    /// work agent gets exactly one re-engagement to resolve merge-phase
    /// conflicts that the preventive auto-rebase and the merge-phase LLM
    /// rerun could not handle. Past the cap the item parks at
    /// <see cref="WorkItemState.MergeConflictResolutionFailed"/>.
    /// </summary>
    public int ConflictReworkAttempts { get; init; }

    /// <summary>
    /// IDs of work items this item depends on. The orchestrator will not pick
    /// this item up until every dependency has reached a terminal state
    /// (Done, Failed, AuditFailed, MergeConflictResolutionFailed, or Cancelled). Immutable after creation.
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
    /// Dispatch priority for Queued items. Higher values pick up first; ties break by
    /// <see cref="CreatedAt"/> ascending so equal-priority items remain FIFO. Default 0;
    /// negative values sort behind defaults, positive values ahead. The API clamps to
    /// the range [-1000, 1000] and may apply a per-project cap.
    /// </summary>
    public int Priority { get; init; } = 0;

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
    /// Name of the sandbox (e.g. multipass VM) suspended during graceful host
    /// shutdown so the orchestrator can <c>multipass start &lt;name&gt;</c> the
    /// same VM on the next process startup. Set by the suspend-on-shutdown
    /// handler; cleared by the startup resume handler once the VM is back to
    /// Running. Null for items that were not suspended (the steady-state and
    /// post-resume state).
    /// </summary>
    public string? SuspendedVmName { get; init; }

    /// <summary>
    /// UTC timestamp captured when the suspend-on-shutdown handler froze this
    /// item's sandbox. Paired with <see cref="SuspendedVmName"/>; null when
    /// the item is not suspended.
    /// </summary>
    public DateTimeOffset? SuspendedAt { get; init; }

    /// <summary>
    /// Absolute path INSIDE the sandbox VM to the file capturing the active
    /// agent CLI's stdout/stderr. Set by <see cref="PipelineRunner"/> at agent
    /// invocation time and preserved across a multipass suspend/start cycle so
    /// the startup resume handler can <c>tail</c> the file to recover output
    /// the host-side stream lost on shutdown. Null when no agent is running or
    /// the active CLI has not opted into tee'd capture.
    /// </summary>
    public string? AgentLogPath { get; init; }

    /// <summary>
    /// Caller-supplied identifiers keyed by namespace. The same item can carry
    /// IDs in multiple external systems (e.g. <c>jobtrack</c>, <c>github</c>,
    /// <c>linear</c>). Keys are short, lowercase, dash-separated identifiers
    /// (see <see cref="Validation.ValidateExternalIdNamespace"/>); values follow
    /// the same character rules as the legacy single-value field (see
    /// <see cref="Validation.ValidateExternalId"/>). The pair
    /// <c>(projectId, namespace, value)</c> is unique within a project; the
    /// same string can appear in two different namespaces on the same item.
    ///
    /// The legacy single-value <c>externalId</c> field is preserved as a
    /// projection — see <see cref="ExternalId"/> — under the reserved namespace
    /// <c>legacy</c>.
    /// </summary>
    public IReadOnlyDictionary<string, string> ExternalIds { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Legacy single-value form, preserved for the deprecation window. Returns
    /// the value at namespace <c>legacy</c> if present; otherwise the first
    /// value in <see cref="ExternalIds"/> ordered ordinal-ignore-case by key
    /// (deterministic across reads). Null when the dictionary is empty. New
    /// code should read <see cref="ExternalIds"/> directly.
    /// </summary>
    public string? ExternalId =>
        ExternalIds.TryGetValue("legacy", out var legacy)
            ? legacy
            : ExternalIds
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => (string?)kv.Value)
                .FirstOrDefault();

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

    /// <summary>
    /// Content-hashed identifier of the sandbox baseline image this work item is
    /// pinned to. Stamped at pickup time from the sandbox provider's live config
    /// (profile, flavor, cloud-init, extra runcmd, extra cloud-init) and preserved
    /// across audit / rework iterations so an in-flight item keeps using the
    /// baseline it started with even when the operator edits config mid-flight.
    /// Null for items created before this column existed, items whose pickup
    /// predates the stamping logic, and items whose sandbox provider does not
    /// expose a baseline-ref resolver (process / bubblewrap). When null, the
    /// provider falls back to computing the ref from live config — backward-
    /// compatible behaviour for the migration window.
    /// </summary>
    public string? BaselineImageRef { get; init; }

    public WorkItem With(
        WorkItemState state,
        string? error = null,
        WorkItemCancellationReason? cancellationReason = null,
        string? failureKind = null,
        DateTimeOffset? quotaResetAt = null,
        string? cancellationSource = null) => this with
        {
            State = state,
            LastError = error,
            // Both Failed("quota") and WaitingForQuotaReset are quota-shaped
            // states that must preserve FailureKind / QuotaResetAt /
            // NextQuotaRetryAt so the retry scheduler can re-arm timers
            // across host restarts.
            FailureKind = IsQuotaShapedState(state) ? (failureKind ?? FailureKind) : null,
            QuotaResetAt = IsQuotaShapedState(state) ? (quotaResetAt ?? QuotaResetAt) : null,
            NextQuotaRetryAt = IsQuotaShapedState(state) ? NextQuotaRetryAt : null,
            QuotaRetryFrom = IsQuotaShapedState(state) ? QuotaRetryFrom : null,
            // CancellationReason is only meaningful when transitioning to Cancelled.
            CancellationReason = state == WorkItemState.Cancelled ? cancellationReason : null,
            // CancellationSource is preserved on Failed (so triage shows what cancelled the
            // phase) and on Cancelled (so we record whether the cancel came from operator
            // vs host shutdown deadline). Cleared on Queued/successful states.
            CancellationSource = IsCancellationSourceCarryingState(state)
                ? (cancellationSource ?? CancellationSource)
                : null,
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

    private static bool IsQuotaShapedState(WorkItemState state) =>
        state is WorkItemState.Failed or WorkItemState.WaitingForQuotaReset;

    private static bool IsCancellationSourceCarryingState(WorkItemState state) =>
        state is WorkItemState.Failed or WorkItemState.Cancelled;
}
