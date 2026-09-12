using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Intermediate webhook/event publishing for the pipeline. Owns the
/// fire-and-forget progress signals surfaced to webhook subscribers
/// (iteration/audit/merge events); work-item.* terminal events still cover
/// the boundary outcomes. Publishes are best-effort: dispatcher/store
/// failures must NEVER bubble out of the pipeline, so
/// <see cref="TryPublishEventAsync"/> swallows them and logs at Debug.
/// Cancellation is the exception — when the caller's token fires we rethrow
/// so the pipeline can unwind for shutdown rather than absorbing the signal.
/// Extracted mechanically from <see cref="PipelineRunner"/>; behavior is
/// unchanged and the runner delegates to this collaborator.
/// </summary>
internal sealed class PipelineWebhookPublisher
{
    private readonly IWorkItemStore _store;
    private readonly IWebhookDispatcher _webhooks;
    private readonly IGitHost _gitHost;
    private readonly ILogger<PipelineRunner> _log;

    public PipelineWebhookPublisher(
        IWorkItemStore store,
        IWebhookDispatcher webhooks,
        IGitHost gitHost,
        ILogger<PipelineRunner> log)
    {
        _store = store;
        _webhooks = webhooks;
        _gitHost = gitHost;
        _log = log;
    }

    /// <summary>Explicit map from <see cref="AuditSeverity"/> to the wire string
    /// documented in webhooks.md. Keeps the contract stable independently of
    /// any future enum rename.</summary>
    private static string ToWireSeverity(AuditSeverity s) => s switch
    {
        AuditSeverity.Info => "Info",
        AuditSeverity.Warning => "Warning",
        AuditSeverity.Error => "Error",
        _ => s.ToString(),
    };

    public async Task TryPublishEventAsync(WorkItem item, Project project, string eventName, object details, CancellationToken ct)
    {
        try
        {
            var current = await _store.GetAsync(item.Id, ct) ?? item;
            await _webhooks.PublishAsync(new WebhookEvent
            {
                Event = eventName,
                WorkItem = current,
                Project = project,
                Details = details,
            }, CancellationToken.None);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "{Event} webhook publish failed for {Id}", eventName, item.Id);
        }
    }

    public Task PublishIterationStartedAsync(
        WorkItem item, Project project, string phase, int iteration, CancellationToken ct)
    {
        // Capture the timestamp at the call site (before the store read inside
        // TryPublishEventAsync) so DispatchedAt is the actual dispatch moment.
        var dispatchedAt = DateTimeOffset.UtcNow;
        return TryPublishEventAsync(item, project, "iteration.started", new IterationStartedDetails
        {
            WorkItemId = item.Id.ToString(),
            Iteration = iteration,
            Phase = phase,
            DispatchedAt = dispatchedAt,
        }, ct);
    }

    public async Task PublishIterationCompletedAsync(
        WorkItem item, Project project, string phase, int iteration,
        string repoId, string workBranch, DateTimeOffset startedAt, CancellationToken ct)
    {
        var commitSha = await TryResolveBranchTipAsync(repoId, workBranch, ct);
        var durationMs = (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
        await TryPublishEventAsync(item, project, "iteration.completed", new IterationCompletedDetails
        {
            WorkItemId = item.Id.ToString(),
            Iteration = iteration,
            Phase = phase,
            CommitSha = commitSha,
            DurationMs = durationMs,
        }, ct);
    }

    public Task PublishAuditStartedAsync(
        WorkItem item, Project project, int iteration, IReadOnlyList<IAuditor> auditors, CancellationToken ct)
    {
        return TryPublishEventAsync(item, project, "audit.started", new AuditStartedDetails
        {
            WorkItemId = item.Id.ToString(),
            Iteration = iteration,
            AuditorsScheduled = auditors.Select(a => a.Name).ToList(),
        }, ct);
    }

    public Task PublishAuditFindingsEmittedAsync(
        WorkItem item, Project project, int iteration,
        IReadOnlyList<AuditFinding> findings, int blocking, int nonBlocking, CancellationToken ct)
    {
        var payload = findings.Select(f => new AuditFindingPayload
        {
            Auditor = f.AuditorName,
            Severity = ToWireSeverity(f.Severity),
            Title = f.Title,
            Location = f.Location,
            Description = f.Description,
        }).ToList();
        return TryPublishEventAsync(item, project, "audit.findings.emitted", new AuditFindingsEmittedDetails
        {
            WorkItemId = item.Id.ToString(),
            Iteration = iteration,
            Findings = payload,
            Blocking = blocking,
            NonBlocking = nonBlocking,
        }, ct);
    }

    public Task PublishAuditCompletedAsync(
        WorkItem item, Project project, int iteration, string verdict, DateTimeOffset startedAt, CancellationToken ct)
    {
        var durationMs = (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
        return TryPublishEventAsync(item, project, "audit.completed", new AuditCompletedDetails
        {
            WorkItemId = item.Id.ToString(),
            Iteration = iteration,
            Verdict = verdict,
            DurationMs = durationMs,
        }, ct);
    }

    public Task PublishMergeStartedAsync(
        WorkItem item, Project project, string baseBranch, string workBranch, CancellationToken ct)
    {
        return TryPublishEventAsync(item, project, "merge.started", new MergeStartedDetails
        {
            WorkItemId = item.Id.ToString(),
            BaseBranch = baseBranch,
            WorkBranch = workBranch,
        }, ct);
    }

    public Task PublishMergeCompletedAsync(
        WorkItem item, Project project, string baseBranch, string workBranch,
        string? mergeSha, CancellationToken ct)
    {
        return TryPublishEventAsync(item, project, "merge.completed", new MergeCompletedDetails
        {
            WorkItemId = item.Id.ToString(),
            BaseBranch = baseBranch,
            WorkBranch = workBranch,
            MergeSha = mergeSha,
        }, ct);
    }

    private async Task<string?> TryResolveBranchTipAsync(string repoId, string branch, CancellationToken ct)
    {
        try { return await _gitHost.ResolveCommitAsync(repoId, branch, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Failed to resolve commit SHA for branch {Branch} in repo {Repo}", branch, repoId);
            return null;
        }
    }
}
