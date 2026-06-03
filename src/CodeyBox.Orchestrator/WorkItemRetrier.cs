using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Consolidates retry logic for terminal work items, ensuring consistent state
/// transitions, audit logs, and side effects (e.g. stream summary deletion).
/// </summary>
public sealed class WorkItemRetrier
{
    private readonly IWorkItemStore _store;
    private readonly ITaskQueue _queue;
    private readonly IGitHost _gitHost;
    private readonly IAgentStreamSummaryStore? _streamSummaries;
    private readonly IAuditReportStore? _auditReports;
    private readonly IProjectRepository? _projects;
    private readonly IReleaseStore? _releases;
    private readonly ILogger<WorkItemRetrier> _log;

    public WorkItemRetrier(
        IWorkItemStore store,
        ITaskQueue queue,
        IGitHost gitHost,
        ILogger<WorkItemRetrier> log,
        IAgentStreamSummaryStore? streamSummaries = null,
        IAuditReportStore? auditReports = null,
        IProjectRepository? projects = null,
        IReleaseStore? releases = null)
    {
        _store = store;
        _queue = queue;
        _gitHost = gitHost;
        _streamSummaries = streamSummaries;
        _auditReports = auditReports;
        _projects = projects;
        _releases = releases;
        _log = log;
    }

    public async Task<(bool Success, string? Error, WorkItemState? ResumeState, string? ActualFrom)> RetryAsync(
        WorkItem item,
        string? from = null,
        string trigger = "manual",
        CancellationToken ct = default)
    {
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
                return (false, $"cannot auto-pick retry phase for work item {item.Id}: {ex.Message}", null, null);
            }
        }

        var requestedFrom = from!.Trim().ToLowerInvariant();
        var resumeState = requestedFrom switch
        {
            "work" => WorkItemState.Queued,
            "audit" => WorkItemState.WorkComplete,
            "merge" => WorkItemState.AuditPassed,
            "upstream" => WorkItemState.Merged,
            _ => (WorkItemState?)null,
        };

        if (resumeState is null)
            return (false, $"invalid 'from' value '{from}'", null, null);

        var actualFrom = requestedFrom;

        // For from != "work", the pipeline expects the bare repo to still be present.
        if (resumeState != WorkItemState.Queued)
        {
            var present = await _gitHost.RepositoryExistsAsync(item.Id, ct);
            if (!present)
                return (false, $"cannot retry from '{from}': bare repo for work item {item.Id} no longer exists", null, null);

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

        // Reset RecoveryAttempts and increment QuotaRetryAttempts if this is an auto-retry.
        var resumed = item.With(resumeState.Value, error: null) with
        {
            RecoveryAttempts = 0,
            QuotaRetryAttempts = trigger != "manual" ? item.QuotaRetryAttempts + 1 : item.QuotaRetryAttempts,
            StartedAt = null
        };

        // Atomic conditional update to prevent race conditions.
        // We retry from Failed, AuditFailed, MergeConflictResolutionFailed, Cancelled, or AbandonedAfterRecoveryAttempts.
        var updated = await _store.TryUpdateIfStateAsync(resumed, item.State, ct);
        if (!updated)
        {
            return (false, "work item state changed concurrently; retry aborted", null, null);
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
                reverted = await _store.TryUpdateIfStateAsync(item, resumeState.Value, CancellationToken.None);
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
                    item.Id, resumeState.Value, item.State);
                return (false, $"queue enqueue failed after state update; rolled back to {item.State}: {ex.Message}", null, actualFrom);
            }

            _log.LogError(ex,
                "Retry of work item {Id} updated state to {State} but queue kick failed and rollback did not apply",
                item.Id, resumeState.Value);
            return (false, $"queue enqueue failed after state update and rollback did not apply: {ex.Message}", null, actualFrom);
        }

        AuditLog.WorkItemRetried(item.Id, trigger == "manual" ? auditFrom : $"{auditFrom} (auto-retry: {trigger})");
        return (true, null, resumeState, actualFrom);
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
        return ahead
            ? ("audit", "work branch has prior commits ahead of base")
            : ("work", "work branch has no commits ahead of base");
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

        var requestedFrom = (from ?? "work").Trim().ToLowerInvariant();
        var resumeState = requestedFrom switch
        {
            "work" => WorkItemState.Queued,
            "audit" => WorkItemState.WorkComplete,
            "merge" => WorkItemState.AuditPassed,
            _ => (WorkItemState?)null,
        };
        if (resumeState is null)
            return new ResumeOutcome(
                ResumeStatus.BadRequest,
                $"invalid 'from' value '{from}'; expected one of: work, audit, merge",
                null,
                null);

        // Resume preserves the prior bare repo + work-branch (that is the
        // entire point — recovering the agent commits the operator's cancel
        // left intact). If either is gone, the operator must fall back to
        // /replay for a fresh start.
        const string preconditionMessage =
            "bare repo or work-branch no longer present; cannot resume — use POST /workitems/{id}/replay for a fresh start";
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

        // from=audit / from=merge bypass the work phase, so the existing
        // commits on the work-branch must already be auditable. The cheapest
        // signal is the audit-reports table: a non-empty set proves the audit
        // phase ran at least once on these commits. If it hasn't, force the
        // operator through from=work (which produces a rework iteration on top
        // of the prior commits) rather than running auditors on a clean diff.
        if (resumeState.Value != WorkItemState.Queued && _auditReports is not null)
        {
            var reports = await _auditReports.GetByWorkItemAsync(item.Id.ToString(), ct);
            if (reports.Count == 0)
                return new ResumeOutcome(
                    ResumeStatus.Conflict,
                    $"cannot resume from '{requestedFrom}': work-branch has never reached an audit-passing state (no prior audit reports). Use from=work to produce an auditable rework iteration first.",
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
            State = resumeState.Value,
            UpdatedAt = DateTimeOffset.UtcNow,
            LastError = null,
            CancellationReason = null,
            CancellationSource = null,
            FailureKind = null,
            RecoveryAttempts = 0,
            StartedAt = null,
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
}
