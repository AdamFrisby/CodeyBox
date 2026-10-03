using CodeyBox.Audit;
using CodeyBox.Core;
using CodeyBox.Projects;

namespace CodeyBox.Orchestrator;

// PipelineControlDecisions — the control-plane side of the
// control/execution split defined by the ExecutorPhaseRequest/Result
// contract (see src/CodeyBox.Core/ExecutorPhase.cs).
//
// This type owns the pipeline's decisions and nothing else: the work-item
// state machine's verdict evaluations, audit orchestration (which auditors
// run, in what order, how many iterations, convergence), rework/merge
// verdicts, and escalation message content. It is a static, dependency-free
// cluster of pure functions: inputs in, verdicts out. It must never gain a
// dependency on sandbox or agent-runner types (ISandbox, ISandboxProvider,
// IAgentRunner, IAgentRegistry) — PipelineControlExecutionSplitTests pins
// that with reflection. Anything that needs execution detail to decide
// (auditor-to-runner binding, quota routing, placement) stays on
// PipelineRunner until the phase contract surfaces it explicitly.
//
// PipelineRunner keeps the facade (IPipelineRunner) and the orchestration
// spine; it calls into this type for every decision below. Behaviour is
// unchanged: the bodies moved verbatim.
internal static class PipelineControlDecisions
{
    internal const int AuditEscalationHistoryLimit = 25;
    internal const int AuditEscalationFindingsPerIterationLimit = 20;
    internal const int AuditEscalationFindingDescriptionLimit = 2000;

    // ── Auditor ordering and verdict evaluation ───────────────────────────

    internal static IReadOnlyList<IAuditor> OrderAuditorsForShortCircuit(
        IReadOnlyList<IAuditor> auditors,
        bool auditShortCircuitEnabled)
    {
        if (!auditShortCircuitEnabled || auditors.Count <= 1)
            return auditors;

        return auditors
            .Select((auditor, index) => new { Auditor = auditor, Index = index })
            .OrderBy(x => AuditorOrdering.TierOf(x.Auditor))
            .ThenBy(x => x.Index)
            .Select(x => x.Auditor)
            .ToList();
    }

    internal static bool HasAuditBlockingFinding(AuditResult result, Project project)
        => result.Findings.Any(f => f.Severity >= project.Audit.FailingSeverity);

    internal static bool IsDeclaredShortCircuitBlockingResult(AuditResult result)
        => !result.Passed || result.Findings.Any(f => f.Severity == AuditSeverity.Error);

    internal static bool RequiresPassedBuildTestGate(IAuditor auditor)
        => auditor is IRequiresPassedBuildTestGate
           || string.Equals(auditor.Kind, "llm", StringComparison.OrdinalIgnoreCase);

    internal static int BuildTestGateOrderingTier(IAuditor auditor)
    {
        var evidence = auditor.BuildTestGateEvidence;
        if ((evidence & BuildTestGateEvidence.Build) == BuildTestGateEvidence.Build)
            return 0;
        if ((evidence & BuildTestGateEvidence.Test) == BuildTestGateEvidence.Test)
            return 1;
        return 2;
    }

    internal static int BatchOrderingTier(IAuditor auditor, bool detectDeclaredShortCircuit)
    {
        if (auditor.Role == AuditorRole.BuildTestGate)
            return 0;
        if (detectDeclaredShortCircuit && auditor.CanShortCircuitOnBlockingFinding)
            return 1;
        return 2;
    }

    internal static AuditFinding MissingBuildTestGateFinding(IReadOnlyList<IAuditor> gatedReviewAuditors)
    {
        var auditorList = string.Join(", ", gatedReviewAuditors.Select(a => a.Name));
        return new AuditFinding(
            AuditorName: "audit:build-test-gate",
            Severity: AuditSeverity.Error,
            Title: "build/test-gated auditor skipped because no verified build/test gate passed",
            Description: $"The configured build/test-gated auditor(s) require verified deterministic build and test evidence before they can run: {auditorList}. Configure build/test auditor(s) with role 'build-test-gate' and gateEvidence 'build-and-test', or separate 'build' and 'test' gates, that actually run and pass before the gated auditor(s).");
    }

