using Serilog;
using Serilog.Context;

namespace CodeyBox.Core;

/// <summary>
/// Structured audit-tier event helpers. Every method emits a Serilog event
/// tagged <c>Audit=true</c> so the audit-only file sink can filter them.
/// Properties pushed via <see cref="WorkItemScope"/> / <see cref="ProjectScope"/>
/// are inherited by all events logged while those scopes are active.
///
/// All methods are non-throwing: Serilog's self-log swallows internal errors,
/// so a misconfigured or unreachable sink never aborts a pipeline phase.
/// </summary>
public static class AuditLog
{
    /// <summary>
    /// Pushes <c>WorkItemId</c> into the ambient log context for the lifetime
    /// of the returned scope. Dispose the scope to remove the property.
    /// </summary>
    public static IDisposable WorkItemScope(WorkItemId id) =>
        LogContext.PushProperty("WorkItemId", id.ToString());

    /// <summary>
    /// Pushes <c>ProjectId</c> into the ambient log context for the lifetime
    /// of the returned scope. Dispose the scope to remove the property.
    /// </summary>
    public static IDisposable ProjectScope(ProjectId id) =>
        LogContext.PushProperty("ProjectId", id.Value);

    // ── Work item lifecycle ──────────────────────────────────────────────────

    public static void WorkItemCreated(WorkItemId id, ProjectId projectId, string title) =>
        Audit("work_item.created")
            .Information("Work item {WorkItemId} created for project {ProjectId}: {Title}",
                id.ToString(), projectId.Value, title);

    public static void WorkItemTransitioned(WorkItemId id, string toState) =>
        Audit("work_item.transitioned")
            .Information("Work item {WorkItemId} → {State}", id.ToString(), toState);

    public static void WorkItemCancelled(WorkItemId id) =>
        Audit("work_item.cancelled")
            .Information("Work item {WorkItemId} cancelled", id.ToString());

    public static void WorkItemRetried(WorkItemId id, string from) =>
        Audit("work_item.retried")
            .Information("Work item {WorkItemId} retried from phase {From}", id.ToString(), from);

    /// <summary>
    /// Emitted by <c>POST /workitems/{id}/resume</c> when an operator resumes a
    /// previously operator-cancelled item, preserving the bare repo and the
    /// work-branch + agent commits already on it. Distinct from
    /// <see cref="WorkItemRetried"/> (which handles the terminal-failed retry
    /// paths and the parent-cascade uncancel path) so operators can isolate
    /// intentional resume actions from the broader retry-after-failure churn.
    ///
    /// Reason is forced through an empty-string sentinel because Serilog drops
    /// null properties, and the timeline reader at
    /// <c>src/CodeyBox.Api/AuditLogTimelineReader.cs</c> relies on the property
    /// being present so it can distinguish "reason omitted" from "log line
    /// schema changed". Both ends must stay in sync.
    /// </summary>
    public static void WorkItemResumed(WorkItemId id, string from, string? reason) =>
        Audit("work_item.resumed")
            .ForContext("Reason", reason ?? "")
            .Information(
                "Work item {WorkItemId} resumed from phase {From} (priorState=Cancelled)",
                id.ToString(),
                from);

    /// <summary>
    /// Emitted when the pipeline auto-retries a work item after a transient
    /// (unattributed) host-side cancellation. Distinct from
    /// <see cref="WorkItemRetried"/> (operator-driven retry) and the
    /// quota-router retry path so dashboards can isolate cancellation noise
    /// from quota churn or operator activity.
    /// </summary>
    public static void WorkItemTransientCancelRetried(
        WorkItemId id, string phase, string cancellationSource, int attempt, int maxAttempts) =>
        Audit("work_item.transient_cancel_retried")
            .Warning(
                "Work item {WorkItemId} auto-retried after transient cancellation: phase={Phase} source={CancellationSource} attempt={Attempt}/{MaxAttempts}",
                id.ToString(), phase, cancellationSource, attempt, maxAttempts);

    public static void WorkItemRecovered(WorkItemId id, string fromState, string toState, int attempt) =>
        Audit("work_item.recovered")
            .Information(
                "Recovering {WorkItemId} from non-terminal state {FromState} → {ToState} (recovery attempt {Attempt}, presumed lost on prior shutdown)",
                id.ToString(), fromState, toState, attempt);

    public static void WorkItemAbandonedAfterRecovery(WorkItemId id, int maxAttempts) =>
        Audit("work_item.abandoned_after_recovery")
            .Warning(
                "Work item {WorkItemId} abandoned after {MaxAttempts} recovery attempts; operator intervention required",
                id.ToString(), maxAttempts);

    public static void WorkItemFailed(WorkItemId id, string error) =>
        Audit("work_item.failed")
            .Warning("Work item {WorkItemId} failed: {Error}", id.ToString(), error);

    public static void WorkItemPickedUp(int workerId, WorkItemId id) =>
        Audit("work_item.picked_up")
            .Information("Worker {WorkerId} picked up work item {WorkItemId}", workerId, id.ToString());

    // ── Worker pool lifecycle ────────────────────────────────────────────────

    public static void WorkerPoolSpawnThrottled(long waitMs) =>
        Audit("worker_pool.spawn_throttled")
            .Information("Worker spawn throttled by spawn interval: waiting {WaitMs}ms", waitMs);

    public static void WorkerPoolWorkerStarted(int workerIndex, WorkItemId id) =>
        Audit("worker_pool.worker_started")
            .Information("Worker pool: worker {WorkerIndex} started for work item {WorkItemId}",
                workerIndex, id.ToString());

    public static void WorkerPoolWorkerFinished(int workerIndex, WorkItemId id) =>
        Audit("worker_pool.worker_finished")
            .Information("Worker pool: worker {WorkerIndex} finished for work item {WorkItemId}",
                workerIndex, id.ToString());

    public static void WorkItemDependenciesResolved(WorkItemId id) =>
        Audit("work_item.dependencies_resolved")
            .Information("Work item {WorkItemId} enqueued: all dependencies reached terminal state", id.ToString());

    public static void WorkItemDependentCancelled(WorkItemId id, WorkItemId parentId) =>
        Audit("work_item.dependent_cancelled")
            .Information("Work item {WorkItemId} cascade-cancelled because parent {ParentWorkItemId} was cancelled",
                id.ToString(), parentId.ToString());

    public static void WorkItemPatched(
        WorkItemId id,
        bool titleChanged,
        bool promptChanged,
        bool agentChanged,
        bool workTimeoutChanged = false,
        bool mergeTimeoutChanged = false,
        bool minModelScoreChanged = false,
        bool requiredCapabilitiesChanged = false,
        bool auditBudgetChanged = false) =>
        Audit("work_item.patched")
            .Information(
                "Work item {WorkItemId} patched: title={TitleChanged} prompt={PromptChanged} agent={AgentChanged} workTimeout={WorkTimeoutChanged} mergeTimeout={MergeTimeoutChanged} minModelScore={MinModelScoreChanged} requiredCapabilities={RequiredCapabilitiesChanged} auditBudget={AuditBudgetChanged}",
                id.ToString(), titleChanged, promptChanged, agentChanged,
                workTimeoutChanged, mergeTimeoutChanged, minModelScoreChanged, requiredCapabilitiesChanged, auditBudgetChanged);

    /// <summary>
    /// Distinct audit event for priority changes. Records the previous and new
    /// priority values explicitly so the audit trail captures the mutation —
    /// <see cref="WorkItemPatched"/>'s flags-only shape would otherwise erase it.
    /// </summary>
    public static void WorkItemPriorityChanged(WorkItemId id, int oldPriority, int newPriority) =>
        Audit("work_item.priority_changed")
            .Information("Work item {WorkItemId} priority changed: {OldPriority} → {NewPriority}",
                id.ToString(), oldPriority, newPriority);

