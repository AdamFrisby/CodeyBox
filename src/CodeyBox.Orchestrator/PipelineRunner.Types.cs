using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using CodeyBox.Agents;
using CodeyBox.Audit;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Projects;
using CodeyBox.Sandbox;

namespace CodeyBox.Orchestrator;

// PipelineRunner.Types.cs — top-level pipeline types formerly at the bottom of PipelineRunner.cs: terminal/audit exceptions, webhook payloads, and PipelineOptions.
internal sealed class AuditFailedException : Exception
{
    public AuditFailedException(string message) : base(message) { }
}

/// <summary>
/// Seeding or refreshing the bare repo from the git remote failed
/// (<c>git clone --bare</c> / <c>git fetch</c> against
/// <c>Project.RepositoryUrl</c>). This runs on the orchestrator host, over the
/// network, against the git remote: it involves no agent and no provider
/// quota, so it is always infrastructure. It must never contribute to a
/// quota-exhaustion verdict (no <c>MarkExhausted</c>, no observed-failure
/// record, no quota park) — only to an <c>infrastructure</c> work-item
/// failure with a bounded transient retry.
/// </summary>
internal sealed class GitRepositorySeedingException : Exception
{
    public GitRepositorySeedingException(string operation, string message, Exception innerException)
        : base($"repository {operation} failed: {message}", innerException)
    {
        Operation = operation;
    }

    /// <summary>Which git-transport step failed: <c>ensure-repository</c> (seed or refresh) or <c>resolve-default-branch</c>.</summary>
    public string Operation { get; }
}

internal sealed class RequiredBuildFailedException : Exception
{
    public RequiredBuildFailedException(string message) : base(message) { }
}

/// <summary>
/// The required build failed on the work branch AND the same failure
/// reproduces on the base branch tip with no contributing error file inside
/// the item's diff — the base is broken, not the work. Thrown by
/// <see cref="RequiredBuildGate"/> instead of
/// <see cref="RequiredBuildFailedException"/>; the outer catch parks the
/// item at <see cref="ResumeState"/> (branch preserved, no failure charge),
/// records the project-level base-broken condition, and holds
/// build-dependent dispatch until the base tip builds again.
/// </summary>
internal sealed class BaseBuildBrokenException : Exception
{
    public BaseBuildBrokenException(
        string message,
        string baseBranch,
        string baseSha,
        string? baseBuildOutput,
        WorkItemState resumeState,
        string? phase = null)
        : base(message)
    {
        BaseBranch = baseBranch;
        BaseSha = baseSha;
        BaseBuildOutput = baseBuildOutput;
        ResumeState = resumeState;
        Phase = phase;
    }

    /// <summary>The base branch whose tip failed the same required build.</summary>
    public string BaseBranch { get; }

    /// <summary>The base branch tip SHA that reproduced the failure.</summary>
    public string BaseSha { get; }

    /// <summary>Bounded, redacted output of the failing base-tip build.</summary>
    public string? BaseBuildOutput { get; }

    /// <summary>
    /// The resumable state the item returns to while the hold is active:
    /// Queued for a work-phase detection (branch preserved via
    /// <see cref="WorkItem.PreserveWorkBranchOnQueuedPickup"/>), WorkComplete
    /// for the audit/rework gate, AuditPassed for the merge-resume gate.
    /// </summary>
    public WorkItemState ResumeState { get; }

    /// <summary>Pipeline phase string that detected the failure (for logging).</summary>
    public string? Phase { get; }
}

/// <summary>
/// A declared e2e-replay capability could not be made green by the
/// post-implementation replay gate (missing/broken replay that authoring +
/// verification could not resolve). Like <see cref="RequiredBuildFailedException"/>
/// this is a work-quality failure — the gate working as designed — not infra.
/// </summary>
internal sealed class E2eReplayGateBlockedException : Exception
{
    public E2eReplayGateBlockedException(string message) : base(message) { }
}

internal sealed class RequiredBuildVerificationUnavailableException : Exception
{
    public RequiredBuildVerificationUnavailableException(string message) : base(message) { }
    public RequiredBuildVerificationUnavailableException(string message, Exception innerException)
        : base(message, innerException) { }
}

internal sealed class AuditHistoryLoadFailedException : Exception
{
    public AuditHistoryLoadFailedException(string message, Exception innerException)
        : base(message, innerException) { }
}

internal sealed class AuditHistoryPersistenceFailedException : Exception
{
    public AuditHistoryPersistenceFailedException(string message, Exception innerException)
        : base(message, innerException) { }
}

