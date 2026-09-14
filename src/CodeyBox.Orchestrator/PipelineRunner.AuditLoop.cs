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

// PipelineRunner.AuditLoop.cs — Audit/rework loop core: iteration driver, rework execution, and session outcome metrics.
public sealed partial class PipelineRunner
{
    private async Task<bool> RunAuditLoopAsync(
        WorkItem item,
        Project project,
        IAgentRunner runner,
        IReadOnlyList<IAuditor> auditors,
        string repoId,
        string baseBranch,
        string workBranch,
        bool selfReviewChecklistEnabled,
        CancellationToken ct,
        CancellationToken hostShutdownToken)
    {
        var currentWorkAttemptStartedAt = await ResolveCurrentWorkAttemptStartedAtAsync(item.Id, ct);
        var priorAuditHistory = await LoadPersistedAuditProgressHistoryAsync(item, currentWorkAttemptStartedAt, ct);
        if (priorAuditHistory is [.., var latestPrior] && !AuditProgressRequiresRework(latestPrior))
        {
            _log.LogInformation(
                "Ignoring persisted passing audit iteration {Iteration} for work item {Id}; merge requires a fresh audit pass in this pickup",
                latestPrior.Iteration,
                item.Id);
            priorAuditHistory = [];

            // Purge the stale prior-run rows before the fresh audit restarts at
            // iteration 1. Without this, the fresh iteration-1 upsert only
            // overwrites the stale iteration-1 row and stale iterations 2..N
            // survive in the same work-attempt partition — so the merge gate,
            // which validates the highest-iteration record, would read a stale
            // (possibly "review agent failed to run") verdict instead of the
            // fresh pass and either block the item forever or ship an unreviewed
            // verdict. See EnsureCurrentRealAuditPassBeforeMergeAsync.
            if (_auditProgress is not null)
            {
                try
                {
                    var purged = await _auditProgress.PurgeAuditProgressAsync(
                        item.Id, currentWorkAttemptStartedAt, ct);
                    if (purged > 0)
                        _log.LogInformation(
                            "Purged {Count} stale audit-progress row(s) for work item {Id} before fresh re-audit",
                            purged,
                            item.Id);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new AuditHistoryPersistenceFailedException(
                        $"failed to purge stale audit progress for work item {item.Id} before fresh re-audit; " +
                        "cannot safely re-audit without removing the stale prior-run verdict",
                        ex);
                }
            }
        }
        var configuredMaxIterations = ResolveConfiguredAuditMaxIterations(item, project);
        var maxIterations = ResolveAuditMaxIterations(item, project, priorAuditHistory);
        var incompleteFinalReworkExtensionUsed = HasIncompleteFinalReworkExtension(
            priorAuditHistory,
            configuredMaxIterations);
        var auditHistory = priorAuditHistory
            .Select(h => h with { MaxIterations = maxIterations })
            .ToList();
        var startIteration = auditHistory.Count == 0 ? 1 : auditHistory.Max(h => h.Iteration) + 1;

        // Human-review resume: a parked iteration never reached a verdict, so
        // any persisted rows at/after its iteration are partial crash-style
        // residue (in-progress snapshots, partial code findings), not rework
        // evidence. Drop them from the in-memory history so the loop restarts
        // at the parked iteration and the deployment stage consumes the
        // recorded verdict (or re-parks while undecided) instead of
        // misfiring the missing-audit resume-rework path. Earlier complete
        // iterations stay as rework context. The stale rows are overwritten
        // when the resumed iteration persists its final snapshot (the store
        // upserts by (work item, attempt, iteration)). The check runs
        // whenever the review store is wired (one indexed read per audit-loop
        // entry — the loop itself only receives code-target auditors, so a
        // human-kind auditor is never in `auditors`; the deployment stage
        // composes its own panel). A mid-flight knob change that uncomposes
        // the reviewer orphans the pending review to the expiry sweeper.
        var humanResumeReview = await LoadActiveHumanReviewAsync(item.Id, ct);
        if (humanResumeReview is not null
            && currentWorkAttemptStartedAt is not null
            && humanResumeReview.RequestedAt < currentWorkAttemptStartedAt)
        {
            // Stale across work attempts: a retry re-ran the work phase after
            // the park, so the reviewed code may be gone. A verdict must
            // never apply across attempts — expire the review fail-closed
            // (bounded teardown, no silent pass) and audit the fresh code.
            _log.LogWarning(
                "Work item {Id}: human deployment review for iteration {Iteration} predates the current work attempt; expiring it and auditing fresh",
                item.Id, humanResumeReview.Iteration);
            await ExpireStaleHumanReviewAsync(item, humanResumeReview, ct).ConfigureAwait(false);
            humanResumeReview = null;
        }

        if (humanResumeReview is not null && humanResumeReview.Iteration >= startIteration)
        {
            var dropped = auditHistory.RemoveAll(h => h.Iteration >= humanResumeReview.Iteration);
            if (dropped > 0)
                _log.LogInformation(
                    "Work item {Id}: superseded {Count} partial audit-progress row(s) at/after parked human-review iteration {Iteration}",
                    item.Id, dropped, humanResumeReview.Iteration);
            startIteration = auditHistory.Count == 0 ? 1 : auditHistory.Max(h => h.Iteration) + 1;
        }

        if (startIteration > maxIterations)
            return await HandleExhaustedPersistedAuditHistoryAsync(item, project, auditHistory, ct);

        var resumeReworkParked = await RunMissingAuditResumeReworkAsync(
            item, project, runner, repoId, baseBranch, workBranch,
            auditHistory, startIteration, maxIterations, ct, hostShutdownToken);
        if (resumeReworkParked) return true;

        for (var iteration = startIteration; iteration <= maxIterations; iteration++)
        {
            if (hostShutdownToken.IsCancellationRequested)
                throw new OperationCanceledException(hostShutdownToken);

            // Human-review resume: the parked iteration was code-clean, so
            // re-running the rebase, mechanical fixers, and code stage would
            // burn quota, mutate the reviewed tree, and risk diverging from
            // the held deployment. Skip them; the deployment stage below
            // consumes the recorded verdict (or re-parks while undecided).
            var resumeHumanReview = humanResumeReview is not null
                && humanResumeReview.Iteration == iteration;
            if (!resumeHumanReview && iteration > 1)
                await MaybeIncrementalRebaseAsync(item, runner, repoId, baseBranch, workBranch, project, ct);

            if (!resumeHumanReview)
                await RunMechanicalFixersAsync(
                    item,
                    project,
                    repoId,
                    baseBranch,
                    workBranch,
                    auditors,
                    iteration,
                    ct,
                    hostShutdownToken);

            // Per-iteration audit phase scope. Disposed explicitly before the
            // rework scope (below) so codeybox.phase.duration_ms{phase=audit}
            // measures only the auditing work — not nested rework or later
            // iterations. The `using` still guarantees disposal on the pass
            // (return) and exhausted (throw) paths.
            using var auditPhaseScope = BeginPhaseScope(item, "audit");

            var auditShortCircuitEnabled = _pipelineTuning.Current.AuditShortCircuitEnabled;
            var scheduledAuditors = OrderAuditorsForShortCircuit(auditors, auditShortCircuitEnabled);
            var scheduledAuditorNames = scheduledAuditors.Select(a => a.Name).ToList();
            await PublishAuditStartedAsync(item, project, iteration, scheduledAuditors, ct);
            var auditPhaseStart = DateTimeOffset.UtcNow;
            await Transition(item, WorkItemState.Auditing, ct, project);
            using var auditPhase = new PhaseCancellation("audit", ct, _opts.TimeProvider);
            auditPhase.SetPhaseTimeout(project.Audit.PerIterationTimeout);
            auditPhase.HookHostShutdown(hostShutdownToken, _opts.ShutdownGrace);
            var startingWorkBranchTip = await TryResolveWorkBranchTipAsync(repoId, workBranch, ct);
            await PersistAuditProgressAsync(
                item,
                currentWorkAttemptStartedAt,
                BuildAuditProgressSnapshot(
                    iteration,
                    maxIterations,
                    [],
                    [],
                    0,
                    startingWorkBranchTip,
                    AuditProgressStatuses.InProgress,
                    scheduledAuditorNames,
                    [],
                    _opts.TimeProvider.GetUtcNow()),
                ct);

            IReadOnlyList<AuditFinding> findings;
            AgentKind? activeAuditAgentKind;
            bool declaredShortCircuitBlocking;
            bool incompleteVerdict;
            IReadOnlyList<string> completedAuditors;
            IReadOnlyList<string> incompleteAuditors;
            AuditFinding? requiredBuildFinding;
            IReadOnlyList<TestFailureAttributionResult> iterationAttributions = [];
            int? revisionForCtx = null;
            List<AuditFinding>? priorBlockingFindings = null;
            // Hoisted so the deployment stage (below, outside the code-stage
            // try) reuses the same progress prefix: partial deployment-stage
            // progress snapshots carry the required-build pre-collections.
            var preCollectedFindings = new List<AuditFinding>();
            var preCompletedAuditors = new List<string>();
            Func<AuditProgressUpdate, CancellationToken, Task> progressUpdateWithPreCollected =
                async (progress, progressCt) =>
                {
                    IReadOnlyList<AuditFinding> partialFindings = progress.Operation == AuditProgressUpdateOperation.Replace
                        ? progress.Findings
                        : [.. preCollectedFindings, .. progress.Findings];
                    IReadOnlyList<string> partialCompletedAuditors = progress.Operation == AuditProgressUpdateOperation.Replace
                        ? progress.CompletedAuditors
                        : [.. preCompletedAuditors, .. progress.CompletedAuditors];
                    var partialBlocking = partialFindings
                        .Where(f => f.Severity >= project.Audit.FailingSeverity)
                        .ToList();
                    var partialTip = await TryResolveWorkBranchTipAsync(repoId, workBranch, progressCt)
                        .ConfigureAwait(false);
                    await PersistAuditProgressAsync(
                        item,
                        currentWorkAttemptStartedAt,
                        BuildAuditProgressSnapshot(
                            iteration,
                            maxIterations,
                            partialFindings,
                            partialBlocking,
                            partialFindings.Count - partialBlocking.Count,
                            partialTip,
                            AuditProgressStatuses.InProgress,
                            scheduledAuditorNames,
                            partialCompletedAuditors,
                            _opts.TimeProvider.GetUtcNow()),
                        progressCt).ConfigureAwait(false);
                };
            // Code stage is skipped on human-review resume (see above); the
            // dangling open brace below is closed after the rebalance with
            // the resume defaults. Indentation is intentionally unchanged to
            // keep the diff reviewable. itemWasPlanned is declared here
            // because the loop tail (metrics tags) reads it on both paths.
            bool itemWasPlanned;
            IReadOnlyList<AuditFinding> blocking;
            if (!resumeHumanReview)
            {
            try
            {
                revisionForCtx = await TryLookupIterationRevisionAsync(item.Id, iteration, ct);
                priorBlockingFindings = auditHistory
                    .Where(h => h.Iteration < iteration && h.IsComplete)
                    .OrderByDescending(h => h.Iteration)
                    .Select(h => h.BlockingFindingsDetails)
                    .FirstOrDefault()?
                    .Select(f => new AuditFinding(
                        f.AuditorName,
                        f.Severity,
                        f.Title,
                        f.Description,
                        f.Location))
                    .ToList();
                var ctx = new AuditContext(item.Id, workBranch, baseBranch, iteration, item.Prompt,
                    ModelId: item.ModelId, ReasoningMode: item.ReasoningMode,
                    PromptRevisionAtDispatch: revisionForCtx,
                    BuildScriptRequired: project.Audit.BuildScriptRequired,
                    ProjectId: project.Id.Value,
                    Target: AuditTarget.Code,
                    // Carry the approved plan into the code audit so the
                    // plan-adherence reviewer can compare the diff against it.
                    // Null for unplanned items, which the reviewer treats as
                    // "no plan to check" and passes as a no-op.
                    PlanArtifact: item.PlanArtifact,
                    PriorBlockingFindings: priorBlockingFindings);
                var prePassedBuildTestGateEvidence = BuildTestGateEvidence.None;
                var auditorsForCollection = scheduledAuditors;
                var preGateAttributions = new List<TestFailureAttributionResult>();
                if (scheduledAuditors.Any(RequiresPassedBuildTestGate))
                {
                    var requiredBuildGateResult = await _requiredBuildGate.RunForAuditGateAsync(
                        item, project, repoId, baseBranch, workBranch, iteration, auditPhase.Token);
                    if (requiredBuildGateResult.Applies)
                        preCompletedAuditors.Add(RequiredBuildGateIdentity.AuditorName);
                    if (requiredBuildGateResult.TestFailureAttributions is { Count: > 0 } preAttr)
                        preGateAttributions.AddRange(preAttr);
                    if (requiredBuildGateResult.Finding is not null)
                    {
                        preCollectedFindings.Add(requiredBuildGateResult.Finding);
                        auditorsForCollection = scheduledAuditors
                            .Where(a => !RequiresPassedBuildTestGate(a))
                            .ToList();
                    }
                }

                var collectTask = CollectFindingsAsync(
                    item,
                    project,
                    runner,
                    auditorsForCollection,
                    repoId,
                    ctx,
                    auditShortCircuitEnabled,
                    prePassedBuildTestGateEvidence,
                    progressUpdateWithPreCollected,
                    auditPhase.Token);
                var completedAuditTask = await Task.WhenAny(collectTask, WaitForCancellationAsync(hostShutdownToken));
                if (completedAuditTask != collectTask)
                {
                    var drainTask = Task.Delay(_opts.AuditShutdownDrain);
                    completedAuditTask = await Task.WhenAny(collectTask, drainTask);
                    if (completedAuditTask != collectTask)
                    {
                        await auditPhase.Cts.CancelAsync();
                        throw auditPhase.Wrap(new OperationCanceledException(hostShutdownToken));
                    }
                }

                var collection = await collectTask;
                findings = [.. preCollectedFindings, .. collection.Findings];
                activeAuditAgentKind = collection.ActiveAuditAgentKind;
                declaredShortCircuitBlocking = collection.DeclaredShortCircuitBlocking;
                incompleteVerdict = collection.IncompleteVerdict;
                completedAuditors = [.. preCompletedAuditors, .. (collection.CompletedAuditors ?? [])];
                incompleteAuditors = collection.IncompleteAuditors ?? [];
                iterationAttributions = [.. preGateAttributions, .. (collection.TestFailureAttributions ?? [])];
                if (hostShutdownToken.IsCancellationRequested)
                    throw auditPhase.Wrap(new OperationCanceledException(hostShutdownToken));

                if (incompleteVerdict || scheduledAuditors.Any(RequiresPassedBuildTestGate))
                {
                    requiredBuildFinding = null;
                }
                else
                {
                    var postGate = await _requiredBuildGate.RunForAuditGateAsync(
                        item, project, repoId, baseBranch, workBranch, iteration, auditPhase.Token);
                    requiredBuildFinding = postGate.Finding;
                    if (postGate.TestFailureAttributions is { Count: > 0 } postAttr)
                        iterationAttributions = [.. iterationAttributions, .. postAttr];
                }
            }
            catch (OperationCanceledException oce) when (oce is not PhaseCancellationException)
            {
                throw auditPhase.Wrap(oce);
            }

            if (requiredBuildFinding is not null)
            {
                findings = [.. findings, requiredBuildFinding];
                completedAuditors = [.. completedAuditors, RequiredBuildGateIdentity.AuditorName];
            }

            // Emit cross-review event once per iteration when at least one LLM
            // auditor actually ran with a different agent than the work agent.
            if (activeAuditAgentKind is not null)
                AuditLog.CrossReviewActive(runner.Kind, activeAuditAgentKind.Value);

            // Rebalance the code audit for PLANNED items: findings from the
            // configured approach reviewer(s) are demoted to advisory so a planned
            // item's rework does not re-litigate an approach the plan stage already
            // reviewed. Objective gates keep full blocking authority; unplanned
            // items are unaffected. See PlannedItemAuditRebalance.
            var pipelineTuning = _pipelineTuning.Current;
            itemWasPlanned = HasReviewedPlanArtifact(item);
            blocking = PlannedItemAuditRebalance.SelectBlocking(
                findings,
                project.Audit.FailingSeverity,
                itemWasPlanned,
                pipelineTuning.PlannedItemAuditRebalanceEnabled,
                pipelineTuning.PlannedItemAdvisoryAuditors).ToList();
            }
            else
            {
                // Human-review resume defaults: the code stage was clean at
                // park time, so the resumed iteration reuses a clean code
                // verdict. Stored code findings are merged by the deployment
                // stage consume path below (it owns the held deployment and
                // the parked code outcome together).
                findings = [];
                activeAuditAgentKind = null;
                declaredShortCircuitBlocking = false;
                incompleteVerdict = false;
                completedAuditors = [];
                incompleteAuditors = [];
                requiredBuildFinding = null;
                iterationAttributions = [];
                blocking = [];
                itemWasPlanned = HasReviewedPlanArtifact(item);
            }

            // Cost-ordered ladder rung 2: deployment stage (lazy).
            // Runs only when the code stage above reached a complete verdict
            // with zero blocking findings. Provisions exactly ONE deployment
            // from the project's recipe, runs deployment-targeted auditors
            // against the live endpoint, and tears the deployment down on
            // every exit path. Findings merge into the normal rework loop
            // below. A fresh deployment is provisioned per iteration — never
            // reused against post-rework code. When the phase does not apply
            // (toggle off, no recipe, no deployment auditors) nothing is
            // provisioned and the iteration completes on the code verdict.
            if (!incompleteVerdict && blocking.Count == 0)
            {
                DeploymentStageOutcome? deploymentStage;
                try
                {
                    var humanStage = await RunDeploymentStageAsync(
                        item,
                        project,
                        runner,
                        repoId,
                        baseBranch,
                        workBranch,
                        iteration,
                        revisionForCtx,
                        priorBlockingFindings,
                        auditShortCircuitEnabled,
                        progressUpdateWithPreCollected,
                        scheduledAuditorNames,
                        findings,
                        completedAuditors,
                        codeStageClean: true,
                        auditPhase.Token);
                    if (humanStage.Parked)
                    {
                        // The iteration parked for human review: the worker
                        // slot and audit sandbox are released here (this
                        // return unwinds them) while only the deployment stays
                        // alive. Resume happens on verdict or expiry.
                        auditPhaseScope.Dispose();
                        return true;
                    }

                    deploymentStage = humanStage.Outcome;
                }
                catch (OperationCanceledException oce) when (oce is not PhaseCancellationException)
                {
                    throw auditPhase.Wrap(oce);
                }

                if (deploymentStage is not null)
                {
                    findings = [.. findings, .. deploymentStage.Findings];
                    blocking = [.. blocking, .. deploymentStage.Blocking];
                    activeAuditAgentKind ??= deploymentStage.ActiveAuditAgentKind;
                    declaredShortCircuitBlocking |= deploymentStage.DeclaredShortCircuitBlocking;
                    incompleteVerdict |= deploymentStage.IncompleteVerdict;
                    completedAuditors = [.. completedAuditors, .. deploymentStage.CompletedAuditors];
                    incompleteAuditors = [.. incompleteAuditors, .. deploymentStage.IncompleteAuditors];
                }
            }
            if (incompleteVerdict && findings.Count == 0)
            {
                var incompleteList = incompleteAuditors.Count == 0
                    ? "unknown auditor"
                    : string.Join(", ", incompleteAuditors);
                var incompleteTip = await TryResolveWorkBranchTipAsync(repoId, workBranch, ct);
                await PersistAuditProgressAsync(
                    item,
                    currentWorkAttemptStartedAt,
                    BuildAuditProgressSnapshot(
                        iteration,
                        maxIterations,
                        findings,
                        [],
                        0,
                        incompleteTip,
                        AuditProgressStatuses.Incomplete,
                        scheduledAuditorNames,
                        completedAuditors,
                        _opts.TimeProvider.GetUtcNow()),
                    ct);
                throw new AuditUnavailableException(
                    $"audit iteration {iteration} did not reach a complete verdict before any auditor produced findings; incomplete auditor(s): {incompleteList}");
            }
            if (declaredShortCircuitBlocking && blocking.Count == 0)
            {
                if (findings.Count == 0)
                {
                    findings = [new AuditFinding(
                        "audit:short-circuit",
                        AuditSeverity.Error,
                        "short-circuit gate failed without findings",
                        "A short-circuit-capable auditor returned a failing AuditResult without any findings.")];
                }

                blocking = findings.ToList();
            }
            if (incompleteVerdict && blocking.Count == 0)
            {
                blocking = findings.ToList();
            }
            if (incompleteVerdict
                && iteration == maxIterations
                && !incompleteFinalReworkExtensionUsed
                && maxIterations < ProjectAudit.MaxIterationBudget)
            {
                maxIterations++;
                incompleteFinalReworkExtensionUsed = true;
            }
            var nonBlocking = findings.Count - blocking.Count;
            var workBranchTip = await TryResolveWorkBranchTipAsync(repoId, workBranch, ct);
            var progressSnapshot = BuildAuditProgressSnapshot(
                iteration,
                maxIterations,
                findings,
                blocking,
                nonBlocking,
                workBranchTip,
                incompleteVerdict ? AuditProgressStatuses.Incomplete : AuditProgressStatuses.Complete,
                scheduledAuditorNames,
                completedAuditors,
                _opts.TimeProvider.GetUtcNow());
            auditHistory.Add(progressSnapshot);
            await PersistAuditProgressAsync(item, currentWorkAttemptStartedAt, progressSnapshot, ct);

            AuditLog.AuditIterationComplete(iteration, maxIterations, blocking.Count, nonBlocking);
            CodeyBoxMeters.AuditBlockingFindings.Record(blocking.Count,
                new KeyValuePair<string, object?>("iteration", iteration.ToString()));

            await PublishAuditFindingsEmittedAsync(item, project, iteration, findings, blocking.Count, nonBlocking, ct);

            var iterUsage = await TryGetUsageSummaryAsync(item.Id);
            await _webhooks.PublishAsync(new WebhookEvent
            {
                Event = "work_item.audit_iteration",
                WorkItem = await _store.GetAsync(item.Id, ct) ?? item,
                Project = project,
                Details = new AuditIterationDetails(
                    iteration, maxIterations, blocking.Count, nonBlocking,
                    activeAuditAgentKind?.Value),
                Usage = iterUsage?.Iteration,
                UsageTotal = iterUsage?.Total,
            }, CancellationToken.None);

            var auditVerdict = blocking.Count == 0 ? AuditVerdict.Pass : AuditVerdict.Fail;
            await PublishAuditCompletedAsync(item, project, iteration, auditVerdict, auditPhaseStart, ct);
            await ResetRecoveryAttemptsAfterRealProgressEventAsync(
                item.Id,
                RecoveryProgressEvent.AuditVerdictProduced,
                "audit-verdict-produced",
                ct);

            // Tag every audit-iteration meter with the self-review-checklist
            // gate state so dashboards can compare iteration count + first-audit
            // pass-rate WITH vs WITHOUT the injected checklist. `iteration` is
            // tagged too so passed/failed at iter 1 (first-audit pass-rate) can
            // be sliced directly.
            var selfReviewTag = new KeyValuePair<string, object?>(
                "self_review_checklist", selfReviewChecklistEnabled ? "on" : "off");
            var iterationTag = new KeyValuePair<string, object?>(
                "iteration", iteration.ToString());
            // Cohort tag for planned-vs-unplanned measurement. Every AuditIterations
            // emission carries it so dashboards can compare code-stage iteration
            // count across the two cohorts.
            var plannedTag = new KeyValuePair<string, object?>(
                "planned", itemWasPlanned ? "on" : "off");

            if (blocking.Count == 0)
            {
                _log.LogInformation("Audit iteration {Iter} passed for {Id} ({NonBlocking} non-blocking findings)",
                    iteration, item.Id, nonBlocking);
                AuditLog.AuditPassed(iteration);
                CodeyBoxMeters.AuditIterations.Add(1,
                    new KeyValuePair<string, object?>("outcome", "passed"),
                    selfReviewTag,
                    iterationTag,
                    plannedTag);
                EmitSessionAuditOutcomeMetrics(iteration, "passed");
                if (iteration == 1)
                {
                    EmitFirstAuditOutcomeMetric("passed", plannedTag);
                    EmitSessionFirstAuditOutcomeMetric("passed");
                }
                return false;
            }
            if (iteration == 1)
            {
                EmitFirstAuditOutcomeMetric("failed", plannedTag);
                EmitSessionFirstAuditOutcomeMetric("failed");
            }

            _log.LogInformation("Audit iteration {Iter} of {Max} found {Count} blocking findings for {Id}",
                iteration, maxIterations, blocking.Count, item.Id);

            // NotDiffAttributable flake escalation: a test failure that
            // reproduces on the base branch is not caused by the diff, so it
            // must NOT be fed back to the rework agent. Spawn (or reuse) an
            // isolated base-branch fix item, park the parent on a dependsOn
            // gate, and leave the audit loop — the parent resumes (re-audited
            // from the next iteration) once the child merges.
            var flakeParked = await TryEscalateNotDiffAttributableAsync(
                item, project, baseBranch, iterationAttributions, ct);
            if (flakeParked)
            {
                CodeyBoxMeters.AuditIterations.Add(1,
                    new KeyValuePair<string, object?>("outcome", "flake_escalated"),
                    selfReviewTag,
                    iterationTag,
                    plannedTag);
                EmitSessionAuditOutcomeMetrics(iteration, "flake_escalated");
                auditPhaseScope.Dispose();
                return true;
            }

            if (iteration == maxIterations)
            {
                if (HasAuditConvergenceProgress(auditHistory))
                {
                    var escalated = await ParkAuditMaxIterationsForOperatorAsync(item, project, auditHistory, ct);
                    var outcome = escalated ? "delegation_escalated" : "needs_operator_input";
                    CodeyBoxMeters.AuditIterations.Add(1,
                        new KeyValuePair<string, object?>("outcome", outcome),
                        selfReviewTag,
                        iterationTag,
                        plannedTag);
                    EmitSessionAuditOutcomeMetrics(iteration, outcome);
                    return true;
                }

                CodeyBoxMeters.AuditIterations.Add(1,
                    new KeyValuePair<string, object?>("outcome", "failed"),
                    selfReviewTag,
                    iterationTag,
                    plannedTag);
                EmitSessionAuditOutcomeMetrics(iteration, "failed");
                AuditLog.AuditFailed(iteration, blocking.Count);
                var summary = string.Join("; ", blocking
                    .Take(PromptComposer.AuditEscalationSummaryFindingLimit)
                    .Select(f => $"[{f.AuditorName}] {f.Title}"));
                throw new AuditFailedException(
                    $"Audit did not pass after {iteration} iterations. {blocking.Count} blocking finding(s): {summary}");
            }

            CodeyBoxMeters.AuditIterations.Add(1,
                new KeyValuePair<string, object?>("outcome", "reworking"),
                selfReviewTag,
                iterationTag,
                plannedTag);
            // Close the audit phase scope before the incremental rebase and
            // rework begins; neither should contribute to audit duration.
            auditPhaseScope.Dispose();

            // Keep the work branch close to base BETWEEN audit/rework
            // iterations so the merge-time rebase has less to consolidate
            // (smaller and rarer conflicts). Best-effort: any failure logs a
            // warning and the rework dispatch proceeds against the
            // un-rebased branch. Hot-reloadable; off by default. Must run
            // BEFORE the rework dispatch — once the agent has cloned, it is
            // operating on a snapshot of origin and any subsequent
            // force-push to the work branch would race the agent's working
            // tree. Cancellation propagates so a shutdown mid-rebase tears
            // down cleanly instead of being swallowed.
            await MaybeIncrementalRebaseAsync(item, runner, repoId, baseBranch, workBranch, project, ct);

            // Rework following audit iteration N is the input that will be
            // evaluated by audit iteration N+1, so emit it as iteration N+1.
            var reworkIterationNumber = iteration + 1;
            var parked = await RunAuditReworkAsync(
                item, project, runner, repoId, baseBranch, workBranch,
                findings, iteration, reworkIterationNumber, maxIterations,
                auditHistory, ct, hostShutdownToken,
                // An incomplete verdict is not a finished verdict: its findings
                // may be partial (interrupted auditors), so an empty rework
                // against them is not a silent-failure signal.
                auditHasBlockingFindings: blocking.Count > 0 && !incompleteVerdict);
            if (parked) return true;
        }
        return false;
    }

