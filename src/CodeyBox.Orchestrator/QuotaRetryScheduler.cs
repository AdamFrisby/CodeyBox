using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Hosted service that automatically retries work items that failed due to
/// quota exhaustion, once quota is available again.
/// </summary>
public sealed class QuotaRetryScheduler : BackgroundService
{
    private readonly IWorkItemStore _store;
    private readonly ITaskQueue _queue;
    private readonly AgentClassRouter? _router;
    private readonly IProjectRepository? _projects;
    private readonly IQueueController? _queueController;
    private readonly IWebhookDispatcher? _webhooks;
    private readonly OrchestratorOptions _opts;
    private readonly TimeProvider _time;
    private readonly ILogger<QuotaRetryScheduler> _log;

    // Active timers for targeted wakeups. Key = WorkItemId.
    private readonly ConcurrentDictionary<WorkItemId, ITimer> _targetedTimers = new();

    public QuotaRetryScheduler(
        IWorkItemStore store,
        ITaskQueue queue,
        OrchestratorOptions opts,
        ILogger<QuotaRetryScheduler> log,
        AgentClassRouter? router = null,
        IProjectRepository? projects = null,
        IQueueController? queueController = null,
        IWebhookDispatcher? webhooks = null,
        TimeProvider? timeProvider = null)
    {
        _store = store;
        _queue = queue;
        _opts = opts;
        _log = log;
        _router = router;
        _projects = projects;
        _queueController = queueController;
        _webhooks = webhooks;
        _time = timeProvider ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_opts.AutoRetryOnQuotaFailure.Enabled)
        {
            _log.LogInformation("Quota auto-retry is disabled");
            return;
        }

        _log.LogInformation("Quota auto-retry is enabled. Periodic interval: {Interval}, Drift margin: {Margin}",
            _opts.AutoRetryOnQuotaFailure.PeriodicCheckInterval,
            _opts.AutoRetryOnQuotaFailure.ClockDriftSafetyMargin);

        // Re-arm timers from the DB on startup.
        await RearmTimersAsync(stoppingToken);