internal class AuditorIdleTimeoutException : TimeoutException
{
    public AuditorIdleTimeoutException(
        string auditorName,
        AgentKind agentKind,
        TimeSpan timeout,
        string? budgetPath = null)
        : base($"auditor '{auditorName}' (agent: {agentKind.Value}) exceeded budget {(budgetPath ?? AuditBudgetOrdering.AuditorIdleTimeoutPath)}={timeout}: produced no output or verdict within {timeout}")
    {
        AuditorName = auditorName;
        AgentKind = agentKind;
        Timeout = timeout;
        BudgetPath = budgetPath ?? AuditBudgetOrdering.AuditorIdleTimeoutPath;
    }

    public string AuditorName { get; }
    public AgentKind AgentKind { get; }
    public TimeSpan Timeout { get; }

    /// <summary>
    /// Config path of the budget that was exceeded, recorded so a
    /// budget-exceeded termination is reported distinctly from an auditor
    /// that ran and produced findings.
    /// </summary>
    public string BudgetPath { get; }
}

/// <summary>
/// A single auditor run outlived the absolute per-auditor wall-clock bound
/// (<c>CodeyBox:PipelineTuning:AuditorAbsoluteTimeout</c>), measured from run
/// start regardless of output or sandbox activity. Derives from
/// <see cref="AuditorIdleTimeoutException"/> so every existing idle-timeout
/// handling site (incomplete-verdict capture, timeout involvement outcome,
/// the LLM single-retry path) treats it as a budget termination rather than
/// an ordinary infrastructure failure; the <see cref="BudgetPath"/>
/// distinguishes which budget was exceeded and the message carries its
/// configured value.
/// </summary>
internal sealed class AuditorAbsoluteTimeoutException(
    string auditorName,
    AgentKind agentKind,
    TimeSpan timeout)
    : AuditorIdleTimeoutException(
        auditorName,
        agentKind,
        timeout,
        "CodeyBox:PipelineTuning:AuditorAbsoluteTimeout")
{
}

/// <summary>
/// Sandbox-to-bare work-branch publication failed at a non-conflict stage
/// (initial push, fetch, target-tip read, ancestry guard, post-reconcile
/// push, tip verification, or post-push host sync) without rewriting any
/// history. The agent turn is complete and both the source tree and the
/// observed target tip are intact; only the push/reconcile/sync did not
/// land, so the resume checkpoint is retained for a bounded retry. Carries
/// the failing stage plus safe source/target identities for triage.
/// Distinct from <see cref="SandboxPushReconcileConflictException"/>, which
/// signals irreconcilable histories: labelling a transport/guard failure as
/// a content conflict would be false evidence for the recovery policy, and a
/// plain <see cref="InvalidOperationException"/> would be unmatchable by it.
/// </summary>
internal sealed class SandboxWorkBranchPublishException : InvalidOperationException
{
    public SandboxWorkBranchPublishException(
        string branch,
        string stage,
        string? sourceSha = null,
        string? targetSha = null,
        string? detail = null,
        Exception? inner = null)
        : base(BuildMessage(branch, stage, sourceSha, targetSha, detail, inner), inner)
    {
        Branch = branch;
        Stage = stage;
        SourceSha = sourceSha;
        TargetSha = targetSha;
        Detail = detail;
    }

    /// <summary>Work branch whose publication did not land.</summary>
    public string Branch { get; }

    /// <summary>
    /// Publication stage that failed: <c>push</c>, <c>fetch</c>,
    /// <c>read-target</c>, <c>rebase-ancestry</c>,
    /// <c>push-after-reconcile</c>, <c>verify-publication</c>, or
    /// <c>sync</c>. Never a free-form git/sandbox message: only the raw
    /// exception type name of a wrapped cause is included, never its text.
    /// </summary>
    public string Stage { get; }

    /// <summary>Source tip the publisher tried to publish, when known.</summary>
    public string? SourceSha { get; }

    /// <summary>Target tip observed on the branch, when known.</summary>
    public string? TargetSha { get; }

    /// <summary>
    /// Redacted, truncated diagnostic excerpt for the failing stage (never
    /// raw git/sandbox output: secrets are scrubbed at the throw site, so
    /// this message is safe for logs and operator surfaces).
    /// </summary>
    public string? Detail { get; }

    /// <summary>
    /// Walks the exception chain for this failure shape so the pipeline
    /// recovery contract can classify it without matching on message text.
    /// </summary>
    public static bool TryFindIn(Exception? ex, out SandboxWorkBranchPublishException? found)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is SandboxWorkBranchPublishException match)
            {
                found = match;
                return true;
            }
        }

        found = null;
        return false;
    }

    private static string BuildMessage(
        string branch, string stage, string? sourceSha, string? targetSha, string? detail, Exception? inner)
    {
        var message = $"sandbox work-branch publication failed for branch '{branch}' at stage '{stage}'";
        if (sourceSha is not null)
            message += $" (source {sourceSha})";
        if (targetSha is not null)
            message += $" (target {targetSha})";
        if (detail is not null)
            message += $": {detail}";
        if (inner is not null)
            message += $" (cause: {inner.GetType().Name})";
        return message + "; resolve the cause and retry — the resume checkpoint is retained for a bounded retry";
    }
}