    /// <summary>
    /// NotDiffAttributable flake escalation for one audit iteration. When the
    /// iteration's test-failure attributions contain a genuine
    /// NotDiffAttributable verdict, the failure reproduces on the base branch
    /// and must not be fed back to the rework agent. Delegates to
    /// <see cref="NonDeterministicTestEscalationService"/> (spawn-or-reuse a
    /// base-branch fix item, park the parent on dependsOn) and publishes a
    /// webhook event. Returns true when the parent was parked. Never throws:
    /// any fault returns false so the audit loop falls back to normal rework.
    /// </summary>
    private async Task<bool> TryEscalateNotDiffAttributableAsync(
        WorkItem item,
        Project project,
        string baseBranch,
        IReadOnlyList<TestFailureAttributionResult> attributions,
        CancellationToken ct)
    {
        if (attributions is null || attributions.Count == 0)
            return false;
        if (!NonDeterministicTestEscalationPolicy.HasActionableTests(attributions))
            return false;

        NonDeterministicTestEscalationService service;
        try
        {
            service = _flakeEscalation
                ?? new NonDeterministicTestEscalationService(
                    _store, _taskQueue, _flakeEscalationOptions, _opts.TimeProvider);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Flake escalation service construction for work item {Id} failed; falling back to normal rework",
                item.Id);
            return false;
        }

