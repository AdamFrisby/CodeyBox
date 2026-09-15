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

        var missingAuditors = MissingCompletedAuditors(latest.ScheduledAuditors, latest.CompletedAuditors);
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

    private static IReadOnlyList<string> MissingCompletedAuditors(
        IReadOnlyList<string>? scheduledAuditors,
        IReadOnlyList<string>? completedAuditors)
    {
        if (scheduledAuditors is null || scheduledAuditors.Count == 0)
            return [];

        var completed = new HashSet<string>(completedAuditors ?? [], StringComparer.Ordinal);
        return scheduledAuditors
            .Where(a => !completed.Contains(a))
            .Distinct(StringComparer.Ordinal)
            .ToList();
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

    private static int ResolveAuditMaxIterations(
        WorkItem item,
        Project project,
        IReadOnlyList<AuditProgressSnapshot> priorAuditHistory)
    {
        var projectBudget = ResolveProjectAuditIterationBudget(project);
        var maxIterations = ResolveConfiguredAuditMaxIterations(item, project);

        if (priorAuditHistory.Count > 0)
        {
            // Retrying an item parked at the audit ceiling is an explicit
            // operator re-drive, so continue from the prior trajectory even
            // when static per-item budget overrides are capped at project
            // defaults.
            var priorMaxIteration = priorAuditHistory.Max(h => h.Iteration);
            maxIterations = Math.Max(maxIterations, priorMaxIteration + projectBudget);
        }

        return Math.Min(ProjectAudit.MaxIterationBudget, maxIterations);
    }

    private static int ResolveConfiguredAuditMaxIterations(WorkItem item, Project project)
    {
        var projectBudget = ResolveProjectAuditIterationBudget(project);
        return Math.Min(
            ProjectAudit.MaxIterationBudget,
            ResolveConfiguredAuditIterationBudget(item, project.Audit, projectBudget));
    }

    private static int ResolveProjectAuditIterationBudget(Project project)
        => Math.Clamp(project.Audit.MaxIterations, 1, ProjectAudit.MaxIterationBudget);

    private static bool HasIncompleteFinalReworkExtension(
        IReadOnlyList<AuditProgressSnapshot> priorAuditHistory,
        int configuredMaxIterations)
        => priorAuditHistory.Any(progress =>
            !progress.IsComplete
            && AuditProgressRequiresRework(progress)
            && progress.Iteration >= configuredMaxIterations);

    private static int ResolveConfiguredAuditIterationBudget(
        WorkItem item,
        ProjectAudit audit,
        int projectBudget)
    {
        var overrideCap = ResolveAuditBudgetOverrideCap(audit, projectBudget);
        var requestedOverride = Math.Max(
            item.AuditMaxIterations.GetValueOrDefault(),
            ResolveComplexityAuditIterationBudget(item.AuditComplexity, audit).GetValueOrDefault());
        return Math.Max(projectBudget, Math.Min(overrideCap, requestedOverride));
    }

    private static int ResolveAuditBudgetOverrideCap(ProjectAudit audit, int projectBudget)
        => Math.Clamp(
            audit.BudgetOverrideMaxIterations.GetValueOrDefault(projectBudget),
            projectBudget,
            ProjectAudit.MaxIterationBudget);

    private static int? ResolveComplexityAuditIterationBudget(string? complexity, ProjectAudit audit)
        => string.IsNullOrWhiteSpace(complexity)
            ? null
            : audit.ComplexityIterationBudgets.TryGetValue(complexity.Trim(), out var budget) && budget > 0
                ? budget
                : null;

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

    private static IReadOnlyList<string> FingerprintFindings(IReadOnlyList<AuditFinding> findings)
        => findings
            .Select(f =>
            {
                var (files, _) = FindingIdComputer.ParseLocation(f.Location);
                return FindingIdComputer.Compute(f.AuditorName, f.Title, files);
            })
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

    private static AuditProgressFinding ToProgressFinding(AuditFinding finding) => new(
        finding.AuditorName,
        finding.Severity,
        finding.Title,
        finding.Description,
        finding.Location);

    private static AuditFinding ToAuditFinding(AuditProgressFinding finding) => new(
        finding.AuditorName,
        finding.Severity,
        finding.Title,
        finding.Description,
        finding.Location);

    private static AuditFindingPayload ToEscalationWebhookFinding(AuditProgressFinding finding) => new()
    {
        Auditor = finding.AuditorName,
        Severity = finding.Severity.ToString(),
        Title = finding.Title,
        Description = TruncateForEscalation(finding.Description),
        Location = finding.Location,
    };

    private static string TruncateForEscalation(string value)
        => value.Length <= AuditEscalationFindingDescriptionLimit
            ? value
            : value[..AuditEscalationFindingDescriptionLimit] + "...";

    // Cold-tier extraction forwarder: implementation lives on PromptComposer.
    internal static string BuildAuditMaxIterationEscalationMessage(
        IReadOnlyList<AuditProgressSnapshot> history,
        DateTimeOffset? now = null) =>
        new PromptComposer().BuildAuditMaxIterationEscalationMessage(history, now);

    internal static string BuildEmptyReworkEscalationMessage(
        IReadOnlyList<AuditProgressSnapshot> history,
        AgentKind agent,
        int reworkIterationNumber,
        int attempts,
        bool converging,
        DateTimeOffset? now = null)
    {
        var last = history[^1];
        var remaining = BuildBlockingFindingSummary(last);
        var retrySummary = attempts == 0
            ? "without an escalation retry"
            : $"after {attempts} escalation retry attempt(s)";
        var progressSummary = converging
            ? "audit history was converging"
            : "audit history had not established convergence yet";

        return
            $"Rework agent {agent.Value} produced no changes on rework iteration {reworkIterationNumber} {retrySummary}; " +
            $"{progressSummary}. Parked for operator review instead of hard-failing on a blank in-budget rework pass. " +
            $"{remaining.Count} blocking finding(s) remain after audit iteration {last.Iteration}/{last.MaxIterations} ({last.NonBlockingFindings} non-blocking advisory finding(s) also recorded)" +
            (remaining.Count == 0 ? "." : $": {remaining.Summary}") +
            $" {FormatAuditVerdictProvenance(last, now)}";
    }

    // Cold-tier extraction forwarder: implementation lives on PromptComposer.
    internal static string FormatAuditVerdictProvenance(AuditProgressSnapshot snapshot, DateTimeOffset? now = null) =>
        new PromptComposer().FormatAuditVerdictProvenance(snapshot, now);

    // Cold-tier extraction forwarder: implementation lives on PromptComposer.
    internal static string FormatVerdictAge(TimeSpan age) =>
        new PromptComposer().FormatVerdictAge(age);

    // Cold-tier extraction forwarder: implementation lives on PromptComposer.
    internal static (int Count, string Summary) BuildBlockingFindingSummary(AuditProgressSnapshot snapshot) =>
        new PromptComposer().BuildBlockingFindingSummary(snapshot);

    private static AuditMaxIterationsEscalationDetails BuildAuditMaxIterationEscalationDetails(
        WorkItemId workItemId,
        IReadOnlyList<AuditProgressSnapshot> history)
    {
        var last = history[^1];
        var signals = BuildAuditProgressSignals(history);
        return new AuditMaxIterationsEscalationDetails
        {
            WorkItemId = workItemId.ToString(),
            Iteration = last.Iteration,
            MaxIterations = last.MaxIterations,
            BlockingFindings = last.BlockingFindings,
            NonBlockingFindings = last.NonBlockingFindings,
            ProgressObserved = signals.Count > 0,
            ProgressSignals = signals,
            History = BuildAuditProgressIterationDetails(history),
            RemainingBlockingFindings = BlockingProgressFindingsForSummary(last)
                .Take(AuditEscalationFindingsPerIterationLimit)
                .Select(ToEscalationWebhookFinding)
                .ToList(),
            ResumeHint = "Use POST /workitems/{id}/retry with from omitted or from='audit' to continue from the existing work branch.",
            VerdictStatus = last.Status,
            VerdictRecordedAt = last.RecordedAt,
        };
    }

    private static IReadOnlyList<AuditProgressIterationDetails> BuildAuditProgressIterationDetails(
        IReadOnlyList<AuditProgressSnapshot> history)
        => history.TakeLast(AuditEscalationHistoryLimit)
            .Select(h => new AuditProgressIterationDetails
            {
                Iteration = h.Iteration,
                BlockingFindings = h.BlockingFindings,
                NonBlockingFindings = h.NonBlockingFindings,
                Status = h.Status,
                RecordedAt = h.RecordedAt,
                BlockingFindingsDetails = h.BlockingFindingsDetails
                    .Take(AuditEscalationFindingsPerIterationLimit)
                    .Select(ToEscalationWebhookFinding)
                    .ToList(),
                Findings = h.Findings
                    .Take(AuditEscalationFindingsPerIterationLimit)
                    .Select(ToEscalationWebhookFinding)
                    .ToList(),
            })
            .ToList();

    private static IReadOnlyList<string> BuildAuditProgressSignals(IReadOnlyList<AuditProgressSnapshot> history)
    {
        if (history.Count < 2)
            return [];

        var last = history[^1];
        var signals = new List<string>();
        if (history.Take(history.Count - 1).Any(h => h.BlockingFindings > last.BlockingFindings))
            signals.Add("blocking_findings_decreased");

        var lastTotal = last.BlockingFindings + last.NonBlockingFindings;
        if (history.Take(history.Count - 1).Any(h => h.BlockingFindings + h.NonBlockingFindings > lastTotal))
            signals.Add("total_findings_decreased");

        var lastIds = last.BlockingFindingIds.ToHashSet(StringComparer.Ordinal);
        if (history.Take(history.Count - 1).Any(h => !h.BlockingFindingIds.ToHashSet(StringComparer.Ordinal).SetEquals(lastIds)))
            signals.Add("blocking_findings_changed");

        if (last.WorkBranchTip is { } lastTip
            && history.Take(history.Count - 1).Any(h => h.WorkBranchTip is { } tip && !string.Equals(tip, lastTip, StringComparison.Ordinal)))
            signals.Add("work_branch_tip_changed");

        return signals;
    }

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
