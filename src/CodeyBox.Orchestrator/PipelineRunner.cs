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

// PipelineRunner.cs — Orchestration spine: RunAsync entry point. Phase implementations live in sibling PipelineRunner.*.cs partials; Transition*/cancellation machinery in PipelineRunner.Transitions.cs.
public sealed partial class PipelineRunner : IPipelineRunner
{
    public async Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default)
    {
        using var workItemScope = AuditLog.WorkItemScope(item.Id);

        // Root span for the whole pipeline run. Becomes the parent of every phase,
        // agent-invocation, and sandbox span started within this async flow.
        using var rootSpan = CodeyBoxActivities.Pipeline.StartActivity("pipeline.run", ActivityKind.Internal);
        if (rootSpan is not null)
        {
            rootSpan.SetTag("codeybox.work_item_id", item.Id.ToString());
            rootSpan.SetTag("codeybox.project_id", item.ProjectId.Value);
            rootSpan.SetTag("codeybox.agent", item.Agent?.Value ?? "(default)");
            rootSpan.SetTag("codeybox.model", item.ModelId ?? "(default)");
            rootSpan.SetTag("codeybox.state", item.State.ToString());
        }

        Project project;
        try
        {
            project = await _projects.GetAsync(item.ProjectId, ct)
                ?? throw new InvalidOperationException($"Unknown project '{item.ProjectId}'");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Work item {Id} could not resolve project", item.Id);
            await TransitionFailed(item, ex.Message, CancellationToken.None, project: null, failureKind: "infrastructure");
            return;
        }

        using var projectScope = AuditLog.ProjectScope(project.Id);

        try
        {
            // The persisted row is authoritative. In particular, an operator
            // may have edited the prompt after this queued snapshot was read;
            // validating the stale argument would incorrectly resume an old
            // conversation against the superseded prompt.
            var dispatchedItem = item;
            var persistedItem = await _store.GetAsync(item.Id, ct) ?? item;
            var sameRuntimeRoute = persistedItem.Agent == dispatchedItem.Agent
                && string.Equals(
                    persistedItem.AgentInstanceId,
                    dispatchedItem.AgentInstanceId,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    persistedItem.AgentClassId,
                    dispatchedItem.AgentClassId,
                    StringComparison.OrdinalIgnoreCase);
            item = persistedItem.AgentTurnResumeCheckpoint is null && sameRuntimeRoute
                ? persistedItem with
                {
                    // Model/reasoning are deliberately runtime-only routing
                    // selections and have no work_items columns. Preserve them
                    // only when the persisted route still matches the pickup;
                    // every durable checkpoint restores its own exact values
                    // below instead.
                    ModelId = dispatchedItem.ModelId,
                    ReasoningMode = dispatchedItem.ReasoningMode,
                }
                : persistedItem;

            // Durable turn checkpoints are bound to the exact runner route,
            // model, and reasoning mode whose work tree and scratchpad were
            // captured. Restore that route before availability and runner
            // lookup so saved provider state cannot cross an account route.
            if (item.AgentTurnResumeCheckpoint is { } durableTurnResume)
            {
                item = item with
                {
                    Agent = durableTurnResume.Agent,
                    AgentInstanceId = durableTurnResume.AgentInstanceRoute,
                    ModelId = durableTurnResume.ModelId,
                    ReasoningMode = durableTurnResume.ReasoningMode,
                };
            }

            ValidateAgentTurnResumeCheckpoint(item);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            _log.LogError(ex, "Work item {Id} has an invalid durable agent-turn checkpoint", item.Id);
            await TransitionFailed(
                item,
                "The durable agent-turn checkpoint is invalid and cannot be resumed safely.",
                CancellationToken.None,
                project,
                failureKind: "restore");
            return;
        }

        if (item.JobType == JobType.AgentControl)
        {
            await RunAgentControlAsync(item, project, ct);
            return;
        }

        try
        {
            project = project with { Audit = ResolveAuditProfileForWorkItem(project, item) };
            _mechanicalFixerComposer.Validate(project);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Work item {Id} could not validate audit configuration for project {ProjectId}", item.Id, project.Id);
            await TransitionFailed(item, ex.Message, CancellationToken.None, project, failureKind: "configuration");
            return;
        }

        var agentKind = item.Agent ?? project.DefaultAgent;
        if (!_agents.TryGet(agentKind, out var agentRunner))
        {
            await TransitionFailed(
                item,
                $"No runner registered for agent '{agentKind}'",
                CancellationToken.None,
                project,
                failureKind: WorkItemFailureKinds.AgentUnavailable,
                agent: agentKind);
            return;
        }

        // The retry endpoint and recovery scheduler set the entry state to a
        // pre-phase marker so the pipeline resumes at the matching phase. Compute
        // this before dispatch-time availability gates: a WorkComplete /
        // AuditPassed / Merged continuation must not be parked just because the
        // original work agent is paused when the next phase uses another agent
        // or no agent at all.
        var entry = item.State;
        var resumingPreempt = item.HasAgentTurnRecoveryBoundary;
        var resumingConflictRework = entry is WorkItemState.ReworkingForConflict;
        var skipWork = entry is WorkItemState.WorkComplete or WorkItemState.AuditPassed or WorkItemState.Merged
            or WorkItemState.Delegating
            || resumingConflictRework
            || (resumingPreempt && entry is WorkItemState.Reworking);
        // Operator-triggered escape hatch: the item already failed the
        // constrained work/audit/rework cycle. The delegation block below
        // runs a single unconstrained turn, then the item re-enters the
        // normal flow at the audit phase (skipAudit stays false).
        var isDelegationEntry = entry is WorkItemState.Delegating;
        var skipAudit = entry is WorkItemState.Merged
            || resumingConflictRework;
        var skipMerge = entry is WorkItemState.Merged;
        var planningEnabledAtEntry = ShouldUsePlanningPhase(item, project);
        var planningLifecycleRequiredAtEntry = !skipWork
            && (planningEnabledAtEntry || IsPlanningLifecycleState(entry));

        // ── Credential smoke gate ────────────────────────────────────────────────
        // Run before ANY sandbox is allocated. Skipped when the project opts out
        // (e.g. Copilot), when the gate is disabled globally, or when no probe is
        // registered for this agent. Results are cached per-credential-fingerprint.
        if (_smokeGate is not null && !project.SkipCredentialSmokeTest)
        {
            AgentSmokeResult? smokeResult;
            try
            {
                smokeResult = await _smokeGate.CheckAsync(agentKind, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Smoke gate check threw for {Agent}; skipping gate", agentKind.Value);
                smokeResult = null;
            }

            if (smokeResult is { Ok: false })
            {
                AuditLog.AgentSmokeFailed(agentKind, smokeResult.FailureReason, smokeResult.Duration, smokeResult.Category);
                await _webhooks.PublishAsync(new WebhookEvent
                {
                    Event = "agent.smoke_failed",
                    WorkItem = item,
                    Project = project,
                    Details = new AgentSmokeFailedDetails
                    {
                        AgentKind = agentKind.Value,
                        Reason = smokeResult.FailureReason,
                        Category = smokeResult.Category,
                    },
                }, CancellationToken.None);
                await TransitionFailed(item,
                    $"credential smoke test failed: {smokeResult.FailureReason}",
                    CancellationToken.None, project, failureKind: WorkItemFailureKinds.AgentUnavailable, agent: agentKind);
                return;
            }

            if (smokeResult is { Ok: true })
                AuditLog.AgentSmokeSucceeded(agentKind, smokeResult.Duration);
        }

        // ── In-VM smoke gate ─────────────────────────────────────────────────────
        // The host credential gate above only proves the host holds the right
        // env-vars; it cannot see whether the agent CLI actually runs inside the
        // sandbox. On the class-routed path the router already gated the chosen
        // member, but a direct-agent work item (no AgentClass / DefaultAgentClass)
        // would otherwise reach the runner without any in-VM check and reproduce
        // the exit-127 / auth cascade. Gate the work-phase agent here too — a
        // cache hit is free, so a class-routed item just re-asserts its verdict.
        //
        // Deliberately NOT tied to project.SkipCredentialSmokeTest: that flag
        // opts out of the host-side *credential* probe (HTTP env-var check),
        // which is exactly the over-permissive check this in-VM gate exists to
        // backstop. Skipping the in-sandbox binary/auth/trust verification for a
        // project that disabled credential smoke would reopen the very cascade
        // this gate closes. Agents with no first-party sandbox CLI (e.g. copilot)
        // have no IInVmSmokeProbe and are exempted in the coverage policy, so the
        // gate is a free pass-through for them regardless of this flag.
        var completionModeCheck = item.JobType == JobType.CheckAndAct
            && item.Check is not null
            && string.Equals(item.Check.Mode, CheckAndActModes.Completion, StringComparison.OrdinalIgnoreCase);
        var initialSmokePhase = item.JobType == JobType.CheckAndAct
            ? completionModeCheck ? null : "check"
            : isDelegationEntry
                ? "delegation"
                : skipWork
                ? null
                : planningLifecycleRequiredAtEntry
                    ? "planning"
                    : "work";
        if (initialSmokePhase is not null)
        {
            var initialSmokeTarget = ResolvePhaseSmokeTarget(project, initialSmokePhase, item.BaselineImageRef);
            var smokeAvailability = await EnsureAgentSmokeAvailableAsync(
                agentKind, initialSmokeTarget, ct);
            if (!smokeAvailability.Available)
            {
                var reason = smokeAvailability.Reason ?? "in-VM smoke gate excluded agent";
                if (IsOperatorPaused(smokeAvailability))
                {
                    await TransitionWaitingForAgentResumeAsync(
                        item,
                        reason,
                        project,
                        agentKind,
                        RetryFromForAgentPausePhase(initialSmokePhase, item.State));
                    return;
                }

                // The exclusion category isn't carried by the availability snapshot
                // (the registry collapses sources into a single reason string), so
                // we default to Unknown here. The underlying probe still recorded
                // the correct category at the source (InVmSmokeProber /
                // PeriodicSmokeProbeService); this branch is just the dispatch-time
                // re-rejection of an already-recorded exclusion.
                AuditLog.AgentSmokeFailed(agentKind, reason, TimeSpan.Zero, SmokeFailureCategory.Unknown);
                await _webhooks.PublishAsync(new WebhookEvent
                {
                    Event = "agent.smoke_failed",
                    WorkItem = item,
                    Project = project,
                    Details = new AgentSmokeFailedDetails
                    {
                        AgentKind = agentKind.Value,
                        Reason = reason,
                    },
                }, CancellationToken.None);
                await TransitionFailed(item,
                    $"in-VM smoke gate: {reason}",
                    CancellationToken.None, project, failureKind: WorkItemFailureKinds.AgentUnavailable, agent: agentKind);
                return;
            }
        }

        // ── check-and-act branch ─────────────────────────────────────────────
        // A CheckAndAct item runs a single agent invocation in a sandbox that
        // evaluates a yes/no question against the project repo and returns a
        // structured JSON verdict on stdout. It never opens a PR, never merges,
        // never pushes upstream. On a matching verdict it enqueues a Normal
        // follow-up item; on a non-matching verdict it finishes Done with the
        // verdict recorded.
        if (item.JobType == JobType.CheckAndAct)
        {
            await RunCheckAndActAsync(item, project, agentRunner, ct);
            return;
        }

        ClaudeSessionLifecycle? claudeSessionLifecycle = null;
        try
        {
            await using var sandboxContext = new WorkSandboxContext(_sandboxes, _pipelineTuning, _log);
            var configuredBaseBranch = item.BaseBranch ?? project.DefaultBaseBranch;
            var repoId = await _gitHost.EnsureRepositoryAsync(item.Id, project.RepositoryUrl, configuredBaseBranch, ct);
            var baseBranch = configuredBaseBranch ?? await _gitHost.GetDefaultBranchAsync(repoId, ct);
            var hadRecordedWorkBranchAtEntry = !string.IsNullOrWhiteSpace(item.WorkBranch);
            var workBranch = item.WorkBranch ?? DefaultWorkBranchFor(item.Id);
            if (string.Equals(workBranch, baseBranch, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"workBranch must differ from baseBranch (both '{baseBranch}'); refusing to bypass merge-phase containment");

            // Session-mode dispatch: when the work item, its project, and the
            // global flag all opt in for the Claude resumable worker, open one
            // worker session+VM for implementation. Plan-off items open it
            // up-front; plan-on items open it only after the PLAN is approved so
            // the planning sandbox cannot mutate the implementation VM or host
            // repo before review. The lifecycle is published via the AsyncLocal
            // so the work / rework agent-phase path picks it up without any
            // explicit threading; the outer try/finally closes it on every exit
            // path. Items that don't opt in see no behaviour change:
            // claudeSessionLifecycle stays null and RunAgentPhaseAsync takes the
            // legacy independent-phase branch.
            if (!skipWork && !skipAudit && !planningLifecycleRequiredAtEntry)
            {
                claudeSessionLifecycle = await TryOpenClaudeSessionLifecycleAsync(
                    item,
                    project,
                    agentRunner,
                    repoId,
                    ct);
                // Publish the lifecycle on the AsyncLocal in RunAsync's own
                // frame. AsyncLocal values set inside an awaited child method
                // do NOT propagate back to the caller's ExecutionContext, so
                // any assignment inside TryOpen would be invisible to the
                // planning / work / rework reads below — assign here in the
                // parent frame so the value flows down to all agent turns.
                _ambientSessionLifecycle.Value = claudeSessionLifecycle;
            }

            if (planningLifecycleRequiredAtEntry)
            {
                var planningEntryModelId = item.ModelId;
                var planningEntryReasoningMode = item.ReasoningMode;
                var planningEntryAgentInstanceId = item.AgentInstanceId;
                item = await RunPlanningLifecycleIfNeededAsync(
                    item,
                    project,
                    agentRunner,
                    repoId,
                    baseBranch,
                    ct,
                    hostShutdownToken);
                claudeSessionLifecycle = _ambientSessionLifecycle.Value;

                var postPlanning = await _store.GetAsync(item.Id, ct) ?? item;
                if (!HasApprovedCurrentPlan(postPlanning))
                {
                    if (postPlanning.State == WorkItemState.Queued
                        && string.IsNullOrWhiteSpace(postPlanning.PlanArtifact))
                    {
                        _log.LogInformation(
                            "Planning lifecycle for work item {WorkItemId} exited before approval because the item was rewound to Queued at prompt revision {PromptRevision}.",
                            postPlanning.Id,
                            postPlanning.PromptRevision);
                        return;
                    }

                    throw new InvalidOperationException(
                        $"Planning lifecycle for work item {postPlanning.Id} did not produce an approved plan before implementation.");
                }

                item = postPlanning with
                {
                    ModelId = planningEntryModelId,
                    ReasoningMode = planningEntryReasoningMode,
                    AgentInstanceId = planningEntryAgentInstanceId,
                };

                if (!skipWork && !skipAudit && claudeSessionLifecycle is null)
                {
                    claudeSessionLifecycle = await TryOpenClaudeSessionLifecycleAsync(
                        item,
                        project,
                        agentRunner,
                        repoId,
                        ct);
                    _ambientSessionLifecycle.Value = claudeSessionLifecycle;
                }
            }
            if (!string.Equals(item.WorkBranch, workBranch, StringComparison.Ordinal))
            {
                item = item with { WorkBranch = workBranch };
                await _store.UpdateAsync((await _store.GetAsync(item.Id, ct) ?? item) with { WorkBranch = workBranch }, ct);
            }

            // Snapshot the branch tip that the interrupted turn started from
            // before the pickup rebase below can move the durable work ref. The
            // checkpoint commit is later compared against this exact commit so a
            // resumed no-op is accepted only when the interrupted turn had
            // already produced meaningful source changes.
            var resumePreTurnCommitSha = resumingPreempt
                ? await ResolveAgentTurnPreTurnCommitAsync(repoId, workBranch, baseBranch, ct)
                : null;

            // Fresh work-phase entry (a new WI, or a retry-from-work) must
            // observe a pristine base state. Reset the work branch in the
            // bare repo to the base tip so the sandbox clone does not carry
            // over a prior failed-attempt's commits — without this, the
            // retried agent inspects the work tree, sees its own prior work
            // already applied, and exits without writing anything, producing
            // the fail-quiet "Agent produced no changes to commit" symptom.
            // Existing explicit/non-owned queued branches remain protected
            // unless this is a watchdog/dead-worker recovery attempt. Operator
            // resume-from-work is explicit: ResumeAsync marks Queued entries
            // that intentionally preserve an existing work branch so the work
            // agent can continue on top of it. If a preserved branch
            // disappeared before pickup, there is nothing left to preserve and
            // the reset path creates it from base instead of silently returning
            // to Queued.
            // For non-Queued entries (resume from audit/merge/upstream) the
            // existing rebase preserves prior phase commits as intended.
            //
            // Do not run the required-build gate here. Queued entry is the
            // agent's chance to produce or repair work; a pre-existing broken
            // branch is inherited state, not this turn's output. Reset-eligible
            // branches are reset to base before the agent runs, and preserved
            // branches are handed to the agent as-is. The required-build gate
            // runs after the agent turn below and classifies only that output.
            using (BeginPhaseScope(item, "pickup"))
            {
                var branchEntry = entry is WorkItemState.Planning or WorkItemState.PlanReview or WorkItemState.PlanApproved
                    ? WorkItemState.Queued
                    : entry;
                if (item.AgentTurnRecoveryLease is not null)
                {
                    // The retained VM is the only authoritative mutable tree.
                    // Do not reset/rebase the host work branch until that tree
                    // has been content-bound into its immutable checkpoint.
                    _log.LogInformation(
                        "Deferring work-branch mutation for retained-sandbox conversion of work item {WorkItemId}",
                        item.Id);
                }
                else if (branchEntry is WorkItemState.Queued)
                {
                    var branchExists = await _gitHost.BranchExistsAsync(repoId, workBranch, ct);
                    var preserveExistingWorkBranch = branchExists
                        && ShouldPreserveQueuedWorkBranch(item, workBranch, hadRecordedWorkBranchAtEntry);
                    if (item.PreserveWorkBranchOnQueuedPickup && !branchExists)
                    {
                        _log.LogWarning(
                            "Work item {WorkItemId} requested queued pickup preservation for branch {WorkBranch}, but the branch is missing; resetting it to base {BaseBranch}",
                            item.Id, workBranch, baseBranch);
                    }

                    if (preserveExistingWorkBranch)
                    {
                        if (item.PreserveWorkBranchOnQueuedPickup)
                        {
                            await RebaseExistingWorkBranchOntoFreshBaseAsync(item, agentRunner, repoId, baseBranch, workBranch, project, ct);
                        }
                        _log.LogInformation(
                            "Preserving work branch {WorkBranch} for queued pickup of work item {WorkItemId}",
                            workBranch, item.Id);
                    }
                    else
                    {
                        await _gitHost.ResetWorkBranchToBaseAsync(repoId, workBranch, baseBranch, ct);
                    }
                }
                else if (!skipWork || !skipAudit || !skipMerge)
                {
                    await RebaseExistingWorkBranchOntoFreshBaseAsync(item, agentRunner, repoId, baseBranch, workBranch, project, ct);
                }
            }

            // Compose auditors up-front: the work-phase prompt advises the
            // agent to run the mechanical (shell) auditors itself before
            // committing, pre-empting iter-1 rework cycles for trivial
            // findings (format, lint, build-WaE).
            //
            // Filter to Code-target auditors so the code-audit phase mirrors the
            // plan-review phase's target filtering (which composes Plan-target
            // auditors). Every built-in preset is CodeOnly or PlanAndCode today,
            // so this is currently a no-op for the shipped set — but it keeps the
            // Targets seam symmetric so a Plan-only auditor never runs its
            // code-diff RunAsync during the code audit.
            var auditors = _auditorComposer.ComposeForTarget(project, agentRunner, AuditTarget.Code);
            AuditLog.AuditProfileSelected(project.Audit.Profile, auditors.Select(a => a.Name).ToArray());
            // currentRunAuditPass gates the merge on "an audit pass was produced
            // in THIS pickup". Two resume paths seed it true without running a
            // fresh audit here, and both are deliberate:
            //   • skipMerge (entered from Merged): the merge commit is already
            //     fixed; we are only resuming the upstream-push phase, so there
            //     is nothing to re-audit.
            //   • resumingConflictRework: conflict resolution re-touches the
            //     tree, but the operator invariant this gate enforces targets
            //     the WorkComplete/AuditPassed resume path (a whole prior-run
            //     verdict carried straight to merge). A conflict-rework resume
            //     still runs EnsureCurrentRealAuditPassBeforeMergeAsync, so the
            //     realness half of the invariant (no infra / "review agent
            //     failed to run" verdict) is enforced against the latest record;
            //     only the currency half is relaxed, because conflict resolution
            //     is a mechanical merge of already-audited changes rather than a
            //     new semantic edit. Widening this to force a full re-audit on
            //     every conflict rework is tracked separately and intentionally
            //     out of scope for the merge-currency fix.
            var currentRunAuditPass = skipMerge || resumingConflictRework;

            // Snapshot the self-review-checklist gate once per pickup so the
            // work prompt, the audit-iteration tag, and the audit-log event
            // all agree on the same state even if the operator hot-reloads
            // PipelineTuningOptions mid-item. The audit-log event fires only
            // when this pickup is actually dispatching work; resume pickups
            // skip it because the prompt that built the code under audit was
            // emitted (with its own gate state) on an earlier pickup.
            var selfReviewChecklistEnabled = _pipelineTuning.Current.SelfReviewChecklistEnabled;
            if (!skipWork)
                AuditLog.SelfReviewChecklistInjected(item.Id, selfReviewChecklistEnabled);

            // -------- Phase 1: Work --------
            if (!skipWork)
            {
                using var workPhaseScope = BeginPhaseScope(item, "work");
                var workIterationStart = DateTimeOffset.UtcNow;
                if (planningLifecycleRequiredAtEntry)
                {
                    var enteredWork = await TryEnterWorkFromApprovedPlanAsync(item, project, ct);
                    if (enteredWork is null)
                        return;
                    item = enteredWork;
                    await PublishIterationStartedAsync(item, project, IterationPhase.Work, AuditProgressIterationNumbers.WorkPhase, ct);
                    await _store.RecordIterationDispatchAsync(
                        item.Id, AuditProgressIterationNumbers.WorkPhase, item.PromptRevision, workIterationStart, ct);
                }
                else
                {
                    await PublishIterationStartedAsync(item, project, IterationPhase.Work, AuditProgressIterationNumbers.WorkPhase, ct);
                    await _store.RecordIterationDispatchAsync(
                        item.Id, AuditProgressIterationNumbers.WorkPhase, item.PromptRevision, workIterationStart, ct);
                    await Transition(item, WorkItemState.Working, ct, project);
                    item = item with { State = WorkItemState.Working };
                }
                string? workAgentStdout = null;
                var (workTimeout, _) = ResolveEffectiveWorkTimeout(item, project);
                using (var workPhase = new PhaseCancellation("work", ct, _opts.TimeProvider))
                {
                    workPhase.SetPhaseTimeout(ResolvePhaseAbsoluteTimeout(workTimeout));
                    workPhase.HookHostShutdown(hostShutdownToken, _opts.ShutdownGrace);
                    // In-iteration quota fallback: if the chosen agent hits quota
                    // mid-flight, swap to the next class member and retry. Audit,
                    // rework, and merge phases are wrapped equivalently below.
                    var sandboxTarget = SandboxTargetResolver.ResolveProjectPhase(project, project.NetworkProfiles.Work);
                    try
                    {
                        workAgentStdout = await InvokeAgentWithQuotaFallbackAsync(item, project, "work", iteration: null,
                            async (runner, trialItem, attemptCt) =>
                                await RunWithStuckProbeAsync(trialItem, project, runner.Kind, "work", workPhase, ct, phaseCt =>
                                    RunAgentPhaseAsync(trialItem, runner, repoId, baseBranch, workBranch,
                                        _promptComposer.BuildInitialWorkPrompt(
                                            trialItem.Prompt,
                                            project.AllowAgentQuestions,
                                            auditors,
                                            selfReviewChecklistEnabled,
                                            ApprovedPlanForImplementation(trialItem, planningLifecycleRequiredAtEntry)),
                                        isInitial: true,
                                        networkProfile: sandboxTarget.NetworkProfile,
                                        sandboxFlavor: sandboxTarget.Flavor,
                                        project: project,
                                        phaseCt,
                                        hostShutdownToken,
                                        buildFailurePolicy: RequiredBuildPolicy.Terminal,
                                        auditorsForPreemptiveSelfReview: auditors,
                                        resumePreTurnCommitSha: resumePreTurnCommitSha),
                                    workToken: attemptCt),
                            ct,
                            phaseCancellation: workPhase,
                            attemptTimeout: workTimeout);
                    }
                    catch (OperationCanceledException oce) when (oce is not PhaseCancellationException)
                    {
                        throw workPhase.Wrap(oce);
                    }
                }
                await Transition(item, WorkItemState.WorkComplete, ct, project);
                await PublishIterationCompletedAsync(item, project, IterationPhase.Work, AuditProgressIterationNumbers.WorkPhase,
                    repoId, workBranch, workIterationStart, ct);
                if (resumingPreempt)
                {
                    await ClearPreemptAsync(item, ct);
                    item = item with
                    {
                        PreemptedAt = null,
                        PreemptCheckpoint = null,
                        AgentTurnResumeCheckpoint = null,
                        AgentTurnRecoveryLease = null,
                    };
                }

                // When agent questions are enabled, parse stdout for <codeybox-question> blocks
                // and park the work item at NeedsOperatorInput if any new questions were found.
                if (project.AllowAgentQuestions && _questionStore is not null && workAgentStdout is not null)
                {
                    var parked = await TryParkForQuestionsAsync(item, project, workAgentStdout, ct);
                    if (parked) return; // Pipeline parked; resume when operator answers.
                }
            }
            else if (resumingPreempt && entry is WorkItemState.Reworking)
            {
                var resumeIteration = item.AgentTurnResumeCheckpoint?.Iteration ?? 1;
                using var reworkPhaseScope = BeginPhaseScope(item, "rework");
                await PublishIterationStartedAsync(item, project, IterationPhase.Rework, resumeIteration, ct);
                var resumeReworkStart = DateTimeOffset.UtcNow;
                await Transition(item, WorkItemState.Reworking, ct, project);
                string? reworkStdout = null;
                var (resumeWorkTimeout, _) = ResolveEffectiveWorkTimeout(item, project);
                using (var reworkPhase = new PhaseCancellation("rework-resume", ct, _opts.TimeProvider))
                {
                    reworkPhase.SetPhaseTimeout(ResolvePhaseAbsoluteTimeout(resumeWorkTimeout));
                    reworkPhase.HookHostShutdown(hostShutdownToken, _opts.ShutdownGrace);
                    var sandboxTarget = SandboxTargetResolver.ResolveProjectPhase(project, project.NetworkProfiles.Rework);
                    try
                    {
                        reworkStdout = await InvokeAgentWithQuotaFallbackAsync(item, project, "rework", resumeIteration,
                            async (runner, trialItem, attemptCt) =>
                                await RunWithStuckProbeAsync(trialItem, project, runner.Kind, "rework", reworkPhase, ct,
                                    phaseCt => RunAgentPhaseAsync(trialItem, runner, repoId, baseBranch, workBranch,
                                        trialItem.PreemptCheckpoint is { } checkpointRef
                                            ? _promptComposer.BuildInterruptedReworkResumePrompt(trialItem.Prompt, checkpointRef)
                                            : trialItem.Prompt,
                                        isInitial: false,
                                        networkProfile: sandboxTarget.NetworkProfile,
                                        sandboxFlavor: sandboxTarget.Flavor,
                                        project: project,
                                        phaseCt,
                                        hostShutdownToken,
                                        // The audit loop runs immediately after this resume-rework
                                        // path, so a non-compiling tree is re-detected by the audit
                                        // build gate and folded into the iteration's findings.
                                        buildFailurePolicy: RequiredBuildPolicy.DeferToAuditLoop,
                                        iteration: resumeIteration,
                                        resumePreTurnCommitSha: resumePreTurnCommitSha),
                                    workToken: attemptCt),
                            ct,
                            phaseCancellation: reworkPhase,
                            attemptTimeout: resumeWorkTimeout);
                    }
                    catch (OperationCanceledException oce) when (oce is not PhaseCancellationException)
                    {
                        throw reworkPhase.Wrap(oce);
                    }
                }
                await Transition(item, WorkItemState.WorkComplete, ct, project);
                await PublishIterationCompletedAsync(item, project, IterationPhase.Rework, resumeIteration,
                    repoId, workBranch, resumeReworkStart, ct);
                await ClearPreemptAsync(item, ct);
                item = item with
                {
                    PreemptedAt = null,
                    PreemptCheckpoint = null,
                    AgentTurnResumeCheckpoint = null,
                    AgentTurnRecoveryLease = null,
                };

                if (project.AllowAgentQuestions && _questionStore is not null && reworkStdout is not null)
                {
                    var parked = await TryParkForQuestionsAsync(item, project, reworkStdout, ct);
                    if (parked) return;
                }
            }

            // -------- Phase 1.D: Delegation (operator-triggered escape hatch) --------
            // Exactly one unconstrained turn. On success the item re-enters the
            // normal flow at the audit phase below (skipAudit is false for a
            // Delegating entry); the phase never merges and never re-enters
            // itself. A null result means the item parked (NeedsOperatorInput,
            // quota, or transient) or failed fast — stop the pipeline.
            if (isDelegationEntry)
            {
                var delegationResult = await RunDelegationPhaseAsync(
                    item, project, repoId, baseBranch, workBranch, ct, hostShutdownToken);
                if (delegationResult is null)
                    return;
                item = delegationResult;
            }

            // -------- Phase 1.5: Audit + rework loop --------
            var requiredBuildApplies = false;
            if (!skipAudit)
            {
                requiredBuildApplies = await _requiredBuildGate.AppliesAsync(item.Id, project.Id, repoId, baseBranch, workBranch, ct);
            }
            // Mechanical fixers must run even when no auditors apply (and no
            // required-build gate fires). The audit loop is the host for
            // mechanical-edit, so a project that configures fixers without
            // auditors still needs to enter it once to normalize the tree —
            // the loop exits at iteration 1 because empty scheduled auditors
            // produce zero findings and zero blocking findings.
            var mechanicalFixersConfigured = project.Audit.MechanicalFixers.Count > 0;
            var auditGateConfigured = auditors.Count > 0 || requiredBuildApplies || mechanicalFixersConfigured;
            if (!skipAudit && auditGateConfigured)
            {
                var auditParked = await RunAuditLoopAsync(item, project, agentRunner, auditors, repoId, baseBranch, workBranch, selfReviewChecklistEnabled, ct, hostShutdownToken);
                if (auditParked) return; // Pipeline parked; resume when operator answers.
                if (resumingPreempt)
                {
                    await ClearPreemptAsync(item, ct);
                    item = item with
                    {
                        PreemptedAt = null,
                        PreemptCheckpoint = null,
                        AgentTurnResumeCheckpoint = null,
                        AgentTurnRecoveryLease = null,
                    };
                }
                await Transition(item, WorkItemState.AuditPassed, ct, project);
                currentRunAuditPass = true;
            }
            else if (resumingPreempt)
            {
                await ClearPreemptAsync(item, ct);
                item = item with
                {
                    PreemptedAt = null,
                    PreemptCheckpoint = null,
                    AgentTurnResumeCheckpoint = null,
                    AgentTurnRecoveryLease = null,
                };
            }
            else if (!skipAudit)
            {
                // Reached only when !skipAudit but auditGateConfigured is false
                // (no auditors, no required-build gate, no mechanical fixers), so
                // there is nothing to audit this pickup. The assignment is a
                // defensive no-op: EnsureCurrentRealAuditPassBeforeMergeAsync
                // returns immediately when auditGateConfigured is false and never
                // reads currentRunAuditPass. Kept for symmetry so the "a pass was
                // produced this pickup" flag stays true on every non-skipped path.
                currentRunAuditPass = true;
            }

            // -------- Phase 1.5b: skip-audit-resume build gate --------
            // skipAudit is now true only for items entered from Merged (which
            // also set skipMerge=true and are excluded below) or for a
            // resumingConflictRework resume. Since Merged is excluded by
            // !skipMerge, the ONLY path that actually reaches this block is the
            // conflict-rework resume (AuditPassed no longer skips audit — it
            // re-runs the full audit loop above). The audit loop is skipped for
            // conflict rework, so the required-build gate above never runs for
            // it; re-verify the build here before any merge work so a
            // conflict-resolved tree that does not compile cannot be promoted to
            // merge. EnforceOnAuditPassedResumeAsync keeps its historical name
            // for compatibility; treat it as the skip-audit-resume build gate.
            if (skipAudit && !skipMerge)
            {
                await _requiredBuildGate.EnforceOnAuditPassedResumeAsync(
                    item, project, repoId, baseBranch, workBranch, ct);
            }

            // -------- Phase 1.6: Post-act re-validation (check-and-act follow-ups) --------
            // For items that were enqueued as the on-yes follow-up of a CheckAndAct
            // (OriginCheckWorkItemId set), re-run the originating check's question
            // against the now-modified repo BEFORE the merge phase. If the re-check
            // still returns the actionable answer the remediation did not satisfy
            // the check — the agent gets sent back to rework with the failing
            // verdict as feedback, bounded by the existing rework/iteration cap.
            // Skipped when resuming past merge (re-validation already happened on
            // the first pass) and for items not produced by a check.
            if (!skipMerge && item.OriginCheckWorkItemId is not null)
            {
                await RunPostActRevalidationLoopAsync(
                    item, project, agentRunner, repoId, baseBranch, workBranch, ct, hostShutdownToken);
                // RunPostActRevalidationLoopAsync mutates the item via the store on each
                // verdict. Refresh the in-memory snapshot so the downstream merge / PR
                // open phases see the updated ReCheckVerdicts list.
                item = await _store.GetAsync(item.Id, ct) ?? item;
            }

            if (!skipMerge)
            {
                await EnsureCurrentRealAuditPassBeforeMergeAsync(
                    item,
                    auditGateConfigured,
                    currentRunAuditPass,
                    ct);

                // Post-implementation e2e-replay gate: every declared e2e-replay
                // test case must end this pickup with a committed replay that
                // re-runs green. A block throws E2eReplayGateBlockedException,
                // caught below and mapped to a work-quality failure. No-op when
                // the gate is unwired or its own knob is off.
                await EnforceE2eReplayGateBeforeMergeAsync(item, ct);
            }

            // Open PR record (local metadata) AFTER the audit converges.
            // Skip if we're resuming past merge — merge is the only consumer.
            PullRequest? pr = null;
            if (!skipMerge)
            {
                pr = await _prs.OpenAsync(new OpenPullRequest(
                    RepositoryId: repoId,
                    SourceBranch: workBranch,
                    TargetBranch: baseBranch,
                    Title: item.Title,
                    Description: $"Work item {item.Id} via {agentKind.Value} (project {project.Id})"), ct);
            }

            // The upstream remote is constructed before the merge phase rather
            // than at upstream-push time so the pre-merge canonical-base refresh
            // (below) can reuse its auth path. CompleteAsync is still called
            // later, on the same instance.
            var upstream = _upstreamFactory.Create(project);

            // -------- Phase 2: Merge (agent-driven) --------
            // The merge phase is wrapped in a reusable async helper so the
            // upstream-push phase can re-invoke it on a 405 auto-merge race
            // against upstream main motion without duplicating the
            // PhaseCancellation + quota-fallback + stuck-probe wiring.
            async Task<(string MergeSha, string? AgentStdout)> RunMergePhase(CancellationToken phaseCt)
            {
                using var mergePhase = new PhaseCancellation("merge", phaseCt, _opts.TimeProvider);
                mergePhase.SetPhaseTimeout(ResolvePhaseAbsoluteTimeout(item.MergeTimeout));
                mergePhase.HookHostShutdown(hostShutdownToken, _opts.ShutdownGrace);
                try
                {
                    return await InvokeAgentWithQuotaFallbackAsync(item, project, "merge", iteration: null,
                        async (runner, trialItem, attemptCt) =>
                            await RunWithStuckProbeAsync(trialItem, project, runner.Kind, "merge", mergePhase, phaseCt, mergeCt =>
                                RunAgentMergePhaseAsync(trialItem, runner, repoId, baseBranch, workBranch,
                                    networkProfile: project.NetworkProfiles.Merge,
                                    project: project,
                                    mergeCt,
                                    hostShutdownToken),
                                workToken: attemptCt),
                        phaseCt,
                        phaseCancellation: mergePhase,
                        attemptTimeout: item.MergeTimeout);
                }
                catch (OperationCanceledException oce) when (oce is not PhaseCancellationException)
                {
                    throw mergePhase.Wrap(oce);
                }
            }

            string? mergeSha = null;
            string? agentStdout = null;
            if (!skipMerge)
            {
                using var mergePhaseScope = BeginPhaseScope(item, "merge");
                await PublishMergeStartedAsync(item, project, baseBranch, workBranch, ct);
                await Transition(item, WorkItemState.Merging, ct, project);

                // Stale-base guard. The per-work-item bare repo's local base
                // was snapshotted at item dispatch; sibling work items merged
                // since then have moved the canonical upstream tip. Without
                // this refresh, the merge phase agent would compose the merge
                // against the stale fork-point, producing a mergeSha whose
                // first-parent ancestry omits everything sibling work landed,
                // which then silently reverts that work when GitHub's merge
                // commit is published. Best-effort: a failure here logs a
                // warning rather than parking — the existing 405 auto-merge
                // race recovery + non-fast-forward push reconcile still catch
                // motion that races our refresh, and a transient fetch failure
                // shouldn't strand an item that's done all its agent work.
                if (project.Upstream.Kind != "noop")
                {
                    await TryRefreshCanonicalBaseBeforeMergeAsync(item, project, upstream, repoId, baseBranch, ct);
                }

                try
                {
                    (mergeSha, agentStdout) = await RunMergePhase(ct);
                }
                catch (MergeConflictResolutionFailedException firstFailure)
                {
                    // Third-line fallback: c9fd5b75 (preventive auto-rebase) and the
                    // merge-phase agent (77ce33c667 on 405 race) have both run their
                    // course. Re-engage the ORIGINAL work agent — who knows why this
                    // PR was written — with a focused conflict-resolution prompt on
                    // the existing work branch. Capped at one iteration per merge
                    // attempt; a second failure parks at MergeConflictResolutionFailed.
                    var current = await _store.GetAsync(item.Id, ct) ?? item;
                    var conflictReworkAttemptAlreadyReserved =
                        resumingConflictRework && current.ConflictReworkAttempts > 0;
                    if (current.ConflictReworkAttempts > 0 && !conflictReworkAttemptAlreadyReserved)
                    {
                        _log.LogWarning(
                            "Work item {Id} merge conflict-rework already ran ({Attempts}); not re-engaging the agent",
                            item.Id, current.ConflictReworkAttempts);
                        throw;
                    }

                    var reworkOutcome = await RunConflictReworkIterationAsync(
                        current, project, agentRunner, repoId, baseBranch, workBranch,
                        firstFailure, ct, hostShutdownToken,
                        countAttempt: !conflictReworkAttemptAlreadyReserved);
                    if (!reworkOutcome.Success)
                    {
                        throw new MergeConflictResolutionFailedException(
                            reworkOutcome.ParkReason!,
                            firstFailure,
                            failureKind: reworkOutcome.FailureKind,
                            agent: reworkOutcome.Agent);
                    }

                    // Refresh the local snapshot so subsequent UpdateAsync
                    // calls (which use UPDATE … SET … from a stale `item`)
                    // don't clobber the bumped ConflictReworkAttempts and the
                    // new state recorded during the rework iteration.
                    item = await _store.GetAsync(item.Id, ct) ?? item;
                    await Transition(item, WorkItemState.Merging, ct, project);
                    (mergeSha, agentStdout) = await RunMergePhase(ct);
                }
                await _prs.MarkMergedAsync(pr!.Id, mergeSha!, ct);
                // mergeSha is the LOCAL bare-repo merge sha produced by the
                // agent; it does NOT match the squash commit GitHub mints at
                // auto-merge time. Persist it on LocalSquashSha so race
                // recovery can still walk its first-parent ancestry; MergeSha
                // is reserved for the GitHub-side authoritative sha returned
                // by upstream.CompleteAsync, written in RunUpstreamPushPhaseAsync.
                await _store.UpdateAsync(item with { LocalSquashSha = mergeSha }, ct);
                await Transition(item, WorkItemState.Merged, ct, project);
                await PublishMergeCompletedAsync(item, project, baseBranch, workBranch, mergeSha, ct);
            }

            // -------- Phase 3: Upstream push (separate atomic unit) --------
            if (item.PushUpstream && project.Upstream.Kind != "noop")
            {
                await RunUpstreamPushPhaseAsync(
                    item, project, upstream, repoId, baseBranch, workBranch, mergeSha, agentStdout,
                    reRunMergePhase: RunMergePhase,
                    ct, hostShutdownToken);
            }
            else
            {
                await Transition(item, WorkItemState.Done, ct, project);
            }
        }
        catch (PhaseCancellationException pex) when (
            hostShutdownToken.IsCancellationRequested
            || pex.Source == CancellationSources.HostShutdown)
        {
            // Host is shutting down — leave the item in its current mid-flight
            // state. The recovery loop will reset and re-enqueue it on next startup.
            PhaseCancellation.LogBoundary(_log, "RunAsync.host-shutdown", pex.Phase, pex.Source,
                operatorRequested: ct.IsCancellationRequested,
                hostShutdown: true,
                exception: pex);
            _log.LogInformation(
                "Work item {Id} interrupted by host shutdown in phase '{Phase}' (source={Source}); leaving in mid-flight state for recovery",
                item.Id, pex.Phase, pex.Source);
            throw;
        }
        catch (PhaseCancellationException pex) when (
            ct.IsCancellationRequested
            || pex.Source == CancellationSources.Operator)
        {
            PhaseCancellation.LogBoundary(_log, "RunAsync.operator-cancel", pex.Phase, pex.Source,
                operatorRequested: true,
                hostShutdown: false,
                exception: pex);
            if (IsRecoveryCancellation(item.Id))
            {
                _log.LogInformation(
                    "Work item {Id} was aborted by recovery in phase '{Phase}'; leaving recovered durable state intact",
                    item.Id, pex.Phase);
                throw;
            }

            await HandleOperatorCancelAsync(item, project, pex.Phase);
            throw;
        }
        catch (PhaseCancellationException pex) when (CancellationSources.IsPhaseTimeout(pex.Source))
        {
            // Actual configured timeout fired — the per-phase wall-clock cap
            // we set via SetPhaseTimeout. Surface as failureKind="timeout" so
            // the operator can tell apart "your WorkTimeout is too tight"
            // from the (formerly-conflated) "host-side cancellation glitch".
            PhaseCancellation.LogBoundary(_log, "RunAsync.configured-timeout", pex.Phase, pex.Source,
                operatorRequested: false,
                hostShutdown: false,
                exception: pex);
            _log.LogWarning(
                "Work item {Id} hit configured timeout in phase '{Phase}' (source={Source})",
                item.Id, pex.Phase, pex.Source);
            // A work-phase timeout names the budget that was in force, where
            // that value came from (item, project, or global default), and how
            // to raise it — the budget is unreachable after the fact through
            // PATCH (Failed items are not editable), so the message must carry
            // the remedy. Other phases keep the terse form.
            string timeoutError;
            if (string.Equals(pex.Source, CancellationSources.PhaseTimeout("work"), StringComparison.Ordinal))
            {
                var (timeoutBudget, timeoutSource) = ResolveEffectiveWorkTimeout(item, project);
                timeoutError = WorkTimeoutPolicy.FormatTimeoutError(
                    pex.Phase, item.Id, timeoutBudget, timeoutSource, project.Id.Value);
            }
            else
            {
                timeoutError = $"phase '{pex.Phase}' exceeded configured timeout ({pex.Source})";
            }
            await TransitionFailed(item,
                timeoutError,
                CancellationToken.None, project,
                failureKind: "timeout",
                cancellationSource: pex.Source);
        }
        catch (PhaseCancellationException pex)
        {
            // Unattributed cancellation — neither operator cancel, nor host
            // shutdown, nor a configured timeout. Treat as transient candidate:
            // try the auto-retry path; if exhausted, surface a clearer error
            // instead of the old generic "A task was canceled." string.
            PhaseCancellation.LogBoundary(_log, "RunAsync.unattributed", pex.Phase, pex.Source,
                operatorRequested: false,
                hostShutdown: false,
                exception: pex);
            await HandleTransientCancellationAsync(item, project, pex);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || hostShutdownToken.IsCancellationRequested)
        {
            // Legacy fallthrough for OCEs that bypassed PhaseCancellation —
            // preserves the previous behaviour for code paths still using
            // raw OCE propagation (e.g. early-cancel checks before phase setup).
            if (hostShutdownToken.IsCancellationRequested)
            {
                _log.LogInformation(
                    "Work item {Id} interrupted by host shutdown (legacy OCE path); leaving in mid-flight state for recovery",
                    item.Id);
            }
            else
            {
                if (IsRecoveryCancellation(item.Id))
                {
                    _log.LogInformation(
                        "Work item {Id} was aborted by recovery (legacy OCE path); leaving recovered durable state intact",
                        item.Id);
                    throw;
                }

                await HandleOperatorCancelAsync(item, project);
            }
            throw;
        }
        catch (OperationCanceledException ex)
        {
            // Last-resort catch: an OCE escaped all PhaseCancellation scopes
            // without attribution AND neither root token is cancelled. Treat as
            // an unknown-source transient cancellation so the auto-retry path
            // covers it instead of dead-ending with a generic "timeout" label.
            _log.LogWarning(ex,
                "Work item {Id} hit an unwrapped OperationCanceledException with no clear source; routing to transient-retry path",
                item.Id);
            await HandleTransientCancellationAsync(item, project,
                new PhaseCancellationException("unknown", CancellationSources.Unknown, ex));
        }
        catch (AgentTurnCheckpointConvertedException ex)
        {
            await ScheduleConvertedAgentTurnCheckpointAsync(item, project, ex.Phase);
        }
        catch (AgentTurnResumeClaimConflictException ex)
        {
            _log.LogInformation(
                ex,
                "Skipping duplicate durable agent-turn dispatch for work item {Id} because another worker or state change owns the claim",
                item.Id);
        }
        catch (InvalidAgentTurnResumeCheckpointException ex)
        {
            _log.LogError(ex, "Work item {Id} has an invalid durable agent-turn checkpoint at dispatch", item.Id);
            await TransitionFailed(
                item,
                "The durable agent-turn checkpoint changed and cannot be resumed safely.",
                CancellationToken.None,
                project,
                failureKind: "restore");
        }
        catch (AuditFailedException ex)
        {
            _log.LogWarning("Work item {Id} audit failed: {Error}", item.Id, ex.Message);
            var current = await _store.GetAsync(item.Id, CancellationToken.None) ?? item;
            var failed = current.With(WorkItemState.AuditFailed, ex.Message);
            await _store.UpdateAsync(failed, CancellationToken.None);
            var auditFailedRevision = await BuildTerminalRevisionAsync(failed, CancellationToken.None);
            await _webhooks.PublishAsync(new WebhookEvent
            {
                Event = "work_item.audit_failed",
                WorkItem = failed,
                Project = project,
                PromptRevision = auditFailedRevision?.PromptRevision,
                RevisionAtCompletion = auditFailedRevision?.RevisionAtCompletion,
                RevisionMatches = auditFailedRevision?.RevisionMatches,
            }, CancellationToken.None);
        }
        catch (MergeConflictResolutionFailedException ex)
        {
            _log.LogWarning("Work item {Id} merge conflict resolution failed: {Error}", item.Id, ex.Message);
            var current = await _store.GetAsync(item.Id, CancellationToken.None) ?? item;
            var attributed = ex.Agent is { } failedAgent
                ? current with
                {
                    Agent = failedAgent,
                    AgentInstanceId = current.Agent == failedAgent ? current.AgentInstanceId : null,
                }
                : current;
            var failed = attributed.With(
                WorkItemState.MergeConflictResolutionFailed,
                ex.Message,
                failureKind: ex.FailureKind);
            await _store.UpdateAsync(failed, CancellationToken.None);
            await RecordMergeConflictFailureAttributionAsync(failed, ex);
            var mergeFailedRevision = await BuildTerminalRevisionAsync(failed, CancellationToken.None);
            await _webhooks.PublishAsync(new WebhookEvent
            {
                Event = "work_item.merge_conflict_resolution_failed",
                WorkItem = failed,
                Project = project,
                PromptRevision = mergeFailedRevision?.PromptRevision,
                RevisionAtCompletion = mergeFailedRevision?.RevisionAtCompletion,
                RevisionMatches = mergeFailedRevision?.RevisionMatches,
            }, CancellationToken.None);
        }
        catch (AgentPausedException ex)
        {
            _log.LogInformation(
                "Work item {Id} parking in WaitingForAgentResume: {Reason}",
                item.Id, ex.Message);
            await TransitionWaitingForAgentResumeAsync(
                item,
                ex.Message,
                project,
                ex.Agent,
                RetryFromForAgentPausePhase(ex.Phase, item.State));
        }
        catch (AgentAuthRequiredException ex)
        {
            _log.LogWarning(
                "Work item {Id} failed because agent {Agent} requires re-authentication in phase {Phase}: {Reason}",
                item.Id, ex.Agent.Value, ex.Phase, ex.Message);
            await TransitionFailed(
                item,
                ex.Message,
                CancellationToken.None,
                project,
                failureKind: WorkItemFailureKinds.AuthRequired,
                agent: ex.Agent,
                authFailureScope: ex.Scope);
        }
        catch (AgentInfrastructureFailureException ex)
        {
            _log.LogWarning(
                "Work item {Id} failed because agent {Agent} hit infrastructure failure in phase {Phase}: {Reason}",
                item.Id, ex.Agent.Value, ex.Phase, ex.Message);
            await TransitionFailed(
                item,
                ex.Message,
                CancellationToken.None,
                project,
                failureKind: WorkItemFailureKinds.Infrastructure,
                agent: ex.Agent);
        }
        catch (AgentUnavailableException ex)
        {
            // Pre-dispatch availability/routing failed before an agent reasoning
            // loop could start. Structure the failure so operators can distinguish
            // a concrete unavailable runner from aggregate routing/capacity misses.
            _log.LogWarning("Work item {Id} agent unavailable: {Error}", item.Id, ex.Message);
            var failureKind = ex.Agent is null
                ? WorkItemFailureKinds.AgentRoutingUnavailable
                : WorkItemFailureKinds.AgentUnavailable;
            await TransitionFailed(
                item,
                ex.Message,
                CancellationToken.None,
                project,
                failureKind: failureKind,
                agent: ex.Agent,
                clearAgent: ex.Agent is null);
        }
        catch (AgentStuckException stuckEx)
        {
            await HandleAgentStuckAsync(item, project, stuckEx);
        }
        catch (AgentClassExhaustedException ex)
        {
            _log.LogWarning(
                "Work item {Id} parking in WaitingForQuotaReset: {Reason}",
                item.Id, ex.Message);
            await TransitionWaitingForQuotaResetAsync(item, ex, project);
        }
        catch (ToolchainFaultTransientException ex)
        {
            // A gate subprocess failed with a retryable toolchain-fault
            // signature: the tool failed, not the diff. Re-run the same
            // commit through the existing bounded WaitingForTransientRetry
            // path (attempts + jitter owned by the retry scheduler) instead
            // of recording a finding against the diff. Deliberately distinct
            // from flake attribution, which consults the base branch.
            _log.LogWarning(
                "Work item {Id} hit toolchain fault '{FaultClass}' (signature {Signature}) in phase {Phase}; parking for transient retry",
                item.Id,
                ex.Classification.FaultClass,
                ex.Classification.MatchedSignature,
                ex.Phase ?? "(unknown)");
            await TransitionWaitingForTransientRetryAsync(item, ex.Message, project, ex.Phase, item.Agent);
        }
        catch (RequiredBuildFailedException ex)
        {
            _log.LogWarning("Work item {Id} failed required build gate: {Error}", item.Id, ex.Message);
            await TransitionFailed(item, ex.Message, CancellationToken.None, project, failureKind: "build");
        }
        catch (RequiredBuildVerificationUnavailableException ex)
        {
            _log.LogWarning(ex, "Work item {Id} could not verify required build", item.Id);
            await TransitionFailed(item, ex.Message, CancellationToken.None, project, failureKind: "infrastructure");
        }
        catch (AuditUnavailableException ex)
        {
            _log.LogWarning(ex, "Work item {Id} could not verify audit gate", item.Id);
            // A deterministic refusal (the runner rejected its arguments, so an
            // unchanged retry fails identically) is a configuration error, not a
            // transient provisioning fault: stamping the configuration kind
            // classifies it Deterministic downstream, so it surfaces immediately
            // instead of burning the recovery-attempt budget on identical retries.
            var failureKind = ex.IsDeterministic
                ? WorkItemFailureKinds.Configuration
                : WorkItemFailureKinds.Infrastructure;
            await TransitionFailed(item, ex.Message, CancellationToken.None, project, failureKind: failureKind);
        }
        catch (E2eReplayGateBlockedException ex)
        {
            // A declared e2e-replay capability could not be made green even after
            // the gate's own cheap-model (re-)authoring — a work-quality failure,
            // the gate working as designed. Kind "e2e-replay" mirrors "build":
            // not scored as infra health by TransitionHealthClassifier.
            _log.LogWarning(ex, "Work item {Id} failed the e2e-replay merge gate", item.Id);
            await TransitionFailed(item, ex.Message, CancellationToken.None, project, failureKind: "e2e-replay");
        }
        catch (AuditHistoryLoadFailedException ex)
        {
            _log.LogWarning(ex, "Work item {Id} could not load persisted audit history", item.Id);
            await TransitionFailed(item, ex.Message, CancellationToken.None, project, failureKind: "infrastructure");
        }
        catch (AuditHistoryPersistenceFailedException ex)
        {
            _log.LogWarning(ex, "Work item {Id} could not persist audit progress", item.Id);
            await TransitionFailed(item, ex.Message, CancellationToken.None, project, failureKind: "infrastructure");
        }
        catch (ProjectMechanicalFixerConfigurationException ex)
        {
            _log.LogWarning(ex, "Work item {Id} mechanical edit configuration is invalid", item.Id);
            await TransitionFailed(item, ex.Message, CancellationToken.None, project, failureKind: "configuration");
        }
        catch (MechanicalFixerException ex)
        {
            // The mechanical-edit phase is infrastructure-level (sandbox
            // clone / git plumbing / patch import) and is NOT a substitute
            // for the audit gate — csharp:format-check still runs as a
            // safety net. Park as a transient retry instead of failing the
            // item terminally so an isolated infra hiccup (bare-repo
            // contention, sandbox provisioning glitch, patch race) gets a
            // bounded retry budget; if the budget exhausts the scheduler
            // surfaces it as a real failure. ResumeStateForTransientRetry
            // maps "mechanical-edit" → WorkComplete so the retry replays
            // the same phase boundary the cancellation path uses.
            _log.LogWarning(ex, "Work item {Id} mechanical edit phase failed; scheduling transient retry", item.Id);
            await TransitionWaitingForTransientRetryAsync(
                item,
                ex.Message,
                project,
                phase: "mechanical-edit",
                agent: null);
        }
        catch (TerminalQuotaError ex)
        {
            // Quota rejection is never a terminal Failure: the agent (or a peer
            // in its class) will become available again at ResetAt, so the item
            // must always park as WaitingForQuotaReset so QuotaRetryScheduler
            // re-dispatches on reset. The mid-iteration fallback inside
            // InvokeAgentWithQuotaFallbackAsync only converts to
            // AgentClassExhaustedException when a class is wired; the
            // no-class / single-agent path delivers TerminalQuotaError here
            // unchanged, and that path must NOT hard-fail the work item
            // (acceptance: Claude five_hour rate_limit_event rejection must
            // park, not Fail).
            _log.LogWarning("Work item {Id} hit quota: {Error}", item.Id, ex.Message);
            var current = await _store.GetAsync(item.Id, CancellationToken.None) ?? item;
            await TransitionWaitingForQuotaResetAsync(
                item,
                ex.Message,
                phase: PhaseForQuotaPark(current.State),
                quotaResetAt: ex.ResetAt,
                project: project,
                iteration: null,
                quotaKind: ex.Kind,
                quotaEvidenceTrusted: ex.ProviderSurfaceMatch);
        }
        catch (AgentSessionResumeExhaustedException ex)
        {
            var exhaustedRunner = _agents.TryGet(ex.Agent, out var resolvedRunner)
                ? resolvedRunner
                : agentRunner;
            var authDetection = _authFailureClassifier.DetectDetailed(
                exhaustedRunner.Kind,
                ex.LastResult.Stderr,
                ex.LastResult.Stdout);
            if (authDetection is { Classification.Kind: AgentFailureKind.AuthRequired })
            {
                // Route stdout-only evidence through the corroboration policy
                // so a single model-controlled stdout match cannot globally
                // bench the agent without the forced in-VM probe confirming
                // the prompt. This matches the rebase/merge/audit/check-and-act
                // call sites; previously this branch published side effects
                // unconditionally on the stdout-only path, defeating the
                // corroboration safety net for resumable runners.
                var authHandling = await HandleAuthRequiredDetectionAsync(
                    item,
                    project,
                    exhaustedRunner.Kind,
                    "session-resume",
                    authDetection.Classification,
                    throwOnMatch: false,
                    stdoutOnlyEvidence: authDetection.IsStdoutOnly,
                    requireStdoutOnlyCorroboration: true,
                    matchedConfiguredPattern: authDetection.MatchedConfiguredStderrPattern
                        || authDetection.MatchedConfiguredStdoutPattern,
                    ct: CancellationToken.None);
                _log.LogWarning(
                    "Work item {Id} failed because agent {Agent} requires re-authentication after session resume exhaustion: {Reason}",
                    item.Id, exhaustedRunner.Kind.Value, ex.Message);
                await TransitionFailed(
                    item,
                    authHandling.Reason
                        ?? _authRequiredHandler.BuildReason("session-resume", authDetection.Classification, authDetection.IsStdoutOnly),
                    CancellationToken.None,
                    project,
                    failureKind: WorkItemFailureKinds.AuthRequired,
                    agent: exhaustedRunner.Kind,
                    authFailureScope: authHandling.Scope);
                return;
            }

            var transient = TryBuildTransientAgentFailure(
                exhaustedRunner,
                ex.LastResult,
                phase: null,
                failureContext: "after exhausting session resume");
            if (transient is not null)
            {
                _log.LogWarning(
                    ex,
                    "Work item {Id} hit transient transport failure after session resume exhaustion: agent={Agent} error={Error}",
                    item.Id,
                    ex.Agent.Value,
                    transient.Message);
                await TransitionWaitingForTransientRetryAsync(item, transient, project);
                return;
            }

            _log.LogError(ex, "Work item {Id} failed after session resume exhaustion", item.Id);
            await TransitionFailed(item, ex.Message, CancellationToken.None, project, failureKind: "other");
        }
        catch (TerminalTransientNetworkError ex)
        {
            _log.LogWarning(
                "Work item {Id} hit transient transport failure: phase={Phase} agent={Agent} error={Error}",
                item.Id,
                ex.Phase ?? "(unknown)",
                ex.Agent.Value,
                ex.Message);
            await TransitionWaitingForTransientRetryAsync(item, ex, project);
        }
        catch (SandboxDiskDeferredException)
        {
            // Disk-guard preflight refused a sandbox launch. Re-throw so
            // OrchestratorService can route this to the same defer-and-requeue
            // path as the budget cap (audit + disk.deferred webhook +
            // ScheduleDeferredRequeue). Without this re-throw the catch-all
            // below would mark the item terminally Failed.
            throw;
        }
        catch (SandboxProvisioningDeferredException)
        {
            // Host-side sandbox provisioning exhausted a transient retry
            // budget. Re-throw so OrchestratorService can move the item back
            // to a durable pre-phase state and re-enqueue it instead of
            // treating the infrastructure flap as an agent failure.
            throw;
        }
        catch (NoActionRequiredException ex)
        {
            // The agent's explicit, structured determination that no action is
            // warranted — a terminal resolution, not a failure. The item is
            // resolved to NoActionRequired with the reasoning preserved, the
            // no-changes breaker is untouched (it was never fed), and the
            // item does not re-enter the queue. An empty diff WITHOUT such a
            // report still lands in the generic failure catch below.
            _log.LogInformation(
                "Work item {Id} resolved as no action required by agent {Agent}: {Reason}",
                item.Id, ex.Agent.Value, SanitizedAgentDetail.FromRaw(ex.Reason).Value);
            await TransitionNoActionRequiredAsync(item, project, ex, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Work item {Id} failed", item.Id);
            var current = await _store.GetAsync(item.Id, CancellationToken.None) ?? item;
            var failureKind = current.AgentTurnRecoveryLease is not null
                && current.HasTypedAgentTurnRecoveryBoundary
                    ? WorkItemFailureKinds.Infrastructure
                    : "other";
            await TransitionFailed(
                current,
                ex.Message,
                CancellationToken.None,
                project,
                failureKind: failureKind,
                agent: failureKind == WorkItemFailureKinds.Infrastructure
                    ? current.Agent
                    : null);
        }
        finally
        {
            // Tear down the resumable Claude worker VM on every exit path
            // (success, terminal failure, host shutdown). The lifecycle's
            // CloseSessionAsync disposes the VM regardless of suspend state,
            // so a session that completed cleanly + got suspended after the
            // last rework turn still has its VM destroyed here — no idle VMs
            // leak past terminal transitions.
            if (claudeSessionLifecycle is not null)
            {
                try
                {
                    await CloseAmbientClaudeSessionAsync(
                        claudeSessionLifecycle,
                        item,
                        project,
                        "pipeline terminal cleanup");
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex,
                        "Claude session terminal cleanup failed for work item {Id}; marking the item failed and rethrowing so cleanup can be retried",
                        item.Id);
                    await TransitionFailed(
                        item,
                        $"Claude session terminal cleanup failed: {ex.Message}",
                        CancellationToken.None,
                        project,
                        failureKind: "infrastructure");
                    throw;
                }
                _ambientSessionLifecycle.Value = null;
            }

            if (_stdoutBroadcaster is not null)
            {
                try { await _stdoutBroadcaster.CompleteAsync(item.Id); }
                catch { /* best-effort: SignalR clients may have disconnected */ }
            }
        }
    }

    internal const string CoAuthoredByTrailer = "\n\n" + CodeyBoxTrailers.CoAuthoredBy;

    /// <summary>
    /// Alias for <see cref="CodeyBoxTrailers.PromptRevisionEnvVar"/>. Kept as an
    /// internal const so existing call sites in this assembly keep the short
    /// name; the canonical definition is shared via Core so audit modules and
    /// rework-prompt templates reference the same symbol.
    /// </summary>
    internal const string PromptRevisionEnvVar = CodeyBoxTrailers.PromptRevisionEnvVar;

}
