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
/// Details payload for the <c>agent.smoke_failed</c> event, fired when a
/// credential smoke test fails at startup or at work-item pickup.
/// </summary>
public sealed record AgentSmokeFailedDetails
{
    public required string AgentKind { get; init; }
    public string? Reason { get; init; }
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
/// branch. Failed iterations surface via <c>work_item.failed</c> instead.
/// </summary>
public sealed record IterationCompletedDetails
{
    public required string WorkItemId { get; init; }
    public required int Iteration { get; init; }
    public required string Phase { get; init; }
    /// <summary>Tip of the work branch after the iteration committed; null when not resolvable.</summary>
    public string? CommitSha { get; init; }
    public required long DurationMs { get; init; }
    public required bool Success { get; init; }
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
    /// <summary>Conflicted paths that the agent resolved during merge; null when the merge had no conflicts or the host did not track them.</summary>
    public IReadOnlyList<string>? Conflicts { get; init; }
}
