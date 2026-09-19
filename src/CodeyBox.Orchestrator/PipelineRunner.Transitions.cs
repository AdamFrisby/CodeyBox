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

// PipelineRunner.Transitions.cs — Transitions and cancellation: PhaseScope, Transition/TransitionFailed, quota/transient parking, cost recording, and questions/suggestions pickup.
public sealed partial class PipelineRunner
{
    /// <summary>
    /// Opens a per-phase trace span and records the phase wall-clock duration to
    /// <see cref="CodeyBoxMeters.PhaseDuration"/> on disposal. When no listener is
    /// registered no span is started; the histogram <c>Record</c> still runs on
    /// every phase exit but the SDK discards it cheaply (a tag-array build plus a
    /// no-op store), so the disabled path stays near-free rather than literally
    /// zero work.
    /// </summary>
    private static PhaseScope BeginPhaseScope(WorkItem item, string phase) => new(item, phase);

    private struct PhaseScope : IDisposable
    {
        private readonly Activity? _activity;
        private readonly long _startTs;
        private readonly string _phase;
        private bool _disposed;

        public PhaseScope(WorkItem item, string phase)
        {
            _phase = phase;
            _startTs = Stopwatch.GetTimestamp();
            _disposed = false;
            _activity = CodeyBoxActivities.Pipeline.StartActivity($"phase.{phase}", ActivityKind.Internal);
            if (_activity is not null)
            {
                _activity.SetTag("codeybox.work_item_id", item.Id.ToString());
                _activity.SetTag("codeybox.phase", phase);
                _activity.SetTag("codeybox.agent", (item.Agent?.Value) ?? "(default)");
            }
        }

