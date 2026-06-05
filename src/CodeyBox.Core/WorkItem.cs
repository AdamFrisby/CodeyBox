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
    /// One-shot pickup policy for operator resume-from-work. When true and the
    /// item is queued, the next work-phase pickup keeps an existing work branch
    /// instead of resetting it to base. <see cref="With(WorkItemState)"/> preserves
    /// the flag across Queued→Queued transitions only while the target work branch
    /// remains recorded (so a cascade re-recovery keeps operator intent) and clears
    /// it whenever the branch is cleared. The watchdog Working→Queued recovery also
    /// clears it because that path regenerates the work branch and the prior
    /// preserve-target is lost.
    /// </summary>
    public bool PreserveWorkBranchOnQueuedPickup { get; init; }

    /// <summary>
    /// Agent preference for this work item. When no <see cref="AgentClassId"/> is set,
    /// overrides the project's default agent. When <see cref="AgentClassId"/> is set,
    /// this field is <b>not consulted</b> during class routing: members are chosen purely
    /// by <see cref="AgentMembership.QualityScore"/>, quota availability, smoke gates,
    /// and related routing rules. At pickup the orchestrator <b>rewrites</b> this field
    /// to whichever class member the router actually chose. Per-agent concurrency caps
    /// participate in routing as an additional gate: when the top-ranked eligible
    /// member is at its cap, the router spills to the next eligible-and-free member.
    /// Only when every eligible member is at its cap does the item defer. There is no
    /// mechanism today to hard-pin a work item to a specific agent inside a class.
    /// <para>
    /// Reflects the CURRENT phase's agent and is overwritten as the item moves
    /// through Work → Audit → Rework → Merge; for the full per-phase audit trail
    /// (who ran each phase, with start/end and outcome) use
    /// <see cref="IAgentInvolvementStore"/> / the <c>agentHistory</c> array on
    /// the work-item read model.
    /// </para>
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
    /// Agent kind whose operator pause parked this item in
    /// <see cref="WorkItemState.WaitingForAgentResume"/>. Separate from
    /// <see cref="Agent"/> because later phases can be blocked by an audit,
    /// merge, or conflict-rework agent that is not the original work owner.
    /// Null means the paused blocker set had multiple agents or predates this
    /// field; the resume scheduler requeues those rows on pause-state changes
    /// and lets routing decide again.
    /// </summary>
    public AgentKind? AgentPauseTarget { get; init; }

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
    /// Default 0: open to ANY agent — most tasks should run on whatever agent is
    /// available, and the quality-score-preferred router still picks the strongest
    /// free member first.
    /// <para>
    /// <b>Deprecated as the eligibility gate.</b> Use <see cref="RequiredCapabilities"/>
    /// to gate which models may touch sensitive/architectural work. MinModelScore is
    /// retained alongside the capability gate during the transition window: both must
    /// pass. Persisted; existing records without the column default to 95 on read
    /// (legacy backfill).
    /// </para>
    /// </summary>
    public int MinModelScore { get; init; } = 0;

    /// <summary>
    /// Clearance tags this work item demands of the agent member that runs it.
    /// Empty (the default) means "no clearance required" — any member of the
    /// resolved <see cref="AgentClass"/> is eligible. When non-empty the router
    /// only routes to members whose <see cref="AgentMembership.Capabilities"/>
    /// covers EVERY tag here.
    /// <para>
    /// Replaces <see cref="MinModelScore"/> as the eligibility/clearance mechanism.
    /// Capabilities express trust ("this model may handle sensitive work"),
    /// QualityScore expresses capability/preference ("this eligible model is
    /// strongest"). The two compose: capabilities gate WHO is eligible, QualityScore
    /// ranks WHICH eligible member wins.
    /// </para>
    /// Tag comparison is ordinal, case-insensitive. Persisted as a JSON array.
    /// </summary>
    public IReadOnlyList<string> RequiredCapabilities { get; init; } = [];

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
    /// Optional per-item audit iteration ceiling. When set, this overrides the
    /// project profile's default max when it is higher, allowing intentionally
    /// hard items to receive a larger audit budget without encoding that policy
    /// into dispatch priority. Raising the budget above the project default also
    /// requires the project audit profile's
    /// <see cref="ProjectAudit.BudgetOverrideMaxIterations"/> cap to allow that
    /// higher value.
    /// </summary>
    public int? AuditMaxIterations { get; init; }

    /// <summary>
    /// Optional operator-supplied complexity label used with
    /// <see cref="ProjectAudit.ComplexityIterationBudgets"/>. Null means the
    /// project default audit budget applies unless <see cref="AuditMaxIterations"/>
    /// is set.
    /// </summary>
    public string? AuditComplexity { get; init; }

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
    /// same VM on the next process startup. Set by the shutdown teardown
    /// handler; cleared by the startup resume handler once the VM is back to
    /// Running. Null for items that were not suspended (the steady-state and
    /// post-resume state).
    /// </summary>
    public string? SuspendedVmName { get; init; }

    /// <summary>
    /// UTC timestamp captured when the shutdown teardown handler froze this
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
    /// Kind of work this item represents. <see cref="JobType.Normal"/> runs the
    /// full work → audit → merge → upstream pipeline. <see cref="JobType.CheckAndAct"/>
    /// runs a single agent invocation in a sandbox that evaluates a yes/no
    /// question against the project repo, persists a structured verdict, and
    /// optionally enqueues a follow-up <see cref="JobType.Normal"/> item.
    /// <see cref="JobType.AgentControl"/> runs an operator control-plane pause
    /// or resume action without launching an agent sandbox.
    /// </summary>
    public JobType JobType { get; init; } = JobType.Normal;

    /// <summary>
    /// Configuration for a <see cref="JobType.CheckAndAct"/> item: the yes/no
    /// question to ask, the actionable condition, and the spec for the
    /// follow-up work item enqueued when the verdict matches. Required when
    /// <see cref="JobType"/> is <see cref="JobType.CheckAndAct"/>; null otherwise.
    /// </summary>
    public CheckAndActSpec? Check { get; init; }

    /// <summary>
    /// Configuration for a <see cref="JobType.AgentControl"/> item. Null for
    /// ordinary and check-and-act work items.
    /// </summary>
    public AgentControlSpec? AgentControl { get; init; }

    /// <summary>
    /// Verdict the agent returned for a <see cref="JobType.CheckAndAct"/> item.
    /// Persisted on the work item once the check phase completes successfully;
    /// null while the item is in flight or for non-check items. The verdict is
    /// authoritative — the orchestrator does not re-ask if the operator
    /// retries the check.
    /// </summary>
    public CheckVerdict? Verdict { get; init; }

    /// <summary>
    /// When this item was enqueued as the on-yes follow-up of a
    /// <see cref="JobType.CheckAndAct"/> check, this points back at the check
    /// item that triggered it. Provides traceability ("why was this item
    /// queued?") without conflating with <see cref="ReplayOfWorkItemId"/>
    /// (which has distinct replay-history semantics) or
    /// <see cref="DependsOn"/> (which gates pickup). Null for items not
    /// produced by a check.
    /// </summary>
    public WorkItemId? OriginCheckWorkItemId { get; init; }

    /// <summary>
    /// Ordered history of post-act re-check verdicts recorded against this
    /// work item. Populated only on follow-up items (those with
    /// <see cref="OriginCheckWorkItemId"/> set) and only by the post-act
    /// re-validation loop that re-runs the originating check's question
    /// against the modified repo state before the merge phase. Each entry
    /// corresponds to one re-validation iteration; the FIRST entry is the
    /// initial post-act re-check (after the work phase committed the
    /// remediation), subsequent entries are recorded after each rework
    /// iteration. The originating check item's own
    /// <see cref="Verdict"/> remains the authoritative initial verdict —
    /// this collection captures only the post-act re-validations on the
    /// follow-up itself. Empty for items that have never been re-validated
    /// (the steady-state for non-follow-up items).
    /// </summary>
    public IReadOnlyList<CheckVerdict> ReCheckVerdicts { get; init; } = [];

    /// <summary>
    /// Name of the task template that produced this work item, when the item
    /// was created by expanding a JSON template from the templates directory.
    /// Null for manually-created, replayed, suggestion-promoted, and
    /// check-follow-up items.
    /// </summary>
    public string? TemplateName { get; init; }

    /// <summary>
    /// Zero-based index of the template entry that produced this work item.
    /// Paired with <see cref="TemplateName"/> to trace an expanded work item
    /// back to a specific JSON array element.
    /// </summary>
    public int? TemplateEntryIndex { get; init; }

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
        string? cancellationSource = null)
    {
        var preserveQueuedPickup =
            state == WorkItemState.Queued
            && State == WorkItemState.Queued
            && PreserveWorkBranchOnQueuedPickup
            && !string.IsNullOrWhiteSpace(WorkBranch);

        return this with
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
            AgentPauseTarget = state == WorkItemState.WaitingForAgentResume ? AgentPauseTarget : null,
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
            WorkBranch = state == WorkItemState.Queued && !preserveQueuedPickup ? null : WorkBranch,
            PreserveWorkBranchOnQueuedPickup = preserveQueuedPickup,
            PreemptedAt = state is WorkItemState.Working or WorkItemState.Reworking ? PreemptedAt : null,
            PreemptCheckpoint = state is WorkItemState.Working or WorkItemState.Reworking ? PreemptCheckpoint : null,
        };
    }

    private static bool IsQuotaShapedState(WorkItemState state) =>
        state is WorkItemState.Failed or WorkItemState.WaitingForQuotaReset;

    private static bool IsCancellationSourceCarryingState(WorkItemState state) =>
        state is WorkItemState.Failed or WorkItemState.Cancelled;
}
