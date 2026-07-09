using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

internal enum WorkItemRetryFailureKind
{
    None,
    StateChangedConcurrently,
}

internal readonly record struct WorkItemRetryResult(
    bool Success,
    string? Error,
    WorkItemState? ResumeState,
    string? ActualFrom,
    IReadOnlyList<string>? OpenQuestions,
    WorkItemRetryFailureKind FailureKind = WorkItemRetryFailureKind.None)
{
    public void Deconstruct(
        out bool success,
        out string? error,
        out WorkItemState? resumeState,
        out string? actualFrom,
        out IReadOnlyList<string>? openQuestions)
    {
        success = Success;
        error = Error;
        resumeState = ResumeState;
        actualFrom = ActualFrom;
        openQuestions = OpenQuestions;
    }
}

/// <summary>
/// Consolidates retry logic for terminal and operator-parked work items,
/// ensuring consistent state transitions, audit logs, and side effects (e.g.
/// stream summary deletion).
/// </summary>
public sealed class WorkItemRetrier
{
    private enum RetryAccounting
    {
        None,
        QuotaAutoRetry,
        TransientAutoRetry,
        AgentRestoreAutoRetry,
    }

    private readonly IWorkItemStore _store;
    private readonly ITaskQueue _queue;
    private readonly IGitHost _gitHost;
    private readonly IAgentStreamSummaryStore? _streamSummaries;
    private readonly IAuditProgressStore? _auditProgress;
    private readonly IProjectRepository? _projects;
    private readonly IReleaseStore? _releases;
    private readonly IWorkItemQuestionStore? _questions;
    private readonly ILogger<WorkItemRetrier> _log;

    public WorkItemRetrier(
        IWorkItemStore store,
        ITaskQueue queue,
        IGitHost gitHost,
        ILogger<WorkItemRetrier> log,
        IAgentStreamSummaryStore? streamSummaries = null,
        IProjectRepository? projects = null,
        IReleaseStore? releases = null,
        IWorkItemQuestionStore? questions = null,
        IAuditProgressStore? auditProgress = null)
    {
        _store = store;
        _queue = queue;
        _gitHost = gitHost;
        _streamSummaries = streamSummaries;
        // Null intentionally disables durable audit-progress history for narrow
        // test fixtures; production DI wires this dependency explicitly.
        _auditProgress = auditProgress;
        _projects = projects;
        _releases = releases;
        _questions = questions;
        _log = log;
    }

    public async Task<(bool Success, string? Error, WorkItemState? ResumeState, string? ActualFrom, IReadOnlyList<string>? OpenQuestions)> RetryAsync(
        WorkItem item,
        string? from = null,
        string trigger = "manual",
        CancellationToken ct = default)
        => ToPublicResult(await RetryCoreAsync(item, from, trigger, RetryAccounting.None, ct));

    public async Task<(bool Success, string? Error, WorkItemState? ResumeState, string? ActualFrom, IReadOnlyList<string>? OpenQuestions)> RetryQuotaAutoAsync(
        WorkItem item,
        string? from,
        string trigger,
        CancellationToken ct = default)
        => ToPublicResult(await RetryCoreAsync(item, from, trigger, RetryAccounting.QuotaAutoRetry, ct));

    public async Task<(bool Success, string? Error, WorkItemState? ResumeState, string? ActualFrom, IReadOnlyList<string>? OpenQuestions)> RetryTransientAutoAsync(
        WorkItem item,
        string? from,
        string trigger,
        CancellationToken ct = default)
        => ToPublicResult(await RetryCoreAsync(item, from, trigger, RetryAccounting.TransientAutoRetry, ct));

    public async Task<(bool Success, string? Error, WorkItemState? ResumeState, string? ActualFrom, IReadOnlyList<string>? OpenQuestions)> RetryAgentRestoreAsync(
        WorkItem item,
        string? from,
        string trigger,
        CancellationToken ct = default)
        => ToPublicResult(await RetryCoreAsync(item, from, trigger, RetryAccounting.AgentRestoreAutoRetry, ct));

