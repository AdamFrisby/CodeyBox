namespace CodeyBox.Orchestrator;

/// <summary>
/// Operational tuning knobs consumed by <see cref="PipelineRunner"/>'s
/// quota-fallback and merge-staging retry paths. Bound from
/// <c>CodeyBox:PipelineTuning</c> and hot-reloaded via
/// <see cref="PipelineTuningSnapshot"/>.
/// </summary>
public sealed class PipelineTuningOptions
{
    /// <summary>
    /// Maximum PLAN-review attempts for one planning lifecycle. The pipeline
    /// snapshots this hot-reloadable value when a review lifecycle starts, so
    /// an in-flight loop has a stable cap while the next lifecycle observes a
    /// configuration edit.
    /// </summary>
    public int MaxPlanReviewIterations { get; set; } = PlanReviewIterationLimit.DefaultValue;

    /// <summary>
    /// Fraction (0, 1] of the operator task's distinctive terms the canonical
    /// PLAN must reproduce for the deterministic <see cref="PlanApprovalPolicy"/>
    /// task-binding gate to approve it. Higher values demand the plan cover more
    /// of the task before the pipeline persists <c>PlanApproved</c>, tightening
    /// the independent (non-LLM) check against a forged reviewer pass. Default
    /// <see cref="PlanApprovalPolicy.DefaultTaskBindingCoverage"/>.
    /// </summary>
    public double PlanTaskBindingCoverageRatio { get; set; } = PlanApprovalPolicy.DefaultTaskBindingCoverage;

