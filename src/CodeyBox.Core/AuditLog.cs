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
        bool minModelScoreChanged = false) =>
        Audit("work_item.patched")
            .Information(
                "Work item {WorkItemId} patched: title={TitleChanged} prompt={PromptChanged} agent={AgentChanged} workTimeout={WorkTimeoutChanged} mergeTimeout={MergeTimeoutChanged} minModelScore={MinModelScoreChanged}",
                id.ToString(), titleChanged, promptChanged, agentChanged,
                workTimeoutChanged, mergeTimeoutChanged, minModelScoreChanged);

    /// <summary>
    /// Distinct audit event for priority changes. Records the previous and new
    /// priority values explicitly so the audit trail captures the mutation —
    /// <see cref="WorkItemPatched"/>'s flags-only shape would otherwise erase it.
    /// </summary>
    public static void WorkItemPriorityChanged(WorkItemId id, int oldPriority, int newPriority) =>
        Audit("work_item.priority_changed")
            .Information("Work item {WorkItemId} priority changed: {OldPriority} → {NewPriority}",
                id.ToString(), oldPriority, newPriority);

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

    public static void AgentStuckDetected(AgentKind agent, string phase, TimeSpan stuckDuration) =>
        Audit("agent.stuck_detected")
            .Warning("Agent {Agent} stuck in phase {Phase} for {StuckSeconds}s with no CPU or network activity",
                agent.Value, phase, (int)stuckDuration.TotalSeconds);

    public static void AgentKilledByStuckProbe(AgentKind agent, string phase) =>
        Audit("agent.killed_by_stuck_probe")
            .Warning("Agent {Agent} killed by stuck probe in phase {Phase}", agent.Value, phase);

    // ── Sandbox lifecycle ────────────────────────────────────────────────────

    public static void SandboxCreated(string vmName, string? networkProfile) =>
        Audit("sandbox.created")
            .Information("Sandbox {VmName} created with network profile {NetworkProfile}",
                vmName, networkProfile);

    public static void SandboxLaunchTransientRetry(WorkItemId workItemId, int attempt, string errorClass) =>
        Audit("sandbox.launch_transient_retry")
            .Information("Sandbox launch transient failure for work item {WorkItemId}; retry {Attempt}; errorClass={ErrorClass}",
                workItemId.ToString(), attempt, errorClass);

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
        Audit("agent.smoke_failed")
            .Warning("Agent {Agent} credential smoke test failed in {DurationMs}ms: {Reason}",
                agent.Value, (long)duration.TotalMilliseconds, reason);

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

    // ── Budget caps ──────────────────────────────────────────────────────────

    public static void BudgetDeferred(WorkItemId id, ProjectId projectId, string reason) =>
        Audit("budget.deferred")
            .Information("Work item {WorkItemId} for project {ProjectId} deferred by budget cap: {Reason}",
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

    // ── Internal helper ──────────────────────────────────────────────────────

    private static Serilog.ILogger Audit(string eventName) =>
        Log.Logger
            .ForContext("Audit", true)
            .ForContext("EventName", eventName);
}
