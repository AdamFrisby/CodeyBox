using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Hosted service that automatically retries work items that failed due to
/// quota exhaustion, once quota is available again.
/// </summary>
public sealed class QuotaRetryScheduler : BackgroundService, IDisposable
{
    private readonly IWorkItemStore _store;
    private readonly WorkItemRetrier _retrier;
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
        WorkItemRetrier retrier,
        OrchestratorOptions opts,
        ILogger<QuotaRetryScheduler> log,
        AgentClassRouter? router = null,
        IProjectRepository? projects = null,
        IQueueController? queueController = null,
        IWebhookDispatcher? webhooks = null,
        TimeProvider? timeProvider = null)
    {
        _store = store;
        _retrier = retrier;
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
        await foreach (var item in _store.ListByStateAsync(WorkItemState.WaitingForQuotaReset, ct))
        {
            if (item.NextQuotaRetryAt.HasValue)
            {
                ScheduleTargetedRetry(item.Id, item.NextQuotaRetryAt.Value);
                count++;
            }
        }
        _log.LogInformation("Re-armed {Count} quota retry timers", count);
    }

    // The periodic sweep is the safety net: it walks every Failed/quota item
    // and asks the router whether it could run now, ignoring NextQuotaRetryAt
    // entirely. NextQuotaRetryAt is an *optimisation* (drives the targeted
    // timer), not a "don't even try" gate — probe caches can be stale, the
    // park-time estimate can be wrong, and class members can refill earlier
    // than predicted. Keeping the sweep router-driven means we recover even
    // when those estimates miss.
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
        await foreach (var item in _store.ListByStateAsync(WorkItemState.WaitingForQuotaReset, ct))
        {
            await TryRetryAsync(item, "periodic", ct);
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
                if (item is { State: WorkItemState.Failed, FailureKind: "quota" }
                    || item is { State: WorkItemState.WaitingForQuotaReset })
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

        // Re-use logic from shared WorkItemRetrier to ensure identical side effects,
        // audit logs, and conditional state updates (prevents race conditions).
        var (success, error, _) = await _retrier.RetryAsync(item, from: "work", trigger, ct);

        if (!success)
        {
            _log.LogWarning("Failed to trigger quota auto-retry for work item {Id}: {Error}", item.Id, error);
            return;
        }

        if (_webhooks is not null)
        {
            var project = await _projects!.GetAsync(item.ProjectId, ct);
            var updated = await _store.GetAsync(item.Id, ct);
            await _webhooks.PublishAsync(new WebhookEvent
            {
                Event = "work_item.auto_retry",
                WorkItem = updated ?? item,
                Project = project,
                Details = new
                {
                    workItemId = item.Id.ToString(),
                    reason = "quota",
                    attemptNumber = (updated?.QuotaRetryAttempts ?? item.QuotaRetryAttempts + 1),
                    triggeredBy = trigger
                }
            }, CancellationToken.None);
        }
    }

    /// <summary>
    /// Notifies the scheduler that a work item has failed with a quota error,
    /// so it can schedule a targeted retry.
    /// </summary>
    public async Task NotifyQuotaFailureAsync(WorkItem item)
    {
        if (!_opts.AutoRetryOnQuotaFailure.Enabled) return;
        var isQuotaFailed = item.State == WorkItemState.Failed && item.FailureKind == "quota";
        var isWaitingReset = item.State == WorkItemState.WaitingForQuotaReset;
        if (!isQuotaFailed && !isWaitingReset) return;

        // Park time should be the soonest any class member can plausibly become
        // eligible — MIN(resetAt) across exhausted members — not the last-tried
        // agent's reset (which is often the latest, e.g. gemini's per-model
        // daily window of 21–24h vs claude's 5h rolling cap).
        DateTimeOffset? resetAt = null;
        if (_router is not null && _projects is not null)
        {
            try
            {
                var project = await _projects.GetAsync(item.ProjectId, CancellationToken.None);
                resetAt = await _router.ComputeEarliestExhaustedResetAsync(item, project, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Failed to compute earliest class-member reset for {Id}; falling back to failing-agent reset", item.Id);
            }
        }

        // Always consider the failing agent's own reset: probe caches may be
        // stale, and the failure provides fresh information the probe doesn't
        // have yet. Take the earliest across both sources.
        if (item.QuotaResetAt is { } failingReset
            && (resetAt is null || failingReset < resetAt.Value))
        {
            resetAt = failingReset;
        }

        // Fall back to a pipeline-supplied NextQuotaRetryAt if neither
        // earliest-member-reset nor failing-agent reset is available (covers the
        // WaitingForQuotaReset path where the pipeline already computed a wake time).
        DateTimeOffset? nextRetryAt = resetAt.HasValue
            ? resetAt.Value.Add(_opts.AutoRetryOnQuotaFailure.ClockDriftSafetyMargin)
            : item.NextQuotaRetryAt;
        if (nextRetryAt is null) return;

        try
        {
            var updated = await _store.TryUpdateIfStateAsync(
                item with { NextQuotaRetryAt = nextRetryAt }, item.State, CancellationToken.None);
            if (updated)
            {
                ScheduleTargetedRetry(item.Id, nextRetryAt.Value);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error updating NextQuotaRetryAt for work item {Id}", item.Id);
        }
    }
    public override void Dispose()
    {
        foreach (var timer in _targetedTimers.Values)
        {
            timer.Dispose();
        }
        _targetedTimers.Clear();
        base.Dispose();
    }
}
