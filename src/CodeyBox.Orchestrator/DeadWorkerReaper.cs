using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Periodic background sweep that detects workers whose heartbeat has gone
/// stale and recovers any work items they were holding. Also exposed as a
/// callable method (<see cref="RunOnceAsync"/>) so
/// <see cref="OrchestratorService"/> can invoke it synchronously at startup
/// before the worker pool begins pulling from the queue.
///
/// Idempotency guarantee: <see cref="IWorkerRegistry.ClaimDeadWorkersAsync"/>
/// atomically DELETEs stale rows in one transaction; only the caller that
/// successfully removed a row performs recovery for that worker. Concurrent
/// or restarted reapers are safe.
/// </summary>
public sealed class DeadWorkerReaper : BackgroundService
{
    private readonly IWorkerRegistry _registry;
    private readonly IWorkItemStore _store;
    private readonly ITaskQueue _queue;
    private readonly IWebhookDispatcher? _webhooks;
    private readonly DeadWorkerOptions _opts;
    private readonly ILogger<DeadWorkerReaper> _log;

    public DeadWorkerReaper(
        IWorkerRegistry registry,
        IWorkItemStore store,
        ITaskQueue queue,
        DeadWorkerOptions opts,
        ILogger<DeadWorkerReaper> log,
        IWebhookDispatcher? webhooks = null)
    {
        _registry = registry;
        _store = store;
        _queue = queue;
        _opts = opts;
        _log = log;
        _webhooks = webhooks;
    }

    /// <summary>
    /// Runs a single reaper sweep. Safe to call concurrently or repeatedly;
    /// the registry's atomic DELETE ensures no double-recovery.
    /// </summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            var cutoff = DateTimeOffset.UtcNow - _opts.DeadWorkerThreshold;
            var dead = await _registry.ClaimDeadWorkersAsync(cutoff, ct);
            foreach (var worker in dead)
                await RecoverWorkerAsync(worker, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Dead-worker reaper sweep failed");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_opts.CheckInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await RunOnceAsync(stoppingToken);
    }

    private async Task RecoverWorkerAsync(WorkerRegistration worker, CancellationToken ct)
    {
        if (worker.CurrentWorkItemId is null)
        {
            _log.LogDebug("Dead worker {WorkerId} (host={Host}) had no active work item; row removed", worker.WorkerId, worker.HostName);
            return;
        }

        if (!Guid.TryParse(worker.CurrentWorkItemId, out var guid))
        {
            _log.LogWarning("Dead worker {WorkerId} had malformed work item id '{ItemId}'; skipping", worker.WorkerId, worker.CurrentWorkItemId);
            return;
        }

        var itemId = new WorkItemId(guid);
        var item = await _store.GetAsync(itemId, ct);
        if (item is null)
        {
            _log.LogWarning("Dead worker {WorkerId} referenced work item {ItemId} which no longer exists", worker.WorkerId, itemId);
            return;
        }

        if (!string.IsNullOrWhiteSpace(item.PreemptCheckpoint)
            && item.State is WorkItemState.Working or WorkItemState.Reworking)
        {
            var preempted = item with { StartedAt = null, UpdatedAt = DateTimeOffset.UtcNow };
            await _store.UpdateAsync(preempted, ct);
            await _queue.EnqueueAsync(itemId, ct);
            _log.LogInformation(
                "Dead worker {WorkerId}: work item {ItemId} has preempt checkpoint {Ref}; re-enqueued for clean resume",
                worker.WorkerId, itemId, item.PreemptCheckpoint);
            return;
        }

        if (item.State == WorkItemState.Working)
        {
            var failed = item with
            {
                State = WorkItemState.Failed,
                LastError = "worker died while work phase was running without a preempt checkpoint",
                RecoveryAttempts = item.RecoveryAttempts + 1,
                StartedAt = null,
                PreemptedAt = null,
                PreemptCheckpoint = null,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            await _store.UpdateAsync(failed, ct);
            _log.LogWarning(
                "Dead worker {WorkerId}: work item {ItemId} was Working without a preempt checkpoint; marked Failed",
                worker.WorkerId, itemId);
            return;
        }

        var recoveryTarget = MapToRecoveryState(item.State);
        if (recoveryTarget is null)
        {
            _log.LogInformation(
                "Dead worker {WorkerId}: item {ItemId} in non-recoverable state {State} (already terminal or not worker-owned); no action",
                worker.WorkerId, itemId, item.State);
            return;
        }

        var fromState = item.State;
        var attempt = item.RecoveryAttempts + 1;
        WorkItem updated;

        if (attempt > _opts.MaxRecoveryAttempts)
        {
            updated = item with
            {
                State = WorkItemState.Failed,
                LastError = "exceeded MaxRecoveryAttempts",
                RecoveryAttempts = attempt,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            _log.LogWarning(
                "Dead worker {WorkerId}: work item {ItemId} exceeded MaxRecoveryAttempts ({Max}); failing permanently",
                worker.WorkerId, itemId, _opts.MaxRecoveryAttempts);
            AuditLog.DeadWorkerFailedTerminal(itemId, worker.WorkerId, attempt);
        }
        else
        {
            updated = item with
            {
                State = recoveryTarget.Value,
                LastError = null,
                RecoveryAttempts = attempt,
                UpdatedAt = DateTimeOffset.UtcNow,
                // Re-queued items must not appear in-flight to CountInFlightAsync.
                StartedAt = recoveryTarget == WorkItemState.Queued ? null : item.StartedAt,
            };
            _log.LogInformation(
                "Dead worker {WorkerId}: recovering work item {ItemId} from {From} → {To} (attempt {Attempt}/{Max})",
                worker.WorkerId, itemId, fromState, recoveryTarget, attempt, _opts.MaxRecoveryAttempts);
            AuditLog.DeadWorkerRecovered(itemId, worker.WorkerId, fromState, recoveryTarget.Value, attempt);
        }

        await _store.UpdateAsync(updated, ct);

        if (_webhooks is not null)
        {
            _ = _webhooks.PublishAsync(new WebhookEvent
            {
                Event = "work_item.recovered",
                WorkItem = updated,
                Details = new
                {
                    workItemId = itemId.ToString(),
                    projectId = item.ProjectId.Value,
                    fromState = fromState.ToString(),
                    toState = updated.State.ToString(),
                    reason = "dead worker detected",
                    recoveryAttempt = attempt,
                    maxRecoveryAttempts = _opts.MaxRecoveryAttempts,
                },
            }, CancellationToken.None);
        }

        if (updated.State != WorkItemState.Failed)
            await _queue.EnqueueAsync(itemId, ct);
    }

    /// <summary>
    /// Maps a mid-flight worker-owned state to the state the reaper should
    /// recover it into, or null if the state is terminal / not worker-owned.
    /// </summary>
    internal static WorkItemState? MapToRecoveryState(WorkItemState state) => state switch
    {
        WorkItemState.Reworking => WorkItemState.Queued,
        WorkItemState.Auditing => WorkItemState.WorkComplete,
        WorkItemState.Merging => WorkItemState.AuditPassed,
        WorkItemState.UpstreamPushing => WorkItemState.Merged,
        _ => null,
    };
}