    /// <summary>
    /// Distinct audit event for post-hoc dependency edits via PATCH /workitems/{id}.
    /// Records the previous and new dependency-id sets explicitly so the audit
    /// trail captures the mutation; <see cref="WorkItemPatched"/>'s flags-only
    /// shape would otherwise erase it.
    /// </summary>
    public static void WorkItemDependenciesChanged(
        WorkItemId id,
        IReadOnlyList<WorkItemId> oldDependsOn,
        IReadOnlyList<WorkItemId> newDependsOn) =>
        Audit("work_item.dependencies_changed")
            .Information("Work item {WorkItemId} dependencies changed: [{OldDependsOn}] → [{NewDependsOn}]",
                id.ToString(),
                string.Join(",", oldDependsOn.Select(d => d.ToString())),
                string.Join(",", newDependsOn.Select(d => d.ToString())));

    public static void WorkItemReordered(int count) =>
        Audit("work_item.reordered")
            .Information("Queue reordered: {Count} items repositioned", count);

    // ── Agent lifecycle ──────────────────────────────────────────────────────

    public static void AgentStarted(AgentKind agent, string sandboxName, string phase) =>
        Audit("agent.started")
            .Information("Agent {Agent} started in sandbox {Sandbox} for phase {Phase}",
                agent.Value, sandboxName, phase);

    public static void AgentFinished(
        AgentKind agent, string sandboxName, bool success, int? exitCode, TimeSpan duration,
        string? stdoutTail = null, string? stderrTail = null)
    {
        var log = Audit("agent.finished");
        if (stdoutTail is not null) log = log.ForContext("StdoutTail", TruncateAuditTail(stdoutTail));
        if (stderrTail is not null) log = log.ForContext("StderrTail", TruncateAuditTail(stderrTail));
        log.Information("Agent {Agent} finished in sandbox {Sandbox}: success={Success} exit={ExitCode} duration={DurationMs}ms",
            agent.Value, sandboxName, success, exitCode, (long)duration.TotalMilliseconds);
    }

    private static string TruncateAuditTail(string s) =>
        s.Length <= 2048 ? s : "…" + s[^2048..];