    // NOTE: NormalizeBuildTestGateRun, BuildTestGatePassEvidence,
    // IsOptionalSkippedBuildTestGate, and HasPassedBuildAndTestGateEvidence
    // stay on PipelineRunner (see CollectFindingsBatchAsync above): they
    // operate on the private AuditorRunRecord/AuditorBatchResult batch types,
    // which bundle the auditor with its bound IAgentRunner. Moving them to
    // PipelineControlDecisions would drag an agent-runner dependency into the
    // control plane. See docs/concepts/pipeline-control-execution-split.md.

    internal static AuditResult SkippedGateConsumerResult(
        IAuditor auditor,
        BuildTestGateEvidence missingEvidence)
    {
        var description =
            $"skipped: {missingEvidence} gate evidence was not produced this iteration, so the auditor was not run. " +
            "The failing gate's own findings drive the rework verdict; running anyway could only surface a " +
            "derived runner failure against absent build outputs.";
        return new AuditResult(
            Passed: false,
            Findings:
            [
                new AuditFinding(
                    auditor.Name,
                    AuditSeverity.Warning,
                    "skipped: build failed",
                    description),
            ],
            RawOutput: description)
        {
            BuildTestGateEvidenceVerified = false,
        };
    }

    internal static AuditResult NormalizePlanReviewRunResult(
        IAuditor auditor,
        AuditContext ctx,
        AuditResult result)
    {
        if (!AuditTargetSemantics.IsPlanReview(ctx.EffectiveTarget)
            || result.Passed
            || result.Findings.Any(f => f.Severity == AuditSeverity.Error))
        {
            return result;
        }

        return result with
        {
            Findings =
            [
                .. result.Findings,
                new AuditFinding(
                    auditor.Name,
                    AuditSeverity.Error,
                    "plan rejected by reviewer",
                    "The plan reviewer returned an explicit reject verdict (passed=false) without an error-severity finding."),
            ],
        };
    }

    // ── Iteration budgets and audit history ───────────────────────────────

