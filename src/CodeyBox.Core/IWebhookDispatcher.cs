namespace CodeyBox.Core;

/// <summary>
/// Dispatches pipeline events to configured webhook endpoints. Constructed
/// by the orchestrator with a <see cref="WebhookEvent"/> and called fire-and-
/// forget — implementations are responsible for delivery and retries and
/// should not throw on failure.
/// </summary>
public interface IWebhookDispatcher
{
    Task PublishAsync(WebhookEvent evt, CancellationToken ct);
}

/// <summary>Details payload for sandbox leak events.</summary>
public sealed record SandboxLeakDetails
{
    public required string Name { get; init; }
    public double AgeMinutes { get; init; }
    public long? DiskMb { get; init; }
    /// <summary>Stable reason code explaining why the sandbox was classified as leaked.</summary>
    public string? Reason { get; init; }
    /// <summary>Set only for <c>sandbox.leak_disposed</c>.</summary>
    public DateTimeOffset? DisposedAt { get; init; }
    /// <summary>Set only for <c>sandbox.leak_dispose_failed</c>.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Details payload for the <c>agent.smoke_failed</c> and <c>agent.smoke_recovered</c>
/// events. Fired on three distinct edge transitions: a credential smoke test
/// fails at startup or on a periodic sweep; a periodic sweep recovers a
/// previously-excluded agent; the fast-fail circuit breaker excludes the agent
/// after consecutive sub-threshold non-zero exits. The <see cref="Reason"/>
/// field distinguishes credential failures (e.g. "auth", "timeout") from
/// fast-fail trips (which contain the phrase
/// <c>"fast-fail circuit breaker"</c>); <see cref="Reason"/> is null on the
/// <c>agent.smoke_recovered</c> variant.
///
/// <para><see cref="Category"/> distinguishes transient failures (network
/// blip, 5xx, timeout — keep retrying) from persistent ones (auth /
/// credential expiry / missing binary — operator must re-authorize). Routing
/// to a separate "operator action required" channel is the only way to
/// prevent a healthy-quota agent (e.g. gemini at 100%) from being
/// indefinitely benched by a credential failure that the retry loop will
/// never resolve. Defaults to <see cref="SmokeFailureCategory.Unknown"/> on
/// the recovered variant so receivers don't have to special-case "Reason is
/// null implies category" — the field is always meaningful.</para>
/// </summary>
public sealed record AgentSmokeFailedDetails
{
    public required string AgentKind { get; init; }
    public string? Reason { get; init; }
    public SmokeFailureCategory Category { get; init; } = SmokeFailureCategory.Unknown;
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Details payload for the <c>work_item.pull_request_opened</c> event,
/// surfaced via <see cref="WebhookEvent.Details"/>.
/// </summary>
public sealed record PullRequestOpenedDetails
{
    public required string WorkBranch { get; init; }
    public required string BaseBranch { get; init; }
    public required int PullRequestNumber { get; init; }
    public required string PullRequestUrl { get; init; }
    public string? MergedSha { get; init; }
}

/// <summary>
/// Details payload for the <c>project.budget_warning</c>,
/// <c>project.budget_exceeded</c>, and <c>project.budget_recovered</c> events.
/// </summary>
public sealed record ProjectBudgetEventDetails
{
    public required string ProjectId { get; init; }
    public required decimal CurrentSpendUsd { get; init; }
    public required decimal BudgetUsd { get; init; }
    public required double Pct { get; init; }
    public required int ThresholdPct { get; init; }
}

/// <summary>Phase for <c>iteration.started</c> / <c>iteration.completed</c> events.</summary>
public static class IterationPhase
{
    public const string Work = "work";
    public const string Rework = "rework";
}

/// <summary>Verdict for <c>audit.completed</c> events.</summary>
public static class AuditVerdict
{
    public const string Pass = "pass";
    public const string Fail = "fail";
}

/// <summary>
/// Details payload for the <c>iteration.started</c> event, fired when the
/// pipeline dispatches an agent work or rework iteration. Intermediate
/// progress event — subscribers opt in via EventFilter.
/// </summary>
public sealed record IterationStartedDetails
{
    public required string WorkItemId { get; init; }
    public required int Iteration { get; init; }
    /// <summary><c>"work"</c> for the initial work attempt, <c>"rework"</c> for subsequent attempts driven by audit findings.</summary>
    public required string Phase { get; init; }
    public required DateTimeOffset DispatchedAt { get; init; }
}

/// <summary>
/// Details payload for the <c>iteration.completed</c> event. Emitted after
/// a work or rework iteration successfully produces a commit on the work
/// branch. Failed iterations surface via <c>work_item.failed</c> instead —
/// no <c>success: false</c> variant is fired today, so the absence of this
/// event paired with a terminal failure event is the signal trackers use.
/// </summary>
public sealed record IterationCompletedDetails
{
    public required string WorkItemId { get; init; }
    public required int Iteration { get; init; }
    public required string Phase { get; init; }
    /// <summary>Tip of the work branch after the iteration committed; null when not resolvable.</summary>
    public string? CommitSha { get; init; }
    public required long DurationMs { get; init; }
}

/// <summary>
/// Details payload for the <c>audit.started</c> event, fired at the start
/// of each audit iteration before any auditors run.
/// </summary>
public sealed record AuditStartedDetails
{
    public required string WorkItemId { get; init; }
    public required int Iteration { get; init; }
    /// <summary>Auditor names scheduled to run this iteration, in stable order.</summary>
    public required IReadOnlyList<string> AuditorsScheduled { get; init; }
}

/// <summary>
/// One finding entry inside an <c>audit.findings.emitted</c> payload. Maps
/// 1:1 to <see cref="AuditFinding"/> with the severity rendered as a string
/// so receivers don't have to know the enum ordinal.
/// </summary>
public sealed record AuditFindingPayload
{
    public required string Auditor { get; init; }
    public required string Severity { get; init; }
    public required string Title { get; init; }
    /// <summary>File and optional line hint (e.g. <c>"src/foo.cs:42"</c>); null when the auditor did not point at a location.</summary>
    public string? Location { get; init; }
    public required string Description { get; init; }
}

/// <summary>
/// Details payload for the <c>audit.findings.emitted</c> event. Carries the
/// full list of findings collected for one audit iteration so trackers can
/// render them as comments without polling the audit-findings endpoint.
/// </summary>
public sealed record AuditFindingsEmittedDetails
{
    public required string WorkItemId { get; init; }
    public required int Iteration { get; init; }
    public required IReadOnlyList<AuditFindingPayload> Findings { get; init; }
    /// <summary>Findings whose severity is ≥ the project's failing severity.</summary>
    public required int Blocking { get; init; }
    /// <summary>Findings whose severity is below the project's failing severity.</summary>
    public required int NonBlocking { get; init; }
}

/// <summary>
/// Details payload for the <c>audit.completed</c> event, fired after the
/// verdict for one audit iteration is known.
/// </summary>
public sealed record AuditCompletedDetails
{
    public required string WorkItemId { get; init; }
    public required int Iteration { get; init; }
    /// <summary><c>"pass"</c> when no blocking findings; <c>"fail"</c> when blocking findings drove rework or the final-iteration failure.</summary>
    public required string Verdict { get; init; }
    public required long DurationMs { get; init; }
}

/// <summary>
/// Details payload for the <c>audit.auditor_timed_out</c> event.
/// </summary>
public sealed record AuditAuditorTimedOutDetails
{
    public required string WorkItemId { get; init; }
    public required string Auditor { get; init; }
    public required string Agent { get; init; }
    public required int Iteration { get; init; }
    public string? SandboxId { get; init; }
}

/// <summary>
/// Details payload for <c>work_item.needs_operator_input</c> when the audit
/// loop parks with audit history that an operator should inspect, including
/// iteration-ceiling-with-progress and genuine empty-rework parks.
/// </summary>
public sealed record AuditMaxIterationsEscalationDetails
{
    public required string WorkItemId { get; init; }
    public required int Iteration { get; init; }
    public required int MaxIterations { get; init; }
    public required int BlockingFindings { get; init; }
    public required int NonBlockingFindings { get; init; }
    public required bool ProgressObserved { get; init; }
    public required IReadOnlyList<string> ProgressSignals { get; init; }
    public required IReadOnlyList<AuditProgressIterationDetails> History { get; init; }
    public required IReadOnlyList<AuditFindingPayload> RemainingBlockingFindings { get; init; }
    public required string ResumeHint { get; init; }
}

/// <summary>One audit iteration inside <see cref="AuditMaxIterationsEscalationDetails"/>.</summary>
public sealed record AuditProgressIterationDetails
{
    public required int Iteration { get; init; }
    public required int BlockingFindings { get; init; }
    public required int NonBlockingFindings { get; init; }
    public required IReadOnlyList<AuditFindingPayload> BlockingFindingsDetails { get; init; }
    public required IReadOnlyList<AuditFindingPayload> Findings { get; init; }
}

/// <summary>Details payload for the <c>merge.started</c> event.</summary>
public sealed record MergeStartedDetails
{
    public required string WorkItemId { get; init; }
    public required string BaseBranch { get; init; }
    public required string WorkBranch { get; init; }
}

/// <summary>
/// Details payload for the <c>merge.completed</c> event. Emitted when the
/// merge phase succeeds and produces a merge commit on the work branch.
/// </summary>
public sealed record MergeCompletedDetails
{
    public required string WorkItemId { get; init; }
    public required string BaseBranch { get; init; }
    public required string WorkBranch { get; init; }
    public string? MergeSha { get; init; }
}

/// <summary>
/// Details payload for <c>work_item.conflict_rework_started</c>. Emitted when
/// the orchestrator engages the original work agent as the third-line fallback
/// to resolve a merge-phase conflict that the preventive auto-rebase and the
/// merge-phase LLM rerun could not handle.
/// </summary>
public sealed record ConflictReworkStartedDetails
{
    public required string WorkItemId { get; init; }
    public required string BaseBranch { get; init; }
    public required string WorkBranch { get; init; }
    /// <summary>SHA of the work branch tip at the moment the rework iteration began.</summary>
    public required string WorkBranchTip { get; init; }
    /// <summary>SHA of the upstream base the rework will reconcile against.</summary>
    public required string BaseTip { get; init; }
    /// <summary>Paths the merge phase reported conflicts on.</summary>
    public required IReadOnlyList<string> ConflictFiles { get; init; }
}

/// <summary>
/// Details payload for <c>work_item.conflict_rework_finished</c>. Emitted when
/// the rework iteration completes — successfully (new branch tip ready for a
/// fresh merge attempt) or as a parked failure (semantic-incompatible exit,
/// destructive-action guard, or the post-rework merge still failed).
/// </summary>
public sealed record ConflictReworkFinishedDetails
{
    public required string WorkItemId { get; init; }
    public required string BaseBranch { get; init; }
    public required string WorkBranch { get; init; }
    /// <summary>True iff the rework iteration produced a clean work branch ready for re-merge.</summary>
    public required bool Success { get; init; }
    /// <summary>SHA of the work branch tip after the rework iteration (null on failure paths that never advanced).</summary>
    public string? NewWorkBranchTip { get; init; }
    /// <summary>Files added/modified by the rework iteration.</summary>
    public IReadOnlyList<string>? FilesChanged { get; init; }
    /// <summary>Inserted-line count over the rework's diff (best effort).</summary>
    public int? Insertions { get; init; }
    /// <summary>Deleted-line count over the rework's diff (best effort).</summary>
    public int? Deletions { get; init; }
    /// <summary>Verbatim <c>SEMANTIC_INCOMPATIBLE:</c> reason when the agent declared the two intents incompatible; null otherwise.</summary>
    public string? SemanticIncompatibleReason { get; init; }
    /// <summary>Free-form park reason when <see cref="Success"/> is false. Mirrors <c>LastError</c>.</summary>
    public string? ParkReason { get; init; }
}