    internal async Task<WorkItemRetryResult> RetryQuotaAutoDetailedAsync(
        WorkItem item,
        string? from,
        string trigger,
        CancellationToken ct = default)
        => await RetryCoreAsync(item, from, trigger, RetryAccounting.QuotaAutoRetry, ct);

    private static (bool Success, string? Error, WorkItemState? ResumeState, string? ActualFrom, IReadOnlyList<string>? OpenQuestions) ToPublicResult(
        WorkItemRetryResult result) =>
        (
            result.Success,
            result.Error,
            result.ResumeState,
            result.ActualFrom,
            result.OpenQuestions);

    private async Task<WorkItemRetryResult> RetryCoreAsync(
        WorkItem item,
        string? from,
        string trigger,
        RetryAccounting accounting,
        CancellationToken ct)
    {
        if (item.State == WorkItemState.NeedsOperatorInput && _questions is not null)
        {
            var openQuestions = (await _questions.ListByWorkItemAsync(item.Id.ToString(), ct))
                .Where(q => string.Equals(q.State, "open", StringComparison.Ordinal))
                .Select(q => q.QuestionId)
                .Take(5)
                .ToArray();
            if (openQuestions.Length > 0)
            {
                return new WorkItemRetryResult(
                    false,
                    "cannot retry item while operator questions are open; answer or dismiss them first",
                    null,
                    null,
                    openQuestions);
            }
        }

        // A null/blank `from` means "operator did not specify" — auto-pick
        // based on work-branch state. Explicit values (including from the
        // quota auto-retry scheduler, which always passes a normalized phase)
        // always win.
        string? autoPickReason = null;
        if (string.IsNullOrWhiteSpace(from))
        {
            try
            {
                (from, autoPickReason) = await AutoPickRetryFromAsync(item, ct);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                _log.LogWarning(ex, "Failed to auto-pick retry phase for work item {Id}; retry aborted", item.Id);
                return new WorkItemRetryResult(
                    false,
                    $"cannot auto-pick retry phase for work item {item.Id}: {ex.Message}",
                    null,
                    null,
                    null);
            }
        }

        if (!RetryFromPolicy.TryNormalize(from, out var requestedFrom)
            || !RetryFromPolicy.TryGetResumeState(requestedFrom, out var resumeState))
        {
            return new WorkItemRetryResult(
                false,
                $"invalid 'from' value '{from}'",
                null,
                null,
                null);
        }

        var actualFrom = requestedFrom;
        var retryingBeforeWork = resumeState is WorkItemState.PlanReview or WorkItemState.PlanApproved;

        if (ValidatePlanningResumeBoundary(item, requestedFrom) is { } planningBoundaryError)
            return new WorkItemRetryResult(false, planningBoundaryError, null, null, null);

        // For from != "work", the pipeline expects the bare repo to still be present.
        if (resumeState != WorkItemState.Queued && !retryingBeforeWork)
        {
            var present = await _gitHost.RepositoryExistsAsync(item.Id, ct);
            if (!present)
            {
                return new WorkItemRetryResult(
                    false,
                    $"cannot retry from '{from}': bare repo for work item {item.Id} no longer exists",
                    null,
                    null,
                    null);
            }

            // The work branch must also exist — earlier work-phase failures can
            // leave the item in Failed without ever producing a commit, in which
            // case the requested post-work resume would crash the pipeline with
            // "pathspec 'codeybox/...' did not match any file(s)". Silently
            // re-route to the work phase so the operator doesn't need to track
            // which phase produced commits.
            var workBranch = item.WorkBranch;
            var branchPresent = !string.IsNullOrEmpty(workBranch)
                && await _gitHost.BranchExistsAsync(item.Id.ToString(), workBranch, ct);
            if (!branchPresent)
            {
                _log.LogInformation(
                    "Retry of work item {Id} requested from='{RequestedFrom}' but work branch '{WorkBranch}' is missing in the bare repo; auto-falling back to from='work'",
                    item.Id,
                    requestedFrom,
                    workBranch ?? "(unset)");
                resumeState = WorkItemState.Queued;
                actualFrom = "work";
            }
        }
        var clearingQueuedPlan = resumeState == WorkItemState.Queued;

        // Reset RecoveryAttempts and increment only the counter owned by the
        // auto-retry path that invoked us. Terminal-failure-recovery and manual
        // retries do not bump quota/transient counters. On any retry we clear
        // NextTerminalRetryAt so a stale backoff schedule does not gate the
        // next sweep. A manual retry also clears TerminalRetryAttempts:
        // operator-forgiveness lets the cap reset.
        var resetsTerminalRetries = trigger == "manual";
        var resumed = item.With(resumeState, error: null) with
        {
            RecoveryAttempts = 0,
            RecoveryAttemptSourceState = null,
            QuotaRetryAttempts = accounting == RetryAccounting.QuotaAutoRetry
                ? item.QuotaRetryAttempts + 1
                : item.QuotaRetryAttempts,
            TransientRetryAttempts = accounting == RetryAccounting.TransientAutoRetry
                ? item.TransientRetryAttempts + 1
                : 0,
            TransientRetryFirstFailedAt = accounting == RetryAccounting.TransientAutoRetry
                ? item.TransientRetryFirstFailedAt
                : null,
            TransientRetryFrom = accounting == RetryAccounting.TransientAutoRetry
                ? item.TransientRetryFrom
                : null,
            TerminalRetryAttempts = resetsTerminalRetries ? 0 : item.TerminalRetryAttempts,
            NextTerminalRetryAt = null,
            StartedAt = null
        };
        if (clearingQueuedPlan)
        {
            resumed = WorkItemRecoveryPolicy.ClearPlanFieldsIfQueued(resumed) with
            {
                PreserveWorkBranchOnQueuedPickup = false,
            };
        }

        // Atomic conditional update to prevent race conditions.
        // We retry from Failed, AuditFailed, MergeConflictResolutionFailed,
        // Cancelled, AbandonedAfterRecoveryAttempts, NeedsOperatorInput, or
        // WaitingForQuotaReset, or WaitingForTransientRetry. Eligibility gates
        // that must apply across HTTP, scheduler, and operator paths live in
        // this retrier before the write.
        var updated = accounting is RetryAccounting.TransientAutoRetry or RetryAccounting.AgentRestoreAutoRetry
            ? await _store.TryUpdateIfStateAndUpdatedAtAsync(resumed, item.State, item.UpdatedAt, ct)
            : await _store.TryUpdateIfStateAsync(resumed, item.State, ct);
        if (!updated)
        {
            return new WorkItemRetryResult(
                false,
                "work item state changed concurrently; retry aborted",
                null,
                null,
                null,
                WorkItemRetryFailureKind.StateChangedConcurrently);
        }

        if (_streamSummaries is not null)
        {
            try { await _streamSummaries.DeleteByWorkItemAsync(item.Id, ct); }
            catch (Exception ex) { _log.LogWarning(ex, "Failed to delete stream summaries for work item {Id}", item.Id); }
        }

        var auditFrom = actualFrom == requestedFrom
            ? requestedFrom
            : $"{actualFrom} (fallback from '{requestedFrom}': work branch missing)";
        if (autoPickReason is not null)
            auditFrom = $"{auditFrom} (auto-pick: {autoPickReason})";
        try
        {
            await _queue.EnqueueAsync(resumed.Id, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var reverted = false;
            try
            {
                reverted = await _store.TryUpdateIfStateAsync(item, resumeState, CancellationToken.None);
            }
            catch (Exception rollbackEx)
            {
                _log.LogError(rollbackEx,
                    "Failed to roll back work item {Id} after retry queue kick failed",
                    item.Id);
            }

            if (reverted)
            {
                _log.LogWarning(ex,
                    "Retry of work item {Id} updated state to {State} but queue kick failed; rolled back to {PreviousState}",
                    item.Id, resumeState, item.State);
                return new WorkItemRetryResult(
                    false,
                    $"queue enqueue failed after state update; rolled back to {item.State}: {ex.Message}",
                    null,
                    actualFrom,
                    null);
            }

            _log.LogError(ex,
                "Retry of work item {Id} updated state to {State} but queue kick failed and rollback did not apply",
                item.Id, resumeState);
            return new WorkItemRetryResult(
                false,
                $"queue enqueue failed after state update and rollback did not apply: {ex.Message}",
                null,
                actualFrom,
                null);
        }

        AuditLog.WorkItemRetried(item.Id, trigger == "manual" ? auditFrom : $"{auditFrom} (auto-retry: {trigger})");
        return new WorkItemRetryResult(true, null, resumeState, actualFrom, null);
    }

    /// <summary>
    /// Picks a sensible default <c>from</c> phase for retries when the operator
    /// didn't specify one. The motivating scenario: a long-running item that
    /// already produced commits on its work branch in a prior iteration is
    /// retried after a Failed; if we default to <c>from=work</c> the agent
    /// observes the prior commits, judges "nothing to add", exits cleanly with
    /// zero diff — and the orchestrator classifies the empty diff as a failure.
    /// The fix: when the work branch is ahead of base, default to <c>from=audit</c>
    /// so the existing tip is re-audited (and merged if it passes, reworked if
    /// it doesn't) rather than discarded.
    ///
    /// Returns <c>("work", reason)</c> only for expected "nothing to audit"
    /// states such as a missing work branch. Unexpected probe failures are
    /// allowed to abort the retry so we do not silently discard prior work.
    /// </summary>
    private async Task<(string From, string Reason)> AutoPickRetryFromAsync(
        WorkItem item,
        CancellationToken ct)
    {
        var workBranch = item.WorkBranch;
        if (string.IsNullOrEmpty(workBranch))
            return ("work", "no work branch on record");

        var repoId = item.Id.ToString();
        if (!await _gitHost.RepositoryExistsAsync(item.Id, ct))
            return ("work", "bare repo missing");

        bool branchPresent;
        try
        {
            branchPresent = await _gitHost.BranchExistsAsync(repoId, workBranch, ct);
        }
        catch (ArgumentException)
        {
            return ("work", "work branch name invalid");
        }
        if (!branchPresent)
            return ("work", "work branch missing");

        var baseBranch = await ResolveBaseBranchAsync(item, repoId, ct);
        var ahead = await _gitHost.BranchHasCommitsAheadAsync(repoId, baseBranch, workBranch, ct);
        if (!ahead)
            return ("work", "work branch has no commits ahead of base");

        var interruptedAudit = await TryGetInterruptedAuditProgressAsync(item, ct);
        if (interruptedAudit is { Findings.Count: > 0 })
            return ("rework", $"last audit iteration {interruptedAudit.Iteration} was interrupted after partial findings");

        if (interruptedAudit is not null)
            return ("audit", $"last audit iteration {interruptedAudit.Iteration} was interrupted before findings; restarting audit cleanly");

        return ("audit", "work branch has prior commits ahead of base");
    }

    private async Task<AuditProgressRecord?> TryGetInterruptedAuditProgressAsync(
        WorkItem item,
        CancellationToken ct)
    {
        if (_auditProgress is null)
            return null;

        var currentWorkAttemptStartedAt = await ResolveCurrentWorkAttemptStartedAtAsync(item.Id, ct);
        var progress = await _auditProgress.GetAuditProgressAsync(item.Id, currentWorkAttemptStartedAt, ct);
        var latest = progress
            .Where(p => p.Iteration > 0)
            .OrderByDescending(p => p.Iteration)
            .FirstOrDefault();
        return latest is not null && !AuditProgressStatuses.IsComplete(latest.Status)
            ? latest
            : null;
    }

    private async Task<string> ResolveBaseBranchAsync(WorkItem item, string repoId, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(item.BaseBranch))
            return item.BaseBranch!;

        if (item.ReleaseId is { } releaseId && _releases is not null)
        {
            var release = await _releases.GetAsync(releaseId, ct);
            if (!string.IsNullOrWhiteSpace(release?.BranchName))
                return release.BranchName!;
        }

        if (_projects is not null)
        {
            var project = await _projects.GetAsync(item.ProjectId, ct);
            if (!string.IsNullOrWhiteSpace(project?.DefaultBaseBranch))
                return project.DefaultBaseBranch!;
        }

        return await _gitHost.GetDefaultBranchAsync(repoId, ct);
    }

