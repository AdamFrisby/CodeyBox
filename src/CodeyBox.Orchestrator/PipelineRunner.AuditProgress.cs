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

// PipelineRunner.AuditProgress.cs — Audit-progress persistence: snapshots, verdict selection, and merge-gate history.
public sealed partial class PipelineRunner
{
    private async Task EnsureCurrentRealAuditPassBeforeMergeAsync(
        WorkItem item,
        bool auditGateConfigured,
        bool currentRunAuditPass,
        CancellationToken ct)
    {
        if (!auditGateConfigured)
            return;

        if (!currentRunAuditPass)
        {
            throw new AuditUnavailableException(
                $"work item {item.Id} cannot merge because no audit pass was produced in this pipeline pickup");
        }

        if (_auditProgress is null)
            return;

        var currentWorkAttemptStartedAt = await ResolveCurrentWorkAttemptStartedAtAsync(item.Id, ct);
        IReadOnlyList<AuditProgressRecord> records;
        try
        {
            records = await _auditProgress.GetAuditProgressAsync(item.Id, currentWorkAttemptStartedAt, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new AuditHistoryLoadFailedException(
                $"failed to load latest audit progress for merge gate on work item {item.Id}",
                ex);
        }

        // Select the latest COMPLETE record for the highest iteration in this
        // work-attempt partition. Rows left in_progress/incomplete by an
        // interrupted run (host restart, cancellation) are superseded — never
        // authoritative — so the gate ignores them instead of blocking the
        // merge on a frozen partial verdict or, worse, accepting one.
        // On a resume that discards a passing/escaped prior verdict,
        // RunAuditLoopAsync purges the stale prior-run rows before the fresh
        // audit restarts at iteration 1, so the highest surviving iteration is
        // always this pickup's audit — max-iteration and "most recent" agree.
        // The store's UNIQUE(work_item_id, work_attempt_started_at, iteration)
        // key means at most one row per iteration; the Index tiebreaker is a
        // defensive no-op kept only to make the ordering total.
        var latest = SelectMergeGateVerdict(records);
        if (latest is null)
        {
            throw new AuditUnavailableException(
                $"work item {item.Id} cannot merge because no completed audit progress record exists for this pickup");
        }

        if (latest.BlockingFindings > 0)
        {
            throw new AuditUnavailableException(
                $"work item {item.Id} cannot merge because latest audit iteration {latest.Iteration} still has {latest.BlockingFindings} blocking finding(s)");
        }

        var missingAuditors = PipelineControlDecisions.MissingCompletedAuditors(latest.ScheduledAuditors, latest.CompletedAuditors);
        if (missingAuditors.Count > 0)
        {
            throw new AuditUnavailableException(
                $"work item {item.Id} cannot merge because latest audit iteration {latest.Iteration} did not complete auditor(s): {string.Join(", ", missingAuditors)}");
        }

        // Scan both the non-blocking Findings and the BlockingFindingsDetails:
        // an infrastructure "review agent failed to run" result can surface in
        // either collection depending on how it was classified, and the merge
        // gate must reject it regardless.
        if (HasLlmAgentExecutionFailureSentinel(latest.Findings, f => f.Title)
            || HasLlmAgentExecutionFailureSentinel(latest.BlockingFindingsDetails, f => f.Title))
        {
            throw new AuditUnavailableException(
                $"work item {item.Id} cannot merge because latest audit iteration {latest.Iteration} contains review-agent infrastructure failure results");
        }

        if (await LatestAuditReportIterationHasLlmAgentExecutionFailureAsync(item.Id, latest.Iteration, ct))
        {
            throw new AuditUnavailableException(
                $"work item {item.Id} cannot merge because latest audit report iteration {latest.Iteration} contains review-agent infrastructure failure results");
        }
    }

    /// <summary>
    /// Runs the post-implementation e2e-replay gate for the work item and throws
    /// <see cref="E2eReplayGateBlockedException"/> when a declared e2e-replay case
    /// could not be made green. A no-op when no gate is wired; the gate itself is
    /// a no-op when its <c>Enabled</c> knob is off (returns a disabled result).
    /// </summary>
    private async Task EnforceE2eReplayGateBeforeMergeAsync(WorkItem item, CancellationToken ct)
    {
        if (_e2eReplayGate is null)
            return;

        var result = await _e2eReplayGate.EvaluateAsync(item.Id, ct);
        if (!result.Enabled)
            return;

        if (result.Blocked)
        {
            var detail = string.Join("; ", result.Blockers.Select(b => $"'{b.Name}' ({b.TestCaseId}): {b.Reason}"));
            throw new E2eReplayGateBlockedException(
                $"work item {item.Id} cannot merge because {result.Blockers.Count} declared e2e-replay case(s) lack a working committed replay: {detail}");
        }

        if (result.VerifiedCaseIds.Count > 0)
            _log.LogInformation(
                "E2E replay gate cleared work item {WorkItemId}: {VerifiedCount} declared e2e case(s) have a green committed replay.",
                item.Id, result.VerifiedCaseIds.Count);
    }

    private async Task<bool> LatestAuditReportIterationHasLlmAgentExecutionFailureAsync(
        WorkItemId workItemId,
        int iteration,
        CancellationToken ct)
    {
        if (_auditReports is null)
            return false;

        IReadOnlyList<AuditReport> reports;
        try
        {
            reports = await _auditReports.GetByWorkItemAsync(
                workItemId.ToString(), AuditTarget.Code, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(
                ex,
                "Failed to load diagnostic audit reports for merge gate on work item {WorkItemId}; relying on durable audit progress",
                workItemId);
            return false;
        }

        return reports
            .Where(r => r.Target == AuditTarget.Code && r.Iteration == iteration)
            .GroupBy(r => r.AuditorName, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(r => r.StartedAt).First())
            .Any(r => HasLlmAgentExecutionFailureSentinel(r.Findings, f => f.Title));
    }

    private async Task<DateTimeOffset?> ResolveCurrentWorkAttemptStartedAtAsync(
        WorkItemId workItemId,
        CancellationToken ct)
    {
        var iterations = await _store.GetIterationsAsync(workItemId, ct);
        return iterations
            .Where(i => i.Iteration == AuditProgressIterationNumbers.WorkPhase)
            .OrderByDescending(i => i.DispatchedAt)
            .Select(i => (DateTimeOffset?)i.DispatchedAt)
            .FirstOrDefault();
    }

    private async Task<string?> TryResolveWorkBranchTipAsync(
        string repoId,
        string workBranch,
        CancellationToken ct)
    {
        try
        {
            return await _gitHost.ResolveCommitAsync(repoId, $"refs/heads/{workBranch}", ct);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            _log.LogDebug(ex, "Could not resolve work branch tip for audit progress detection in repo {RepoId} branch {WorkBranch}", repoId, workBranch);
            return null;
        }
    }

    // Control/execution seam compatibility: these decisions now live on
    // PipelineControlDecisions. The one-line forwarders below stay so the
    // existing suite (which pins PipelineRunner.X) passes unmodified; new
    // code must call PipelineControlDecisions directly.
    internal static bool AuditProgressRequiresRework(AuditProgressSnapshot progress)
        => PipelineControlDecisions.AuditProgressRequiresRework(progress);

    internal static IReadOnlyList<AuditProgressFinding> BlockingProgressFindingsForSummary(AuditProgressSnapshot progress) =>
        PipelineControlDecisions.BlockingProgressFindingsForSummary(progress);

    internal static string BuildAuditMaxIterationEscalationMessage(
        IReadOnlyList<AuditProgressSnapshot> history,
        DateTimeOffset? now = null) =>
        PipelineControlDecisions.BuildAuditMaxIterationEscalationMessage(history, now);

    internal static string FormatVerdictAge(TimeSpan age) =>
        PipelineControlDecisions.FormatVerdictAge(age);

    internal static (int Count, string Summary) BuildBlockingFindingSummary(AuditProgressSnapshot snapshot) =>
        PipelineControlDecisions.BuildBlockingFindingSummary(snapshot);

    internal static string BuildEmptyReworkEscalationMessage(
        IReadOnlyList<AuditProgressSnapshot> history,
        AgentKind agent,
        int reworkIterationNumber,
        int attempts,
        bool converging,
        DateTimeOffset? now = null) =>
        PipelineControlDecisions.BuildEmptyReworkEscalationMessage(
            history, agent, reworkIterationNumber, attempts, converging, now);

    /// <summary>
    /// Deployment-stage outcome for one audit iteration. Null (rather than an
    /// empty outcome) signals "phase skipped" so the caller can distinguish
    /// "no deployment auditors" from "auditors ran clean".
    /// </summary>
    private sealed record DeploymentStageOutcome(
        IReadOnlyList<AuditFinding> Findings,
        IReadOnlyList<AuditFinding> Blocking,
        IReadOnlyList<string> CompletedAuditors,
        IReadOnlyList<string> IncompleteAuditors,
        AgentKind? ActiveAuditAgentKind,
        bool DeclaredShortCircuitBlocking,
        bool IncompleteVerdict,
        DeploymentEndpoint Endpoint);

}
