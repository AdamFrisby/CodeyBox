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

// PipelineRunner.AuditParking.cs — Audit parking and delegation: max-iteration/empty-rework parks, delegation phase, and operator handoff.
public sealed partial class PipelineRunner
{
    /// <summary>
    /// Item-level resilience for an empty rework (no commit, no infra signature).
    /// Re-dispatches with an escalated instruction up to
    /// <c>PipelineTuning.EmptyReworkEscalationRetries</c> times when the audit
    /// history shows convergence progress; otherwise (or when retries exhaust)
    /// parks the item via the same operator-input flow the audit-iteration
    /// ceiling uses, so a partially-converged item is preserved instead of
    /// being terminal-failed on a single declined pass. Terminal no-progress
    /// failure still belongs to the audit ceiling itself.
    /// </summary>
    /// <remarks>
    /// Rework after audit iteration N feeds audit iteration N+1, so
    /// reworkIterationNumber can equal maxIterations while the blank pass is
    /// still in-budget.
    /// </remarks>
    internal async Task<bool> HandleEmptyReworkAsync(
        WorkItem item,
        Project project,
        ReworkProducedNoChangesException emptyEx,
        IReadOnlyList<AuditProgressSnapshot> auditHistory,
        int auditIteration,
        int reworkIterationNumber,
        int maxIterations,
        string baseReworkPrompt,
        Func<string, Task<string?>> dispatchAsync,
        PhaseCancellation reworkPhase,
        DateTimeOffset reworkStart,
        string repoId,
        string workBranch,
        CancellationToken ct)
    {
        if (auditHistory.Count == 0)
            throw new InvalidOperationException("Empty rework handling requires at least one audit progress snapshot.");

        // With zero blocking findings there was nothing for the agent to
        // change, so the empty pass is a correct no-op — not a silent-failure
        // signal. Likewise, a rework driven by a superseded (non-complete)
        // verdict was dispatched against findings that were never finished,
        // so its empty pass says nothing about the agent's health either.
        // Refund the no-changes outcome the dispatch recorded so neither pass
        // counts toward the no-changes circuit breaker.
        if (auditHistory[^1].BlockingFindings == 0 || !auditHistory[^1].IsComplete)
            _availability?.RefundNoChangesOutcome(emptyEx.Agent, item.Id);

        var converging = HasAuditConvergenceProgress(auditHistory);
        var configuredRetries = Math.Max(0, _pipelineTuning.Current.EmptyReworkEscalationRetries);
        var attempts = converging ? configuredRetries : 0;

        _log.LogWarning(
            "Work item {Id} rework iteration {Iter} produced no changes (agent {Agent}); " +
            "converging={Converging}, configured escalation retries={ConfiguredRetries}, effective escalation attempts={Attempts}",
            item.Id, reworkIterationNumber, emptyEx.Agent.Value, converging, configuredRetries, attempts);
        CodeyBoxMeters.ReworkEmptyEvents.Add(1,
            new KeyValuePair<string, object?>("outcome", "detected"));

        string? lastStdout = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException oce)
            {
                throw reworkPhase.Wrap(oce);
            }