    public readonly record struct AgentPauseResumeOutcome(
        bool Success,
        string? Error,
        WorkItem? Resumed,
        string RetryFrom);

    public async Task<AgentPauseResumeOutcome> ResumeAfterAgentPauseAsync(
        WorkItem item,
        string source,
        CancellationToken ct = default)
    {
        if (item.State != WorkItemState.WaitingForAgentResume)
            return new AgentPauseResumeOutcome(false, $"work item is in state {item.State}", null, "work");

        // Prefer the dedicated agent-pause column; fall back to the legacy
        // quota_retry_from value for rows parked before agent_pause_retry_from
        // existed so legacy WaitingForAgentResume rows keep resuming at the
        // correct phase boundary.
        var retryFrom = AgentPauseResumeMapper.NormalizeRetryFrom(
            item.AgentPauseRetryFrom ?? item.QuotaRetryFrom);
        var resumeState = AgentPauseResumeMapper.ResumeStateForRetryFrom(retryFrom);
        var resumed = WorkItemRecoveryPolicy.ClearPlanFieldsIfQueued(item.With(resumeState, error: null) with
        {
            FailureKind = null,
            QuotaResetAt = null,
            NextQuotaRetryAt = null,
            QuotaRetryFrom = null,
            QuotaRetryPhase = null,
            NextTransientRetryAt = null,
            TransientRetryAttempts = 0,
            TransientRetryFirstFailedAt = null,
            TransientRetryFrom = null,
            AgentPauseRetryFrom = null,
            StartedAt = null,
        });

        var updated = await _store.TryUpdateIfStateAsync(
                resumed,
                WorkItemState.WaitingForAgentResume,
                ct)
            .ConfigureAwait(false);
        if (!updated)
        {
            return new AgentPauseResumeOutcome(
                false,
                "work item state changed before agent-pause resume",
                null,
                retryFrom);
        }

        try
        {
            await _queue.EnqueueAsync(resumed.Id, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var reverted = false;
            try
            {
                reverted = await _store.TryUpdateIfStateAsync(
                        item,
                        resumeState,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception rollbackEx)
            {
                _log.LogError(rollbackEx,
                    "Failed to roll back work item {Id} after agent-pause resume queue kick failed",
                    item.Id);
            }

            if (reverted)
            {
                _log.LogWarning(ex,
                    "Agent-pause resume of work item {Id} updated state to {State} but queue kick failed; rolled back to WaitingForAgentResume",
                    item.Id,
                    resumeState);
                return new AgentPauseResumeOutcome(
                    false,
                    $"queue enqueue failed after state update; rolled back to WaitingForAgentResume: {ex.Message}",
                    null,
                    retryFrom);
            }

            _log.LogError(ex,
                "Agent-pause resume of work item {Id} updated state to {State} but queue kick failed and rollback did not apply",
                item.Id,
                resumeState);
            return new AgentPauseResumeOutcome(
                false,
                $"queue enqueue failed after state update and rollback did not apply: {ex.Message}",
                null,
                retryFrom);
        }

        AuditLog.AgentPauseWaitingItemResumed(item.Id, source, retryFrom);
        return new AgentPauseResumeOutcome(true, null, resumed, retryFrom);
    }

    /// <summary>
    /// Outcome of a resume attempt. <see cref="ResumeStatus"/> maps 1:1 to HTTP
    /// status codes so the API endpoint can be a thin adapter.
    /// </summary>
    public enum ResumeStatus
    {
        Ok = 200,
        BadRequest = 400,
        Conflict = 409,
        PreconditionFailed = 412,
    }

    public readonly record struct ResumeOutcome(
        ResumeStatus Status,
        string? Error,
        WorkItem? Resumed,
        WorkItemState? ResumeState);

    /// <summary>
    /// Operator-cancel resume: restores a Cancelled work item to the pipeline
    /// while preserving its bare repo + work-branch + agent commits.
    ///
    /// Distinct from <see cref="RetryAsync"/>: retry uses
    /// <c>WorkItem.With(Queued)</c> which intentionally CLEARS WorkBranch (the
    /// failed-retry path generates a fresh branch), while resume must keep the
    /// existing branch so the rework picks up on top of the prior commits.
    /// Also emits a distinct <c>work_item.resumed</c> audit event so operators
    /// can isolate intentional resume actions from retry-after-failure churn.
    ///
    /// Always enqueues — the dispatcher uses <see cref="ITaskQueue"/> as a
    /// generic "something changed, re-check the DB" kick channel, not a
    /// Queued-only worker queue (matches DeadWorkerReaper / RetryAsync / the
    /// transient-cancel auto-retry — all enqueue regardless of target state).
    /// </summary>
    public async Task<ResumeOutcome> ResumeAsync(
        WorkItem item,
        string from,
        string? reason,
        CancellationToken ct = default)
    {
        if (item.State != WorkItemState.Cancelled)
            return new ResumeOutcome(
                ResumeStatus.Conflict,
                $"cannot resume item in state {item.State}; only Cancelled items can be resumed",
                null,
                null);

        var rawFrom = from ?? RetryFromPolicy.Work;
        if (!RetryFromPolicy.TryNormalize(rawFrom, out var requestedFrom)
            || requestedFrom is RetryFromPolicy.ConflictRework or RetryFromPolicy.Upstream
            || !RetryFromPolicy.TryGetResumeState(requestedFrom, out var resumeState))
        {
            return new ResumeOutcome(
                ResumeStatus.BadRequest,
                $"invalid 'from' value '{from}'; expected one of: planning, plan_review, plan_approved, work, rework, audit, merge",
                null,
                null);
        }
        if (ValidatePlanningResumeBoundary(item, requestedFrom) is { } planningBoundaryError)
            return new ResumeOutcome(
                ResumeStatus.Conflict,
                planningBoundaryError,
                null,
                null);
        var resumingFromPlanning = requestedFrom == "planning";
        var resumingBeforeWork = requestedFrom is "planning" or "plan_review" or "plan_approved";

        // Resume preserves the prior bare repo + work-branch (that is the
        // entire point — recovering the agent commits the operator's cancel
        // left intact). If either is gone, the operator must fall back to
        // /replay for a fresh start.
        const string preconditionMessage =
            "bare repo or work-branch no longer present; cannot resume — use POST /workitems/{id}/replay for a fresh start";
        if (!resumingBeforeWork)
        {
            var repoPresent = await _gitHost.RepositoryExistsAsync(item.Id, ct);
            if (!repoPresent)
                return new ResumeOutcome(ResumeStatus.PreconditionFailed, preconditionMessage, null, null);

            var workBranch = item.WorkBranch;
            bool branchPresent;
            try
            {
                branchPresent = !string.IsNullOrEmpty(workBranch)
                    && await _gitHost.BranchExistsAsync(item.Id.ToString(), workBranch, ct);
            }
            catch (ArgumentException)
            {
                // A legacy/corrupt WorkBranch value can fail name validation in the
                // git host. Treat that the same as "branch not present" — the
                // operator's recovery path is the same (412 → /replay).
                branchPresent = false;
            }
            if (!branchPresent)
                return new ResumeOutcome(ResumeStatus.PreconditionFailed, preconditionMessage, null, null);
        }

        // from=rework / from=audit / from=merge bypass the work phase, so the existing
        // commits on the work branch must already have durable workflow-owned
        // audit progress. Audit reports are diagnostic rows and may be
        // retention-swept, so they are deliberately not used as a resume
        // precondition.
        if (!resumingBeforeWork && resumeState != WorkItemState.Queued && _auditProgress is not null)
        {
            var currentWorkAttemptStartedAt = await ResolveCurrentWorkAttemptStartedAtAsync(item.Id, ct);
            var progress = await _auditProgress.GetAuditProgressAsync(item.Id, currentWorkAttemptStartedAt, ct);
            if (progress.Count == 0)
                return new ResumeOutcome(
                    ResumeStatus.Conflict,
                    $"cannot resume from '{requestedFrom}': work branch has no durable audit progress. Use from=work to produce an auditable rework iteration first.",
                    null,
                    null);
        }

        // Build the resumed item by hand rather than via WorkItem.With() —
        // With(Queued) intentionally clears WorkBranch for the failed-retry
        // path. Resume's whole purpose is to preserve it. AuditIterations,
        // UsageTotal, QuotaResetAt, Priority, ExternalId and ReleaseId are
        // preserved by virtue of not appearing in this initializer. Agent
        // fallback history lives in a separate append-only store
        // (IAgentFallbackHistoryStore), not on WorkItem, so it is unaffected
        // by this rebuild.
        var resumed = item with
        {
            State = resumeState,
            UpdatedAt = DateTimeOffset.UtcNow,
            LastError = null,
            CancellationReason = null,
            CancellationSource = null,
            FailureKind = null,
            NextTransientRetryAt = null,
            TransientRetryAttempts = 0,
            TransientRetryFirstFailedAt = null,
            TransientRetryFrom = null,
            RecoveryAttempts = 0,
            RecoveryAttemptSourceState = null,
            StartedAt = null,
            PlanArtifact = resumingFromPlanning ? null : item.PlanArtifact,
            PlanGeneratedAt = resumingFromPlanning ? null : item.PlanGeneratedAt,
            PlanReviewedAt = resumingFromPlanning ? null : item.PlanReviewedAt,
            PlanReviewSummary = resumingFromPlanning ? null : item.PlanReviewSummary,
            PlanReviewAttempts = resumingFromPlanning ? 0 : item.PlanReviewAttempts,
            PreserveWorkBranchOnQueuedPickup = resumeState == WorkItemState.Queued && !resumingFromPlanning,
        };

        // Conditional update guards against a racing cascade-cancel or
        // concurrent resume request — we only mutate from Cancelled.
        var updated = await _store.TryUpdateIfStateAsync(resumed, WorkItemState.Cancelled, ct);
        if (!updated)
            return new ResumeOutcome(
                ResumeStatus.Conflict,
                "concurrent resume request already processed this item",
                null,
                null);

        if (_streamSummaries is not null)
        {
            try { await _streamSummaries.DeleteByWorkItemAsync(resumed.Id, ct); }
            catch (Exception ex) { _log.LogWarning(ex, "Failed to delete stream summaries for resumed work item {Id}", resumed.Id); }
        }

        // Always kick the dispatcher. The orchestrator's PickNextEligibleAsync
        // routes by state on every kick (see OrchestratorService:286-311), so
        // a kick for a WorkComplete or AuditPassed item is what gets the
        // pipeline to re-enter the matching phase.
        await _queue.EnqueueAsync(resumed.Id, ct);

        AuditLog.WorkItemResumed(resumed.Id, requestedFrom, reason);

        return new ResumeOutcome(ResumeStatus.Ok, null, resumed, resumeState);
    }

    private static string? ValidatePlanningResumeBoundary(WorkItem item, string requestedFrom)
    {
        return requestedFrom switch
        {
            "plan_review" when string.IsNullOrWhiteSpace(item.PlanArtifact) =>
                "cannot resume from 'plan_review': planning artifact is missing; use from=planning to run planning first.",
            "plan_approved" when string.IsNullOrWhiteSpace(item.PlanArtifact) =>
                "cannot resume from 'plan_approved': approved planning artifact is missing; use from=planning to run planning first.",
            "plan_approved" when item.PlanReviewedAt is null =>
                "cannot resume from 'plan_approved': planning artifact has not been reviewed; use from=plan_review to review it first.",
            _ => null,
        };
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
}