internal sealed class SandboxPushReconcileConflictException : InvalidOperationException
{
    public SandboxPushReconcileConflictException(
        string branch,
        string strategy,
        string? stage = null,
        string? sourceSha = null,
        string? targetSha = null)
        : base(BuildMessage(branch, strategy, stage, sourceSha, targetSha))
    {
        Branch = branch;
        Strategy = strategy;
        Stage = stage;
        SourceSha = sourceSha;
        TargetSha = targetSha;
    }

    public string Branch { get; }
    public string Strategy { get; }
    public string? Stage { get; }
    public string? SourceSha { get; }
    public string? TargetSha { get; }

    /// <summary>
    /// Single source of truth for recognizing the sandbox-publication typed
    /// conflict contract in an exception chain, mirroring
    /// <see cref="CodeyBox.Core.UpstreamPushReconcileConflictException.TryFindIn"/>
    /// for the later upstream boundary. Arbitrary message text never
    /// qualifies — only the typed contract does.
    /// </summary>
    public static bool TryFindIn(
        Exception? source,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SandboxPushReconcileConflictException? conflict)
    {
        for (var current = source; current is not null; current = current.InnerException)
        {
            if (current is SandboxPushReconcileConflictException typed)
            {
                conflict = typed;
                return true;
            }
        }

        conflict = null;
        return false;
    }

    private static string BuildMessage(
        string branch, string strategy, string? stage, string? sourceSha, string? targetSha)
    {
        var message = $"sandbox {strategy} conflict while reconciling push of work branch '{branch}'";
        if (stage is not null)
            message += $" at stage '{stage}'";
        if (sourceSha is not null || targetSha is not null)
            message += $" (source {ToShortSha(sourceSha)} onto target {ToShortSha(targetSha)})";
        return message + "; manual resolution required";
    }

    private static string ToShortSha(string? sha) =>
        sha is not null && sha.Length >= 12 ? sha[..12] : "unknown";
}

internal sealed class MechanicalFixerException : InvalidOperationException
{
    public MechanicalFixerException(string message)
        : base(message)
    {
    }

    public MechanicalFixerException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal sealed record QuestionAskedDetails(
    string WorkItemId,
    string ProjectId,
    string QuestionId,
    string QuestionText);

/// <summary>
/// Structured payload for <c>work_item.needs_operator_input</c> when the
/// park reason is a human deployment review: the operator's endpoint,
/// expiry, and backing question in one place (the full acceptance-criteria
/// brief travels on the question itself).
/// </summary>
internal sealed record HumanReviewParkedDetails(
    string WorkItemId,
    string ProjectId,
    int Iteration,
    string DeploymentId,
    string Endpoint,
    DateTimeOffset ExpiresAt,
    string QuestionId);

public sealed record QuestionAnsweredDetails(
    string WorkItemId,
    string ProjectId,
    string QuestionId,
    string Answer,
    string? AnsweredBy);

public sealed record QuestionDismissedDetails(
    string WorkItemId,
    string ProjectId,
    string QuestionId,
    string Reason);

/// <summary>
/// Webhook payload for <c>agent.fallback</c>: quota exhaustion or a per-attempt
/// timeout triggered the pipeline to retry the same iteration against the next
/// class member. <see cref="ToAgent"/> is null when no fallback was available
/// and the item parked in WaitingForQuotaReset.
/// </summary>
public sealed record AgentFallbackDetails(
    string WorkItemId,
    string Phase,
    int? Iteration,
    string FromAgent,
    string? FromModel,
    string? ToAgent,
    string? ToModel,
    string Reason);

internal sealed record AuditIterationDetails(
    int Iteration,
    int TotalIterations,
    int BlockingFindings,
    int NonBlockingFindings,
    /// <summary>
    /// Set when at least one LLM auditor ran with a different agent than the
    /// work agent (cross-review active). Null when all auditors used the same
    /// agent as the work phase (including after quota/credential fallthrough).
    /// Receivers that do not care about this field ignore it safely.
    /// </summary>
    string? AuditAgentKind = null);

internal sealed record AuditProgressSnapshot(
    int Iteration,
    int MaxIterations,
    int BlockingFindings,
    int NonBlockingFindings,
    IReadOnlyList<string> BlockingFindingIds,
    IReadOnlyList<AuditProgressFinding> BlockingFindingsDetails,
    IReadOnlyList<AuditProgressFinding> Findings,
    string? WorkBranchTip,
    string Status = AuditProgressStatuses.Complete,
    IReadOnlyList<string>? ScheduledAuditors = null,
    IReadOnlyList<string>? CompletedAuditors = null,
    // When this verdict was recorded. Stamped at build time for live-loop
    // snapshots and echoed from the store for loaded history, so park reasons
    // can report the verdict's age and status without a database query. Null
    // when unknown (e.g. test fixtures that predate the field).
    DateTimeOffset? RecordedAt = null)
{
    public bool IsComplete => AuditProgressStatuses.IsComplete(Status);
}

internal sealed record SuggestionWebhookDetails(
    string Id,
    string Title,
    string Category,
    string Severity,
    string EstimatedEffort,
    IReadOnlyList<string> FilesReferenced);

public sealed record PipelineOptions
{
    /// <summary>
    /// Shipped default for <see cref="PhaseAbsoluteTimeoutMultiplier"/>: each
    /// phase's absolute wall-clock cap is its per-attempt timeout times this
    /// multiplier. Shared with <see cref="SandboxLeakOptions.DefaultLeakAgeThreshold"/>:
    /// the leak threshold must stay above the maximum legitimate phase
    /// duration, so raising this default (or
    /// <see cref="CodeyBox.Core.WorkTimeoutPolicy.MaxMinutes"/>) requires
    /// re-checking that bound.
    /// </summary>
    public const double DefaultPhaseAbsoluteTimeoutMultiplier = 3.0;