        // Idempotent. The audit loop disposes its audit scope early — before the
        // rework scope opens — so phase.audit duration excludes nested rework;
        // the enclosing `using` then disposes again at iteration end. Recording
        // the histogram / stopping the span exactly once keeps both correct.
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            CodeyBoxMeters.PhaseDuration.Record(
                (long)Stopwatch.GetElapsedTime(_startTs).TotalMilliseconds,
                new KeyValuePair<string, object?>("phase", _phase));
            _activity?.Dispose();
        }
    }

    private async Task Transition(WorkItem item, WorkItemState state, CancellationToken ct, Project? project = null)
    {
        await RunBoundedPostAgentAsync(item.Id, $"transition-to-{state}", ct, async transitionCt =>
        {
            var current = await _store.GetAsync(item.Id, transitionCt) ?? item;
            var next = WorkItemRecoveryPolicy.ResetRecoveryAttemptsAfterRealProgress(
                current.With(state),
                current.State,
                state);
            await _store.UpdateAsync(next, transitionCt);
            await EmitTransitionSideEffectsAsync(next, state, project, transitionCt);
        });
    }

    private async Task TransitionNoActionRequiredAsync(
        WorkItem item,
        Project? project,
        NoActionRequiredException ex,
        CancellationToken ct)
    {
        await RunBoundedPostAgentAsync(item.Id, "transition-to-no-action-required", ct, async transitionCt =>
        {
            var current = await _store.GetAsync(item.Id, transitionCt) ?? item;
            var next = WorkItemRecoveryPolicy.ResetRecoveryAttemptsAfterRealProgress(
                current.With(WorkItemState.NoActionRequired, BuildNoActionRequiredDetail(ex)),
                current.State,
                WorkItemState.NoActionRequired);
            await _store.UpdateAsync(next, transitionCt);
            await EmitTransitionSideEffectsAsync(next, WorkItemState.NoActionRequired, project, transitionCt);
        });
        AuditLog.WorkItemNoActionRequired(item.Id, SanitizedAgentDetail.FromRaw(ex.Reason).Value);
    }

    /// <summary>
    /// Builds the operator-facing resolution text for a no-action-required
    /// terminal transition. Both halves are agent-controlled, so each is
    /// redacted and truncated at this sink before it reaches LastError,
    /// webhooks, API responses, and the audit log.
    /// </summary>
    private static string BuildNoActionRequiredDetail(NoActionRequiredException ex)
    {
        var reason = SanitizedAgentDetail.FromRaw(ex.Reason).Value;
        if (string.IsNullOrWhiteSpace(ex.Precondition))
            return $"no action required: {reason}";
        var precondition = SanitizedAgentDetail.FromRaw(ex.Precondition).Value;
        return $"no action required: {reason} (precondition checked: {precondition})";
    }

    private async Task EmitTransitionSideEffectsAsync(
        WorkItem item,
        WorkItemState state,
        Project? project,
        CancellationToken ct)
    {
        _log.LogInformation("Work item {Id} → {State}", item.Id, state);
        AuditLog.WorkItemTransitioned(item.Id, state.ToString());
        CodeyBoxMeters.PipelineTransitions.Add(1, new KeyValuePair<string, object?>("to_state", state.ToString()));
        if (WorkItemStates.IsTerminal(state))
        {
            // A credential that reached a real service must stop working
            // when the work stops: revoke the item's secret leases now that
            // the item is terminal and persisted. Best-effort — a
            // lease-infrastructure failure is loud but never fails the
            // transition; the sweep retries whatever stays outstanding.
            await RevokeItemSecretLeasesBestEffortAsync(item.Id, ct).ConfigureAwait(false);
        }
        if (project is null)
            return;

        var usage = await TryGetUsageSummaryAsync(item.Id);
        var revision = await BuildTerminalRevisionAsync(item, ct);
        await _webhooks.PublishAsync(new WebhookEvent
        {
            Event = StateToEventName(state),
            WorkItem = item,
            Project = project,
            Usage = usage?.Iteration,
            UsageTotal = usage?.Total,
            PromptRevision = revision?.PromptRevision,
            RevisionAtCompletion = revision?.RevisionAtCompletion,
            RevisionMatches = revision?.RevisionMatches,
        }, CancellationToken.None);

        if (state == WorkItemState.Done)
            await PropagateTestCasesToJobTrackBestEffortAsync(item, project, ct);
    }

    /// <summary>
    /// On the terminal <see cref="WorkItemState.Done"/> transition, propagates the
    /// item's CodeyBox test cases to JobTrack when the project has opted in.
    /// Strictly best-effort: gated by an injected exporter (null = disabled) and
    /// wrapped so any failure — including a bug in the exporter — is logged and
    /// swallowed rather than failing the already-completed item. Cancellation is
    /// swallowed too: the item is already Done and persisted.
    /// </summary>
    private async Task PropagateTestCasesToJobTrackBestEffortAsync(
        WorkItem item, Project project, CancellationToken ct)
    {
        if (_jobTrackExporter is null || !project.JobTrackExport.Enabled)
            return;

        try
        {
            var summary = await _jobTrackExporter.ExportForWorkItemAsync(item, project, ct);
            if (summary.Failed > 0)
                _log.LogWarning(
                    "JobTrack export for work item {WorkItemId} completed with {Failed} failed case(s); item unaffected.",
                    item.Id, summary.Failed);
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "JobTrack export for work item {WorkItemId} threw; item is already Done and is unaffected.",
                item.Id);
        }
    }

    private async Task ResetRecoveryAttemptsAfterRealProgressEventAsync(
        WorkItemId itemId,
        RecoveryProgressEvent progressEvent,
        string progressLabel,
        CancellationToken ct)
    {
        await RunBoundedPostAgentAsync(itemId, $"reset-recovery-attempts-{progressLabel}", ct, async transitionCt =>
        {
            var current = await _store.GetAsync(itemId, transitionCt);
            if (current is null || current.RecoveryAttempts == 0)
                return;

            var next = WorkItemRecoveryPolicy.ResetRecoveryAttemptsAfterRealProgressEvent(current, progressEvent);
            if (next.RecoveryAttempts == current.RecoveryAttempts
                && next.RecoveryAttemptSourceState == current.RecoveryAttemptSourceState)
            {
                return;
            }

            await _store.UpdateAsync(next, transitionCt);
        });
    }

    /// <summary>
    /// Wraps a post-agent step (state transition, branch push, commit import)
    /// in <see cref="WorkerProgressWatchdogOptions.PostAgentTransitionTimeout"/>
    /// so a hang in any of <c>store.UpdateAsync</c> / <c>webhooks.PublishAsync</c>
    /// / git host calls fails the item within bounded time instead of holding
    /// the worker-pool slot indefinitely.
    /// </summary>
    internal Task RunBoundedPostAgentAsync(
        WorkItemId itemId, string stepName, CancellationToken ct, Func<CancellationToken, Task> body)
        => PostAgentTransitionBound.RunAsync(_watchdogOptionsAccessor, itemId, stepName, ct, body);

    /// <summary>
    /// Adds revision-attribution fields to webhook payloads on terminal-state
    /// events. <c>revisionAtCompletion</c> is the revision recorded for the
    /// iteration with the largest iteration number; comparing it to
    /// <see cref="WorkItem.PromptRevision"/> lets JobTrack tell "agent finished
    /// against the latest prompt" from "agent finished an older revision; the
    /// latest prompt edit was not yet visible". Non-terminal transitions
    /// return null so the existing payload shape is unchanged.
    /// </summary>
    internal async Task<TerminalRevisionAttribution?> BuildTerminalRevisionAsync(WorkItem item, CancellationToken ct)
        => await _terminalRevisionBuilder.BuildTerminalRevisionAsync(item, ct);

    private async Task RecordMergeConflictFailureAttributionAsync(
        WorkItem failed,
        MergeConflictResolutionFailedException ex)
    {
        if (ex.Agent is not { } agent || !WorkItemFailureKinds.IsInfraShaped(ex.FailureKind))
            return;

        var phase = string.IsNullOrWhiteSpace(ex.Phase)
            ? "merge_conflict_resolution"
            : ex.Phase;
        var agentInstanceId = failed.Agent == agent ? failed.AgentInstanceId : null;
        var modelId = failed.Agent == agent ? failed.ModelId : null;
        var involvementId = await RecordInvolvementStartAsync(
            failed.Id,
            agent,
            agentInstanceId,
            modelId,
            phase,
            iteration: null);
        await FinalizeInvolvementAsync(
            involvementId,
            MergeConflictFailureInvolvementOutcome(ex.FailureKind));
    }

    private static string MergeConflictFailureInvolvementOutcome(string? failureKind)
    {
        if (string.Equals(failureKind, WorkItemFailureKinds.AuthRequired, StringComparison.OrdinalIgnoreCase))
            return AgentInvolvementOutcomes.FailureAuth;

        return AgentInvolvementOutcomes.FailureInfrastructure;
    }

    /// <summary>
    /// Best-effort cost summary lookup for webhook usage blocks. Returns null
    /// when the cost store is absent, no rows exist for the work item, or the
    /// read fails — usage is reported as absent in any of those cases.
    /// </summary>
    private Task<WorkItemUsageSummary?> TryGetUsageSummaryAsync(WorkItemId id) =>
        _costUsageRecorder.TryGetUsageSummaryAsync(id);

    private async Task TransitionFailed(
        WorkItem item,
        string error,
        CancellationToken ct,
        Project? project = null,
        string? failureKind = null,
        DateTimeOffset? quotaResetAt = null,
        string? cancellationSource = null,
        AgentKind? agent = null,
        WorkItemAuthFailureScope? authFailureScope = null,
        bool clearAgent = false,
        IReadOnlyCollection<WorkItemState>? expectedStates = null,
        DateTimeOffset? expectedUpdatedAt = null)
    {
        if (string.Equals(failureKind, "transient", StringComparison.OrdinalIgnoreCase))
        {
            await TransitionWaitingForTransientRetryAsync(item, error, project, phase: null, agent: item.Agent);
            return;
        }

        await RunBoundedPostAgentAsync(item.Id, "transition-failed", ct, async transitionCt =>
        {
            var current = await _store.GetAsync(item.Id, transitionCt) ?? item;
            DateTimeOffset? effectiveQuotaResetAt = quotaResetAt;
            if (failureKind == "quota")
            {
                var phase = PhaseForQuotaPark(current.State);
                effectiveQuotaResetAt = await ResolveQuotaResetAtForFailedTransitionAsync(
                    current,
                    project,
                    quotaResetAt,
                    phase,
                    transitionCt);
            }

            var transition = await _terminalTransitions.TransitionFailedAsync(
                current,
                error,
                new WorkItemTerminalFailureTransitionCommand
                {
                    FailureKind = failureKind,
                    AuthFailureScope = authFailureScope,
                    Agent = agent,
                    ClearAgent = clearAgent,
                    QuotaResetAt = effectiveQuotaResetAt,
                    CancellationSource = cancellationSource,
                    ExpectedStates = expectedStates,
                    ExpectedUpdatedAt = expectedUpdatedAt,
                },
            transitionCt);
            if (!transition.Updated || transition.FailedWorkItem is not { } next)
            {
                _log.LogInformation("Work item {Id} state changed concurrently; skipping Failed transition", item.Id);
                return;
            }

            if (failureKind == "quota" && _retryScheduler is not null)
            {
                await _retryScheduler.NotifyQuotaFailureAsync(next);
            }
            _log.LogWarning("Work item {Id} → Failed: {Error}", item.Id, error);
        });
    }

    /// <summary>
    /// Operator-cancel handler. Extracted so both the new
    /// <see cref="PhaseCancellationException"/> catch and the legacy raw-OCE
    /// catch route through identical state-write logic. Idempotent — if the
    /// item is already in a terminal-ish state, the cancel is skipped.
    /// </summary>
    private async Task HandleOperatorCancelAsync(WorkItem item, Project? project, string? phase = null)
    {
        var current = await _store.GetAsync(item.Id, CancellationToken.None) ?? item;
        if (current.State is WorkItemState.Done or WorkItemState.Failed
            or WorkItemState.MergeConflictResolutionFailed
            or WorkItemState.AbandonedAfterRecoveryAttempts)
            return;
        if (IsRecoveredResumeStateForCancelledPhase(current, phase))
        {
            _log.LogInformation(
                "Work item {Id} already advanced to recovery resume state {State} for cancelled phase '{Phase}'; skipping operator-cancel write",
                item.Id,
                current.State,
                phase);
            return;
        }
        // NOTE: do not compare current.State against the RunAsync entry
        // snapshot (item.State): the pipeline legitimately advances
        // Queued -> Working -> ... -> Auditing before a cancel arrives, so a
        // snapshot comparison would suppress every genuine mid-flight cancel
        // (e.g. CancelDuringAudit). The recovered-before-read race is already
        // covered by IsRecoveredResumeStateForCancelledPhase above when the
        // phase is known, and by the guarded TryUpdateIfStateAsync write below
        // otherwise.
        var cancelled = current.With(WorkItemState.Cancelled, "cancelled via API",
            WorkItemCancellationReason.OperatorRequested,
            cancellationSource: CancellationSources.Operator);
        var wrote = await _store.TryUpdateIfStateAsync(cancelled, current.State, CancellationToken.None);
        if (!wrote)
            return;

        AuditLog.WorkItemCancelled(item.Id);
        var effectiveProject = project ?? new Project
        {
            Id = item.ProjectId,
            DisplayName = item.ProjectId.Value,
            RepositoryUrl = string.Empty,
        };
        var cancelledRevision = await BuildTerminalRevisionAsync(cancelled, CancellationToken.None);
        await _webhooks.PublishAsync(new WebhookEvent
        {
            Event = "work_item.cancelled",
            WorkItem = cancelled,
            Project = effectiveProject,
            PromptRevision = cancelledRevision?.PromptRevision,
            RevisionAtCompletion = cancelledRevision?.RevisionAtCompletion,
            RevisionMatches = cancelledRevision?.RevisionMatches,
        }, CancellationToken.None);
    }

    private bool IsRecoveryCancellation(WorkItemId itemId) =>
        _cancellations?.GetRequestKind(itemId) == CancellationRequestKind.Recovery;

    private static bool IsRecoveredResumeStateForCancelledPhase(WorkItem current, string? phase)
    {
        if (string.IsNullOrWhiteSpace(phase))
            return false;

        var resumeState = DurableResumeStateForInterruptedPhase(phase);
        return resumeState is not null && current.State == resumeState.Value;
    }

    /// <summary>
    /// Handles a <see cref="PhaseCancellationException"/> whose source could
    /// not be attributed to operator cancel / host shutdown / configured
    /// timeout. The auto-retry path resets the item to a recoverable pre-phase
    /// state and re-enqueues, up to <see cref="OrchestratorOptions.MaxTransientCancelRetries"/>
    /// attempts; further failures transition to Failed with a pointed error.
    ///
    /// <para>
    /// The recovery state mirrors the dead-worker reaper / startup replay
    /// mapping (Working → Queued, Reworking/Auditing → WorkComplete,
    /// Merging → AuditPassed, UpstreamPushing → Merged) so the next pickup
    /// resumes at the right phase without re-running already-committed work.
    /// </para>
    /// </summary>
    private async Task HandleTransientCancellationAsync(
        WorkItem item,
        Project? project,
        PhaseCancellationException pex)
    {
        var current = await _store.GetAsync(item.Id, CancellationToken.None) ?? item;

        var max = _orchestratorOptions.MaxTransientCancelRetries;
        var attempts = current.TransientCancelRetries;

        if (max <= 0 || attempts >= max)
        {
            var detail = max <= 0
                ? $"phase '{pex.Phase}' cancelled by host (source={pex.Source}); auto-retry disabled (MaxTransientCancelRetries={max})"
                : $"phase '{pex.Phase}' cancelled by host (source={pex.Source}); exhausted {max} transient-cancel retries — operator must investigate (likely supervisor/cancellation-token leak in the orchestrator host)";
            _log.LogError(
                "Work item {Id} surfacing transient-cancel as Failed: phase={Phase} source={Source} attempts={Attempts}/{Max}",
                item.Id, pex.Phase, pex.Source, attempts, max);
            await TransitionFailed(item, detail, CancellationToken.None, project,
                failureKind: "cancelled",
                cancellationSource: pex.Source);
            return;
        }

        var resumeState = ResumeStateForTransientRetry(current, pex.Phase);
        // Use the record initializer (rather than With) so the auto-retry path
        // can preserve CancellationSource even for non-failure target states
        // (Queued, WorkComplete, AuditPassed, Merged) — the operator wants to
        // see what cancelled the prior phase even after the item resumes.
        var resumed = current.With(resumeState,
            error: $"transient cancellation in phase '{pex.Phase}' (source={pex.Source}); auto-retrying") with
        {
            CancellationSource = pex.Source,
            TransientCancelRetries = attempts + 1,
            // Reset RecoveryAttempts the same way WorkItemRetrier does so a
            // run of transient retries doesn't burn the host-crash recovery
            // budget on top of the transient-cancel budget.
            RecoveryAttempts = 0,
            RecoveryAttemptSourceState = null,
            ConsecutiveInfrastructureRecoveries = 0,
        };
        var updated = await _store.TryUpdateIfStateAsync(resumed, current.State, CancellationToken.None);
        if (!updated)
        {
            _log.LogInformation(
                "Work item {Id} state changed concurrently; skipping transient-cancel auto-retry transition",
                item.Id);
            return;
        }
        AuditLog.WorkItemTransientCancelRetried(item.Id, pex.Phase, pex.Source, attempts + 1, max);
        _log.LogWarning(
            "Work item {Id} auto-retrying after transient cancellation: phase={Phase} source={Source} attempt={Attempt}/{Max}; reset to {ResumeState}",
            item.Id, pex.Phase, pex.Source, attempts + 1, max, resumeState);

        // Kick the orchestrator's dispatch loop so the now-non-terminal item is
        // picked back up without waiting for an unrelated kick. If the queue
        // dependency wasn't wired (test bootstrap path), fall back to the
        // existing dispatch behaviour: the next periodic eligibility scan or
        // other workitem completion will surface it.
        if (_taskQueue is not null)
        {
            try { await _taskQueue.EnqueueAsync(item.Id, CancellationToken.None); }
            catch (Exception enqEx)
            {
                _log.LogWarning(enqEx,
                    "Failed to kick task queue for transient-cancel auto-retry of work item {Id}; will rely on the next pickup tick",
                    item.Id);
            }
        }
    }

    /// <summary>
    /// Maps the cancelled phase name onto the work item state the next pickup
    /// should resume from. Mirrors the dead-worker / startup-replay mapping
    /// so the pipeline resumes mid-flight rather than restarting from scratch.
    /// Internal (not private) so the per-phase table is unit-testable directly
    /// — driving the full pipeline through each phase to exercise this switch
    /// would dwarf the table it verifies.
    /// </summary>
    internal static WorkItemState ResumeStateForTransientRetry(WorkItem current, string phase)
    {
        _ = current;
        // Unknown phase name: re-queue from the start — safer than guessing.
        return DurableResumeStateForInterruptedPhase(phase) ?? WorkItemState.Queued;
    }

    private static WorkItemState? DurableResumeStateForInterruptedPhase(string phase) => phase switch
    {
        // Work / rework-resume / rework / audit all left the agent commits on
        // the work branch (or about to); resume at the matching phase entry.
        "planning" => WorkItemState.Queued,
        "work" => WorkItemState.Queued,
        "rework-resume" => WorkItemState.WorkComplete,
        "rework" => WorkItemState.WorkComplete,
        "mechanical-edit" => WorkItemState.WorkComplete,
        "audit" => WorkItemState.WorkComplete,
        "merge" => WorkItemState.AuditPassed,
        "upstream" => WorkItemState.Merged,
        _ => null,
    };

    private async Task<DateTimeOffset> ResolveQuotaResetAtForFailedTransitionAsync(
        WorkItem item,
        Project? project,
        DateTimeOffset? detectedResetAt,
        string phase,
        CancellationToken ct,
        QuotaFailureKind? quotaKind = null)
    {
        var resetAt = ClampQuotaReset(detectedResetAt, _pipelineTuning.Current.MaxParsedQuotaResetWindow);
        if (resetAt is not null)
            return resetAt.Value;

        if (_classRouter is not null)
        {
            try
            {
                var effectiveProject = project ?? await _projects.GetAsync(item.ProjectId, ct);
                resetAt = await _classRouter.ComputeEarliestExhaustedResetAsync(
                    item,
                    effectiveProject,
                    ct,
                    RequiredQuotaRetryCapabilityForPhase(phase));
                if (resetAt is not null)
                    return resetAt.Value;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogDebug(
                    ex,
                    "Failed to compute quota reset fallback for failed work item {Id}; using default pause",
                    item.Id);
            }
        }

        // A transient provider rate limit clears far sooner than a spent
        // account cap, so it resumes on its own (shorter, separately tunable)
        // backoff rather than the hard-quota pause.
        var fallbackPause = quotaKind == QuotaFailureKind.RateLimitExceeded
            ? _pipelineTuning.Current.DefaultRateLimitPause
            : _pipelineTuning.Current.DefaultQuotaFailurePause;
        return DateTimeOffset.UtcNow.Add(fallbackPause);
    }

    private async Task TransitionWaitingForQuotaResetAsync(
        WorkItem item,
        AgentClassExhaustedException ex,
        Project? project)
        => await TransitionWaitingForQuotaResetAsync(
            item,
            ex.Message,
            ex.Phase,
            ex.EarliestResetAt,
            project,
            iteration: null,
            quotaEvidenceTrusted: false);

    private async Task TransitionWaitingForAgentResumeAsync(
        WorkItem item,
        string reason,
        Project? project,
        AgentKind? pausedAgent = null,
        string? retryFrom = null)
        => await WorkItemAgentPauseParking.ParkAsync(
            _store,
            _webhooks,
            _log,
            item,
            reason,
            project,
            pausedAgent,
            CancellationToken.None,
            retryFrom);

    private static string RetryFromForAgentPausePhase(string? phase, WorkItemState currentState) =>
        phase switch
        {
            "planning" => "planning",
            "audit" => "audit",
            "rework" => "audit",
            "delegation" => "delegation",
            "post-act-recheck" => "audit",
            ConflictReworkPhaseKey => "conflict_rework",
            "merge" => "merge",
            "upstream" => "upstream",
            _ => AgentPauseResumeMapper.RetryFromForState(currentState),
        };

    private Task TransitionWaitingForTransientRetryAsync(
        WorkItem item,
        TerminalTransientNetworkError ex,
        Project? project)
        => TransitionWaitingForTransientRetryAsync(item, ex.Message, project, ex.Phase, ex.Agent);

    private async Task TransitionWaitingForTransientRetryAsync(
        WorkItem item,
        string error,
        Project? project,
        string? phase,
        AgentKind? agent)
    {
        var ct = CancellationToken.None;
        var safeError = RedactAndTruncateAgentDetail(error);
        if (IsOperatorCancellationRequested(item.Id))
        {
            _log.LogInformation(
                "Work item {Id} has an active operator cancellation; applying cancellation instead of scheduling transient retry",
                item.Id);
            await HandleOperatorCancelAsync(item, project, phase);
            return;
        }

        await RunBoundedPostAgentAsync(item.Id, "transition-waiting-for-transient-retry", ct, async transitionCt =>
        {
            var current = await _store.GetAsync(item.Id, transitionCt) ?? item;
            if (ShouldRejectTransientRetryParking(current))
            {
                _log.LogInformation(
                    "Work item {Id} is already in state {State}; skipping WaitingForTransientRetry transition",
                    item.Id,
                    current.State);
                return;
            }

            if (IsOperatorCancellationRequested(item.Id))
            {
                _log.LogInformation(
                    "Work item {Id} has an active operator cancellation; applying cancellation instead of scheduling transient retry",
                    item.Id);
                await HandleOperatorCancelAsync(current, project, phase);
                return;
            }

            var next = WorkItemRecoveryPolicy.ReleaseAgentTurnDispatchClaim(
                current.With(
                    WorkItemState.WaitingForTransientRetry,
                    safeError,
                    failureKind: "transient")) with
            {
                TransientRetryFrom = RetryFromForAgentTurnCheckpoint(current)
                    ?? RetryFromForTransientPhase(phase, current.State),
            };

            var updated = await _store.TryUpdateIfStateAsync(next, current.State, transitionCt);
            if (!updated)
            {
                _log.LogInformation(
                    "Work item {Id} state changed concurrently; skipping WaitingForTransientRetry transition",
                    item.Id);
                return;
            }

            var scheduled = next;
            if (_retryScheduler is not null)
            {
                var scheduling = await _retryScheduler.NotifyTransientFailureAsync(next, transitionCt);
                scheduled = scheduling.UpdatedItem;
                if (scheduling.Status == WorkItemAutoRetryScheduleStatus.Exhausted)
                {
                    _log.LogWarning(
                        "Work item {Id} exhausted transient retry budget during WaitingForTransientRetry scheduling: {Reason}",
                        item.Id,
                        scheduling.Reason);
                    return;
                }
            }

            AuditLog.WorkItemTransitioned(item.Id, WorkItemState.WaitingForTransientRetry.ToString());
            var effectiveProject = project ?? new Project
            {
                Id = item.ProjectId,
                DisplayName = item.ProjectId.Value,
                RepositoryUrl = string.Empty,
            };
            await _webhooks.PublishAsync(new WebhookEvent
            {
                Event = "work_item.waiting_for_transient_retry",
                WorkItem = scheduled,
                Project = effectiveProject,
                Details = new
                {
                    workItemId = item.Id.ToString(),
                    phase,
                    agent = agent?.Value,
                    reason = safeError,
                    nextRetryAt = scheduled.NextTransientRetryAt,
                    attempts = scheduled.TransientRetryAttempts,
                },
            }, CancellationToken.None);
        });
    }

    private bool IsOperatorCancellationRequested(WorkItemId itemId) =>
        _cancellations?.GetRequestKind(itemId) == CancellationRequestKind.Operator;

    private static bool ShouldRejectTransientRetryParking(WorkItem item) =>
        item.State == WorkItemState.NeedsOperatorInput
        || WorkItemDependencies.TerminalStates.Contains(item.State);

    private async Task TransitionWaitingForQuotaResetAsync(
        WorkItem item,
        string error,
        string phase,
        DateTimeOffset? quotaResetAt,
        Project? project,
        int? iteration,
        QuotaFailureKind? quotaKind = null,
        bool quotaEvidenceTrusted = false)
    {
        var ct = CancellationToken.None;
        // Sink guard: LastError is persisted and API-served, and agent output
        // is untrusted. Sanitize here as well so any present or future
        // TerminalQuotaError / AgentClassExhausted message that bypassed the
        // construction-time helper is still redacted and truncated before it
        // reaches the store, webhooks, or audit events. Idempotent for
        // already-sanitized messages.
        var safeError = SanitizedAgentDetail.FromRaw(error).Value;
        var current = await _store.GetAsync(item.Id, ct) ?? item;

        // A park that contradicts a fresh, known-healthy probe reading is
        // always wrong — unless the evidence is provider-owned. A
        // TerminalQuotaError raised from stderr/terminal-region evidence
        // carries that trust on the exception: the provider's own rejection
        // is fresher than any lagging probe snapshot. Parks resting only on
        // agent-quotable stdout evidence (or on router-cache state with no
        // fresh rejection) redirect to a transient retry instead.
        if (!quotaEvidenceTrusted
            && await IsQuotaContradictedByProbeForWorkItemAsync(current, project, phase, ct))
        {
            _log.LogWarning(
                "Work item {Id} was targeted for WaitingForQuotaReset ({Reason}), but probe reports healthy quota; redirecting to WaitingForTransientRetry",
                item.Id, error);
            await TransitionWaitingForTransientRetryAsync(current, error, project, phase, current.Agent);
            return;
        }

        var effectiveResetAt = await ResolveQuotaResetAtForFailedTransitionAsync(current, project, quotaResetAt, phase, ct, quotaKind);
        var agentTurnRetryFrom = RetryFromForAgentTurnCheckpoint(current);
        var next = WorkItemRecoveryPolicy.ReleaseAgentTurnDispatchClaim(
            current.With(
                WorkItemState.WaitingForQuotaReset,
                safeError,
                failureKind: "quota",
                quotaResetAt: effectiveResetAt)) with
        {
            NextQuotaRetryAt = effectiveResetAt,
            QuotaRetryFrom = agentTurnRetryFrom ?? RetryFromForQuotaPhase(phase),
            QuotaRetryPhase = agentTurnRetryFrom ?? NormalizeQuotaRetryPhase(phase),
        };

        var updated = await _store.TryUpdateIfStateAsync(next, current.State, ct);
        if (!updated)
        {
            _log.LogInformation(
                "Work item {Id} state changed concurrently; skipping WaitingForQuotaReset transition",
                item.Id);
            return;
        }

        var effectiveProject = project ?? new Project
        {
            Id = item.ProjectId,
            DisplayName = item.ProjectId.Value,
            RepositoryUrl = string.Empty,
        };
        await RecordDirectQuotaParkAsync(next, effectiveProject, effectiveResetAt, ct);

        if (_retryScheduler is not null)
            await _retryScheduler.NotifyQuotaFailureAsync(next);

        AuditLog.WorkItemTransitioned(item.Id, WorkItemState.WaitingForQuotaReset.ToString());
        await _webhooks.PublishAsync(new WebhookEvent
        {
            Event = "work_item.waiting_for_quota_reset",
            WorkItem = next,
            Project = effectiveProject,
            Details = new AgentFallbackDetails(
                WorkItemId: item.Id.ToString(),
                Phase: phase,
                Iteration: iteration,
                FromAgent: (item.Agent ?? effectiveProject.DefaultAgent).Value,
                FromModel: item.ModelId,
                ToAgent: null,
                ToModel: null,
                Reason: safeError),
        }, ct);
    }

    /// <summary>
    /// Resolves the quota probe serving <paramref name="member"/> by member key.
    /// A probe that does not handle the member is never returned; an empty
    /// resolution means no probe claims the member (legacy probe-less path), and
    /// a conflict (already logged at Error by the catalog) means the caller must
    /// fail closed for that member.
    /// </summary>
    private QuotaProbeResolution ResolveQuotaProbe(AgentMembership member)
    {
        if (_quotaProbes is null)
            return new QuotaProbeResolution(null, null);
        return AgentQuotaProbeCatalog.ResolveSubscriptionProbe(_quotaProbes, member, _log);
    }

    private async Task RecordDirectQuotaParkAsync(
        WorkItem item,
        Project? project,
        DateTimeOffset? resetAt,
        CancellationToken ct)
    {
        if (!IsDirectQuotaPark(item, project))
            return;

        var member = DirectAgentMembership.TryCreate(item, project);
        if (member is null)
            return;

        if (ResolveQuotaProbe(member).Probe is { } probe)
        {
            try
            {
                await probe.MarkExhaustedAsync(
                    member,
                    _pipelineTuning.Current.QuotaExhaustionFallbackTtl,
                    resetAt,
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogWarning(
                    ex,
                    "Direct quota park probe write-back failed for {Agent}/{Model}",
                    member.Agent.Value,
                    member.ModelId ?? "(default)");
            }
        }

        _quotaAvailabilityPublisher?.RecordQuotaUsability(
            member,
            isUsable: false,
            publishRecoverySignal: true,
            resetAt);
    }

    private bool IsDirectQuotaPark(WorkItem item, Project? project)
    {
        return DirectAgentMembership.IsDirectRoute(item, project);
    }

    /// <summary>
    /// Maps the per-phase quota-park label to the <c>from</c> phase string the
    /// retry scheduler/<see cref="WorkItemRetrier"/> understands. Each value
    /// chooses the lifecycle slot the item resumes at after the quota window
    /// resets — work/audit/merge/upstream map 1:1 onto Queued/WorkComplete/
    /// AuditPassed/Merged. <c>rework</c> deliberately maps to <c>audit</c>:
    /// resuming via WorkComplete preserves the in-flight WorkBranch (Queued
    /// clears it in <see cref="WorkItem.With(WorkItemState, string?, WorkItemCancellationReason?, string?, DateTimeOffset?, string?)"/>),
    /// so a mid-rework Claude five-hour reset doesn't discard the agent's prior
    /// commits and the audit findings the rework was responding to. Mirrors the
    /// transient-cancel mapper
    /// (<see cref="ResumeStateForTransientRetry"/>: <c>rework → WorkComplete</c>).
    /// Internal (not private) so the phase table is unit-testable directly.
    /// </summary>
    internal static string RetryFromForQuotaPhase(string phase) =>
        QuotaRetryPhasePolicy.RetryFromForPhase(phase);

    internal static string NormalizeQuotaRetryPhase(string phase) =>
        QuotaRetryPhasePolicy.NormalizePhase(phase);

    private static string? RequiredQuotaRetryCapabilityForPhase(string phase) =>
        QuotaRetryPhasePolicy.RequiredCapabilityForPhase(phase);

    internal static string? RetryFromForTransientPhase(string? phase, WorkItemState currentState) => phase switch
    {
        "planning" => "planning",
        "audit" => "audit",
        "rework" => "audit",
        "delegation" => "delegation",
        ConflictReworkPhaseKey => "conflict_rework",
        "post-act-recheck" => "merge",
        "merge" => "merge",
        "upstream" => "upstream",
        _ => ExplicitTransientRetryFromForState(currentState),
    };

    private static string? RetryFromForAgentTurnCheckpoint(WorkItem item)
    {
        if (item.AgentTurnResumeCheckpoint is not { } checkpoint
            || string.IsNullOrWhiteSpace(item.PreemptCheckpoint))
        {
            return null;
        }

        return checkpoint.Phase switch
        {
            AgentTurnResumePhase.Work => RetryFromPolicy.Work,
            AgentTurnResumePhase.Rework => RetryFromPolicy.Rework,
            _ => throw new ArgumentOutOfRangeException(
                nameof(checkpoint),
                checkpoint.Phase,
                "Unsupported durable agent-turn checkpoint phase."),
        };
    }

    private static string? ExplicitTransientRetryFromForState(WorkItemState currentState)
    {
        var retryFrom = AgentPauseResumeMapper.RetryFromForState(currentState);
        return string.Equals(retryFrom, "work", StringComparison.Ordinal)
            ? null
            : retryFrom;
    }

    /// <summary>
    /// Maps the work item's current state to the phase string used when parking
    /// a quota rejection as <see cref="WorkItemState.WaitingForQuotaReset"/>. The
    /// phase drives <see cref="RetryFromForQuotaPhase"/> so the scheduler resumes
    /// the item in the correct lifecycle slot (work / rework / audit / merge /
    /// upstream) after the quota window resets. Internal (not private) so the
    /// state-to-phase table is unit-testable directly.
    /// </summary>
    internal static string PhaseForQuotaPark(WorkItemState state) => state switch
    {
        WorkItemState.Planning => "planning",
        WorkItemState.PlanReview => "planning",
        WorkItemState.Auditing => "audit",
        WorkItemState.Reworking => "rework",
        WorkItemState.ReworkingForConflict => "rework",
        WorkItemState.AuditFailed => "rework",
        WorkItemState.Delegating => "delegation",
        WorkItemState.Merging => "merge",
        WorkItemState.UpstreamPushing => "upstream",
        _ => "work",
    };

    private static string StateToEventName(WorkItemState state) => state switch
    {
        WorkItemState.Planning => "work_item.planning",
        WorkItemState.PlanReview => "work_item.plan_review",
        WorkItemState.PlanApproved => "work_item.plan_approved",
        WorkItemState.Working => "work_item.working",
        WorkItemState.WorkComplete => "work_item.work_complete",
        WorkItemState.Auditing => "work_item.auditing",
        WorkItemState.AuditPassed => "work_item.audit_passed",
        WorkItemState.Reworking => "work_item.reworking",
        WorkItemState.ReworkingForConflict => "work_item.reworking_for_conflict",
        WorkItemState.Delegating => "work_item.delegating",
        WorkItemState.AuditFailed => "work_item.audit_failed",
        WorkItemState.Merging => "work_item.merging",
        WorkItemState.Merged => "work_item.merged",
        WorkItemState.UpstreamPushing => "work_item.upstream_pushing",
        WorkItemState.Done => "work_item.done",
        WorkItemState.Failed => "work_item.failed",
        WorkItemState.Cancelled => "work_item.cancelled",
        WorkItemState.NeedsOperatorInput => "work_item.needs_operator_input",
        WorkItemState.NoActionRequired => "work_item.no_action_required",
        WorkItemState.WaitingForQuotaReset => "work_item.waiting_for_quota_reset",
        WorkItemState.WaitingForTransientRetry => "work_item.waiting_for_transient_retry",
        _ => $"work_item.{state.ToString().ToLowerInvariant()}",
    };

    // ── Cost capture (delegated) ─────────────────────────────────────────────
    //
    // Owned by CostUsageRecorder; the forwarders below keep the existing
    // intra-pipeline and test call-sites unchanged.

    private Task TryRecordCompletionCostAsync(
        CheckAndActCompletionResult result,
        WorkItem item,
        string phase,
        int? iteration,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt) =>
        _costUsageRecorder.TryRecordCompletionCostAsync(
            result, item, phase, iteration, startedAt, endedAt);

    /// <summary>
    /// Best-effort cost capture: extracts token counts from agent output, calculates
    /// estimated USD, and persists a cost row. Any failure is swallowed with a warning
    /// so cost capture never aborts a pipeline phase.
    /// </summary>
    private Task TryRecordCostAsync(
        string? stdout,
        string? stderr,
        AgentKind agentKind,
        string? agentInstanceId,
        WorkItemId workItemId,
        string phase,
        int? iteration,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        string? dispatchModelId) =>
        _costUsageRecorder.TryRecordCostAsync(
            stdout, stderr, agentKind, agentInstanceId, workItemId, phase, iteration, startedAt, endedAt, dispatchModelId);

    private static AgentCostSnapshot NormalizeCostSnapshot(AgentCostSnapshot snapshot, string? dispatchModelId) =>
        CostUsageRecorder.NormalizeCostSnapshot(snapshot, dispatchModelId);

    internal static AgentCostSnapshot ClampCostSnapshot(AgentCostSnapshot snapshot, string? dispatchModelId = null) =>
        CostUsageRecorder.ClampCostSnapshot(snapshot, dispatchModelId);

    internal static string? ResolveCostRowModelId(string? extractedModelId, string? dispatchModelId) =>
        CostUsageRecorder.ResolveCostRowModelId(extractedModelId, dispatchModelId);

    internal static AgentUsageEvent BuildUsageEvent(
        AgentKind agentKind,
        string? dispatchModelId,
        AgentCostSnapshot snapshot,
        decimal usd,
        WorkItemId workItemId,
        DateTimeOffset endedAt,
        string? phase = null,
        DateTimeOffset? startedAt = null) =>
        CostUsageRecorder.BuildUsageEvent(agentKind, dispatchModelId, snapshot, usd, workItemId, endedAt, phase, startedAt);

    internal static AgentUsageEvent BuildUsageEvent(
        AgentKind agentKind,
        string? agentInstanceId,
        string? dispatchModelId,
        AgentCostSnapshot snapshot,
        decimal usd,
        WorkItemId workItemId,
        DateTimeOffset endedAt,
        string? phase = null,
        DateTimeOffset? startedAt = null) =>
        CostUsageRecorder.BuildUsageEvent(agentKind, agentInstanceId, dispatchModelId, snapshot, usd, workItemId, endedAt, phase, startedAt);

    // ── Question parsing + NeedsOperatorInput parking ───────────────────────

    /// <summary>
    /// Parses agent stdout for question blocks, persists new ones, and transitions
    /// the work item to NeedsOperatorInput if at least one new question was created.
    /// Returns true when the work item was parked; false otherwise.
    /// </summary>
    private Task<bool> TryParkForQuestionsAsync(
        WorkItem item, Project project, string agentStdout, CancellationToken ct) =>
        _questionsSuggestions.TryParkForQuestionsAsync(item, project, agentStdout, ct);

    // ── Suggestion pickup ────────────────────────────────────────────────────

    /// <summary>
    /// Tries to read <c>.codeybox/suggestions.json</c> from the sandbox working
    /// directory. Returns the raw content string when the file exists and is
    /// within the 256 KB size limit; null otherwise.
    /// </summary>
    private Task<string?> TryReadSuggestionsFileAsync(ISandbox sandbox, CancellationToken ct) =>
        _questionsSuggestions.TryReadSuggestionsFileAsync(sandbox, ct);

    /// <summary>
    /// Parses raw suggestions JSON, persists valid entries, and fires one
    /// <c>work_item.suggestion</c> webhook per suggestion.
    /// </summary>
    private Task PickUpSuggestionsAsync(
        WorkItem item, Project project, string rawJson, CancellationToken ct) =>
        _questionsSuggestions.PickUpSuggestionsAsync(item, project, rawJson, ct);

    // ── No-action-required report ────────────────────────────────────────────

    /// <summary>
    /// Tries to read <c>.codeybox/no-action-required.json</c> from the sandbox
    /// working directory. Returns the raw content string when the file exists
    /// and is within the 8 KB size limit; null otherwise.
    /// </summary>
    private Task<string?> TryReadNoActionRequiredFileAsync(ISandbox sandbox, CancellationToken ct) =>
        _questionsSuggestions.TryReadNoActionRequiredFileAsync(sandbox, ct);

    /// <summary>
    /// Removes <c>.codeybox/no-action-required.json</c> from the sandbox Git
    /// index so the protocol file is never committed to the work branch.
    /// </summary>
    private Task StripNoActionRequiredFileFromIndexAsync(ISandbox sandbox, CancellationToken ct) =>
        _questionsSuggestions.StripNoActionRequiredFileFromIndexAsync(sandbox, ct);

    private sealed class ActivityTrackingSandbox : ISandbox, ISandboxDecorator
    {
        private readonly ISandbox _inner;
        private readonly Action _touch;
        private int _activeExecs;

        public ActivityTrackingSandbox(ISandbox inner, Action touch)
        {
            _inner = inner;
            _touch = touch;
        }

        public ISandbox InnerSandbox => _inner;

        public string Id => _inner.Id;

        /// <summary>
        /// True while at least one <see cref="ExecAsync"/> call made through
        /// this wrapper is still in flight. The auditor idle guard reads this
        /// as process-activity evidence: a quiet run that still holds live
        /// sandbox work (e.g. a test suite emitting nothing until the final
        /// result) is progressing, not idle.
        /// </summary>
        public bool HasActiveExecs => Volatile.Read(ref _activeExecs) > 0;

        public SandboxAgentOutputTransportKind AgentOutputTransportKind => _inner.AgentOutputTransportKind;
        public SandboxBatchLaunchMode BatchLaunchMode => _inner.BatchLaunchMode;
        public SandboxResourceMetrics? ResourceMetrics => _inner.ResourceMetrics;

        public async Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _activeExecs);
            try
            {
                var originalStdout = exec.StdoutChunkCallback;
                var originalStderr = exec.StderrChunkCallback;
                var watchedExec = exec with
                {
                    StdoutChunkCallback = chunk =>
                    {
                        _touch();
                        originalStdout?.Invoke(chunk);
                    },
                    StderrChunkCallback = chunk =>
                    {
                        _touch();
                        originalStderr?.Invoke(chunk);
                    },
                };
                return await _inner.ExecAsync(watchedExec, ct).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _activeExecs);
            }
        }

        public Task KillActiveExecsAsync(CancellationToken ct = default)
            => _inner.KillActiveExecsAsync(ct);

        public Task<byte[]> GetScreenshotAsync(CancellationToken ct = default)
            => _inner.GetScreenshotAsync(ct);

        public Task SynthesizeInputAsync(IReadOnlyList<SandboxInputEvent> events, CancellationToken ct = default)
            => _inner.SynthesizeInputAsync(events, ct);

        public Task<SandboxAccessibilitySnapshot?> GetAccessibilityAtPointAsync(int x, int y, CancellationToken ct = default)
            => _inner.GetAccessibilityAtPointAsync(x, y, ct);

        public Task<string?> GetAccessibilityTreeJsonAsync(CancellationToken ct = default)
            => _inner.GetAccessibilityTreeJsonAsync(ct);

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