        // Periodic sweep loop.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_opts.AutoRetryOnQuotaFailure.PeriodicCheckInterval, stoppingToken);
                await RunPeriodicSweepAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Error during periodic quota retry sweep");
            }
        }
    }

    private async Task RearmTimersAsync(CancellationToken ct)
    {
        _log.LogInformation("Re-arming quota retry timers from database...");
        var count = 0;
        await foreach (var item in _store.ListByStateAsync(WorkItemState.Failed, ct))
        {
            if (item.FailureKind == "quota" && item.NextQuotaRetryAt.HasValue)
            {
                ScheduleTargetedRetry(item.Id, item.NextQuotaRetryAt.Value);
                count++;
            }
        }
        _log.LogInformation("Re-armed {Count} quota retry timers", count);
    }

    private async Task RunPeriodicSweepAsync(CancellationToken ct)
    {
        _log.LogDebug("Starting periodic quota retry sweep");
        await foreach (var item in _store.ListByStateAsync(WorkItemState.Failed, ct))
        {
            if (item.FailureKind == "quota")
            {
                await TryRetryAsync(item, "periodic", ct);
            }
        }
    }

    private void ScheduleTargetedRetry(WorkItemId id, DateTimeOffset nextRetryAt)
    {
        var now = _time.GetUtcNow();
        var delay = nextRetryAt - now;
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;

        _targetedTimers.AddOrUpdate(id,
            _ => _time.CreateTimer(OnTargetedTimerFired, id, delay, Timeout.InfiniteTimeSpan),
            (_, old) =>
            {
                old.Dispose();
                return _time.CreateTimer(OnTargetedTimerFired, id, delay, Timeout.InfiniteTimeSpan);
            });
    }

    private void OnTargetedTimerFired(object? state)
    {
        if (state is not WorkItemId id) return;
        _targetedTimers.TryRemove(id, out _);

        // Run the retry logic in a background task to avoid blocking the timer thread.
        _ = Task.Run(async () =>
        {
            try
            {
                var item = await _store.GetAsync(id, CancellationToken.None);
                if (item is { State: WorkItemState.Failed, FailureKind: "quota" })
                {
                    await TryRetryAsync(item, "targeted", CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Error during targeted quota retry for work item {Id}", id);
            }
        });
    }

    private async Task TryRetryAsync(WorkItem item, string trigger, CancellationToken ct)
    {
        // 1. Check max retries.
        if (item.QuotaRetryAttempts >= _opts.AutoRetryOnQuotaFailure.MaxAutoRetriesPerWorkItem)
        {
            _log.LogInformation("Work item {Id} reached max quota auto-retries ({Max}); skipping",
                item.Id, _opts.AutoRetryOnQuotaFailure.MaxAutoRetriesPerWorkItem);
            return;
        }

        // 2. Check if queue is paused.
        if (_queueController is not null)
        {
            if (_queueController.State == QueueState.Paused)
            {
                _log.LogInformation("Global queue is paused; skipping auto-retry for work item {Id}", item.Id);
                return;
            }

            var projectState = await _queueController.GetProjectStateAsync(item.ProjectId, ct);
            if (projectState is { Paused: true })
            {
                _log.LogInformation("Project {ProjectId} queue is paused; skipping auto-retry for work item {Id}",
                    item.ProjectId, item.Id);
                return;
            }
        }

        // 3. Resolve project.
        if (_projects is null) return;
        var project = await _projects.GetAsync(item.ProjectId, ct);
        if (project is null) return;

        // 4. Ask the quota gate.
        if (_router is null) return;
        var decision = await _router.ResolveAsync(item, project, ct);
        if (decision.ShouldWait)
        {
            _log.LogDebug("Work item {Id} still gated by quota; decision: {Reason}", item.Id, decision.Reason);
            return;
        }
        if (decision.NoEligibleMembers)
        {
            _log.LogInformation("Work item {Id} has no eligible class members; skipping auto-retry", item.Id);
            return;
        }

        // 5. Trigger retry.
        await PerformRetryAsync(item, trigger, ct);
    }

    private async Task PerformRetryAsync(WorkItem item, string trigger, CancellationToken ct)
    {
        _log.LogInformation("Triggering quota auto-retry ({Trigger}) for work item {Id} (attempt {Attempt})",
            trigger, item.Id, item.QuotaRetryAttempts + 1);

        // Increment retry attempts and reset state to Queued.
        // Re-using logic from RetryAsync endpoint.
        var resumed = item.With(WorkItemState.Queued) with
        {
            QuotaRetryAttempts = item.QuotaRetryAttempts + 1,
            RecoveryAttempts = 0
        };

        // TransitionFailed might have set FailureKind and QuotaResetAt,
        // .With(Queued) clears them.

        await _store.UpdateAsync(resumed, ct);
        AuditLog.WorkItemRetried(item.Id, $"auto-retry ({trigger})");

        await _queue.EnqueueAsync(resumed.Id, ct);

        if (_webhooks is not null)
        {
            var project = await _projects!.GetAsync(item.ProjectId, ct);
            await _webhooks.PublishAsync(new WebhookEvent
            {
                Event = "workitem.auto_retry",
                WorkItem = resumed,
                Project = project,
                Details = new
                {
                    workItemId = item.Id.ToString(),
                    reason = "quota",
                    attemptNumber = resumed.QuotaRetryAttempts,
                    triggeredBy = trigger
                }
            }, CancellationToken.None);
        }
    }

    /// <summary>
    /// Notifies the scheduler that a work item has failed with a quota error,
    /// so it can schedule a targeted retry.
    /// </summary>
    public void NotifyQuotaFailure(WorkItem item)
    {
        if (!_opts.AutoRetryOnQuotaFailure.Enabled) return;
        if (item.State != WorkItemState.Failed || item.FailureKind != "quota" || !item.QuotaResetAt.HasValue) return;

        var nextRetryAt = item.QuotaResetAt.Value.Add(_opts.AutoRetryOnQuotaFailure.ClockDriftSafetyMargin);
        
        // Update the work item with the next retry time so it survives restarts.
        _ = Task.Run(async () =>
        {
            try
            {
                var current = await _store.GetAsync(item.Id, CancellationToken.None);
                if (current is { State: WorkItemState.Failed, FailureKind: "quota" })
                {
                    await _store.UpdateAsync(current with { NextQuotaRetryAt = nextRetryAt }, CancellationToken.None);
                    ScheduleTargetedRetry(item.Id, nextRetryAt);
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Error updating NextQuotaRetryAt for work item {Id}", item.Id);
            }
        });
    }
}