        FlakeEscalationResult result;
        try
        {
            result = await service.TryEscalateAsync(item, attributions, baseBranch, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Flake escalation for work item {Id} threw; falling back to normal rework",
                item.Id);
            return false;
        }

        if (!result.Escalated || result.ChildId is null)
            return false;

        try
        {
            var snapshot = await _store.GetAsync(item.Id, ct).ConfigureAwait(false) ?? item;
            await _webhooks.PublishAsync(new WebhookEvent
            {
                Event = "work_item.flake_escalated",
                WorkItem = snapshot,
                Project = project,
                Details = new
                {
                    childId = result.ChildId.ToString(),
                    reusedExisting = result.ReusedExisting,
                    flakyTests = result.FlakyTests ?? [],
                },
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Flake escalation webhook for work item {Id} failed; parent is still parked",
                item.Id);
        }

        return true;
    }

    /// <summary>
    /// Emits the session-mode audit-iteration histogram with the
    /// <c>self_review</c> tag derived from the live ambient lifecycle.
    /// Skipped when no session lifecycle is active so the metric records
    /// session items only — that's exactly the comparison the brief asks for
    /// (audit-iteration count for session items WITH vs WITHOUT the
    /// pre-emptive self-review turn). Failure to read the flag is silent;
    /// metrics must never break the pipeline.
    /// </summary>
    private void EmitSessionAuditOutcomeMetrics(int iteration, string outcome)
    {
        var lifecycle = _ambientSessionLifecycle.Value;
        if (lifecycle is null)
            return;
        try
        {
            var selfReviewTag = lifecycle.PreemptiveSelfReviewRan ? "on" : "off";
            CodeyBoxMeters.SessionAuditIterations.Record(iteration,
                new KeyValuePair<string, object?>("self_review", selfReviewTag),
                new KeyValuePair<string, object?>("outcome", outcome));
        }
        catch
        {
            // Observability must never break a pipeline step.
        }
    }