    internal static IReadOnlyList<string> MissingCompletedAuditors(
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

    internal static int ResolveAuditMaxIterations(
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

    internal static int ResolveConfiguredAuditMaxIterations(WorkItem item, Project project)
    {
        var projectBudget = ResolveProjectAuditIterationBudget(project);
        return Math.Min(
            ProjectAudit.MaxIterationBudget,
            ResolveConfiguredAuditIterationBudget(item, project.Audit, projectBudget));
    }

    internal static int ResolveProjectAuditIterationBudget(Project project)
        => Math.Clamp(project.Audit.MaxIterations, 1, ProjectAudit.MaxIterationBudget);

    internal static bool HasIncompleteFinalReworkExtension(
        IReadOnlyList<AuditProgressSnapshot> priorAuditHistory,
        int configuredMaxIterations)
        => priorAuditHistory.Any(progress =>
            !progress.IsComplete
            && AuditProgressRequiresRework(progress)
            && progress.Iteration >= configuredMaxIterations);

    internal static int ResolveConfiguredAuditIterationBudget(
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

    internal static int ResolveAuditBudgetOverrideCap(ProjectAudit audit, int projectBudget)
        => Math.Clamp(
            audit.BudgetOverrideMaxIterations.GetValueOrDefault(projectBudget),
            projectBudget,
            ProjectAudit.MaxIterationBudget);

    internal static int? ResolveComplexityAuditIterationBudget(string? complexity, ProjectAudit audit)
        => string.IsNullOrWhiteSpace(complexity)
            ? null
            : audit.ComplexityIterationBudgets.TryGetValue(complexity.Trim(), out var budget) && budget > 0
                ? budget
                : null;

    // ── Convergence, findings, and escalation content ─────────────────────

    internal static bool HasAuditConvergenceProgress(IReadOnlyList<AuditProgressSnapshot> history)
        => BuildAuditProgressSignals(history).Count > 0;

    internal static bool AuditProgressRequiresRework(AuditProgressSnapshot progress)
        // A rework iteration only makes sense when something is blocking the
        // merge. Zero-blocking snapshots (pass verdicts, or partial in-progress
        // snapshots holding advisory findings only) must not dispatch rework:
        // there are no changes for the agent to make, so the pass would come
        // back empty and wedge the item in an empty-rework park loop. Final
        // incomplete verdicts that need attention already promote their
        // findings to blocking at record time, so they still carry
        // BlockingFindings > 0 here.
        => progress.BlockingFindings > 0;

    internal static bool IsNonAdvancingDelegationOutcome(string outcome) =>
        string.Equals(outcome, DelegationOutcomes.NoChanges, StringComparison.Ordinal)
        || string.Equals(outcome, DelegationOutcomes.Failed, StringComparison.Ordinal);

    internal static IReadOnlyList<string> FingerprintFindings(IReadOnlyList<AuditFinding> findings)
        => findings
            .Select(f =>
            {
                var (files, _) = FindingIdComputer.ParseLocation(f.Location);
                return FindingIdComputer.Compute(f.AuditorName, f.Title, files);
            })
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

    internal static AuditProgressFinding ToProgressFinding(AuditFinding finding) => new(
        finding.AuditorName,
        finding.Severity,
        finding.Title,
        finding.Description,
        finding.Location);

    internal static AuditFinding ToAuditFinding(AuditProgressFinding finding) => new(
        finding.AuditorName,
        finding.Severity,
        finding.Title,
        finding.Description,
        finding.Location);

    internal static AuditFindingPayload ToEscalationWebhookFinding(AuditProgressFinding finding) => new()
    {
        Auditor = finding.AuditorName,
        Severity = finding.Severity.ToString(),
        Title = finding.Title,
        Description = TruncateForEscalation(finding.Description),
        Location = finding.Location,
    };

    internal static string TruncateForEscalation(string value)
        => value.Length <= AuditEscalationFindingDescriptionLimit
            ? value
            : value[..AuditEscalationFindingDescriptionLimit] + "...";

    internal static IReadOnlyList<string> BuildAuditProgressSignals(IReadOnlyList<AuditProgressSnapshot> history)
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

    internal static IReadOnlyList<AuditProgressFinding> BlockingProgressFindingsForSummary(AuditProgressSnapshot progress) =>
        new PromptComposer().BlockingProgressFindingsForSummary(progress);

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

    internal static string FormatAuditVerdictProvenance(AuditProgressSnapshot snapshot, DateTimeOffset? now = null) =>
        new PromptComposer().FormatAuditVerdictProvenance(snapshot, now);

    internal static string FormatVerdictAge(TimeSpan age) =>
        new PromptComposer().FormatVerdictAge(age);

    internal static (int Count, string Summary) BuildBlockingFindingSummary(AuditProgressSnapshot snapshot) =>
        new PromptComposer().BuildBlockingFindingSummary(snapshot);

    internal static string BuildEmptyReworkEscalationPrompt(
        string originalPrompt,
        int attempt,
        int totalAttempts)
    {
        var header = $"""
            [empty-rework escalation attempt {attempt}/{totalAttempts}]
            Your previous pass committed NO changes. You MUST modify files to
            address the listed audit findings, or for each finding state precisely
            why it is invalid/already-satisfied. If all escalation attempts are
            exhausted without a commit, this work item will park for operator review.

            """;
        return string.IsNullOrEmpty(originalPrompt) ? header : header + originalPrompt;
    }

    internal static AuditMaxIterationsEscalationDetails BuildAuditMaxIterationEscalationDetails(
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

    internal static IReadOnlyList<AuditProgressIterationDetails> BuildAuditProgressIterationDetails(
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

    // ── Work-item admission validation ────────────────────────────────────

    internal static string? ValidateAgentControlSpec(AgentControlSpec spec)
    {
        if (string.IsNullOrWhiteSpace(spec.Agent))
            return "agentControl.agent is required";

        switch (spec.Action)
        {
            case AgentControlAction.Pause:
                if (string.IsNullOrWhiteSpace(spec.Reason))
                    return "agentControl.reason is required for pause";
                if (AgentPauseValidation.ValidateOptionalReason(spec.Reason, "agentControl.reason") is { } pauseReasonError)
                    return pauseReasonError;
                break;
            case AgentControlAction.Resume:
                if (AgentPauseValidation.ValidateOptionalReason(spec.Reason, "agentControl.reason") is { } resumeReasonError)
                    return resumeReasonError;
                break;
            default:
                return $"unsupported agentControl action '{spec.Action}'";
        }

        if (spec.DurationSeconds is { } seconds && seconds <= 0)
            return "agentControl.durationSeconds must be positive";
        if (spec.DurationSeconds is not null && spec.ExpiresAt is not null)
            return "agentControl: provide either durationSeconds or expiresAt, not both";
        if (spec.ExpiresAt is { } expiresAt && expiresAt <= DateTimeOffset.UtcNow)
            return "agentControl.expiresAt must be in the future";

        return null;
    }
}