    /// <summary>
    /// Last-resort pause applied when a quota-shaped terminal failure occurs and
    /// neither the agent output nor quota probes expose a reset window.
    /// Default 5 minutes.
    /// </summary>
    public TimeSpan DefaultQuotaFailurePause { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Last-resort pause applied when a <em>rate-limited</em> terminal failure
    /// (transient provider 429 / throughput limit) carries no parseable reset
    /// window — no <c>reset after …</c> tail and no <c>Retry-After</c> echo.
    /// Kept separate from <see cref="DefaultQuotaFailurePause"/> because a
    /// short-term throughput limit clears far sooner than a spent account
    /// cap, so the two need different operator responses and different reset
    /// expectations. Default 5 minutes — deliberately and meaningfully longer
    /// than the sub-two-minute in-CLI retry budget that precedes the park, so
    /// the provider has actually had time to drain before the item resumes.
    /// </summary>
    public TimeSpan DefaultRateLimitPause { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Per-process exhausted-member TTL when the chosen agent hits quota
    /// mid-flight. Subscription windows reset on the order of hours; one hour
    /// is a conservative upper bound that keeps the in-process cache useful
    /// across consecutive pickups without blocking long enough to delay an
    /// actual reset by a meaningful amount. Default 1 hour.
    /// </summary>
    public TimeSpan QuotaExhaustionFallbackTtl { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Upper bound for parsed reset-window hints extracted from an agent's
    /// stdout/stderr. Without a cap, a maliciously-crafted Retry-After header
    /// could park an item arbitrarily far in the future. Default 24 hours.
    /// </summary>
    public TimeSpan MaxParsedQuotaResetWindow { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Maximum attempts to restore a missing staging clone before rethrowing
    /// during merge-sandbox creation. Default 2. One retry is the production
    /// heal contract — if the source disappears AGAIN after restore, the loop
    /// falls through to rethrow rather than spinning indefinitely.
    /// </summary>
    public int MergeSandboxStagingRestoreAttempts { get; set; } = 2;

    /// <summary>
    /// Maximum number of operator questions an agent can emit per work item
    /// before the pipeline ignores further questions and continues processing.
    /// Default 10.
    /// </summary>
    public int MaxQuestionsPerWorkItem { get; set; } = 10;

    /// <summary>
    /// Maximum automatic re-invocations after a failed agent exec, applied by
    /// <see cref="Agents.AgentSuspendResilience"/>. Default 1.
    /// </summary>
    public int AgentSuspendMaxRetries { get; set; } = 1;

    /// <summary>
    /// Maximum CLI-native session-resume retries the base agent runner will
    /// attempt after a transient crash that captured a session id in stdout.
    /// Applied by <see cref="Agents.SessionResumeOptions"/>. Default 2 — one
    /// retry covers the typical 429 / OOM / SIGPIPE blip, the second exists
    /// so a single mid-resume blip does not collapse the work item. Set to 0
    /// to disable session resume (fall back to the legacy single-shot
    /// re-invocation retry).
    /// </summary>
    public int AgentSessionResumeMaxAttempts { get; set; } = Agents.SessionResumeOptions.DefaultMaxResumeAttempts;

    /// <summary>
    /// Maximum provider-owned stopped sandboxes retained concurrently because
    /// infrastructure prevented normal agent-turn checkpoint publication.
    /// Enforced atomically in the work-item store. Default 16.
    /// </summary>
    public int MaxRetainedAgentTurnSandboxes { get; set; } = 16;

    /// <summary>
    /// Maximum number of sequential auto-merge race recoveries the upstream-push
    /// loop will perform before parking the item. Each recovery costs a full LLM
    /// merge-phase re-invocation. When the upstream base is a moving target
    /// (hammered by sibling writes / direct pushes), this cap bounds the retry
    /// cost. Default 3 (separate from and narrower than <c>UpstreamPushMaxAttempts</c>).
    /// </summary>
    public int AutoMergeRaceRecoveryMaxAttempts { get; set; } = 3;

    /// <summary>
    /// Maximum times a single merge phase re-queues its landing after
    /// detecting the base branch moved between composition and the
    /// conditional base-ref update. A base-moved retry re-runs the merge
    /// against the fresh base WITHOUT consuming the resolver-guard rework
    /// budget (<see cref="MergeGuardReworkMaxAttempts"/>): motion under a
    /// serialized landing gate is environmental, not evidence the item's
    /// own resolution failed. Past the cap the item parks at
    /// <c>MergeConflictResolutionFailed</c> with the recorded reason so an
    /// operator can see the base never settled. Default 3. Hot-reloaded
    /// with the rest of <c>PipelineTuning</c>.
    /// </summary>
    public int MergeLandingMaxAttempts { get; set; } = 3;

    /// <summary>
    /// Whether the merge phase builds the exact merge-result tree with the
    /// project's required build (the same command as the required-build
    /// gate) in a sandbox before advancing the base branch. A clean
    /// textual merge of two branches that each built on their own base can
    /// still fail to compile when combined (e.g. duplicate symbols); the
    /// gate refuses to land such a tree. Default true. Hot-reloaded with
    /// the rest of <c>PipelineTuning</c>.
    /// </summary>
    public bool MergeResultBuildVerificationEnabled { get; set; } = true;

    /// <summary>
    /// Upper bound for a single merge-result build verification, measured
    /// from sandbox acquisition through build completion. Exceeding it
    /// fails the verification (the item re-queues through rework and
    /// re-verifies) — an unverified tree is never landed. Must be
    /// positive. Default 15 minutes. Hot-reloaded with the rest of
    /// <c>PipelineTuning</c>.
    /// </summary>
    public TimeSpan MergeResultBuildVerificationTimeout { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Maximum conflict-rework turns the merge phase runs after a resolver
    /// safety guard fires (edits outside the permitted conflict hunks, or a
    /// rework that discarded prior commits). Each turn re-engages the work
    /// agent on the refreshed base with the guard reason in the brief; only
    /// when the cap is exhausted does the item park at
    /// <c>MergeConflictResolutionFailed</c>. Bounds the remediation so a
    /// persistently misbehaving resolver cannot loop unbounded. Values below
    /// 1 are treated as 1. Default 2. Hot-reloaded with the rest of
    /// <c>PipelineTuning</c>.
    /// </summary>
    public int MergeGuardReworkMaxAttempts { get; set; } = 2;

    /// <summary>
    /// Whether to keep the same warm VM/sandbox alive across work<->rework cycles.
    /// Default true.
    /// </summary>
    public bool EnableSandboxReuse { get; set; } = true;

    /// <summary>
    /// Maximum reuse cycles (invocations) for a single work sandbox before it is recreated.
    /// Default 3.
    /// </summary>
    public int MaxSandboxReuses { get; set; } = 3;

    /// <summary>
    /// Maximum lifetime of a reused work sandbox before it is recreated.
    /// Default 1 hour.
    /// </summary>
    public TimeSpan MaxSandboxLifetime { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Utilization threshold (CurrentAdmittedSandboxes / MaxConcurrentSandboxes) above which
    /// a reused sandbox is disposed to free capacity for other runs.
    /// Default 0.85 (85%).
    /// </summary>
    public double SandboxPressureThreshold { get; set; } = 0.85;

    /// <summary>
    /// How long a worker may wait for a sandbox admission permit before the
    /// gate logs a warning naming the waiting work item and phase. Normal
    /// acquisition takes seconds; a wait past this threshold means the pool's
    /// sandbox ceiling is saturated and dispatch may be stalling (the
    /// 2026-09-11 incident was silent for hours). Default 5 minutes. Zero
    /// disables the warning.
    /// </summary>
    public TimeSpan SandboxPermitWaitWarningThreshold { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// When true, auditors declaring
    /// <see cref="CodeyBox.Core.IAuditor.CanShortCircuitOnBlockingFinding"/> run before
    /// the remaining auditors and a blocking gate result skips the rest of the
    /// audit iteration. Default true.
    /// </summary>
    public bool AuditShortCircuitEnabled { get; set; } = true;

    /// <summary>
    /// Maximum re-dispatch attempts the audit/rework loop performs when a rework
    /// agent returns with no committed changes AND the audit history shows
    /// convergence progress AND no infra (auth / quota) signature explains the
    /// empty result. Each escalation re-runs the rework with an explicit
    /// instruction telling the agent its previous pass committed nothing and it
    /// MUST either modify files or justify each finding as already satisfied /
    /// invalid. Default <c>1</c>. Set to <c>0</c> to disable escalation entirely
    /// (an empty non-infra rework parks straight away). Hot-reloaded with the
    /// rest of <c>PipelineTuning</c>.
    /// </summary>
    public int EmptyReworkEscalationRetries { get; set; } = 1;

    /// <summary>
    /// Maximum bounded "continue and finish the task" nudges the work
    /// phase sends in the same sandbox session when an agent turn exits
    /// success with no committed changes and no completion summary (empty
    /// stdout) — the signature of a turn that ended early, e.g. a stream that
    /// stops mid-investigation. Each nudge re-invokes the same runner in the
    /// same sandbox with an explicit continuation prompt; when the nudged turn
    /// still produces no changes the normal no-changes handling applies
    /// (branch-ahead re-audit or terminal failure). Default <c>1</c>. Set to
    /// <c>0</c> to disable nudging. Hot-reloaded with the rest of
    /// <c>PipelineTuning</c>.
    /// </summary>
    public int EarlyEndedTurnMaxNudges { get; set; } = 1;

    /// <summary>
    /// Maximum bounded "continue where you left off" nudges the work phase
    /// sends in the same session when an agent turn fails with an
    /// output-truncation transient (the model hit its maximum output token
    /// limit mid-turn). Each nudge resumes the same session with an explicit
    /// continuation prompt instead of starting a fresh turn; when the nudged
    /// turns keep failing the item parks for bounded transient retry, whose
    /// re-dispatch resumes the session via the durable agent-turn checkpoint
    /// where the agent supports it. Bounded by
    /// <see cref="CodeyBox.Core.ProviderTransientRetryPolicy.MaxTruncationContinueNudges"/>
    /// however high this is set. Default <c>2</c>. Set to <c>0</c> to park
    /// truncations for transient retry without an inline nudge. Hot-reloaded
    /// with the rest of <c>PipelineTuning</c>.
    /// </summary>
    public int ProviderTransientTruncationMaxNudges { get; set; } = 2;

    /// <summary>
    /// Correlation window for host-level provider-transient detection: when
    /// the same transient signature is observed from at least
    /// <see cref="ProviderTransientCorrelationThreshold"/> distinct agents
    /// inside this window, the cause is treated as host/network-level and
    /// dispatch pauses for <see cref="ProviderTransientDispatchPause"/>
    /// instead of burning every item's retry budget at once. Default 5
    /// minutes. Hot-reloaded with the rest of <c>PipelineTuning</c>.
    /// </summary>
    public TimeSpan ProviderTransientCorrelationWindow { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Distinct agents that must report the same transient signature inside
    /// <see cref="ProviderTransientCorrelationWindow"/> before dispatch
    /// pauses. A single agent's failure is never host-level evidence, so
    /// values below two behave as two. Default <c>2</c>. Hot-reloaded with
    /// the rest of <c>PipelineTuning</c>.
    /// </summary>
    public int ProviderTransientCorrelationThreshold { get; set; } = 2;

    /// <summary>
    /// How long dispatch pauses when correlated provider-transient evidence
    /// trips the host-level gate. Default 2 minutes. Hot-reloaded with the
    /// rest of <c>PipelineTuning</c>.
    /// </summary>
    public TimeSpan ProviderTransientDispatchPause { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Maximum time a single auditor may run without completing or emitting
    /// an LLM stdout chunk. A value of zero disables the per-auditor idle
    /// guard. Default 5 minutes.
    /// </summary>
    public TimeSpan AuditorIdleTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Absolute per-auditor wall-clock bound for a single auditor run,
    /// measured from run start regardless of output or sandbox activity. The
    /// idle guard (<see cref="AuditorIdleTimeout"/>) is liveness-aware: a run
    /// that keeps producing output or holding live sandbox execs keeps its
    /// slot past the quiet window. Without a second bound such a run could
    /// live forever when it never goes quiet enough to trip idle yet never
    /// finishes; this cap terminates it. Must be kept below the per-iteration
    /// audit timeout (see <see cref="AuditBudgetOrdering"/>) so a single
    /// auditor cannot outlive its iteration. Zero disables the absolute leg
    /// (not recommended). Default 30 minutes.
    /// </summary>
    public TimeSpan AuditorAbsoluteTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Whether audit sandboxes prepend a lightweight <c>dotnet</c> shim that
    /// turns redundant auditor-initiated <c>dotnet build</c> and
    /// <c>dotnet test</c> invocations into an immediate successful no-op.
    /// The deterministic required-build gate has already run before LLM
    /// review, so this protects auditor capacity without changing work,
    /// merge, or conflict-resolution sandboxes. Default true.
    /// </summary>
    public bool BlockRedundantDotnetBuildTestInAuditSandbox { get; set; } = true;

    /// <summary>
    /// Test-runner-specific idle guard applied to the <c>csharp:test-pass</c>
    /// (dotnet test) auditor in place of <see cref="AuditorIdleTimeout"/>. A long
    /// test suite legitimately produces no stdout for longer than a mechanical
    /// gate, so this lets operators grant it a larger idle window without
    /// loosening the guard for every other auditor. Null (the default) means the
    /// generic <see cref="AuditorIdleTimeout"/> applies unchanged. Sourced
    /// through <c>DotnetTestAuditor</c> (an <c>ITestRunnerAuditor</c>), which the
    /// pipeline reads via <see cref="Core.TestRunOptions.IdleTimeout"/>.
    /// </summary>
    public TimeSpan? CSharpTestPassAuditorIdleTimeout { get; set; }

    /// <summary>
    /// Per-test hang-dump timeout injected into the <c>csharp:test-pass</c>
    /// command as <c>--blame-hang --blame-hang-timeout</c>. A single wedged test
    /// then produces a dump instead of stalling the whole run until the idle
    /// guard fires. Null (the default) omits blame-hang, keeping the emitted
    /// command byte-identical to the legacy path.
    /// </summary>
    public TimeSpan? CSharpTestPassBlameHangTimeout { get; set; }

    /// <summary>
    /// Whether to build and seed a cross-agent handoff brief on the next
    /// invocation after the orchestrator falls back from one
    /// <c>AgentKind</c> to another mid-iteration. The brief itself is built
    /// by the wired <c>ICrossAgentHandoffBriefBuilder</c> (currently
    /// <c>AgentStreamBriefBuilder</c>) and injected by the
    /// <c>CrossAgentHandoffPromptPreprocessor</c> as a fenced
    /// <c>[UNTRUSTED DATA SECTION]</c> block. Default <c>false</c>: the
    /// brief is sourced from attacker-influenceable inputs (prior agent
    /// stream + branch state), so the feature is opt-in until an operator
    /// has confidence in the upstream defences for their workload.
    /// </summary>
    public bool EnableHandoffSeeding { get; set; }

    /// <summary>
    /// Whether to inject the runtime-composed self-review checklist into the
    /// one-shot work-phase prompt. The checklist is assembled by
    /// <see cref="CodeyBox.Projects.SelfReviewChecklistComposer"/> from the
    /// active auditors' <see cref="CodeyBox.Core.IAuditor.SelfReviewGuidance"/>
    /// and asks the agent to fix genuine issues it spots before committing;
    /// the formal audit (separate, fresh sandbox) still owns pass/fail.
    /// Default <c>false</c>; opt in to dispatch with the checklist for
    /// measurement / A-B comparison.
    /// </summary>
    public bool SelfReviewChecklistEnabled { get; set; }

    /// <summary>
    /// The plan-stage "approach" reviewer whose CODE-audit findings are demoted
    /// to advisory for planned items by default. This is the architecture LLM
    /// reviewer (audit-type <c>architecture</c>, name <c>architecture:llm-review</c>),
    /// which already ran at the plan-review stage for planned items — re-blocking
    /// on it during code rework is the "re-litigating the approach" the planning
    /// loop is meant to avoid. Operators override the full set via
    /// <see cref="PlannedItemAdvisoryAuditors"/>.
    /// </summary>
    public const string DefaultPlannedItemAdvisoryAuditor = "architecture:llm-review";

    /// <summary>
    /// Whether the code audit is rebalanced toward objective checks for PLANNED
    /// items: blocking findings from the <see cref="PlannedItemAdvisoryAuditors"/>
    /// are demoted to advisory (recorded, not merge-blocking) so a planned item's
    /// code rework does not re-litigate an approach the plan stage already
    /// reviewed. Objective gates (build, tests, security, cheating, completeness,
    /// plan-adherence) always keep full blocking authority. No effect on
    /// unplanned items. Default true. Hot-reloaded with the rest of
    /// <c>PipelineTuning</c>.
    /// </summary>
    public bool PlannedItemAuditRebalanceEnabled { get; set; } = true;

    /// <summary>
    /// Auditor names whose code-audit findings are demoted from blocking to
    /// advisory for planned items when
    /// <see cref="PlannedItemAuditRebalanceEnabled"/> is true. Matched
    /// case-insensitively against <see cref="Core.AuditFinding.AuditorName"/>.
    /// Defaults to the single plan-stage approach reviewer
    /// (<see cref="DefaultPlannedItemAdvisoryAuditor"/>). Never list objective
    /// gates here (build/test gates, security, cheating, completeness,
    /// plan-adherence): demoting those would let a planned item merge past a
    /// real defect. An empty list disables demotion without disabling the flag.
    /// </summary>
    public IList<string> PlannedItemAdvisoryAuditors { get; set; } =
        new List<string> { DefaultPlannedItemAdvisoryAuditor };

    /// <summary>
    /// Minimum Jaccard similarity over a suggestion's normalized token
    /// signature (lower-cased, punctuation- and stop-word-stripped title
    /// tokens plus normalized file paths) for a new agent-filed suggestion to
    /// merge into an existing open or recently-dismissed row instead of
    /// creating a duplicate. Applied by
    /// <see cref="Core.ISuggestionStore.CreateOrMergeAsync"/> at pickup.
    /// Default 0.6. Hot-reloaded with the rest of <c>PipelineTuning</c>.
    /// </summary>
    public double SuggestionDedupeSimilarityThreshold { get; set; } = 0.6;

    /// <summary>
    /// How far back a dismissed suggestion still attracts repeats: a matching
    /// suggestion dismissed within this window bumps its occurrence count
    /// rather than spawning a new row (the dismissal sticks — repeats do not
    /// resurrect it). Open suggestions always match. Default 30 days.
    /// </summary>
    public TimeSpan SuggestionDedupeDismissedMatchWindow { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Cap on the source work-item id list recorded on a merged suggestion.
    /// Once full, repeats still bump the occurrence count but stop appending
    /// ids, bounding row growth. Default 25.
    /// </summary>
    public int SuggestionDedupeMaxRecordedSources { get; set; } = 25;

    /// <summary>
    /// Master switch for base-broken attribution and containment. When true
    /// (default), a required-build failure whose compiler errors point at
    /// files outside the item's diff triggers a base-tip sandbox build; a
    /// reproducing failure parks the item (no failure charge) and sets the
    /// project-level base-broken condition that holds build-dependent
    /// dispatch. When false, every required-build failure stays
    /// item-attributed (historical behaviour). Hot-reloaded with the rest
    /// of <c>PipelineTuning</c>.
    /// </summary>
    public bool BaseBrokenDetectionEnabled { get; set; } = true;

    /// <summary>
    /// Dispatch priority for the auto-filed base-fix work item — one per
    /// broken base SHA. Defaults to <see cref="Core.WorkItemLimits.MaxPriority"/>
    /// so the repair path always outranks ordinary work. Clamped to the
    /// valid priority range. Hot-reloaded with the rest of
    /// <c>PipelineTuning</c>.
    /// </summary>
    public int BaseBrokenFixItemPriority { get; set; } = Core.WorkItemLimits.MaxPriority;

    /// <summary>
    /// Interval between base-tip re-checks for active base-broken
    /// conditions. A sweep resolves the base tip; the same broken SHA skips
    /// the rebuild (its verdict is immutable), a moved tip is built once
    /// and the condition clears when it passes. Default 2 minutes; must be
    /// positive. Hot-reloaded with the rest of <c>PipelineTuning</c>.
    /// </summary>
    public TimeSpan BaseBrokenRecheckInterval { get; set; } = TimeSpan.FromMinutes(2);

    public void Validate()
    {
        _ = PlanReviewIterationLimit.Create(MaxPlanReviewIterations);
        if (DefaultRateLimitPause <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(DefaultRateLimitPause),
                "DefaultRateLimitPause must be a positive TimeSpan");
        }
        if (PlannedItemAdvisoryAuditors is null)
        {
            throw new ArgumentNullException(
                nameof(PlannedItemAdvisoryAuditors),
                "PlannedItemAdvisoryAuditors must not be null (use an empty list to disable demotion).");
        }
        if (PlannedItemAdvisoryAuditors.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "PlannedItemAdvisoryAuditors entries must be non-empty auditor names.",
                nameof(PlannedItemAdvisoryAuditors));
        }
        if (!double.IsFinite(PlanTaskBindingCoverageRatio)
            || PlanTaskBindingCoverageRatio <= 0.0
            || PlanTaskBindingCoverageRatio > 1.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(PlanTaskBindingCoverageRatio),
                PlanTaskBindingCoverageRatio,
                "PlanTaskBindingCoverageRatio must be in the interval (0, 1].");
        }
        if (MaxSandboxReuses < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxSandboxReuses), "MaxSandboxReuses must be >= 1");
        }
        if (MaxRetainedAgentTurnSandboxes is < 1 or > 256)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxRetainedAgentTurnSandboxes),
                "MaxRetainedAgentTurnSandboxes must be between 1 and 256");
        }
        if (MaxSandboxLifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxSandboxLifetime), "MaxSandboxLifetime must be positive");
        }
        if (double.IsNaN(SandboxPressureThreshold)
            || double.IsInfinity(SandboxPressureThreshold)
            || SandboxPressureThreshold < 0.0
            || SandboxPressureThreshold > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(SandboxPressureThreshold), "SandboxPressureThreshold must be between 0.0 and 1.0 inclusive");
        }
        if (AuditorIdleTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(AuditorIdleTimeout), "AuditorIdleTimeout must be non-negative");
        }
        if (AuditorAbsoluteTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(AuditorAbsoluteTimeout), "AuditorAbsoluteTimeout must be non-negative");
        }
        if (CSharpTestPassAuditorIdleTimeout is { } idle && idle < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(CSharpTestPassAuditorIdleTimeout), "CSharpTestPassAuditorIdleTimeout must be non-negative when set");
        }
        if (CSharpTestPassBlameHangTimeout is { } hang && hang <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(CSharpTestPassBlameHangTimeout), "CSharpTestPassBlameHangTimeout must be positive when set");
        }
        if (EmptyReworkEscalationRetries < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(EmptyReworkEscalationRetries),
                "EmptyReworkEscalationRetries must be non-negative");
        }
        if (MergeLandingMaxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MergeLandingMaxAttempts),
                "MergeLandingMaxAttempts must be >= 1");
        }
        if (MergeGuardReworkMaxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MergeGuardReworkMaxAttempts),
                "MergeGuardReworkMaxAttempts must be >= 1");
        }
        if (MergeResultBuildVerificationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MergeResultBuildVerificationTimeout),
                "MergeResultBuildVerificationTimeout must be positive");
        }
        if (EarlyEndedTurnMaxNudges < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(EarlyEndedTurnMaxNudges),
                "EarlyEndedTurnMaxNudges must be non-negative");
        }
        if (ProviderTransientTruncationMaxNudges < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ProviderTransientTruncationMaxNudges),
                "ProviderTransientTruncationMaxNudges must be non-negative");
        }
        if (ProviderTransientCorrelationWindow <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ProviderTransientCorrelationWindow),
                "ProviderTransientCorrelationWindow must be positive");
        }
        if (ProviderTransientCorrelationThreshold < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ProviderTransientCorrelationThreshold),
                "ProviderTransientCorrelationThreshold must be >= 1 (values below 2 behave as 2)");
        }
        if (ProviderTransientDispatchPause < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ProviderTransientDispatchPause),
                "ProviderTransientDispatchPause must be non-negative");
        }
        if (SandboxPermitWaitWarningThreshold < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(SandboxPermitWaitWarningThreshold),
                "SandboxPermitWaitWarningThreshold must be non-negative (zero disables the slow-permit warning)");
        }
        if (BaseBrokenFixItemPriority < Core.WorkItemLimits.MinPriority
            || BaseBrokenFixItemPriority > Core.WorkItemLimits.MaxPriority)
        {
            throw new ArgumentOutOfRangeException(
                nameof(BaseBrokenFixItemPriority),
                BaseBrokenFixItemPriority,
                $"BaseBrokenFixItemPriority must be within [{Core.WorkItemLimits.MinPriority}, {Core.WorkItemLimits.MaxPriority}]");
        }
        if (BaseBrokenRecheckInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(BaseBrokenRecheckInterval),
                BaseBrokenRecheckInterval,
                "BaseBrokenRecheckInterval must be a positive TimeSpan");
        }
        new Core.SuggestionDedupePolicy
        {
            SimilarityThreshold = SuggestionDedupeSimilarityThreshold,
            DismissedMatchWindow = SuggestionDedupeDismissedMatchWindow,
            MaxRecordedSourceIds = SuggestionDedupeMaxRecordedSources,
        }.Validate();
    }
}