    /// <summary>
    /// Emits the always-on first-audit-outcome counter tagged with the planned
    /// cohort. Called once per work item (only at iteration == 1) so dashboards
    /// can chart first-audit pass-rate for PLANNED vs UNPLANNED items — the
    /// measurement proving whether planning improves the first-pass rate.
    /// Observability must never break a pipeline step, so any failure is silent.
    /// </summary>
    private static void EmitFirstAuditOutcomeMetric(string outcome, KeyValuePair<string, object?> plannedTag)
    {
        try
        {
            CodeyBoxMeters.FirstAuditOutcome.Add(1,
                new KeyValuePair<string, object?>("outcome", outcome),
                plannedTag);
        }
        catch
        {
            // Observability must never break a pipeline step.
        }
    }

    /// <summary>
    /// Emits the session-mode first-audit-outcome counter with the
    /// <c>self_review</c> tag. Called once per session item (only at
    /// iteration == 1) so dashboards can chart first-audit pass-rate WITH vs
    /// WITHOUT the pre-emptive self-review turn — the primary measurement
    /// the brief asks for.
    /// </summary>
    private void EmitSessionFirstAuditOutcomeMetric(string outcome)
    {
        var lifecycle = _ambientSessionLifecycle.Value;
        if (lifecycle is null)
            return;
        try
        {
            var selfReviewTag = lifecycle.PreemptiveSelfReviewRan ? "on" : "off";
            CodeyBoxMeters.SessionFirstAuditOutcome.Add(1,
                new KeyValuePair<string, object?>("self_review", selfReviewTag),
                new KeyValuePair<string, object?>("outcome", outcome));
        }
        catch
        {
            // Observability must never break a pipeline step.
        }
    }