    public required string SandboxImageReference { get; init; }
    public IReadOnlyList<string> AgentAllowedHosts { get; init; } = [];
    public IReadOnlyList<string> AuditToolAllowedHosts { get; init; } = [];
    public int UpstreamPushMaxAttempts { get; init; } = 5;
    public TimeSpan UpstreamPushBackoff { get; init; } = TimeSpan.FromSeconds(15);
    public HostGitIdentity? HostGitIdentity { get; init; }
    public TimeSpan ShutdownGrace { get; init; } = TimeSpan.FromSeconds(60);
    public double PhaseAbsoluteTimeoutMultiplier { get; init; } = DefaultPhaseAbsoluteTimeoutMultiplier;
    /// <summary>
    /// Ceiling for the in-sandbox required-build work after the verifier
    /// sandbox has been created: repository clone, checkout, and build script.
    /// Sandbox admission wait is queueing and VM provisioning is bounded by the
    /// sandbox provider. On timeout the required-build gate returns a failed
    /// build result rather than an infrastructure-unavailable result.
    /// </summary>
    public TimeSpan RequiredBuildVerificationTimeout { get; init; } = TimeSpan.FromMinutes(15);
    public TimeSpan AuditShutdownDrain => Min(TimeSpan.FromSeconds(60), ShutdownGrace);
    public TimeSpan AgentPreemptSignalTimeout => Min(TimeSpan.FromSeconds(2), ShutdownGrace);
    public TimeSpan AgentPreemptDrain => Min(TimeSpan.FromSeconds(2), ShutdownGrace);
    public TimeSpan PreemptCheckpointDrain => Min(TimeSpan.FromSeconds(30), ShutdownGrace);
    public TimeSpan SandboxPreserveDrain => Min(TimeSpan.FromSeconds(10), ShutdownGrace);

    /// <summary>
    /// Global default for stuck-agent detection threshold, in minutes.
    /// 0 = globally disabled. Per-project <c>Audit.StuckThresholdMinutes</c>
    /// overrides this when set to a non-negative value.
    /// Must be ≥ 1 (or 0 to disable) when non-negative.
    /// </summary>
    public int StuckThresholdMinutes { get; init; } = 10;

    /// <summary>
    /// When true (default), approving a plan emits/reconciles a
    /// <see cref="CodeyBox.Core.TestCase"/> for each declared test scenario,
    /// linked to the work item. Only planned items (the <c>plan</c> knob on)
    /// ever reach the emit path, so unplanned items are unaffected regardless of
    /// this flag; set it false to keep planning on without materialising test
    /// cases. No effect unless an <see cref="Core.ITestCaseStore"/> is wired.
    /// </summary>
    public bool EmitPlanTestCases { get; init; } = true;

    /// <summary>
    /// Upper bound on the tail of agent stdout/stderr retained in failure details (bytes).
    /// Defaults to 32 KiB. Configurable via <c>CodeyBox:MaxFailureDetailBytes</c>.
    /// </summary>
    public int MaxFailureDetailBytes { get; init; } = SanitizedAgentDetail.DefaultTailMaxBytes;

    /// <summary>
    internal TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a <= b ? a : b;
}