    private static string AuditSingleLine(string? s, int maxLength = 512)
    {
        if (string.IsNullOrEmpty(s))
            return "";

        var normalized = s.Replace('\r', ' ').Replace('\n', ' ');
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength] + "…";
    }

    public static void AgentStuckDetected(AgentKind agent, string phase, TimeSpan stuckDuration) =>
        Audit("agent.stuck_detected")
            .Warning("Agent {Agent} stuck in phase {Phase} for {StuckSeconds}s with no CPU or network activity",
                agent.Value, phase, (int)stuckDuration.TotalSeconds);

    public static void AgentKilledByStuckProbe(AgentKind agent, string phase) =>
        Audit("agent.killed_by_stuck_probe")
            .Warning("Agent {Agent} killed by stuck probe in phase {Phase}", agent.Value, phase);

    /// <summary>
    /// Per-attempt failure of the in-VM agentic conflict resolver. Carries the
    /// full stdout/stderr tail (truncated to <see cref="TruncateAuditTail"/>'s
    /// 2 KiB window) plus the runner kind, sandbox id, working directory, and
    /// attempt counter so operators can diagnose without trawling logs. Emitted
    /// at <c>Warning</c> because every emission represents an iteration that
    /// either burned a retry slot or ended a candidate.
    /// </summary>
    public static void AgenticConflictResolverAttemptFailed(
        WorkItemId workItemId,
        AgentKind agent,
        string sandboxId,
        string workingDirectory,
        int attempt,
        int maxAttempts,
        string reason,
        string? stdoutTail = null,
        string? stderrTail = null)
    {
        var log = Audit("agentic_conflict_resolver.attempt_failed");
        if (stdoutTail is not null) log = log.ForContext("StdoutTail", TruncateAuditTail(stdoutTail));
        if (stderrTail is not null) log = log.ForContext("StderrTail", TruncateAuditTail(stderrTail));
        log.Warning(
            "Agentic conflict resolver attempt {Attempt}/{Max} failed for work item {WorkItemId} (agent={Agent} sandbox={Sandbox} workdir={WorkDir}): {Reason}",
            attempt, maxAttempts, workItemId.ToString(), agent.Value, sandboxId, workingDirectory, reason);
    }

    // ── Sandbox lifecycle ────────────────────────────────────────────────────

    public static void SandboxCreated(string vmName, string? networkProfile) =>
        Audit("sandbox.created")
            .Information("Sandbox {VmName} created with network profile {NetworkProfile}",
                vmName, networkProfile);

    public static void SandboxProvisioningTransientRetry(
        WorkItemId workItemId,
        string operation,
        int attempt,
        string errorClass) =>
        Audit("sandbox.provisioning_transient_retry")
            .Information(
                "Sandbox provisioning transient failure for work item {WorkItemId}; operation={Operation}; retry {Attempt}; errorClass={ErrorClass}",
                workItemId.ToString(), operation, attempt, errorClass);

    public static void SandboxProvisioningDeferred(
        WorkItemId workItemId,
        string provider,
        string operation,
        string errorClass,
        string resumeState,
        TimeSpan recheckIn) =>
        Audit("sandbox.provisioning_deferred")
            .Warning(
                "Sandbox provisioning deferred for work item {WorkItemId}: provider={Provider} operation={Operation} errorClass={ErrorClass} resumeState={ResumeState} recheckIn={RecheckSeconds}s",
                workItemId.ToString(), provider, operation, errorClass, resumeState, (long)recheckIn.TotalSeconds);

    public static void SandboxAgentInfrastructureFailure(
        WorkItemId workItemId,
        AgentKind agent,
        string sandboxName,
        string phase,
        string summary,
        string? reason) =>
        Audit("sandbox.agent_infra_failure")
            .Warning(
                "Agent infrastructure failure for work item {WorkItemId}: agent={Agent} sandbox={Sandbox} phase={Phase} summary={Summary} reason={Reason}",
                workItemId.ToString(), agent.Value, sandboxName, phase, AuditSingleLine(summary), AuditSingleLine(reason));

    public static void SandboxDisposed(string vmName) =>
        Audit("sandbox.disposed")
            .Information("Sandbox {VmName} disposed", vmName);

    public static void SandboxLeakDetected(string name, double ageMinutes, long? diskMb, string? reason = null) =>
        Audit("sandbox.leak_detected")
            .Warning("Leaked sandbox detected: {SandboxName} age={AgeMinutes:F1}min disk={DiskMb}MB reason={Reason}",
                name, ageMinutes, diskMb, reason);

    public static void SandboxLeakDisposed(
        string name,
        double ageMinutes,
        long? diskMb,
        DateTimeOffset disposedAt,
        string? reason = null) =>
        Audit("sandbox.leak_disposed")
            .Information("Leaked sandbox disposed: {SandboxName} age={AgeMinutes:F1}min disk={DiskMb}MB reason={Reason} disposedAt={DisposedAt}",
                name, ageMinutes, diskMb, reason, disposedAt);

    public static void SandboxLeakDisposeFailed(string name, double ageMinutes, long? diskMb, string error, string? reason = null) =>
        Audit("sandbox.leak_dispose_failed")
            .Warning("Failed to dispose leaked sandbox {SandboxName} age={AgeMinutes:F1}min disk={DiskMb}MB reason={Reason}: {Error}",
                name, ageMinutes, diskMb, reason, error);

    public static void SandboxSuspendedOnShutdown(WorkItemId workItemId, string vmName) =>
        Audit("sandbox.suspended_on_shutdown")
            .Information("Suspended sandbox {VmName} for work item {WorkItemId} on graceful shutdown",
                vmName, workItemId.ToString());

    public static void SandboxStoppedOnShutdown(WorkItemId workItemId, string vmName) =>
        Audit("sandbox.stopped_on_shutdown")
            .Information("Stopped sandbox {VmName} for work item {WorkItemId} on graceful shutdown",
                vmName, workItemId.ToString());

    public static void SandboxDisposedOnShutdown(WorkItemId workItemId, string vmName) =>
        Audit("sandbox.disposed_on_shutdown")
            .Information("Disposed sandbox {VmName} for work item {WorkItemId} on graceful shutdown",
                vmName, workItemId.ToString());

    public static void SandboxStartupReconciled(string vmName, string action) =>
        Audit("sandbox.startup_reconciled")
            .Information("Startup reconciler recovered orphaned sandbox {VmName}: {Action}", vmName, action);

    public static void SandboxStartupReconcileFailed(string vmName, string error) =>
        Audit("sandbox.startup_reconcile_failed")
            .Warning(
                "Startup reconciler could not recover orphaned sandbox {VmName}; operator intervention required: {Error}",
                vmName, error);

    public static void SandboxResumedOnStartup(
        WorkItemId workItemId,
        string vmName,
        bool success,
        string? error = null,
        bool adopted = false,
        int? adoptionExitCode = null)
    {
        // Failure (multipass start non-zero, missing VM, etc.) surfaces at
        // Warning so it lights up operator dashboards alongside other resume
        // problems instead of disappearing into the Information stream.
        var log = Audit(success ? "sandbox.resumed_on_startup" : "sandbox.resume_failed_on_startup");
        if (!success)
        {
            log.Warning(
                "Resume of suspended sandbox {VmName} for work item {WorkItemId} failed: error={Error}",
                vmName, workItemId.ToString(), error);
            return;
        }
        log.Information(
            "Resume of suspended sandbox {VmName} for work item {WorkItemId}: success={Success} adopted={Adopted} adoptionExitCode={AdoptionExitCode}",
            vmName, workItemId.ToString(), success, adopted, adoptionExitCode);
    }

    // ── Upstream remote ──────────────────────────────────────────────────────

    public static void UpstreamPrOpened(int prNumber, string? prUrl, string workBranch, string baseBranch) =>
        Audit("upstream.pr_opened")
            .Information("Upstream PR #{PrNumber} opened: {PrUrl} ({WorkBranch} → {BaseBranch})",
                prNumber, prUrl, workBranch, baseBranch);

    public static void UpstreamPrMerged(int prNumber, string mergeSha) =>
        Audit("upstream.pr_merged")
            .Information("Upstream PR #{PrNumber} merged: {MergeSha}", prNumber, mergeSha);

    public static void UpstreamPush(string branch, string safeRemoteUrl) =>
        Audit("upstream.push")
            .Information("Upstream push: branch {Branch} to {RemoteUrl}", branch, safeRemoteUrl);

    public static void UpstreamApiCallFailed(string operation, int statusCode, string owner, string repo) =>
        Audit("upstream.api_call_failed")
            .Warning("Upstream API call failed: {Operation} returned HTTP {StatusCode} for {Owner}/{Repo}",
                operation, statusCode, owner, repo);

    public static void UpstreamPrStaleBaseDetected(
        ProjectId projectId, int prNumber, string headBranch, string baseBranch, string headSha) =>
        Audit("upstream.pr_stale_base")
            .Warning(
                "Stale-base PR detected for project {ProjectId}: #{PrNumber} {HeadBranch} → {BaseBranch} (head sha {HeadSha}) is open with merge conflicts; needs operator rebase",
                projectId.Value, prNumber, headBranch, baseBranch, headSha);

    // ── Authentication ───────────────────────────────────────────────────────

    /// <summary>
    /// Logs that a token was read from an environment variable. Logs only the
    /// env-var NAME — never the token value itself.
    /// </summary>
    public static void TokenRead(string envVar, ProjectId projectId) =>
        Audit("auth.token_read")
            .Information("Token read from env var {EnvVar} for project {ProjectId}",
                envVar, projectId.Value);

    // ── Auditor / audit loop ─────────────────────────────────────────────────

    public static void AuditProfileSelected(string profile, IReadOnlyList<string> auditorNames) =>
        Audit("audit.profile_selected")
            .ForContext("AuditorNames", auditorNames, destructureObjects: true)
            .Information("Audit profile {AuditProfile} selected with {AuditorCount} auditor(s)",
                profile, auditorNames.Count);

    public static void AuditorRun(string auditorName, string worstSeverity, TimeSpan duration, AgentKind agentKind) =>
        Audit("auditor.run")
            .Information("Auditor {AuditorName} completed: worstSeverity={WorstSeverity} duration={DurationMs}ms agentKind={AgentKind}",
                auditorName, worstSeverity, (long)duration.TotalMilliseconds, agentKind.Value);

    /// <summary>
    /// Emitted once per audit iteration when at least one LLM auditor actually
    /// ran with a different agent than the work agent. Tells operators this
    /// iteration used diversified (cross-model) signal.
    /// </summary>
    public static void CrossReviewActive(AgentKind workAgent, AgentKind auditAgent) =>
        Audit("audit.cross_review_active")
            .Information("Cross-review active: workAgent={WorkAgent} auditAgent={AuditAgent}",
                workAgent.Value, auditAgent.Value);

    /// <summary>
    /// Emitted when an in-flight agent invocation classified as
    /// <see cref="AgentFailureKind.QuotaExhausted"/> and the pipeline retried
    /// the same iteration against the next class member. Distinct from
    /// <see cref="QuotaAuditFallthrough"/>, which fires for the audit-agent
    /// gate at iteration *start*.
    /// </summary>
    public static void AgentQuotaFallback(
        WorkItemId workItemId,
        string phase,
        int? iteration,
        AgentKind fromAgent,
        string? fromModel,
        AgentKind toAgent,
        string? toModel,
        string reason) =>
        Audit("quota_router.agent_fallback")
            .Warning(
                "Mid-iteration quota fallback: workItem={WorkItemId} phase={Phase} iteration={Iteration} " +
                "from={FromAgent}/{FromModel} to={ToAgent}/{ToModel} reason={Reason}",
                workItemId.ToString(), phase, iteration,
                fromAgent.Value, fromModel ?? "(default)",
                toAgent.Value, toModel ?? "(default)",
                reason);

    /// <summary>
    /// Emitted when a single agent attempt exceeds its per-attempt timeout and
    /// the pipeline retries the same iteration against the next class member.
    /// Distinct from <see cref="AgentQuotaFallback"/> so operational logs do
    /// not report timeout-driven fallback as quota exhaustion.
    /// </summary>
    public static void AgentAttemptTimeoutFallback(
        WorkItemId workItemId,
        string phase,
        int? iteration,
        AgentKind fromAgent,
        string? fromModel,
        AgentKind toAgent,
        string? toModel,
        string reason) =>
        Audit("agent.attempt_timeout_fallback")
            .Warning(
                "Mid-iteration attempt timeout fallback: workItem={WorkItemId} phase={Phase} iteration={Iteration} " +
                "from={FromAgent}/{FromModel} to={ToAgent}/{ToModel} reason={Reason}",
                workItemId.ToString(), phase, iteration,
                fromAgent.Value, fromModel ?? "(default)",
                toAgent.Value, toModel ?? "(default)",
                reason);

    /// <summary>
    /// Emitted when CLI-native session resume was attempted up to the configured
    /// bound and the pipeline retries the same iteration against the next class
    /// member.
    /// </summary>
    public static void AgentResumeExhaustedFallback(
        WorkItemId workItemId,
        string phase,
        int? iteration,
        AgentKind fromAgent,
        string? fromModel,
        AgentKind toAgent,
        string? toModel,
        string reason) =>
        Audit("agent.resume_exhausted_fallback")
            .Warning(
                "Mid-iteration resume-exhausted fallback: workItem={WorkItemId} phase={Phase} iteration={Iteration} " +
                "from={FromAgent}/{FromModel} to={ToAgent}/{ToModel} reason={Reason}",
                workItemId.ToString(), phase, iteration,
                fromAgent.Value, fromModel ?? "(default)",
                toAgent.Value, toModel ?? "(default)",
                reason);

    /// <summary>
    /// Emitted when every eligible class member returned QuotaExhausted within
    /// a single pickup, and the pipeline parked the item in WaitingForQuotaReset
    /// rather than transitioning it to Failed.
    /// </summary>
    public static void AgentQuotaAllExhausted(
        WorkItemId workItemId,
        string classId,
        string phase,
        int memberCount) =>
        Audit("quota_router.all_exhausted")
            .Warning(
                "All members of class '{ClassId}' exhausted mid-iteration for {WorkItemId} (phase={Phase}, members={MemberCount}); " +
                "parked in WaitingForQuotaReset",
                classId, workItemId.ToString(), phase, memberCount);

    /// <summary>
    /// Emitted when the configured audit agent had insufficient quota and the
    /// pipeline fell through to the work agent. The correlation-breaking
    /// benefit of cross-review was lost for this auditor invocation.
    /// </summary>
    public static void QuotaAuditFallthrough(AgentKind exhaustedAgent, AgentKind fallbackAgent, string auditorName) =>
        Audit("quota_router.audit_fallthrough")
            .Warning("Audit agent '{ExhaustedAgent}' quota exhausted; fell through to '{FallbackAgent}' for auditor '{AuditorName}'",
                exhaustedAgent.Value, fallbackAgent.Value, auditorName);

    /// <summary>
    /// Emitted when the operator's preferred audit agent
    /// (<c>Project.Audit.AuditAgent</c> or
    /// <c>Project.Audit.PerAuditorAgent[auditorName]</c>) is NOT tagged with
    /// the <c>audit</c> capability in the routed agent class, so the audit
    /// router demoted the preference and picked a tagged member from the
    /// class instead. Surfaces a configuration mismatch the operator should
    /// fix — the routing system kept going, but the operator's named
    /// preference was overridden.
    /// </summary>
    public static void AuditAgentNotAuditCapable(AgentKind preferredAgent, string auditorName, string classId) =>
        Audit("quota_router.audit_agent_not_audit_capable")
            .Warning(
                "Preferred audit agent '{PreferredAgent}' for auditor '{AuditorName}' is not tagged with the 'audit' capability in class '{ClassId}'; routing to an audit-capable member instead",
                preferredAgent.Value, auditorName, classId);

    /// <summary>
    /// Emitted when every candidate agent (configured audit agent + class-chain
    /// members + work agent fallback) was quota-exhausted, so the LLM auditor
    /// is being skipped for this audit iteration. The work item continues with
    /// the remaining auditors rather than parking.
    /// </summary>
    public static void LlmAuditorSkippedQuota(WorkItemId workItemId, string auditorName, int candidateCount) =>
        Audit("audit.llm_auditor_skipped_quota")
            .Warning(
                "LLM auditor '{AuditorName}' skipped for {WorkItemId}: all {CandidateCount} candidate agent(s) quota-exhausted",
                auditorName, workItemId.ToString(), candidateCount);

    /// <summary>
    /// Emitted when the pickup-time rebase resolver routed past a candidate
    /// whose non-cap pre-dispatch gate rejected it. <paramref name="rejectedAgent"/>
    /// is the resolver's primary candidate: the configured
    /// <c>Project.Audit.AuditAgent</c> when set and registered, otherwise the
    /// work-phase runner. <paramref name="chosenAgent"/> is the class-chain
    /// member that took over. <paramref name="reason"/>
    /// carries the actual gate reason (for example
    /// <c>quota exhausted (6.0%)</c>) so operators are not misled into reading
    /// a quota steer as a credential problem. Cap-driven reroutes use
    /// <see cref="RebaseResolverAgentCapReroute"/> instead.
    /// </summary>
    public static void RebaseResolverAgentRerouted(
        AgentKind rejectedAgent, AgentKind chosenAgent, string reason) =>
        Audit("rebase_resolver.rerouted")
            .Information(
                "Pickup-time rebase resolver rerouted from '{RejectedAgent}' to class member '{ChosenAgent}' ({Reason})",
                rejectedAgent.Value, chosenAgent.Value, reason);

    /// <summary>
    /// Emitted when every candidate (configured primary + class chain) failed
    /// the resolver's pre-dispatch gates, so the pickup-time rebase resolver
    /// could not run at all. The work item is failed with
    /// <c>failureKind=agent_unavailable</c>; distinct from resolver failures
    /// where an agent ran but produced an unmergeable answer.
    /// <paramref name="candidateReasons"/> carries the per-agent gate reasons
    /// so operators can tell a credential gap from a quota steer.
    /// </summary>
    public static void RebaseResolverAgentUnavailable(
        WorkItemId workItemId, string candidateReasons) =>
        Audit("rebase_resolver.agent_unavailable")
            .Warning(
                "Pickup-time rebase resolver could not run for {WorkItemId}: no candidate agent passed the resolver gates ({CandidateReasons})",
                workItemId.ToString(), candidateReasons);

    /// <summary>
    /// Emitted when the pickup-time rebase resolver routed past an agent
    /// whose creds are viable but whose per-agent concurrency cap is at
    /// ceiling, picking a class member that is below its own cap instead.
    /// Distinct from <c>rebase_resolver.rerouted</c> (which fires only when
    /// the primary's creds are missing): here the primary could have run,
    /// but the operator-configured cap signals that adding a second
    /// in-flight call against this agent's account would compete with
    /// already-running work and risk a 429.
    /// </summary>
    public static void RebaseResolverAgentCapReroute(
        AgentKind rejectedAgent, AgentKind chosenAgent, int rejectedRunning, int rejectedCap) =>
        Audit("rebase_resolver.cap_rerouted")
            .Information(
                "Pickup-time rebase resolver rerouted from '{RejectedAgent}' (running={Running} cap={Cap}) to class member '{ChosenAgent}' — primary at per-agent cap",
                rejectedAgent.Value, rejectedRunning, rejectedCap, chosenAgent.Value);

    /// <summary>
    /// Emitted when every candidate the pickup-time rebase resolver
    /// considered was at its per-agent concurrency cap, so the resolver
    /// fell back to the highest-ranked viable candidate (typically the
    /// primary itself) and ran the call despite the cap. This is the
    /// "reserve pool" escape hatch: better to attempt the call and possibly
    /// 429 than to fail the work item outright when every alternative is
    /// equally saturated. Distinct from <c>rebase_resolver.cap_rerouted</c>
    /// (which fires when a non-saturated alternative exists).
    /// </summary>
    public static void RebaseResolverAllAtCap(
        AgentKind chosenAgent, int chosenRunning, int chosenCap) =>
        Audit("rebase_resolver.all_at_cap")
            .Warning(
                "Pickup-time rebase resolver: every viable agent at per-agent cap; running on '{ChosenAgent}' (running={Running} cap={Cap}) anyway",
                chosenAgent.Value, chosenRunning, chosenCap);

    /// <summary>
    /// Emitted when the pickup-time rebase resolver has finalised which agent
    /// it will actually invoke for conflict resolution (after honouring
    /// <c>Project.Audit.AuditAgent</c>, the credential/quota/cap gates, and any
    /// class-chain reroute). Gives operators a single diagnostic line for "who
    /// ran the resolver" without correlating the reroute/cap/all-at-cap events.
    /// </summary>
    public static void RebaseResolverAgentSelected(
        WorkItemId workItemId, AgentKind chosenAgent) =>
        Audit("rebase_resolver.agent_selected")
            .Information(
                "Pickup-time rebase resolver selected agent '{ChosenAgent}' for {WorkItemId}",
                chosenAgent.Value, workItemId.ToString());

    public static void AuditIterationComplete(int iteration, int maxIterations, int blockingCount, int nonBlockingCount) =>
        Audit("audit.iteration_complete")
            .Information("Audit iteration {Iteration}/{MaxIterations}: blocking={BlockingCount} non-blocking={NonBlockingCount}",
                iteration, maxIterations, blockingCount, nonBlockingCount);

    public static void AuditPassed(int iteration) =>
        Audit("audit.passed")
            .Information("Audit passed on iteration {Iteration}", iteration);

    public static void AuditFailed(int iteration, int blockingCount) =>
        Audit("audit.failed")
            .Warning("Audit failed after {Iteration} iterations: {BlockingCount} blocking findings",
                iteration, blockingCount);

    // ── Webhook delivery ─────────────────────────────────────────────────────

    public static void WebhookDelivered(string endpoint, string eventName, int statusCode, int attempt) =>
        Audit("webhook.delivered")
            .Information("Webhook delivered: endpoint={Endpoint} event={WebhookEvent} status={StatusCode} attempt={Attempt}",
                endpoint, eventName, statusCode, attempt);

    public static void WebhookDeliveryFailed(string endpoint, string eventName, string lastFailure, int attempts) =>
        Audit("webhook.delivery_failed")
            .Warning("Webhook delivery failed: endpoint={Endpoint} event={WebhookEvent} after {Attempts} attempts: {LastFailure}",
                endpoint, eventName, attempts, lastFailure);

    // ── Credential smoke tests ───────────────────────────────────────────────

    public static void AgentSmokeSucceeded(AgentKind agent, TimeSpan duration) =>
        Audit("agent.smoke_succeeded")
            .Information("Agent {Agent} credential smoke test passed in {DurationMs}ms",
                agent.Value, (long)duration.TotalMilliseconds);

    public static void AgentSmokeFailed(AgentKind agent, string? reason, TimeSpan duration) =>
        AgentSmokeFailed(agent, reason, duration, SmokeFailureCategory.Unknown);

    public static void AgentSmokeFailed(
        AgentKind agent, string? reason, TimeSpan duration, SmokeFailureCategory category)
    {
        // Persistent failures must be loud at the WRN level even when the
        // probe has already been benched for hours, because the only signal an
        // operator gets is the log line — silent retry-on-transient is the
        // bug this classification exists to fix. Transient/Unknown stay at WRN
        // (existing contract) but carry the category for downstream filters.
        if (category == SmokeFailureCategory.Persistent)
        {
            Audit("agent.smoke_failed")
                .Warning(
                    "Agent {Agent} credential smoke test failed PERSISTENTLY in {DurationMs}ms " +
                    "(operator action required — re-authorize {Agent}): {Reason}",
                    agent.Value, (long)duration.TotalMilliseconds, agent.Value, reason);
        }
        else
        {
            Audit("agent.smoke_failed")
                .Warning("Agent {Agent} credential smoke test failed in {DurationMs}ms ({Category}): {Reason}",
                    agent.Value, (long)duration.TotalMilliseconds, category, reason);
        }
    }

    /// <summary>
    /// Emitted when a Claude CLI invocation surfaced a 401 Unauthorized from
    /// Anthropic. Logged separately from quota/rate-limit events so operators
    /// can tell shared-OAuth-refresh races (the dominant cause) and access-token
    /// expiry apart from genuine token revocation — see
    /// <c>ClaudeQuotaFailureDetector</c> for why these are not classified as
    /// <c>QuotaFailureKind.Unauthorized</c>.
    /// </summary>
    public static void ClaudeUnauthorizedObserved(string phase, string? sandboxName) =>
        Audit("agent.claude_unauthorized")
            .Warning(
                "Claude returned 401 Unauthorized during phase {Phase} in sandbox {SandboxName}; " +
                "treating as transient (not a quota event). Most commonly caused by an expired access " +
                "token; persistent 401s indicate a revoked or misconfigured credential.",
                phase, sandboxName ?? "(unknown)");

    /// <summary>
    /// Emitted once per active Claude-running sandbox after the host's
    /// <c>~/.claude/.credentials.json</c> rotates and
    /// <c>ClaudeTokenRotationPusher</c> writes the new sanitised bundle into
    /// the VM. Together with the prior <c>credential file mtime rotated</c>
    /// host-side log entry, this lets operators correlate a host rotation with
    /// the per-VM in-flight refresh, and explain the absence of subsequent
    /// <c>agent.claude_unauthorized</c> events on long-running iterations.
    /// </summary>
    public static void ClaudeTokenPushedToVm(string sandboxName) =>
        Audit("agent.claude_token_pushed_to_vm")
            .Information(
                "Rotated Claude access token pushed into sandbox {SandboxName}",
                sandboxName);

    /// <summary>
    /// Emitted when <c>ClaudeTokenRotationPusher</c> tried to write the
    /// rotated bundle into a VM and the exec failed (non-zero exit or the
    /// exec threw). The active iteration in that VM is therefore likely to
    /// surface as <c>agent.claude_unauthorized</c> on its next Anthropic
    /// call; pairing the two events isolates "push failed → 401" from
    /// "genuinely revoked credential → 401".
    /// </summary>
    public static void ClaudeTokenPushFailed(string sandboxName, string reason) =>
        Audit("agent.claude_token_push_failed")
            .Warning(
                "Failed to push rotated Claude access token into sandbox {SandboxName}: {Reason}",
                sandboxName, reason);

    // ── Queue control ────────────────────────────────────────────────────────

    public static void QueuePaused(string reason) =>
        Audit("queue.paused")
            .Information("Queue paused: {Reason}", reason);

    public static void QueueResumed() =>
        Audit("queue.resumed")
            .Information("Queue resumed");

    public static void QueueStartedWhilePaused() =>
        Audit("queue.started_while_paused")
            .Warning("Orchestrator started with queue in Paused state; no new work items will be picked up until the queue is resumed");

    // ── Per-agent pause control ─────────────────────────────────────────────

    public static void AgentPaused(
        AgentKind agent,
        string reason,
        string pausedBy,
        DateTimeOffset? expiresAt) =>
        Audit("agent.paused")
            .Information(
                "Agent {Agent} paused by {PausedBy}: {Reason} expiresAt={ExpiresAt}",
                agent.Value, pausedBy, reason, expiresAt);

    public static void AgentResumed(
        AgentKind agent,
        string resumedBy,
        string? reason = null) =>
        Audit("agent.resumed")
            .Information(
                "Agent {Agent} resumed by {ResumedBy}: {Reason}",
                agent.Value, resumedBy, reason ?? "");

    public static void AgentPauseExpired(AgentKind agent, string? reason) =>
        Audit("agent.pause_expired")
            .Information(
                "Agent {Agent} pause expired: {Reason}",
                agent.Value, reason ?? "");

    public static void AgentStartedWhilePaused(AgentKind agent, string? reason) =>
        Audit("agent.started_while_paused")
            .Warning(
                "Orchestrator started with agent {Agent} paused; no new work will dispatch to it until resumed: {Reason}",
                agent.Value, reason ?? "");

    public static void AgentPauseDispatchDeferred(
        WorkItemId id,
        string reason,
        string retryFrom) =>
        Audit("agent.pause_dispatch_deferred")
            .Information(
                "Work item {WorkItemId} waiting for paused agent resume from={RetryFrom}: {Reason}",
                id.ToString(), retryFrom, reason);

    public static void AgentPauseWaitingItemResumed(
        WorkItemId id,
        string source,
        string retryFrom) =>
        Audit("agent.pause_waiting_item_resumed")
            .Information(
                "Work item {WorkItemId} re-enqueued after agent pause change: source={Source} from={RetryFrom}",
                id.ToString(), source, retryFrom);

    // ── Suggestions ─────────────────────────────────────────────────────────

    public static void SuggestionDismissed(string suggestionId, string? reason) =>
        Audit("suggestion.dismissed")
            .Information("Suggestion {SuggestionId} dismissed: {Reason}", suggestionId, reason?.ReplaceLineEndings(" "));

    public static void SuggestionCreated(string suggestionId, string sourceWorkItemId, string projectId) =>
        Audit("suggestion.created")
            .Information("Suggestion {SuggestionId} created from work item {SourceWorkItemId} in project {ProjectId}",
                suggestionId, sourceWorkItemId, projectId);

    public static void SuggestionPromoted(string suggestionId, string newWorkItemId) =>
        Audit("suggestion.promoted")
            .Information("Suggestion {SuggestionId} promoted to work item {WorkItemId}",
                suggestionId, newWorkItemId);

    public static void SuggestionReverted(string suggestionId) =>
        Audit("suggestion.reverted")
            .Warning("Suggestion {SuggestionId} reverted to open after failed promotion", suggestionId);

    public static void SuggestionRevertFailed(string suggestionId, Exception exception) =>
        Audit("suggestion.revert_failed")
            .Warning(exception,
                "Suggestion {SuggestionId} could not be reverted to open after promotion failure; stuck in 'accepted' with no linked work item",
                suggestionId);

    // ── Changelog automation ─────────────────────────────────────────────────

    public static void ChangelogReleaseRequested(string projectId, string fromTag, string toTag, int prCount) =>
        Audit("changelog.release_requested")
            .Information("Changelog generation requested for project {ProjectId}: {FromTag}→{ToTag} ({PrCount} PRs)",
                projectId, fromTag, toTag, prCount);

    public static void ChangelogGenerated(string projectId, string toTag, string category, int prCount) =>
        Audit("changelog.generated")
            .Information("Changelog generated for project {ProjectId} tag {ToTag}: {Category} ({PrCount} PRs)",
                projectId, toTag, category, prCount);

    public static void ChangelogWebhookReceived(string owner, string repo, string tagName) =>
        Audit("changelog.webhook_received")
            .Information("GitHub release webhook received for {Owner}/{Repo} tag {TagName}", owner, repo, tagName);

    public static void ChangelogWebhookRejected(string reason) =>
        Audit("changelog.webhook_rejected")
            .Warning("GitHub release webhook rejected: {Reason}", reason);

    public static void ChangelogWorkItemCreated(string workItemId, string projectId, string toTag) =>
        Audit("changelog.work_item_created")
            .Information("Changelog work item {WorkItemId} created for project {ProjectId} tag {ToTag}",
                workItemId, projectId, toTag);

    // ── Dead-worker recovery ─────────────────────────────────────────────────

    public static void WorkerRegistered(string workerId, string hostName, int processId) =>
        Audit("worker.registered")
            .Information("Worker {WorkerId} registered on {HostName} (pid={ProcessId})", workerId, hostName, processId);

    public static void WorkerDeregistered(string workerId) =>
        Audit("worker.deregistered")
            .Information("Worker {WorkerId} deregistered (clean shutdown)", workerId);

    public static void DeadWorkerRecovered(WorkItemId itemId, string workerId, WorkItemState fromState, WorkItemState toState, int attempt) =>
        Audit("work_item.worker_dead_recovered")
            .Information("Dead worker {WorkerId}: recovered work item {WorkItemId} from {FromState} to {ToState} (attempt {Attempt})",
                workerId, itemId.ToString(), fromState.ToString(), toState.ToString(), attempt);

    public static void DeadWorkerFailedTerminal(WorkItemId itemId, string workerId, int attempt) =>
        Audit("work_item.worker_dead_failed_terminal")
            .Warning("Dead worker {WorkerId}: work item {WorkItemId} exceeded MaxRecoveryAttempts at attempt {Attempt}; transitioned to Failed",
                workerId, itemId.ToString(), attempt);

    /// <summary>
    /// Emitted when the progress watchdog detects a bound worker whose item
    /// has made no progress (item.updatedAt frozen, no new stream activity)
    /// for the configured window, despite a fresh heartbeat. Diagnoses the
    /// "agent completed but pipeline never transitioned" wedge and the
    /// "pre-agent setup hung before any stream was written" wedge.
    /// </summary>
    public static void WorkItemWatchdogStuck(
        WorkItemId itemId,
        string workerId,
        WorkItemState state,
        long sinceProgressSeconds,
        string? lastStreamEvent) =>
        Audit("work_item.watchdog_stuck")
            .Warning(
                "Watchdog: work item {WorkItemId} (worker {WorkerId}, state {State}) made no progress for {SinceProgressSeconds}s; last stream event: {LastStreamEvent}",
                itemId.ToString(), workerId, state.ToString(), sinceProgressSeconds, lastStreamEvent ?? "<none>");

    /// <summary>
    /// Emitted when the watchdog auto-recovers a wedged item: pool slot
    /// released, item re-queued from its recoverable resume state, and any
    /// dependents that were cascade-cancelled because of this parent are
    /// restored.
    /// </summary>
    public static void WorkItemWatchdogRecovered(
        WorkItemId itemId,
        string workerId,
        WorkItemState fromState,
        WorkItemState toState,
        int dependentsRestored) =>
        Audit("work_item.watchdog_recovered")
            .Warning(
                "Watchdog: recovered work item {WorkItemId} from worker {WorkerId} {FromState} → {ToState}; restored {DependentsRestored} cascade-cancelled dependent(s)",
                itemId.ToString(), workerId, fromState.ToString(), toState.ToString(), dependentsRestored);

    /// <summary>
    /// Emitted when the watchdog detects a wedge but <c>AutoRecover=false</c>:
    /// the item is parked at NeedsOperatorInput with a diagnostic LastError.
    /// </summary>
    public static void WorkItemWatchdogParked(
        WorkItemId itemId,
        string workerId,
        WorkItemState fromState) =>
        Audit("work_item.watchdog_parked")
            .Warning(
                "Watchdog: parked work item {WorkItemId} (worker {WorkerId}, state {FromState}) for operator triage; auto-recover disabled",
                itemId.ToString(), workerId, fromState.ToString());

    /// <summary>
    /// Emitted when the watchdog restores a dependent that had been
    /// cascade-cancelled because <paramref name="parentId"/> was previously
    /// cancelled. Inverse of <see cref="WorkItemDependentCancelled"/>.
    /// </summary>
    public static void WorkItemDependentRestored(WorkItemId id, WorkItemId parentId) =>
        Audit("work_item.dependent_restored")
            .Information(
                "Work item {WorkItemId} restored to Queued: parent {ParentWorkItemId} recovered, every dependency now satisfiable",
                id.ToString(), parentId.ToString());

    /// <summary>
    /// Emitted when the pipeline's post-agent commit/branch-push/state-
    /// transition step exceeds <c>PostAgentTransitionTimeout</c>. The item is
    /// failed within bounded time so the pool slot is released rather than
    /// held indefinitely behind a hung git operation.
    /// </summary>
    public static void WorkItemPostAgentTimeout(WorkItemId itemId, string phase, long timeoutSeconds) =>
        Audit("work_item.post_agent_timeout")
            .Warning(
                "Work item {WorkItemId} post-agent step '{Phase}' exceeded {TimeoutSeconds}s; failing item to release pool slot",
                itemId.ToString(), phase, timeoutSeconds);

    // ── Budget caps ──────────────────────────────────────────────────────────

    public static void BudgetDeferred(WorkItemId id, ProjectId projectId, string reason) =>
        Audit("budget.deferred")
            .Information("Work item {WorkItemId} for project {ProjectId} deferred by budget cap: {Reason}",
                id.ToString(), projectId.Value, reason);

    public static void RefactorExclusivityDeferred(WorkItemId id, ProjectId projectId, string reason) =>
        Audit("refactor.exclusivity_deferred")
            .Information("Work item {WorkItemId} for project {ProjectId} deferred by refactor exclusivity gate: {Reason}",
                id.ToString(), projectId.Value, reason);

    public static void DiskDeferred(WorkItemId id, string mountPath, long freeBytes, long thresholdBytes) =>
        Audit("disk.deferred")
            .Warning(
                "Work item {WorkItemId} deferred: only {FreeBytes:N0} bytes free on {MountPath} (threshold {ThresholdBytes:N0})",
                id.ToString(), freeBytes, mountPath, thresholdBytes);

    public static void StoreDiskFull(string operation) =>
        Audit("store.disk_full")
            .Fatal(
                "SQLite reported SQLITE_FULL during '{Operation}'; host disk is exhausted and no further state transitions can be persisted",
                operation);

    public static void ProjectQueuePaused(ProjectId projectId, string reason) =>
        Audit("project_queue.paused")
            .Information("Project {ProjectId} queue paused: {Reason}", projectId.Value, reason);

    public static void ProjectQueueResumed(ProjectId projectId) =>
        Audit("project_queue.resumed")
            .Information("Project {ProjectId} queue resumed", projectId.Value);

    public static void BudgetAlertWarning(ProjectId projectId, decimal spendUsd, decimal budgetUsd, double pct) =>
        Audit("budget_alert.warning")
            .Warning("Project {ProjectId} budget warning: ${SpendUsd:F4} of ${BudgetUsd:F2} ({Pct:F1}%)",
                projectId.Value, spendUsd, budgetUsd, pct);

    public static void BudgetAlertExceeded(ProjectId projectId, decimal spendUsd, decimal budgetUsd, double pct) =>
        Audit("budget_alert.exceeded")
            .Warning("Project {ProjectId} budget exceeded: ${SpendUsd:F4} of ${BudgetUsd:F2} ({Pct:F1}%)",
                projectId.Value, spendUsd, budgetUsd, pct);

    public static void BudgetAlertRecovered(ProjectId projectId, decimal spendUsd, decimal budgetUsd, double pct) =>
        Audit("budget_alert.recovered")
            .Information("Project {ProjectId} budget recovered: ${SpendUsd:F4} of ${BudgetUsd:F2} ({Pct:F1}%)",
                projectId.Value, spendUsd, budgetUsd, pct);

    public static void BudgetAlertServiceStartupSafe(string reason) =>
        Audit("budget_alert.startup_safe")
            .Warning("BudgetAlertService: {Reason}; cost-budget checks will be skipped until the next tick", reason);

    // ── Quota router ─────────────────────────────────────────────────────────

    public static void QuotaProbed(AgentKind agent, string classId, double availablePct, DateTimeOffset? resetAt, string? notes = null) =>
        Audit("quota_router.probed")
            .Information("Quota probe: agent={Agent} class={ClassId} available={AvailablePct:F1}% resetAt={ResetAt} notes={Notes}",
                agent.Value, classId, availablePct, resetAt, notes);

    public static void QuotaProbed(AgentKind agent, string instanceId, string classId, double availablePct, DateTimeOffset? resetAt, string? notes = null) =>
        Audit("quota_router.probed")
            .Information("Quota probe: agent={Agent} instance={AgentInstance} class={ClassId} available={AvailablePct:F1}% resetAt={ResetAt} notes={Notes}",
                agent.Value, instanceId, classId, availablePct, resetAt, notes);

    public static void QuotaRouterWaiting(string classId, WorkItemId id, TimeSpan recheckIn) =>
        Audit("quota_router.waiting")
            .Warning("Quota router: work item {WorkItemId} waiting — all members of class '{ClassId}' are exhausted; recheck in {RecheckMs}ms",
                id.ToString(), classId, (long)recheckIn.TotalMilliseconds);

    public static void QuotaRouterDeferred(WorkItemId id, TimeSpan recheckIn) =>
        Audit("quota_router.deferred")
            .Information("Quota router: work item {WorkItemId} deferred — re-enqueue scheduled in {RecheckMs}ms",
                id.ToString(), (long)recheckIn.TotalMilliseconds);

    /// <summary>
    /// Emitted by the quota retry scheduler for every quota-shaped item it
    /// evaluates, including no-op outcomes. <paramref name="reason"/> is forced
    /// through an empty-string sentinel so operators can distinguish "no extra
    /// reason" from a schema change in audit-log consumers.
    /// </summary>
    public static void QuotaRetryAttempted(
        WorkItemId id,
        string source,
        string outcome,
        string state,
        string? reason = null) =>
        Audit("quota_retry_attempted")
            .ForContext("Reason", reason ?? "")
            .Information(
                "Quota retry attempted for work item {WorkItemId}: source={Source} outcome={Outcome} state={State} reason={Reason}",
                id.ToString(), source, outcome, state, reason ?? "");

    /// <summary>
    /// Emitted when all class members fail the MinModelScore floor check. Records
    /// the rejected members and their below-floor reasons so the audit log captures
    /// the failure detail even though no member was chosen.
    /// </summary>
    public static void QuotaRouterNoEligible(
        WorkItemId id,
        string classId,
        int minModelScore,
        IEnumerable<(AgentKind Agent, string? ModelId, int EffectiveScore, string RejectReason)> rejected) =>
        Audit("quota_router.scored")
            .Warning(
                "Quota router no-eligible: workItem={WorkItemId} class={ClassId} minModelScore={MinModelScore} " +
                "rejected=[{Rejected}]",
                id.ToString(), classId, minModelScore,
                string.Join("; ", rejected.Select(r => $"{r.Agent.Value}/{r.ModelId ?? "(default)"}:eff={r.EffectiveScore}:{r.RejectReason}")));

    /// <summary>
    /// Emitted once per pickup after a score-based routing decision. Records the
    /// chosen member's scores and all rejected members with their reject reasons,
    /// enabling post-hoc inspection of routing decisions without re-running.
    /// </summary>
    public static void QuotaRouterScored(
        WorkItemId id,
        string classId,
        AgentKind chosenAgent,
        string? chosenModelId,
        int chosenBaseScore,
        int chosenEffectiveScore,
        string appliedModifiers,
        IEnumerable<(AgentKind Agent, string? ModelId, int EffectiveScore, string RejectReason)> rejected) =>
        Audit("quota_router.scored")
            .Information(
                "Quota router scored: workItem={WorkItemId} class={ClassId} " +
                "chosen={Agent}/{ModelId} baseScore={BaseScore} effectiveScore={EffectiveScore} modifiers={Modifiers} " +
                "rejected=[{Rejected}]",
                id.ToString(), classId,
                chosenAgent.Value, chosenModelId ?? "(default)", chosenBaseScore, chosenEffectiveScore,
                appliedModifiers,
                string.Join("; ", rejected.Select(r => $"{r.Agent.Value}/{r.ModelId ?? "(default)"}:eff={r.EffectiveScore}:{r.RejectReason}")));

    // ── Per-agent concurrency + rate-aware gate ──────────────────────────────

    /// <summary>
    /// Emitted when the orchestrator skips a dispatch because the routed agent's
    /// per-agent concurrency cap is at its ceiling. Distinct from
    /// <c>quota_router.deferred</c>: quota was fine, the operator-set cap was the
    /// constraint.
    /// </summary>
    public static void ConcurrencyGated(WorkItemId id, AgentKind agent, int running, int cap) =>
        Audit("concurrency.gated_per_agent")
            .Information(
                "Concurrency gate: work item {WorkItemId} skipped — per-agent cap reached for {Agent}: running={Running} cap={Cap}",
                id.ToString(), agent.Value, running, cap);

    /// <summary>
    /// Emitted when the router's rate-aware gate refuses a member because adding
    /// another concurrent burn would not fit in the remaining quota window —
    /// even though the raw <c>availablePct</c> is still above the MinQuotaPct
    /// floor. Distinct from <c>quota_router.scored</c>'s "quota exhausted"
    /// reject reason so operators can tell apart "no quota" from "would overrun
    /// the window if we run another".
    /// </summary>
    public static void RateAwareGated(
        AgentKind agent,
        string? modelId,
        int running,
        double fitInWindow,
        double avgBurnPct,
        double availablePct,
        int sampleCount,
        AgentBurnEstimateStatus status) =>
        Audit("concurrency.gated_rate_aware")
            .Information(
                "Rate-aware gate: {Agent}/{Model} running={Running} >= fit={FitInWindow:F2} (avgBurn={AvgBurnPct:F1}% available={AvailablePct:F1}% samples={Samples} status={Status})",
                agent.Value, modelId ?? "(default)", running, fitInWindow, avgBurnPct, availablePct, sampleCount, status);

    // ── Plugin loading ───────────────────────────────────────────────────────

    public static void PluginLoaded(string pluginId, string displayName, string assemblyPath) =>
        Audit("plugin.loaded")
            .Information("Plugin loaded: {PluginId} ({DisplayName}) from {AssemblyPath}",
                pluginId, displayName, assemblyPath);

    public static void PluginSkippedNotAllowlisted(string pluginId, string assemblyPath) =>
        Audit("plugin.skipped_not_allowlisted")
            .Information("Plugin {PluginId} skipped: not in Plugins.Allowlist (path: {AssemblyPath})",
                pluginId, assemblyPath);

    public static void PluginSkippedApiVersion(string pluginId, string required, string current) =>
        Audit("plugin.skipped_api_version")
            .Error("Plugin {PluginId} requires host API version {Required} but this host provides {Current}; plugin not loaded",
                pluginId, required, current);

    public static void PluginInitializationFailed(string pluginId, Exception exception) =>
        Audit("plugin.initialization_failed")
            .Error(exception, "Plugin {PluginId} initialization failed; plugin not available", pluginId);

    // ── Hot-reload config ────────────────────────────────────────────────────

    /// <summary>
    /// Emitted by <c>AgentConfigHotReload</c> when an OnChange-driven swap of a
    /// hot-reloadable config block (AgentConcurrency, AgentClasses,
    /// AgentBurnEstimator) actually mutated the in-memory view. Fires at most
    /// once per block per reload; if a reload edits unrelated fields the block
    /// stays silent. Operators trace per-block config drift by filtering on
    /// EventName="config_reloaded".
    /// </summary>
    public static void ConfigReloaded(string block, string oldValue, string newValue) =>
        Audit("config_reloaded")
            .Information("Configuration reloaded: block={Block} oldValue={OldValue} newValue={NewValue}",
                block, oldValue, newValue);

    // ── Transcript sanitisation ──────────────────────────────────────────────

    /// <summary>
    /// Emitted when the preventive Claude thinking-block transcript sanitizer
    /// fails inside <c>PrepareSandboxAsync</c>. The run continues — the
    /// failure detail surfaces later via the reactive retry path if the CLI
    /// call subsequently 400s. This event gives operators an early signal
    /// that the primary prevention mechanism is unhealthy.
    /// </summary>
    public static void ClaudeTranscriptSanitizerFailed(string summary, string? stderr) =>
        Audit("agent.claude_transcript_sanitizer_failed")
            .Warning(
                "Preventive Claude transcript sanitization failed ({Summary}); " +
                "the run will continue but thinking-block 400s may follow. stderr={Stderr}",
                summary, stderr ?? "none");

    /// <summary>
    /// Emitted by <c>CodeyBox.Agents.Claude.ClaudeSessionWorker</c> when it
    /// degrades the active transport for a session from ACP to print at
    /// runtime — either because the ACP transport could not open at session
    /// start or because a turn surfaced an
    /// <c>AcpTransportUnavailableException</c>. The work item is NOT
    /// stranded — the next turn proceeds on print — but the operator should
    /// investigate the ACP path so the metered <c>-p</c> pool stops absorbing
    /// the traffic.
    /// </summary>
    public static void ClaudeAcpTransportDegraded(string sessionId, string reason) =>
        Audit("agent.claude_acp_transport_degraded")
            .Warning(
                "Claude ACP transport degraded to print for session {SessionId}: {Reason}",
                sessionId, reason);

    /// <summary>
    /// Emitted when the CLI-native session-resume liveness probe threw an
    /// unexpected exception against the sandbox (a sandbox/provider bug, not
    /// the agent itself). Surfaces the infrastructure problem rather than
    /// hiding it as an ordinary non-resumable agent exit. The work item still
    /// fails / re-drives, but operators can correlate the failure with the
    /// underlying sandbox fault.
    /// </summary>
    public static void SessionResumeLivenessProbeFailed(AgentKind agent, string exceptionType, string message) =>
        Audit("agent.session_resume_liveness_probe_failed")
            .Warning(
                "Session-resume liveness probe failed unexpectedly for {Agent}: {ExceptionType}: {Message}",
                agent.Value, exceptionType, message);

    /// <summary>
    /// Emitted by <c>PipelineRunner</c> when <c>ClaudeSessionLifecycle.SuspendAsync</c>
    /// throws between a worker turn and the audit phase. The session is then
    /// closed so the long audit does not run with an idle worker VM still
    /// holding host resources, and the next rework turn degrades to the
    /// legacy fresh-sandbox path. Surfacing this is a session-mode acceptance
    /// criterion: a silent swallow would hide the multipass stop/resume
    /// boundary failure from operators.
    /// </summary>
    public static void ClaudeSessionSuspendFailed(WorkItemId itemId, string sessionId, string reason) =>
        Audit("agent.claude_session_suspend_failed")
            .Warning(
                "Claude session suspend failed for work item {WorkItemId} session {SessionId}: {Reason}. " +
                "Closing the session and degrading to the legacy fresh-sandbox rework path.",
                itemId, sessionId, reason);

    // ── Live agent supervision ───────────────────────────────────────────────

    public static void AgentSupervisionInjectionQueued(
        WorkItemId workItemId,
        string sessionId,
        string phase,
        AgentKind agent,
        string actor,
        string injectionId,
        string message)
    {
        Audit("agent.supervision_injection_queued")
            .ForContext("InjectionText", TruncateAuditTail(RawChunkRedactor.Redact(message)))
            .Information(
                "Live supervision injection {InjectionId} queued by {Actor} for work item {WorkItemId} session {SessionId} phase={Phase} agent={Agent}",
                injectionId,
                actor,
                workItemId.ToString(),
                sessionId,
                phase,
                agent.Value);
    }

    public static void AgentSupervisionInjectionStarted(
        WorkItemId workItemId,
        string sessionId,
        string phase,
        AgentKind agent,
        string actor,
        string injectionId) =>
        Audit("agent.supervision_injection_started")
            .Information(
                "Live supervision injection {InjectionId} started for work item {WorkItemId} session {SessionId} phase={Phase} agent={Agent} actor={Actor}",
                injectionId,
                workItemId.ToString(),
                sessionId,
                phase,
                agent.Value,
                actor);

    public static void AgentSupervisionInjectionCompleted(
        WorkItemId workItemId,
        string sessionId,
        string phase,
        AgentKind agent,
        string actor,
        string injectionId,
        bool success,
        string summary)
    {
        Audit("agent.supervision_injection_completed")
            .ForContext("Summary", TruncateAuditTail(RawChunkRedactor.Redact(summary)))
            .Information(
                "Live supervision injection {InjectionId} completed for work item {WorkItemId} session {SessionId} phase={Phase} agent={Agent} actor={Actor} success={Success}",
                injectionId,
                workItemId.ToString(),
                sessionId,
                phase,
                agent.Value,
                actor,
                success);
    }

    // ── Internal helper ──────────────────────────────────────────────────────

    private static Serilog.ILogger Audit(string eventName) =>
        Log.Logger
            .ForContext("Audit", true)
            .ForContext("EventName", eventName);
}