    // RunMechanicalFixersAsync and its helpers (MakeReadOnlyRepositoryMount,
    // BuildMechanicalFixerInputs, ResolveMechanicalPromptRevisionForCommitAsync,
    // ImportMechanicalCommitPatchAsync) live in PipelineRunner.MechanicalEdit.cs
    // so the mechanical-edit phase is editable in isolation from this file.

    private async Task<bool> HandleExhaustedPersistedAuditHistoryAsync(
        WorkItem item,
        Project project,
        IReadOnlyList<AuditProgressSnapshot> auditHistory,
        CancellationToken ct)
    {
        if (auditHistory.Count == 0 || !AuditProgressRequiresRework(auditHistory[^1]))
            return false;

        if (HasAuditConvergenceProgress(auditHistory))
        {
            var escalated = await ParkAuditMaxIterationsForOperatorAsync(item, project, auditHistory, ct);
            var outcome = escalated ? "delegation_escalated" : "needs_operator_input";
            CodeyBoxMeters.AuditIterations.Add(1,
                new KeyValuePair<string, object?>("outcome", outcome),
                new KeyValuePair<string, object?>("planned", HasReviewedPlanArtifact(item) ? "on" : "off"));
            return true;
        }

        var last = auditHistory[^1];
        CodeyBoxMeters.AuditIterations.Add(1,
            new KeyValuePair<string, object?>("outcome", "failed"),
            new KeyValuePair<string, object?>("planned", HasReviewedPlanArtifact(item) ? "on" : "off"));
        var blockingFindings = _promptComposer.BlockingProgressFindingsForSummary(last);
        AuditLog.AuditFailed(last.Iteration, blockingFindings.Count);
        var summary = string.Join("; ", blockingFindings
            .Take(PromptComposer.AuditEscalationSummaryFindingLimit)
            .Select(f => $"[{f.AuditorName}] {f.Title}"));
        throw new AuditFailedException(
            $"Audit did not pass after {last.Iteration} iterations. {blockingFindings.Count} blocking finding(s): {summary}");
    }