            var escalatedPrompt = BuildEmptyReworkEscalationPrompt(
                originalPrompt: baseReworkPrompt,
                attempt: attempt,
                totalAttempts: attempts);
            _log.LogInformation(
                "Re-dispatching empty rework iteration {Iter} for work item {Id} with escalation attempt {Attempt}/{Total}",
                reworkIterationNumber, item.Id, attempt, attempts);
            try
            {
                lastStdout = await dispatchAsync(escalatedPrompt);
                CodeyBoxMeters.ReworkEmptyEvents.Add(1,
                    new KeyValuePair<string, object?>("outcome", "escalation_succeeded"));
                return await CompleteAuditReworkAsync(
                    item,
                    project,
                    reworkIterationNumber,
                    repoId,
                    workBranch,
                    reworkStart,
                    lastStdout,
                    ct);
            }
            catch (OperationCanceledException oce) when (oce is not PhaseCancellationException)
            {
                throw reworkPhase.Wrap(oce);
            }
            catch (ReworkProducedNoChangesException)
            {
                // Still empty — try the next escalation, or fall through to
                // park when the retry budget is exhausted.
                continue;
            }
        }

        // Either we never had convergence, escalation retries are disabled, or
        // every escalation pass came back empty. The failed audit iteration is
        // the budget boundary; reworkIterationNumber is the future audit pass
        // this rework would feed. So rework N of max N is still in-budget.
        if (!converging && auditIteration >= maxIterations)
        {
            CodeyBoxMeters.ReworkEmptyEvents.Add(1,
                new KeyValuePair<string, object?>("outcome", "failed"));
            var last = auditHistory[^1];
            var remaining = _promptComposer.BuildBlockingFindingSummary(last);
            AuditLog.AuditFailed(last.Iteration, remaining.Count);
            throw new AuditFailedException(
                $"Rework agent produced no changes after final audit iteration budget ({auditIteration}/{maxIterations}) with no convergence progress. " +
                $"{remaining.Count} blocking finding(s) ({last.NonBlockingFindings} non-blocking advisory finding(s) also recorded)" +
                (remaining.Count == 0 ? "." : $": {remaining.Summary}"));
        }

        CodeyBoxMeters.ReworkEmptyEvents.Add(1,
            new KeyValuePair<string, object?>("outcome", "parked"));
        _log.LogWarning(
            "Work item {Id} empty rework exhausted escalation attempts; parking through operator-input path " +
            "(agent {Agent}, iteration {Iter}/{MaxIterations}, converging={Converging}, attempts={Attempts})",
            item.Id,
            emptyEx.Agent.Value,
            reworkIterationNumber,
            maxIterations,
            converging,
            attempts);
        await ParkEmptyReworkForOperatorAsync(
            item,
            project,
            auditHistory,
            emptyEx.Agent,
            reworkIterationNumber,
            attempts,
            converging,
            ct);
        return true;
    }

    private static string BuildEmptyReworkEscalationPrompt(
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

    private async Task<bool> ParkAuditMaxIterationsForOperatorAsync(
        WorkItem item,
        Project project,
        IReadOnlyList<AuditProgressSnapshot> history,
        CancellationToken ct)
    {
        var message = _promptComposer.BuildAuditMaxIterationEscalationMessage(history);
        var details = BuildAuditMaxIterationEscalationDetails(item.Id, history);
        if (_delegationEscalation is not null
            && _delegationEscalation.IsAutoTriggerArmed(DelegationTriggers.AuditMaxIterations, item))
        {
            // Automatic escalation on non-convergence: the item leaves the
            // failed cycle for a delegation turn instead of parking. The
            // failure signal is NOT consumed — audit progress, the attempt
            // history, and the park message (preserved into LastError and the
            // escalation webhook) stay on the record, and the delegation
            // trigger meter counts the escalation by condition.
            var escalation = await _delegationEscalation.DelegateAsync(
                item,
                DelegationTriggers.AuditMaxIterations,
                note: null,
                markAutoEscalated: true,
                failureContext: message,
                ct);
            if (escalation.Delegated)
                return true;
            _log.LogWarning(
                "Automatic delegation escalation for work item {Id} refused ({Error}); parking for operator instead",
                item.Id, escalation.Error);
        }
        await ParkAuditForOperatorAsync(
            item,
            project,
            history,
            ct,
            stepName: "audit-max-iterations-escalate",
            message,
            details,
            auditLogReason: "audit max iterations with progress");
        return false;
    }

    private async Task ParkEmptyReworkForOperatorAsync(
        WorkItem item,
        Project project,
        IReadOnlyList<AuditProgressSnapshot> history,
        AgentKind agent,
        int reworkIterationNumber,
        int attempts,
        bool converging,
        CancellationToken ct)
    {
        var message = BuildEmptyReworkEscalationMessage(
            history,
            agent,
            reworkIterationNumber,
            attempts,
            converging);
        var details = BuildAuditMaxIterationEscalationDetails(item.Id, history);
        await ParkAuditForOperatorAsync(
            item,
            project,
            history,
            ct,
            stepName: "audit-empty-rework-escalate",
            message,
            details,
            auditLogReason: "empty audit rework");
    }

    /// <summary>
    /// Parks a delegation turn at <see cref="WorkItemState.NeedsOperatorInput"/>
    /// carrying the reason. Terminal for the delegation attempt: the item
    /// never returns to the cycle that already failed, and the phase cannot
    /// re-enter itself (the trigger flag is consumed by leaving Delegating;
    /// only a new explicit trigger re-arms it). Optionally counts the attempt
    /// (turns that ran). The caller records the first-class delegation event
    /// via <c>RecordDelegationEventAsync</c> before parking when there is an
    /// attempt to attribute; this method only advances attempt accounting and
    /// parks.
    /// </summary>
    private async Task ParkDelegationForOperatorAsync(
        WorkItem item,
        Project project,
        CancellationToken ct,
        string message,
        string auditLogReason,
        string outcome,
        bool countAttempt)
    {
        await RunBoundedPostAgentAsync(item.Id, "park-delegation-for-operator", ct, async transitionCt =>
        {
            var current = await _store.GetAsync(item.Id, transitionCt) ?? item;
            var parked = current.With(WorkItemState.NeedsOperatorInput, message) with
            {
                DelegationAttempts = current.DelegationAttempts + (countAttempt ? 1 : 0),
                // The turn is over: drop the operator note so it cannot leak
                // into a later brief, and record a non-advancing outcome so a
                // proven-unhelpful delegation never re-arms automatic
                // escalation. Paths that never ran a turn (no trigger, not
                // configured) carry no attempt and set no failure flag.
                DelegationNote = null,
                DelegationFailed = current.DelegationFailed
                    || (countAttempt && IsNonAdvancingDelegationOutcome(outcome)),
            };
            var updated = await _store.TryUpdateIfStateAsync(parked, current.State, transitionCt);
            if (!updated)
            {
                _log.LogInformation(
                    "Work item {Id} state changed concurrently; skipping park-delegation-for-operator ({Reason})",
                    item.Id,
                    auditLogReason);
                return;
            }

            _log.LogWarning(
                "Work item {Id} parked after delegation turn for operator review: {Reason}",
                item.Id, auditLogReason);
            AuditLog.WorkItemTransitioned(item.Id, $"NeedsOperatorInput ({auditLogReason})");
            CodeyBoxMeters.PipelineTransitions.Add(1,
                new KeyValuePair<string, object?>("to_state", WorkItemState.NeedsOperatorInput.ToString()));

            var usage = await TryGetUsageSummaryAsync(item.Id);
            await _webhooks.PublishAsync(new WebhookEvent
            {
                Event = "work_item.needs_operator_input",
                WorkItem = parked,
                Project = project,
                Details = new DelegationParkedDetails
                {
                    WorkItemId = item.Id.ToString(),
                    Attempt = parked.DelegationAttempts,
                    Outcome = outcome,
                    Reason = message,
                },
                Usage = usage?.Iteration,
                UsageTotal = usage?.Total,
            }, CancellationToken.None);
        });
    }

    /// <summary>
    /// Whether a delegation-turn outcome completed without advancing the item
    /// (no changes to audit, or the turn itself failed). Only counted turns
    /// feed this verdict; parks that never ran a turn are excluded by the
    /// caller via <c>countAttempt</c>.
    /// </summary>
    private static bool IsNonAdvancingDelegationOutcome(string outcome) =>
        string.Equals(outcome, DelegationOutcomes.NoChanges, StringComparison.Ordinal)
        || string.Equals(outcome, DelegationOutcomes.Failed, StringComparison.Ordinal);

    /// <summary>
    /// Appends the first-class delegation event (brief + agent/model +
    /// resulting branch diff). Best-effort: a store failure is logged and the
    /// pipeline continues, mirroring the failure-event log contract — the
    /// state transition being recorded must not break on the recording.
    /// </summary>
    private async Task RecordDelegationEventAsync(
        WorkItem item,
        string brief,
        AgentKind? agent,
        string? model,
        string outcome,
        string? reason,
        string repoId,
        string baseBranch,
        string workBranch,
        CancellationToken ct)
    {
        if (_delegationEvents is null)
        {
            _log.LogWarning(
                "Work item {Id}: delegation event store is not wired; skipping durable brief/diff record for attempt {Attempt}",
                item.Id, item.DelegationAttempts + 1);
            return;
        }
        if (agent is null)
        {
            // No runner ever dispatched (e.g. failure before the first
            // attempt): there is no agent/model to attribute, so there is
            // nothing honest to record. The park transition still carries the
            // outcome and the attempt accounting.
            _log.LogWarning(
                "Work item {Id}: no delegate agent observed for attempt {Attempt}; skipping delegation event record",
                item.Id, item.DelegationAttempts + 1);
            return;
        }

        try
        {
            // Never throws: returns empty strings when the diff cannot be
            // computed. The success path already verified HEAD advanced, so
            // an empty diff here means inspection failed, not "no changes".
            var (diffStat, fullDiff) = await _gitHost.GetDiffAsync(repoId, baseBranch, workBranch, ct);
            await _delegationEvents.RecordAsync(new DelegationEvent
            {
                Id = Guid.NewGuid().ToString("N"),
                WorkItemId = item.Id,
                Attempt = item.DelegationAttempts + 1,
                Brief = brief,
                Agent = agent.Value,
                Model = model,
                Outcome = outcome,
                Reason = reason,
                DiffStat = diffStat,
                ResultDiff = fullDiff,
                OccurredAt = _opts.TimeProvider.GetUtcNow(),
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex,
                "Work item {Id}: failed to record delegation event for attempt {Attempt}",
                item.Id, item.DelegationAttempts + 1);
        }
    }

    /// <summary>
    /// Records a failed delegation turn (brief + agent/model + resulting diff)
    /// then parks the item at <see cref="WorkItemState.NeedsOperatorInput"/>.
    /// Single home for the record-then-park sequence shared by the
    /// no-change, agent-failure, phase-timeout, and attempt-timeout handlers
    /// so attempt accounting and event recording cannot drift between them.
    /// </summary>
    private async Task FailDelegationTurnAsync(
        WorkItem item,
        Project project,
        string brief,
        AgentKind? agent,
        string? model,
        string outcome,
        string reason,
        string auditLogReason,
        string repoId,
        string baseBranch,
        string workBranch,
        CancellationToken ct)
    {
        await RecordDelegationEventAsync(
            item, brief, agent, model, outcome, reason, repoId, baseBranch, workBranch, ct);
        await ParkDelegationForOperatorAsync(
            item, project, ct, reason,
            auditLogReason: auditLogReason,
            outcome: outcome,
            countAttempt: true);
    }

    private async Task ParkAuditForOperatorAsync(
        WorkItem item,
        Project project,
        IReadOnlyList<AuditProgressSnapshot> history,
        CancellationToken ct,
        string stepName,
        string message,
        object details,
        string auditLogReason)
    {
        var last = history[^1];

        await RunBoundedPostAgentAsync(item.Id, stepName, ct, async transitionCt =>
        {
            var current = await _store.GetAsync(item.Id, transitionCt) ?? item;
            var parked = current.With(WorkItemState.NeedsOperatorInput, message);
            var updated = await _store.TryUpdateIfStateAsync(parked, current.State, transitionCt);
            if (!updated)
            {
                _log.LogInformation(
                    "Work item {Id} state changed concurrently; skipping {StepName} ({Reason})",
                    item.Id,
                    stepName,
                    auditLogReason);
                return;
            }

            _log.LogWarning(
                "Work item {Id} parked at iteration {Iteration}/{MaxIterations} for operator review: {Reason}",
                item.Id, last.Iteration, last.MaxIterations, auditLogReason);
            AuditLog.WorkItemTransitioned(
                item.Id,
                $"NeedsOperatorInput ({auditLogReason}; {_promptComposer.AuditVerdictLineage(last, _opts.TimeProvider.GetUtcNow())})");
            CodeyBoxMeters.PipelineTransitions.Add(1,
                new KeyValuePair<string, object?>("to_state", WorkItemState.NeedsOperatorInput.ToString()));

            var usage = await TryGetUsageSummaryAsync(item.Id);
            await _webhooks.PublishAsync(new WebhookEvent
            {
                Event = "work_item.needs_operator_input",
                WorkItem = parked,
                Project = project,
                Details = details,
                Usage = usage?.Iteration,
                UsageTotal = usage?.Total,
            }, CancellationToken.None);
        });
    }

    /// <summary>
    /// Runs the delegation phase: a single unconstrained repair turn for an
    /// item the normal work/audit/rework cycle already failed to converge.
    /// The turn runs in a sandbox with the repository exactly as the work
    /// phase does (work-profile sandbox target), on the existing work branch,
    /// with a prompt combining the composed convergence brief and a latitude
    /// instruction the work phase does not grant.
    /// <para>
    /// Returns the item (advanced to <see cref="WorkItemState.WorkComplete"/>)
    /// when the delegate committed changes, so the caller falls through to
    /// the audit loop — the result is verified by the same gates as any other
    /// change and is never merged on the delegate's assurance. Returns null
    /// when the item parked (<see cref="WorkItemState.NeedsOperatorInput"/>,
    /// quota, transient) or failed fast; the caller must stop the pipeline.
    /// The phase never transitions to Delegating itself (the trigger owns the
    /// entry) and is unreachable from its own failure path.
    /// </para>
    /// </summary>
    private async Task<WorkItem?> RunDelegationPhaseAsync(
        WorkItem item,
        Project project,
        string repoId,
        string baseBranch,
        string workBranch,
        CancellationToken ct,
        CancellationToken hostShutdownToken)
    {
        var current = await _store.GetAsync(item.Id, ct) ?? item;
        if (!current.DelegationRequested)
        {
            // No explicit new trigger. The pipeline never mints this flag, so
            // a Delegating entry without it is a stale or hand-written state —
            // not a second attempt. Park instead of running so the phase
            // cannot be entered twice on one trigger.
            await ParkDelegationForOperatorAsync(
                current,
                project,
                ct,
                "Delegation was entered without an explicit new trigger; refusing a second attempt on the same trigger. Retry from delegation to authorize another attempt.",
                auditLogReason: "delegation without trigger",
                outcome: DelegationOutcomes.Failed,
                countAttempt: false);
            return null;
        }
        if (_briefComposer is null)
        {
            // No composer, no brief — and running the delegate blind would
            // fake the phase's core input. Park honestly instead.
            await ParkDelegationForOperatorAsync(
                current,
                project,
                ct,
                "Delegation support is not configured on this host (no convergence-brief composer); cannot run the delegate turn.",
                auditLogReason: "delegation not configured",
                outcome: DelegationOutcomes.Failed,
                countAttempt: false);
            return null;
        }

        var brief = await _briefComposer.ComposeAsync(current.Id, ct);
        var delegationPrompt = _promptComposer.BuildDelegationPrompt(brief, current.Prompt);
        var delegationStart = _opts.TimeProvider.GetUtcNow();
        await PublishIterationStartedAsync(
            current, project, IterationPhase.Delegation, AuditProgressIterationNumbers.DelegationPhase, ct);

        using var delegationScope = BeginPhaseScope(current, "delegation");
        var observedAgent = current.Agent;
        string? observedModel = null;
        try
        {
            using (var delegationPhase = new PhaseCancellation("delegation", ct, _opts.TimeProvider))
            {
                delegationPhase.SetPhaseTimeout(ResolvePhaseAbsoluteTimeout(current.WorkTimeout));
                delegationPhase.HookHostShutdown(hostShutdownToken, _opts.ShutdownGrace);
                // In-iteration quota fallback mirrors the work phase: a quota
                // hit mid-turn swaps members and retries; quota exhaustion
                // still parks via the shared outer handlers with
                // QuotaRetryFrom "delegation" so the scheduler resumes this
                // same turn rather than minting a new attempt.
                var sandboxTarget = SandboxTargetResolver.ResolveProjectPhase(project, project.NetworkProfiles.Work);
                try
                {
                    _ = await InvokeAgentWithQuotaFallbackAsync(
                        current, project, "delegation", AuditProgressIterationNumbers.DelegationPhase,
                        async (runner, trialItem, attemptCt) =>
                        {
                            observedAgent = runner.Kind;
                            observedModel = trialItem.ModelId;
                            return await RunWithStuckProbeAsync(
                                trialItem, project, runner.Kind, "delegation", delegationPhase, ct,
                                phaseCt => RunAgentPhaseAsync(
                                    trialItem, runner, repoId, baseBranch, workBranch,
                                    delegationPrompt,
                                    isInitial: false,
                                    networkProfile: sandboxTarget.NetworkProfile,
                                    sandboxFlavor: sandboxTarget.Flavor,
                                    project: project,
                                    phaseCt,
                                    hostShutdownToken,
                                    // The audit loop runs immediately after
                                    // this turn, so a non-compiling tree is
                                    // re-detected there and folded into
                                    // findings — same contract as the
                                    // audit-loop rework resume path.
                                    buildFailurePolicy: RequiredBuildPolicy.DeferToAuditLoop,
                                    reworkNoDiffHandling: ReworkNoDiffHandling.AuditEmptyRework,
                                    suppressNoChangesBreaker: true,
                                    phaseLabelOverride: "delegation",
                                    promptPhaseOverride: AgentPromptPhase.Delegation),
                                workToken: attemptCt);
                        },
                        ct,
                        phaseCancellation: delegationPhase,
                        attemptTimeout: current.WorkTimeout);
                }
                catch (OperationCanceledException oce) when (oce is not PhaseCancellationException)
                {
                    throw delegationPhase.Wrap(oce);
                }
            }
        }
        catch (ReworkProducedNoChangesException empty)
        {
            // The delegate exited cleanly but committed nothing. The attempt
            // is complete and there is nothing to audit — park with the
            // reason instead of returning to the cycle that already failed.
            var noChangeReason =
                $"Delegate ({empty.Agent.Value}) produced no changes; nothing to audit. The item stays parked: retry from delegation to authorize another attempt, or triage manually.";
            await FailDelegationTurnAsync(
                current, project, brief, empty.Agent, observedModel, DelegationOutcomes.NoChanges,
                noChangeReason, "delegate produced no changes", repoId, baseBranch, workBranch, ct);
            return null;
        }
        catch (InvalidOperationException agentFailure) when (IsDelegateAgentFailure(agentFailure))
        {
            // Plain delegate failure (non-zero exit with no recognized quota /
            // auth / transient / infra signature). Same park contract as the
            // no-change path: the item leaves the failed cycle for good.
            var failureReason = RedactAndTruncateAgentDetail(agentFailure.Message);
            await FailDelegationTurnAsync(
                current, project, brief, observedAgent, observedModel, DelegationOutcomes.Failed,
                failureReason, "delegate turn failed", repoId, baseBranch, workBranch, ct);
            return null;
        }
        catch (PhaseCancellationException timeout)
            when (CancellationSources.IsPhaseTimeout(timeout.Source))
        {
            // Genuine delegation-turn timeout (not host shutdown or operator
            // cancel, which propagate untouched): the attempt is spent.
            var timeoutReason =
                $"Delegate turn exceeded its timeout (source={timeout.Source}); the attempt is spent. Retry from delegation to authorize another attempt.";
            await FailDelegationTurnAsync(
                current, project, brief, observedAgent, observedModel, DelegationOutcomes.Failed,
                timeoutReason, "delegate turn timed out", repoId, baseBranch, workBranch, ct);
            return null;
        }
        catch (AgentAttemptTimeoutException attemptTimeout)
        {
            // Per-attempt dispatch timeout: same spent-attempt contract.
            var timeoutReason =
                $"Delegate turn exceeded its per-attempt timeout: {RedactAndTruncateAgentDetail(attemptTimeout.Message)} Retry from delegation to authorize another attempt.";
            await FailDelegationTurnAsync(
                current, project, brief, observedAgent, observedModel, DelegationOutcomes.Failed,
                timeoutReason, "delegate attempt timed out", repoId, baseBranch, workBranch, ct);
            return null;
        }

        // The turn committed changes (RunAgentPhaseAsync verified HEAD
        // advanced). Record what the delegate was told and what it did, then
        // advance to WorkComplete so the audit loop verifies the result with
        // the same gates as any other change. Consuming the one-shot trigger
        // (With clears DelegationRequested off the Delegating state) and
        // counting the attempt happen atomically with the advance.
        await RecordDelegationEventAsync(
            current, brief, observedAgent, observedModel, DelegationOutcomes.Completed,
            reason: null, repoId, baseBranch, workBranch, ct);
        await PublishIterationCompletedAsync(
            current, project, IterationPhase.Delegation, AuditProgressIterationNumbers.DelegationPhase,
            repoId, workBranch, delegationStart, ct);
        WorkItem? advanced = null;
        await RunBoundedPostAgentAsync(
            current.Id, "transition-delegation-to-work-complete", ct, async transitionCt =>
            {
                var latest = await _store.GetAsync(current.Id, transitionCt) ?? current;
                var next = WorkItemRecoveryPolicy.ResetRecoveryAttemptsAfterRealProgress(
                    latest.With(WorkItemState.WorkComplete) with
                    {
                        DelegationAttempts = latest.DelegationAttempts + 1,
                        // The turn completed: the operator note served its
                        // purpose in this turn's brief and must not leak into
                        // a later one. A completed turn is not a failure, so
                        // the delegation-failure flag is untouched.
                        DelegationNote = null,
                    },
                    latest.State,
                    WorkItemState.WorkComplete);
                await _store.UpdateAsync(next, transitionCt);
                await EmitTransitionSideEffectsAsync(next, WorkItemState.WorkComplete, project, transitionCt);
                advanced = next;
            });
        return advanced;
    }

    /// <summary>
    /// The delegate prompt hands the agent failure-shaped detail text only;
    /// this matches a plain agent failure (non-zero exit with no recognized
    /// quota/auth/transient/infra signature) by its message shape so infra
    /// failures ("Failed to read HEAD …") keep propagating to the standard
    /// outer handlers instead of being repackaged as delegate failures.
    /// </summary>
    private static bool IsDelegateAgentFailure(InvalidOperationException ex) =>
        ex.Message.StartsWith("Agent ", StringComparison.Ordinal)
        && ex.Message.Contains(" reported failure", StringComparison.Ordinal);

    private static AuditProgressSnapshot BuildAuditProgressSnapshot(
        int iteration,
        int maxIterations,
        IReadOnlyList<AuditFinding> findings,
        IReadOnlyList<AuditFinding> blocking,
        int nonBlocking,
        string? workBranchTip,
        string status = AuditProgressStatuses.Complete,
        IReadOnlyList<string>? scheduledAuditors = null,
        IReadOnlyList<string>? completedAuditors = null,
        DateTimeOffset? recordedAt = null)
    {
        return new AuditProgressSnapshot(
            iteration,
            maxIterations,
            blocking.Count,
            nonBlocking,
            FingerprintFindings(blocking),
            blocking.Select(ToProgressFinding).ToList(),
            findings.Select(ToProgressFinding).ToList(),
            workBranchTip,
            status,
            scheduledAuditors,
            completedAuditors,
            recordedAt);
    }

    private static AuditProgressSnapshot ToAuditProgressSnapshot(AuditProgressRecord record)
        => new(
            record.Iteration,
            record.MaxIterations,
            record.BlockingFindings,
            record.NonBlockingFindings,
            record.BlockingFindingIds,
            record.BlockingFindingsDetails,
            record.Findings,
            record.WorkBranchTip,
            record.Status,
            record.ScheduledAuditors,
            record.CompletedAuditors,
            record.RecordedAt);

    private static bool HasAuditConvergenceProgress(IReadOnlyList<AuditProgressSnapshot> history)
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

    // Cold-tier extraction forwarder: implementation lives on PromptComposer.
    internal static IReadOnlyList<AuditProgressFinding> BlockingProgressFindingsForSummary(AuditProgressSnapshot progress) =>
        new PromptComposer().BlockingProgressFindingsForSummary(progress);

    private async Task<IReadOnlyList<AuditProgressSnapshot>> LoadPersistedAuditProgressHistoryAsync(
        WorkItem item,
        DateTimeOffset? currentWorkAttemptStartedAt,
        CancellationToken ct)
    {
        // Load prior audit history for the two resume states that re-enter the
        // audit loop with the work phase skipped: WorkComplete and AuditPassed.
        //   • WorkComplete: an interrupted mid-audit resume — the history is
        //     used to continue the loop (or, if the latest is a passing verdict,
        //     to purge and re-audit fresh).
        //   • AuditPassed: a resume/requeue of an item that previously reached a
        //     pass (WorkItemRecoveryPolicy maps AuditPassed/Merging back here).
        //     Its latest recorded verdict is a whole prior-run pass; loading it
        //     here is what lets RunAuditLoopAsync purge the stale prior-run rows
        //     before the fresh iteration-1 re-audit. Without this branch the
        //     purge never fires, stale iterations 2..N survive the same
        //     work-attempt partition, and EnsureCurrentRealAuditPassBeforeMergeAsync
        //     selects the stale highest-iteration record instead of this pickup's
        //     fresh pass — either wedging a cleanly re-audited item forever (on a
        //     stale "review agent failed to run" verdict) or shipping an
        //     unreviewed prior-run verdict.
        if (_auditProgress is null
            || item.State is not (WorkItemState.WorkComplete or WorkItemState.AuditPassed))
            return [];

        IReadOnlyList<AuditProgressRecord> records;
        try
        {
            records = await _auditProgress.GetAuditProgressAsync(item.Id, currentWorkAttemptStartedAt, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new AuditHistoryLoadFailedException(
                $"failed to load durable audit progress history for work item {item.Id}; retry cannot safely continue without prior audit trajectory",
                ex);
        }

        var snapshots = records
            .Select((Record, Index) => (Record, Index))
            .Where(r => r.Record.Iteration > 0)
            .GroupBy(r => r.Record.Iteration)
            .Select(g => g.OrderByDescending(r => r.Index).First().Record)
            .OrderBy(r => r.Iteration)
            .Select(ToAuditProgressSnapshot)
            .ToList();

        // An interrupted run (host restart, cancellation, shutdown drain) can
        // leave a trailing row that never reached a verdict and recorded no
        // findings. There is nothing to rework, so it is superseded here and
        // the next audit re-evaluates iteration 1 from the current work
        // branch. A trailing non-complete row WITH findings is different: it
        // is partial crash-recovery evidence from auditors that did finish,
        // and the resume path reworks those findings before continuing the
        // loop (see RunMissingAuditResumeReworkAsync). Days-old rows never
        // reach this path after a retry: retrying a parked item purges its
        // prior audit-progress partition first, so the next audit writes a
        // fresh row. Backstops for any non-complete verdict that does drive
        // a rework: the merge gate only accepts complete verdicts, and an
        // empty rework driven by a non-complete verdict never feeds the
        // no-changes breaker.
        var (kept, superseded) = DropSupersededAuditVerdicts(snapshots);
        if (superseded > 0)
            _log.LogInformation(
                "Superseded {Count} empty interrupted audit-progress row(s) for work item {Id}; re-auditing from the current work branch",
                superseded,
                item.Id);

        return kept;
    }

    /// <summary>
    /// Pure core of the interrupted-history reconciliation: drops a trailing
    /// snapshot that never reached a verdict and recorded no findings,
    /// preserving iteration order. A trailing non-complete snapshot WITH
    /// findings is kept as partial crash-recovery evidence for the resume
    /// rework path; complete snapshots are always kept. Returns the surviving
    /// snapshots plus the superseded count (0 or 1).
    /// </summary>
    internal static (IReadOnlyList<AuditProgressSnapshot> Kept, int Superseded) DropSupersededAuditVerdicts(
        IEnumerable<AuditProgressSnapshot> snapshots)
    {
        var kept = snapshots.ToList();
        if (kept is [.., { IsComplete: false, Findings.Count: 0 }])
        {
            kept.RemoveAt(kept.Count - 1);
            return (kept, 1);
        }
        return (kept, 0);
    }

    private async Task PersistAuditProgressAsync(
        WorkItem item,
        DateTimeOffset? currentWorkAttemptStartedAt,
        AuditProgressSnapshot progress,
        CancellationToken ct)
    {
        if (_auditProgress is null)
            return;

        try
        {
            // The snapshot's stamp is the verdict's recorded time; fall back to
            // the injected clock only for snapshots built without one so the
            // stored row and the in-memory history agree on the verdict's age.
            var recordedAt = progress.RecordedAt ?? _opts.TimeProvider.GetUtcNow();
            await _auditProgress.RecordAuditProgressAsync(
                item.Id,
                currentWorkAttemptStartedAt,
                new AuditProgressRecord(
                    progress.Iteration,
                    progress.MaxIterations,
                    progress.BlockingFindings,
                    progress.NonBlockingFindings,
                    progress.BlockingFindingIds,
                    progress.BlockingFindingsDetails,
                    progress.Findings,
                    progress.WorkBranchTip,
                    progress.Status,
                    progress.ScheduledAuditors,
                    progress.CompletedAuditors,
                    recordedAt),
                recordedAt,
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new AuditHistoryPersistenceFailedException(
                $"failed to persist durable audit progress for work item {item.Id}; retry cannot safely continue without prior audit trajectory",
                ex);
        }
    }

    /// <summary>
    /// Pure core of the merge-gate verdict selection: the latest COMPLETE
    /// record for the highest iteration. Rows left in_progress/incomplete by
    /// an interrupted run are superseded — never authoritative — so the gate
    /// ignores them instead of blocking the merge on a frozen partial verdict
    /// or, worse, accepting one.
    /// </summary>
    internal static AuditProgressRecord? SelectMergeGateVerdict(IReadOnlyList<AuditProgressRecord> records)
        => records
            .Select((Record, Index) => (Record, Index))
            .Where(r => r.Record.Iteration > 0 && AuditProgressStatuses.IsComplete(r.Record.Status))
            .OrderByDescending(r => r.Record.Iteration)
            .ThenByDescending(r => r.Index)
            .Select(r => r.Record)
            .FirstOrDefault();

}
