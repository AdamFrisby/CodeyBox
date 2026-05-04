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

    public static void WorkItemPatched(WorkItemId id, bool titleChanged, bool promptChanged, bool agentChanged) =>
        Audit("work_item.patched")
            .Information("Work item {WorkItemId} patched: title={TitleChanged} prompt={PromptChanged} agent={AgentChanged}",
                id.ToString(), titleChanged, promptChanged, agentChanged);

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

    public static void SandboxDisposed(string vmName) =>
        Audit("sandbox.disposed")
            .Information("Sandbox {VmName} disposed", vmName);

    public static void SandboxLeakDetected(string name, double ageMinutes, long? diskMb) =>
        Audit("sandbox.leak_detected")
            .Warning("Leaked sandbox detected: {SandboxName} age={AgeMinutes:F1}min disk={DiskMb}MB",
                name, ageMinutes, diskMb);

    public static void SandboxLeakDisposed(string name, double ageMinutes, long? diskMb) =>
        Audit("sandbox.leak_disposed")
            .Information("Leaked sandbox disposed: {SandboxName} age={AgeMinutes:F1}min disk={DiskMb}MB",
                name, ageMinutes, diskMb);

    public static void SandboxLeakDisposeFailed(string name, string error) =>
        Audit("sandbox.leak_dispose_failed")
            .Warning("Failed to dispose leaked sandbox {SandboxName}: {Error}", name, error);

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
    /// Emitted when the configured audit agent had insufficient quota and the
    /// pipeline fell through to the work agent. The correlation-breaking
    /// benefit of cross-review was lost for this auditor invocation.
    /// </summary>
    public static void QuotaAuditFallthrough(AgentKind exhaustedAgent, AgentKind fallbackAgent, string auditorName) =>
        Audit("quota_router.audit_fallthrough")
            .Warning("Audit agent '{ExhaustedAgent}' quota exhausted; fell through to '{FallbackAgent}' for auditor '{AuditorName}'",
                exhaustedAgent.Value, fallbackAgent.Value, auditorName);

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

    // ── Budget caps ──────────────────────────────────────────────────────────

    public static void BudgetDeferred(WorkItemId id, ProjectId projectId, string reason) =>
        Audit("budget.deferred")
            .Information("Work item {WorkItemId} for project {ProjectId} deferred by budget cap: {Reason}",
                id.ToString(), projectId.Value, reason);

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