    private async Task<bool> RunMissingAuditResumeReworkAsync(
        WorkItem item,
        Project project,
        IAgentRunner runner,
        string repoId,
        string baseBranch,
        string workBranch,
        IReadOnlyList<AuditProgressSnapshot> auditHistory,
        int startIteration,
        int maxIterations,
        CancellationToken ct,
        CancellationToken hostShutdownToken)
    {
        if (auditHistory.Count == 0)
            return false;

        var last = auditHistory[^1];
        if (!AuditProgressRequiresRework(last))
            return false;

        var iterations = await _store.GetIterationsAsync(item.Id, ct);
        if (iterations.Any(i => i.Iteration == startIteration)
            && await HasCompletedAuditReworkAsync(item, startIteration, ct))
        {
            return false;
        }

        var findings = last.Findings
            .Select(ToAuditFinding)
            .ToList();
        _log.LogInformation(
            "Resuming work item {Id} from parked audit history by reworking iteration {AuditIteration} findings before audit iteration {NextIteration}",
            item.Id, last.Iteration, startIteration);

        await MaybeIncrementalRebaseAsync(item, runner, repoId, baseBranch, workBranch, project, ct);
        return await RunAuditReworkAsync(
            item, project, runner, repoId, baseBranch, workBranch,
            findings, last.Iteration, startIteration, maxIterations,
            auditHistory, ct, hostShutdownToken,
            // Persisted history reaching this point is complete-only
            // (interrupted rows are superseded on load), so IsComplete is
            // checked defensively: a rework driven by anything less than a
            // finished verdict must not feed the no-changes breaker.
            auditHasBlockingFindings: last.BlockingFindings > 0 && last.IsComplete);
    }