/// <summary>
/// Validated PLAN-review iteration cap. Configuration validation and the
/// orchestration loop share this value object so zero cannot acquire a second,
/// silently-clamped meaning inside the loop.
/// </summary>
public readonly record struct PlanReviewIterationLimit
{
    public const int MinimumValue = 1;
    public const int DefaultValue = 3;

    private PlanReviewIterationLimit(int value) => Value = value;

    public int Value { get; }

    public static bool TryCreate(int value, out PlanReviewIterationLimit limit)
    {
        if (value < MinimumValue)
        {
            limit = default;
            return false;
        }

        limit = new PlanReviewIterationLimit(value);
        return true;
    }

    public static PlanReviewIterationLimit Create(int value)
    {
        if (!TryCreate(value, out var limit))
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                $"MaxPlanReviewIterations must be >= {MinimumValue}");
        }

        return limit;
    }
}

/// <summary>
/// Shared, swappable holder for the current <see cref="PipelineTuningOptions"/>.
/// Registered as a DI singleton so <see cref="PipelineRunner"/> reads through
/// the same reference the hot-reload coordinator writes to.
/// Mirrors the <see cref="AgentConcurrencySnapshot"/> pattern.
/// </summary>
public sealed class PipelineTuningSnapshot
{
    private PipelineTuningOptions _current;

    public PipelineTuningSnapshot(PipelineTuningOptions initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        initial.Validate();
        _current = initial;
    }

    /// <summary>
    /// Current snapshot. Volatile read so a concurrent <see cref="Replace"/>
    /// cannot tear the reference. Callers should bind once into a local for
    /// any compound read.
    /// </summary>
    public PipelineTuningOptions Current => Volatile.Read(ref _current);

    /// <summary>
    /// Atomically publishes <paramref name="next"/> as the new snapshot.
    /// In-flight reads observe either the old or the new reference, never a
    /// partial state.
    /// </summary>
    public void Replace(PipelineTuningOptions next)
    {
        ArgumentNullException.ThrowIfNull(next);
        next.Validate();
        Volatile.Write(ref _current, next);
    }
}
