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

    public async Task<(bool Success, string? Error, WorkItemState? ResumeState)> RetryAsync(
        WorkItem item,
        string from = "work",
        string trigger = "manual",
        CancellationToken ct = default)
    {
        var resumeState = from.Trim().ToLowerInvariant() switch
        {
            "work" => WorkItemState.Queued,
            "audit" => WorkItemState.WorkComplete,
            "merge" => WorkItemState.AuditPassed,
            "upstream" => WorkItemState.Merged,
            _ => (WorkItemState?)null,
        };

        if (resumeState is null)
            return (false, $"invalid 'from' value '{from}'", null);

        // For from != "work", the pipeline expects the bare repo to still be present.
        if (resumeState != WorkItemState.Queued)
        {
            var present = await _gitHost.RepositoryExistsAsync(item.Id, ct);
            if (!present)
                return (false, $"cannot retry from '{from}': bare repo for work item {item.Id} no longer exists", null);
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
            return (false, "work item state changed concurrently; retry aborted", null);
        }

        if (_streamSummaries is not null)
        {
            try { await _streamSummaries.DeleteByWorkItemAsync(item.Id, ct); }
            catch (Exception ex) { _log.LogWarning(ex, "Failed to delete stream summaries for work item {Id}", item.Id); }
        }

        AuditLog.WorkItemRetried(item.Id, trigger == "manual" ? from : $"{from} (auto-retry: {trigger})");
        await _queue.EnqueueAsync(resumed.Id, ct);

        return (true, null, resumeState);
    }
}
