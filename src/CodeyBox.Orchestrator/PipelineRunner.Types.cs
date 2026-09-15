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

internal sealed class RequiredBuildFailedException : Exception
{
    public RequiredBuildFailedException(string message) : base(message) { }
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

internal sealed class SandboxPushReconcileConflictException : InvalidOperationException
{
    public SandboxPushReconcileConflictException(string branch, string strategy)
        : base($"sandbox {strategy} conflict while reconciling push of work branch '{branch}'; manual resolution required")
    {
        Branch = branch;
        Strategy = strategy;
    }

    public string Branch { get; }
    public string Strategy { get; }
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
    public required string SandboxImageReference { get; init; }
    public IReadOnlyList<string> AgentAllowedHosts { get; init; } = [];
    public IReadOnlyList<string> AuditToolAllowedHosts { get; init; } = [];
    public int UpstreamPushMaxAttempts { get; init; } = 5;
    public TimeSpan UpstreamPushBackoff { get; init; } = TimeSpan.FromSeconds(15);
    public HostGitIdentity? HostGitIdentity { get; init; }
    public TimeSpan ShutdownGrace { get; init; } = TimeSpan.FromSeconds(60);
    public double PhaseAbsoluteTimeoutMultiplier { get; init; } = 3.0;
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