    private async Task<bool> HasCompletedAuditReworkAsync(
        WorkItem item,
        int reworkIterationNumber,
        CancellationToken ct)
    {
        if (_involvement is null)
        {
            _log.LogInformation(
                "Work item {Id} has dispatch row for audit rework iteration {Iteration}, but no involvement store is wired; re-running rework to avoid treating an incomplete quota/infra attempt as progress",
                item.Id,
                reworkIterationNumber);
            return false;
        }

        IReadOnlyList<AgentInvolvement> rows;
        try
        {
            rows = await _involvement.ListByWorkItemAsync(item.Id, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(
                ex,
                "Could not verify completed audit rework iteration {Iteration} for work item {Id}; re-running rework rather than skipping on a dispatch row alone",
                reworkIterationNumber,
                item.Id);
            return false;
        }

        var completed = rows.Any(row =>
            string.Equals(row.Phase, "rework", StringComparison.Ordinal)
            && row.Iteration == reworkIterationNumber
            && string.Equals(row.Outcome, AgentInvolvementOutcomes.Success, StringComparison.Ordinal)
            && row.EndedAt is not null);
        if (!completed)
        {
            _log.LogInformation(
                "Work item {Id} has dispatch row for audit rework iteration {Iteration}, but no completed rework involvement; re-running before the next audit iteration",
                item.Id,
                reworkIterationNumber);
        }

        return completed;
    }

    private async Task<bool> RunAuditReworkAsync(
        WorkItem item,
        Project project,
        IAgentRunner runner,
        string repoId,
        string baseBranch,
        string workBranch,
        IReadOnlyList<AuditFinding> findings,
        int auditIteration,
        int reworkIterationNumber,
        int maxIterations,
        IReadOnlyList<AuditProgressSnapshot> auditHistory,
        CancellationToken ct,
        CancellationToken hostShutdownToken,
        bool auditHasBlockingFindings = true)
    {
        // Audit-driven rework is the primary rework path; open a phase.rework
        // span and record codeybox.phase.duration_ms{phase=rework} so rework
        // telemetry matches the documented trace tree (the resume-preempt
        // path opens its own scope independently).
        using var reworkPhaseScope = BeginPhaseScope(item, "rework");
        await PublishIterationStartedAsync(item, project, IterationPhase.Rework, reworkIterationNumber, ct);
        var reworkStart = DateTimeOffset.UtcNow;
        // Snapshot the prompt and revision now, before the rework agent runs.
        // A concurrent PUT /workitems/{id}/prompt landing during this iteration
        // will bump the revision but must not be attributed to it. The
        // re-read also ensures the agent receives the LATEST prompt content,
        // not the orchestrator's stale in-memory snapshot — otherwise the
        // dispatch row, env-var, and trailer would all agree on revision N
        // while the agent was looking at revision N-1's text, defeating the
        // entire point of Layer 1.
        var freshForRework = await _store.GetAsync(item.Id, ct) ?? item;
        await _store.RecordIterationDispatchAsync(
            item.Id, reworkIterationNumber, freshForRework.PromptRevision, reworkStart, ct);
        await Transition(item, WorkItemState.Reworking, ct, project);
        var answeredQuestions = project.AllowAgentQuestions && _questionStore is not null
            ? await _questionStore.ListByWorkItemAsync(item.Id.ToString(), ct)
            : (IReadOnlyList<WorkItemQuestion>)[];
        var baseReworkPrompt = ReworkPromptBuilder.Build(
            freshForRework.Prompt, findings, auditIteration, maxIterations, answeredQuestions, project.AllowAgentQuestions);
        using var reworkPhase = new PhaseCancellation("rework", ct, _opts.TimeProvider);
        reworkPhase.SetPhaseTimeout(ResolvePhaseAbsoluteTimeout(item.WorkTimeout));
        reworkPhase.HookHostShutdown(hostShutdownToken, _opts.ShutdownGrace);
        var sandboxTarget = SandboxTargetResolver.ResolveProjectPhase(project, project.NetworkProfiles.Rework);

        async Task<string?> DispatchAsync(string prompt)
        {
            return await InvokeAgentWithQuotaFallbackAsync(item, project, "rework", iteration: reworkIterationNumber,
                async (workerRunner, trialItem, attemptCt) =>
                    await RunWithStuckProbeAsync(trialItem, project, workerRunner.Kind, "rework", reworkPhase, ct,
                        phaseCt => RunAgentPhaseAsync(trialItem, workerRunner, repoId, baseBranch, workBranch,
                            prompt, isInitial: false,
                            networkProfile: sandboxTarget.NetworkProfile,
                            sandboxFlavor: sandboxTarget.Flavor,
                            project: project,
                            phaseCt,
                            hostShutdownToken,
                            // Audit-driven rework: the next iteration of the audit/rework loop
                            // re-runs the build gate via RunForAuditAsync, which surfaces the
                            // failure as a blocking finding. Terminal-failing here would defeat
                            // the loop's purpose of converging on a fix within the audit budget.
                            buildFailurePolicy: RequiredBuildPolicy.DeferToAuditLoop,
                            iteration: reworkIterationNumber,
                            reworkNoDiffHandling: ReworkNoDiffHandling.AuditEmptyRework,
                            // With zero blocking findings there is nothing for
                            // the agent to change, so an empty diff is the
                            // correct outcome — not a silent-failure signal for
                            // the no-changes circuit breaker. The same holds
                            // when the driving verdict never completed: its
                            // findings may be partial.
                            suppressNoChangesBreaker: !auditHasBlockingFindings),
                        workToken: attemptCt),
                ct,
                phaseCancellation: reworkPhase,
                attemptTimeout: item.WorkTimeout,
                allowAuthRequiredFallback: true);
        }

        string? reworkStdout;
        try
        {
            reworkStdout = await DispatchAsync(baseReworkPrompt);
        }
        catch (OperationCanceledException oce) when (oce is not PhaseCancellationException)
        {
            throw reworkPhase.Wrap(oce);
        }
        catch (ReworkProducedNoChangesException emptyEx)
        {
            // The agent finished cleanly without committing any change AND no
            // infra (auth / quota) signature was matched on its output — those
            // would have thrown TerminalQuotaError / AgentAuthRequiredException
            // before reaching here. The fallback wrapper routes infra failures
            // before empty-rework handling; auth evidence only publishes a
            // fleet-wide bench when it is authoritative or corroborated.
            //
            // Apply converge-aware item-level handling: if the audit history
            // shows convergence, re-dispatch with an escalated instruction so a
            // single empty pass on a converging item does not discard the
            // remaining iteration budget. If retries are still empty, fall back
            // to the operator-input park flow (operator picks up the partially
            // converged item) rather than terminal-failing it. Only hard-fail
            // when both budget AND convergence are absent — that path is
            // handled by the audit-loop ceiling branch.
            var parked = await HandleEmptyReworkAsync(
                item, project, emptyEx, auditHistory, auditIteration, reworkIterationNumber, maxIterations,
                baseReworkPrompt, DispatchAsync, reworkPhase, reworkStart, repoId, workBranch, ct);
            return parked;
        }

        return await CompleteAuditReworkAsync(
            item,
            project,
            reworkIterationNumber,
            repoId,
            workBranch,
            reworkStart,
            reworkStdout,
            ct);
    }

    private async Task<bool> CompleteAuditReworkAsync(
        WorkItem item,
        Project project,
        int reworkIterationNumber,
        string repoId,
        string workBranch,
        DateTimeOffset reworkStart,
        string? reworkStdout,
        CancellationToken ct)
    {
        await PublishIterationCompletedAsync(item, project, IterationPhase.Rework, reworkIterationNumber,
            repoId, workBranch, reworkStart, ct);
        await ResetRecoveryAttemptsAfterRealProgressEventAsync(
            item.Id,
            RecoveryProgressEvent.AuditReworkCompleted,
            "audit-rework-completed",
            ct);
        if (project.AllowAgentQuestions && _questionStore is not null && reworkStdout is not null)
        {
            var parked = await TryParkForQuestionsAsync(item, project, reworkStdout, ct);
            if (parked) return true;
        }

        return false;
    }

}
