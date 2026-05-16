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
    private readonly ILogger<WorkItemRetrier> _log;

    public WorkItemRetrier(
        IWorkItemStore store,
        ITaskQueue queue,
        IGitHost gitHost,
        ILogger<WorkItemRetrier> log,
        IAgentStreamSummaryStore? streamSummaries = null)
    {
        _store = store;
        _queue = queue;
        _gitHost = gitHost;
        _streamSummaries = streamSummaries;
        _log = log;
    }

    public async Task<(bool Success, string? Error, WorkItemState? ResumeState, string? ActualFrom)> RetryAsync(
        WorkItem item,
        string from = "work",
        string trigger = "manual",
        CancellationToken ct = default)
    {
        var requestedFrom = from.Trim().ToLowerInvariant();
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
            QuotaRetryAttempts = trigger != "manual" ? item.QuotaRetryAttempts + 1 : item.QuotaRetryAttempts
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
        AuditLog.WorkItemRetried(item.Id, trigger == "manual" ? auditFrom : $"{auditFrom} (auto-retry: {trigger})");
        await _queue.EnqueueAsync(resumed.Id, ct);

        return (true, null, resumeState, actualFrom);
    }
}
